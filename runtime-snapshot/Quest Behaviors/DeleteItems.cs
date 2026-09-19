// Behavior originally contributed by Mastahg
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Styx;
using Styx.Helpers;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Questing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace DeleteItems
{
    public class DeleteItems : CustomForcedBehavior
    {
        private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(10);

        public DeleteItems(Dictionary<string, string> args)
            : base(args)
        {
            try
            {
                // QuestRequirement* attributes are explained here...
                //    http://www.thebuddyforum.com/mediawiki/index.php?title=Honorbuddy_Programming_Cookbook:_QuestId_for_Custom_Behaviors
                // ...and also used for IsDone processing.
                Names = GetAttributeAsArray<string>("Ids", true, null, null, ",".ToCharArray());
            }
            catch (Exception except)
            {
                LogMessage("error", "BEHAVIOR MAINTENANCE PROBLEM: " + except.Message
                                    + "\nFROM HERE:\n"
                                    + except.StackTrace + "\n");
                IsAttributeProblem = true;
            }
        }

        private string[] Names;

        public QuestCompleteRequirement QuestRequirementComplete { get; private set; }
        public QuestInLogRequirement QuestRequirementInLog { get; private set; }

        private bool _isBehaviorDone;
        private bool _isDisposed;

        // One delete request owns exactly one physical item from pickup until
        // cursor release and disappearance are both observed.
        private ulong _pendingGuid;
        private uint _pendingEntry;
        private DateTime _pendingSince;
        private bool _deleteRequested;

        // A foreign/non-empty cursor makes TryPickUp refuse safely. Bound that
        // refusal too so a destructive profile node cannot spin forever.
        private ulong _pickupRefusalGuid;
        private DateTime _pickupRefusalSince;

        ~DeleteItems()
        {
            Dispose(false);
        }

        public void Dispose(bool isExplicitlyInitiatedDispose)
        {
            if (!_isDisposed)
            {
                if (_pendingGuid != 0)
                {
                    Logging.Write(
                        "[DeleteItems] Disposed with pending item {0} ({1}); cursor ownership is left untouched.",
                        _pendingGuid, _pendingEntry);
                }

                TreeRoot.GoalText = string.Empty;
                TreeRoot.StatusText = string.Empty;
                base.Dispose();
            }

            _isDisposed = true;
        }

        #region Overrides of CustomForcedBehavior

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public override bool IsDone
        {
            get { return _isBehaviorDone; }
        }

        public override void OnStart()
        {
            OnStart_HandleAttributeProblem();
            TreeRoot.GoalText = "Deleting explicitly requested inventory items";
        }

        public override void OnTick()
        {
            if (_isBehaviorDone || _isDisposed)
                return;

            if (IsAttributeProblem)
            {
                _isBehaviorDone = true;
                return;
            }

            if (StyxWoW.Me == null)
            {
                FailClosed("player inventory is unavailable");
                return;
            }

            if (_pendingGuid != 0)
            {
                TickPendingDelete();
                return;
            }

            WoWItem item = FindNextRequestedItem();
            if (item == null)
            {
                TreeRoot.StatusText = "DeleteItems completed: no requested bag items remain";
                _isBehaviorDone = true;
                return;
            }

            BeginDelete(item);
        }

        #endregion

        private WoWItem FindNextRequestedItem()
        {
            if (StyxWoW.Me == null || StyxWoW.Me.BagItems == null)
                return null;

            foreach (string name in Names ?? new string[0])
            {
                uint entry = name.ToUInt32();
                if (entry == 0)
                    continue;

                WoWItem item = StyxWoW.Me.BagItems.FirstOrDefault(
                    candidate => candidate != null && candidate.IsValid && candidate.Entry == entry);
                if (item != null)
                    return item;
            }

            return null;
        }

        private void BeginDelete(WoWItem item)
        {
            ulong expectedGuid = item.Guid;
            uint expectedEntry = item.Entry;
            if (expectedGuid == 0 || expectedEntry == 0)
            {
                FailClosed("selected item has no stable GUID/entry identity");
                return;
            }

            if (!item.TryPickUp())
            {
                if (_pickupRefusalGuid != expectedGuid)
                {
                    _pickupRefusalGuid = expectedGuid;
                    _pickupRefusalSince = DateTime.UtcNow;
                }

                TreeRoot.StatusText = "Waiting for an empty cursor and stable requested item slot";
                if (DateTime.UtcNow - _pickupRefusalSince >= DeleteTimeout)
                    FailClosed("safe item pickup remained refused for the bounded delete window");
                return;
            }

            _pickupRefusalGuid = 0;
            _pickupRefusalSince = DateTime.MinValue;
            _pendingGuid = expectedGuid;
            _pendingEntry = expectedEntry;
            _pendingSince = DateTime.UtcNow;
            _deleteRequested = false;

            TreeRoot.StatusText = "Submitting owned delete request for item " + _pendingEntry;
            TryIssueDeleteRequest();
        }

        private void TickPendingDelete()
        {
            if (DateTime.UtcNow - _pendingSince >= DeleteTimeout)
            {
                FailClosed("delete confirmation/acknowledgement did not complete within the bounded window");
                return;
            }

            int cursorState = ReadOwnedCursorState(_pendingEntry);

            if (cursorState == 0)
            {
                if (!PendingItemStillObserved())
                {
                    Logging.Write(
                        "[DeleteItems] Confirmed deletion of item {0} ({1}).",
                        _pendingGuid, _pendingEntry);
                    ClearPendingDelete();
                    return;
                }

                // The exact physical item is visible again. The delete was not
                // acknowledged; relinquish the completed cursor transaction and
                // let a later tick reacquire the current bag slot safely.
                Logging.Write(
                    "[DeleteItems] Item {0} ({1}) returned to inventory; retrying from a fresh slot observation.",
                    _pendingGuid, _pendingEntry);
                ClearPendingDelete();
                return;
            }

            if (cursorState != 1)
            {
                TreeRoot.StatusText = "DeleteItems paused: cursor ownership changed";
                return;
            }

            if (!_deleteRequested)
            {
                TryIssueDeleteRequest();
                return;
            }

            int confirmation = TryConfirmPendingDelete();
            if (confirmation > 0)
                TreeRoot.StatusText = "Waiting for owned delete acknowledgement for item " + _pendingEntry;
            else
                TreeRoot.StatusText = "Waiting for exact delete confirmation popup for item " + _pendingEntry;
        }

        private bool PendingItemStillObserved()
        {
            if (_pendingGuid == 0)
                return false;

            if (StyxWoW.Me != null && StyxWoW.Me.BagItems != null &&
                StyxWoW.Me.BagItems.Any(item => item != null && item.Guid == _pendingGuid))
                return true;

            WoWItem observed = ObjectManager.GetObjectByGuid<WoWItem>(_pendingGuid);
            return observed != null && observed.IsValid;
        }

        private void TryIssueDeleteRequest()
        {
            try
            {
                bool submitted = Lua.GetReturnVal<bool>(
                    BuildOwnedDeleteRequestLua(_pendingEntry), 0U);
                if (submitted)
                {
                    _deleteRequested = true;
                    return;
                }

                Logging.Write(
                    "[DeleteItems] Delete request for item {0} ({1}) was refused because cursor/popup ownership changed.",
                    _pendingGuid, _pendingEntry);
            }
            catch (Exception error)
            {
                Logging.Write(
                    "[DeleteItems] Delete request for item {0} ({1}) failed safely: {2}",
                    _pendingGuid, _pendingEntry, error.Message);
            }
        }

        private int TryConfirmPendingDelete()
        {
            try
            {
                return Lua.GetReturnVal<int>(
                    BuildOwnedDeleteConfirmationLua(_pendingEntry), 0U);
            }
            catch (Exception error)
            {
                Logging.Write(
                    "[DeleteItems] Delete confirmation for item {0} ({1}) failed safely: {2}",
                    _pendingGuid, _pendingEntry, error.Message);
                return 0;
            }
        }

        private static int ReadOwnedCursorState(uint expectedEntry)
        {
            if (expectedEntry == 0)
                return 2;

            string lua = string.Format(
                CultureInfo.InvariantCulture,
                "local cursorType,cursorItemId=GetCursorInfo(); " +
                "if not cursorType then return 0 end; " +
                "if cursorType=='item' and CursorHasItem() and tonumber(cursorItemId)=={0} then return 1 end; " +
                "return 2",
                expectedEntry);
            try
            {
                return Lua.GetReturnVal<int>(lua, 0U);
            }
            catch
            {
                return 2;
            }
        }

        private static string BuildOwnedDeleteRequestLua(uint expectedEntry)
        {
            if (expectedEntry == 0)
                throw new ArgumentOutOfRangeException("expectedEntry");

            return string.Format(
                CultureInfo.InvariantCulture,
                "local cursorType,cursorItemId=GetCursorInfo(); " +
                "if cursorType~='item' or not CursorHasItem() or tonumber(cursorItemId)~={0} then return false end; " +
                "if StaticPopup_FindVisible('DELETE_ITEM') or StaticPopup_FindVisible('DELETE_GOOD_ITEM') then return false end; " +
                "DeleteCursorItem(); return true",
                expectedEntry);
        }

        private static string BuildOwnedDeleteConfirmationLua(uint expectedEntry)
        {
            if (expectedEntry == 0)
                throw new ArgumentOutOfRangeException("expectedEntry");

            return string.Format(
                CultureInfo.InvariantCulture,
                "local cursorType,cursorItemId=GetCursorInfo(); " +
                "if cursorType~='item' or not CursorHasItem() or tonumber(cursorItemId)~={0} then return 0 end; " +
                "local good=StaticPopup_FindVisible('DELETE_GOOD_ITEM'); " +
                "if good and good.which=='DELETE_GOOD_ITEM' then " +
                " if not good.editBox or not good.button1 then return -1 end; " +
                " good.editBox:SetText(DELETE_ITEM_CONFIRM_STRING); " +
                " if good.button1:IsEnabled()==1 then good.button1:Click(); return 2 end; return 1 end; " +
                "local normal=StaticPopup_FindVisible('DELETE_ITEM'); " +
                "if normal and normal.which=='DELETE_ITEM' then " +
                " if not normal.button1 then return -1 end; normal.button1:Click(); return 2 end; " +
                "return 0",
                expectedEntry);
        }

        private void ClearPendingDelete()
        {
            _pendingGuid = 0;
            _pendingEntry = 0;
            _pendingSince = DateTime.MinValue;
            _deleteRequested = false;
        }

        private void FailClosed(string reason)
        {
            LogMessage(
                "error",
                "DeleteItems stopped without claiming deletion success: " + reason
                + ". Cursor ownership was not cleared or stolen.");
            TreeRoot.StatusText = "DeleteItems stopped: " + reason;
            _isBehaviorDone = true;
            TreeRoot.Stop();
        }
    }
}
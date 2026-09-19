using System;
using System.Collections.Generic;
using System.Linq;
using Styx.Logic;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Inventory.Frames.Gossip;
using Styx.Logic.Pathing;
using Styx.Logic.Profiles;
using Styx.Logic.Questing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Action = TreeSharp.Action;

namespace Styx.Bot.Quest_Behaviors.GossipEvent
{
    /// <summary>
    /// Source-bound single-option gossip quest action with authoritative progress acknowledgement.
    /// GossipOptionIndex is zero-based; GossipFrame converts it to WoW's one-based Lua index.
    /// </summary>
    public class GossipEvent : CustomForcedBehavior
    {
        public enum SuccessEvidenceType
        {
            ObjectiveProgress,
            QuestComplete,
        }

        public GossipEvent(Dictionary<string, string> args)
            : base(args)
        {
            try
            {
                CollectionDistance = GetAttributeAsNullable<double>(
                    "CollectionDistance", false, ConstrainAs.Range, null) ?? 100.0;
                Location = GetAttributeAsNullable<WoWPoint>(
                    "", true, ConstrainAs.WoWPointNonEmpty, null) ?? WoWPoint.Zero;
                MobIds = GetNumberedAttributesAsArray<int>(
                    "MobId", 1, ConstrainAs.MobId, new[] { "NpcId" });
                GossipOptionIndex = GetAttributeAsNullable<int>(
                    "GossipOptionIndex", true, new ConstrainTo.Domain<int>(0, 64), null) ?? -1;
                Range = GetAttributeAsNullable<double>(
                    "Range", false, ConstrainAs.Range, null) ?? 4.0;
                RequireLos = GetAttributeAsNullable<bool>(
                    "RequireLos", false, null, null) ?? false;
                MaxAttempts = GetAttributeAsNullable<int>(
                    "MaxAttempts", false, ConstrainAs.RepeatCount, null) ?? 1;
                AcknowledgementTimeout = GetAttributeAsNullable<int>(
                    "AcknowledgementTimeout", false, ConstrainAs.Milliseconds, null) ?? 5000;
                GossipOpenTimeout = GetAttributeAsNullable<int>(
                    "GossipOpenTimeout", false, ConstrainAs.Milliseconds, null) ?? 3000;
                TargetWaitTimeout = GetAttributeAsNullable<int>(
                    "TargetWaitTimeout", false, ConstrainAs.Milliseconds, null) ?? 30000;
                NavigationTimeout = GetAttributeAsNullable<int>(
                    "NavigationTimeout", false, ConstrainAs.Milliseconds, null) ?? 120000;
                WaitForNpcs = GetAttributeAsNullable<bool>(
                    "WaitForNpcs", false, null, null) ?? true;
                QuestId = GetAttributeAsNullable<int>(
                    "QuestId", true, ConstrainAs.QuestId(this), null) ?? 0;
                ObjectiveIndex = GetAttributeAsNullable<int>(
                    "ObjectiveIndex", false, null, null) ?? -1;
                SuccessEvidence = GetAttributeAsNullable<SuccessEvidenceType>(
                    "SuccessEvidence", true, null, null) ?? SuccessEvidenceType.ObjectiveProgress;
                QuestRequirementComplete = GetAttributeAsNullable<QuestCompleteRequirement>(
                    "QuestCompleteRequirement", false, null, null) ?? QuestCompleteRequirement.NotComplete;
                QuestRequirementInLog = GetAttributeAsNullable<QuestInLogRequirement>(
                    "QuestInLogRequirement", false, null, null) ?? QuestInLogRequirement.InLog;

                if (QuestId <= 0 || MobIds == null || MobIds.Length != 1 ||
                    Location == WoWPoint.Zero ||
                    MaxAttempts <= 0 || AcknowledgementTimeout <= 0 ||
                    GossipOpenTimeout <= 0 || TargetWaitTimeout <= 0 ||
                    NavigationTimeout <= 0)
                    IsAttributeProblem = true;
                if (SuccessEvidence == SuccessEvidenceType.ObjectiveProgress &&
                    (ObjectiveIndex < 0 || ObjectiveIndex > 3))
                    IsAttributeProblem = true;
            }
            catch (Exception except)
            {
                LogMessage("error",
                    "BEHAVIOR MAINTENANCE PROBLEM: " + except.Message +
                    "\nFROM HERE:\n" + except.StackTrace + "\n");
                IsAttributeProblem = true;
            }
        }

        public double CollectionDistance { get; private set; }
        public WoWPoint Location { get; private set; }
        public int[] MobIds { get; private set; }
        public int GossipOptionIndex { get; private set; }
        public double Range { get; private set; }
        public bool RequireLos { get; private set; }
        public int MaxAttempts { get; private set; }
        public int AcknowledgementTimeout { get; private set; }
        public int GossipOpenTimeout { get; private set; }
        public int TargetWaitTimeout { get; private set; }
        public int NavigationTimeout { get; private set; }
        public bool WaitForNpcs { get; private set; }
        public int QuestId { get; private set; }
        public int ObjectiveIndex { get; private set; }
        public SuccessEvidenceType SuccessEvidence { get; private set; }
        public QuestCompleteRequirement QuestRequirementComplete { get; private set; }
        public QuestInLogRequirement QuestRequirementInLog { get; private set; }
        public int? InitialObjectiveCount { get; private set; }
        public int Counter { get; private set; }

        private bool _isBehaviorDone;
        private bool _isDisposed;
        private Composite _root;
        private long _lastSubmissionUtc = -1;
        private long _gossipOpenStartedUtc = -1;
        private long _targetWaitStartedUtc = -1;
        private long _navigationStartedUtc = -1;
        private ulong _interactionGuid;

        private LocalPlayer Me { get { return ObjectManager.Me; } }

        public override string SubversionId
        {
            get { return "$Id: GossipEvent.cs source-bound 2026-09-19 $"; }
        }

        public override string SubversionRevision
        {
            get { return "$Revision: source-bound-v1 $"; }
        }

        internal static bool IsAuthoritativeAcknowledged(
            SuccessEvidenceType evidence,
            int baseline,
            int? currentCount,
            bool questComplete)
        {
            if (evidence == SuccessEvidenceType.ObjectiveProgress)
                return currentCount.HasValue && currentCount.Value > baseline;
            if (evidence == SuccessEvidenceType.QuestComplete)
                return questComplete;
            return false;
        }

        internal static bool IsAcknowledgementPending(
            long nowUtcMilliseconds,
            long submittedUtcMilliseconds,
            int timeoutMilliseconds)
        {
            if (submittedUtcMilliseconds < 0 || timeoutMilliseconds <= 0 ||
                nowUtcMilliseconds < submittedUtcMilliseconds)
                return false;
            return nowUtcMilliseconds - submittedUtcMilliseconds < timeoutMilliseconds;
        }

        internal static bool CanSelectGossipOption(int optionIndex, int observedOptionCount)
        {
            return optionIndex >= 0 && observedOptionCount > 0 &&
                optionIndex < observedOptionCount;
        }

        internal static bool IsWithinSourceAnchor(
            WoWPoint candidate,
            WoWPoint anchor,
            double collectionDistance)
        {
            if (collectionDistance <= 0 || double.IsNaN(collectionDistance)
                || double.IsInfinity(collectionDistance)
                || float.IsNaN(candidate.X) || float.IsNaN(candidate.Y) || float.IsNaN(candidate.Z)
                || float.IsNaN(anchor.X) || float.IsNaN(anchor.Y) || float.IsNaN(anchor.Z)
                || float.IsInfinity(candidate.X) || float.IsInfinity(candidate.Y) || float.IsInfinity(candidate.Z)
                || float.IsInfinity(anchor.X) || float.IsInfinity(anchor.Y) || float.IsInfinity(anchor.Z))
                return false;

            return candidate.DistanceSqr(anchor) <= collectionDistance * collectionDistance;
        }

        private static long UtcNowMilliseconds()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private static bool IsCurrentGossipNpc(ulong guid)
        {
            if (guid == 0)
                return false;

            string expected = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "0x{0:X16}",
                guid);
            try
            {
                return Lua.GetReturnVal<bool>(
                    "return UnitGUID('npc') == '" + expected + "'",
                    0U);
            }
            catch
            {
                return false;
            }
        }

        private int? ReadObjectiveCount()
        {
            if (QuestId <= 0 || ObjectiveIndex < 0 || ObjectiveIndex > 3)
                return null;
            LocalPlayer player = Me;
            PlayerQuest quest = player?.QuestLog?.GetQuestById((uint)QuestId);
            if (quest == null || !quest.GetData(out QuestDescriptorData data) ||
                data.ObjectivesDone == null || ObjectiveIndex >= data.ObjectivesDone.Length)
                return null;
            return data.ObjectivesDone[ObjectiveIndex];
        }

        private bool HasAuthoritativeSuccess()
        {
            bool questComplete = QuestId > 0 &&
                UtilIsProgressRequirementsMet(
                    QuestId,
                    QuestInLogRequirement.InLog,
                    QuestCompleteRequirement.Complete);

            if (SuccessEvidence == SuccessEvidenceType.QuestComplete)
                return IsAuthoritativeAcknowledged(
                    SuccessEvidence, 0, null, questComplete);

            if (SuccessEvidence == SuccessEvidenceType.ObjectiveProgress && QuestId > 0 &&
                QuestObjectiveCompletion.IsNormalObjectiveComplete(
                    Me?.QuestLog?.GetQuestById((uint)QuestId), ObjectiveIndex))
                return true;

            return InitialObjectiveCount.HasValue &&
                IsAuthoritativeAcknowledged(
                    SuccessEvidence,
                    InitialObjectiveCount.Value,
                    ReadObjectiveCount(),
                    questComplete);
        }

        private WoWUnit FindTarget()
        {
            LocalPlayer player = Me;
            if (player == null || MobIds == null)
                return null;

            return ObjectManager.GetObjectsOfType<WoWUnit>()
                .Where(unit => unit != null && unit.IsValid && unit.IsAlive &&
                    unit.CanSelect && MobIds.Contains((int)unit.Entry) &&
                    unit.Guid != 0 &&
                    IsWithinSourceAnchor(unit.Location, Location, CollectionDistance))
                .OrderBy(unit => unit.DistanceSqr)
                .FirstOrDefault();
        }

        private RunStatus DeferAuthoritativeAttempt(string reason)
        {
            LogMessage("warning",
                "GossipEvent is deferring without authoritative {0} acknowledgement for quest {1}: {2}",
                SuccessEvidence, QuestId, reason);
            try
            {
                if (GossipFrame.Instance.IsVisible)
                    GossipFrame.Instance.Close();
            }
            catch { }
            _isBehaviorDone = true;
            return RunStatus.Success;
        }

        private void ResetForRetry()
        {
            _lastSubmissionUtc = -1;
            _gossipOpenStartedUtc = -1;
            _navigationStartedUtc = -1;
            _interactionGuid = 0;
            try
            {
                if (GossipFrame.Instance.IsVisible)
                    GossipFrame.Instance.Close();
            }
            catch { }
        }

        private RunStatus TickBehavior()
        {
            if (_isBehaviorDone || IsDone)
                return RunStatus.Success;

            LocalPlayer player = Me;
            if (player == null || !player.IsValid || !StyxWoW.IsInGame)
            {
                TreeRoot.StatusText = "Waiting for valid player observation";
                return RunStatus.Running;
            }

            if (HasAuthoritativeSuccess())
            {
                _isBehaviorDone = true;
                return RunStatus.Success;
            }

            if (SuccessEvidence == SuccessEvidenceType.ObjectiveProgress &&
                !InitialObjectiveCount.HasValue)
            {
                return DeferAuthoritativeAttempt(
                    "the initial objective count is unavailable");
            }

            long now = UtcNowMilliseconds();

            if (_lastSubmissionUtc >= 0)
            {
                if (IsAcknowledgementPending(
                        now, _lastSubmissionUtc, AcknowledgementTimeout))
                {
                    TreeRoot.StatusText =
                        "Waiting for authoritative gossip-event acknowledgement";
                    return RunStatus.Running;
                }

                if (Counter >= MaxAttempts)
                    return DeferAuthoritativeAttempt(
                        "bounded gossip submissions were exhausted");

                ResetForRetry();
            }

            if (_gossipOpenStartedUtc >= 0)
            {
                if (GossipFrame.Instance.IsVisible)
                {
                    if (!IsCurrentGossipNpc(_interactionGuid))
                    {
                        if (IsAcknowledgementPending(
                                now, _gossipOpenStartedUtc, GossipOpenTimeout))
                        {
                            TreeRoot.StatusText =
                                "Waiting for exact source-bound gossip NPC frame";
                            return RunStatus.Running;
                        }

                        if (Counter >= MaxAttempts)
                            return DeferAuthoritativeAttempt(
                                "the open gossip frame did not belong to the exact interacted NPC");

                        ResetForRetry();
                        return RunStatus.Running;
                    }

                    var entries = GossipFrame.Instance.GossipOptionEntries;
                    if (entries != null)
                    {
                        if (!CanSelectGossipOption(
                                GossipOptionIndex, entries.Count))
                        {
                            return DeferAuthoritativeAttempt(
                                "the observed gossip menu does not contain the exact source-bound option");
                        }

                        if (IsDone)
                            return RunStatus.Success;

                        GossipFrame.Instance.SelectGossipOption(GossipOptionIndex);
                        _lastSubmissionUtc = UtcNowMilliseconds();
                        _gossipOpenStartedUtc = -1;
                        _interactionGuid = 0;
                        TreeRoot.StatusText =
                            "Submitted source-bound gossip option; waiting for quest progress";
                        return RunStatus.Running;
                    }
                }

                if (IsAcknowledgementPending(
                        now, _gossipOpenStartedUtc, GossipOpenTimeout))
                {
                    TreeRoot.StatusText = "Waiting for gossip menu";
                    return RunStatus.Running;
                }

                if (Counter >= MaxAttempts)
                    return DeferAuthoritativeAttempt(
                        "the gossip menu did not become usable within bounded attempts");

                ResetForRetry();
            }

            if (Counter >= MaxAttempts)
                return DeferAuthoritativeAttempt(
                    "bounded gossip attempts were exhausted");

            WoWUnit target = FindTarget();
            if (target == null)
            {
                if (Location.DistanceSqr(Me.Location) > 4.0)
                {
                    _targetWaitStartedUtc = -1;
                    if (_navigationStartedUtc < 0)
                        _navigationStartedUtc = now;
                    if (!IsAcknowledgementPending(
                            now, _navigationStartedUtc, NavigationTimeout))
                        return DeferAuthoritativeAttempt(
                            "the source-bound gossip-event location was not reached within the bounded navigation window");

                    TreeRoot.StatusText = "Moving to source-bound gossip-event location";
                    MoveResult movement = Navigator.MoveTo(Location);
                    if (movement == MoveResult.Failed ||
                        movement == MoveResult.PathGenerationFailed)
                        return DeferAuthoritativeAttempt(
                            "the source-bound gossip-event location is not currently navigable");
                    return RunStatus.Running;
                }

                _navigationStartedUtc = -1;

                if (!WaitForNpcs)
                    return DeferAuthoritativeAttempt(
                        "the source-bound gossip NPC is not present");

                if (_targetWaitStartedUtc < 0)
                    _targetWaitStartedUtc = now;
                if (IsAcknowledgementPending(
                        now, _targetWaitStartedUtc, TargetWaitTimeout))
                {
                    TreeRoot.StatusText = "Waiting for source-bound gossip NPC";
                    return RunStatus.Running;
                }

                return DeferAuthoritativeAttempt(
                    "the source-bound gossip NPC did not appear within the bounded wait");
            }

            _targetWaitStartedUtc = -1;

            if (target.DistanceSqr > Range * Range ||
                RequireLos && !target.InLineOfSight)
            {
                if (_navigationStartedUtc < 0)
                    _navigationStartedUtc = now;
                if (!IsAcknowledgementPending(
                        now, _navigationStartedUtc, NavigationTimeout))
                    return DeferAuthoritativeAttempt(
                        "the exact source-bound gossip NPC was not reached within the bounded navigation window");

                TreeRoot.StatusText = "Moving to source-bound gossip NPC";
                MoveResult movement = Navigator.MoveTo(target.Location);
                if (movement == MoveResult.Failed ||
                    movement == MoveResult.PathGenerationFailed)
                    return DeferAuthoritativeAttempt(
                        "the exact source-bound gossip NPC is not currently navigable");
                return RunStatus.Running;
            }

            _navigationStartedUtc = -1;

            if (Me.IsMoving)
            {
                WoWMovement.MoveStop();
                return RunStatus.Running;
            }

            // Close a stale frame before establishing a new interaction lifetime.
            if (GossipFrame.Instance.IsVisible)
                GossipFrame.Instance.Close();

            ulong guid = target.Guid;
            if (guid == 0 || !target.IsValid || !target.IsAlive ||
                target.DistanceSqr > Range * Range ||
                RequireLos && !target.InLineOfSight)
                return RunStatus.Running;

            if (IsDone)
                return RunStatus.Success;

            target.Interact();
            Counter++;
            _interactionGuid = guid;
            _gossipOpenStartedUtc = UtcNowMilliseconds();
            TreeRoot.StatusText = "Waiting for source-bound gossip menu";
            return RunStatus.Running;
        }

        protected override Composite CreateBehavior()
        {
            return _root ?? (_root = new Action(ret => TickBehavior()));
        }

        public override bool IsDone
        {
            get
            {
                return _isBehaviorDone
                    || HasAuthoritativeSuccess()
                    || !UtilIsProgressRequirementsMet(
                        QuestId,
                        QuestRequirementInLog,
                        QuestRequirementComplete);
            }
        }

        public override void OnStart()
        {
            OnStart_HandleAttributeProblem();

            Counter = 0;
            _lastSubmissionUtc = -1;
            _gossipOpenStartedUtc = -1;
            _targetWaitStartedUtc = -1;
            _navigationStartedUtc = -1;
            _interactionGuid = 0;

            if (!IsAttributeProblem &&
                SuccessEvidence == SuccessEvidenceType.ObjectiveProgress)
                InitialObjectiveCount = ReadObjectiveCount();

            if (!IsDone)
            {
                PlayerQuest quest = Me?.QuestLog?.GetQuestById((uint)QuestId);
                TreeRoot.GoalText = "GossipEvent: " +
                    (quest != null ? """ + quest.Name + """ : "In Progress");
            }
        }

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                try
                {
                    TreeRoot.GoalText = string.Empty;
                    TreeRoot.StatusText = string.Empty;
                }
                finally
                {
                    _isDisposed = true;
                    base.Dispose();
                }
            }
            GC.SuppressFinalize(this);
        }
    }
}
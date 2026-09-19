// Behavior originally contributed by Nesox.
//
// DOCUMENTATION:
//     http://www.thebuddyforum.com/mediawiki/index.php?title=Honorbuddy_Custom_Behavior:_UseItemOn
//
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;

using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.Logic.Profiles;
using Styx.Logic.Questing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

using TreeSharp;
using Action = TreeSharp.Action;


namespace Styx.Bot.Quest_Behaviors.UseItemOn
{
    /// <summary>
    /// Allows you to use items on nearby gameobjects/npc's
    /// ##Syntax##
    /// QuestId: The id of the quest.
    /// MobId1, MobId2, ...MobIdN: The ids of the mobs.
    /// ItemId: The id of the item to use.
    /// [Optional]NumOfTimes: Number of times to use said item.
    /// [Optional]WaitTime: Time to wait after using an item. DefaultValue: 1500 ms
    /// [Optional]CollectionDistance: The distance it will use to collect objects. DefaultValue:100 yards
    /// [Optional]HasAura: If a unit has a certian aura to check before using item. (By: j0achim)
    /// [Optional]Range: The range to object that it will use the item
    /// [Optional]MobState: The state of the npc -> Dead, Alive, BelowHp. None is default
    /// [Optional]MobHpPercentLeft: Will only be used when NpcState is BelowHp
    /// ObjectType: the type of object to interact with, expected value: Npc/Gameobject
    /// [Optional]X,Y,Z: The general location where theese objects can be found
    /// </summary>
    public class UseItemOn : CustomForcedBehavior
    {
        public enum ObjectType
        {
            Npc,
            GameObject,
        }

        public enum NpcStateType
        {
            Alive,
            BelowHp,
            Dead,
            DontCare,
        }

        public enum NavigationType
        {
            Mesh,
            CTM,
            None,
        }

        public enum SuccessEvidenceType
        {
            InvocationCount,
            ObjectiveProgress,
            QuestComplete,
        }

        public UseItemOn(Dictionary<string, string> args)
            : base(args)
        {
            try
            {
                int tmpMobHasAuraId;
                int tmpMobHasAuraMissingId;

                // QuestRequirement* attributes are explained here...
                //    http://www.thebuddyforum.com/mediawiki/index.php?title=Honorbuddy_Programming_Cookbook:_QuestId_for_Custom_Behaviors
                // ...and also used for IsDone processing.
                CollectionDistance = GetAttributeAsNullable<double>("CollectionDistance", false, ConstrainAs.Range, null) ?? 100.0;
                tmpMobHasAuraId = GetAttributeAsNullable<int>("HasAuraId", false, ConstrainAs.AuraId, new[] { "HasAura" }) ?? 0;
                tmpMobHasAuraMissingId = GetAttributeAsNullable<int>("IsMissingAuraId", false, ConstrainAs.AuraId, null) ?? 0;
                MobHpPercentLeft = GetAttributeAsNullable<double>("MobHpPercentLeft", false, ConstrainAs.Percent, new[] { "HpLeftAmount" }) ?? 100.0;
                ItemId = GetAttributeAsNullable<int>("ItemId", true, ConstrainAs.ItemId, null) ?? 0;
                Location = GetAttributeAsNullable<WoWPoint>("", false, ConstrainAs.WoWPointNonEmpty, null) ?? Me.Location;
                MobIds = GetNumberedAttributesAsArray<int>("MobId", 1, ConstrainAs.MobId, new[] { "NpcId" });
                MobType = GetAttributeAsNullable<ObjectType>("MobType", false, null, new[] { "ObjectType" }) ?? ObjectType.Npc;
                NumOfTimes = GetAttributeAsNullable<int>("NumOfTimes", false, ConstrainAs.RepeatCount, null) ?? 1;
                SuccessEvidence = GetAttributeAsNullable<SuccessEvidenceType>("SuccessEvidence", false, null, null) ?? SuccessEvidenceType.InvocationCount;
                ObjectiveIndex = GetAttributeAsNullable<int>("ObjectiveIndex", false, null, null) ?? -1;
                MaxAttempts = GetAttributeAsNullable<int>("MaxAttempts", false, ConstrainAs.RepeatCount, null) ?? NumOfTimes;
                AcknowledgementTimeout = GetAttributeAsNullable<int>("AcknowledgementTimeout", false, ConstrainAs.Milliseconds, null) ?? 5000;
                SubmissionRefusalTimeout = GetAttributeAsNullable<int>("SubmissionRefusalTimeout", false, ConstrainAs.Milliseconds, null) ?? 5000;
                if (SubmissionRefusalTimeout <= 0)
                    IsAttributeProblem = true;
                NpcState = GetAttributeAsNullable<NpcStateType>("MobState", false, null, new[] { "NpcState" }) ?? NpcStateType.DontCare;
                NavigationState = GetAttributeAsNullable<NavigationType>("Nav", false, null, new[] { "Navigation" }) ?? NavigationType.Mesh;
                WaitForNpcs = GetAttributeAsNullable<bool>("WaitForNpcs", false, null, null) ?? false;
                Range = GetAttributeAsNullable<double>("Range", false, ConstrainAs.Range, null) ?? 4;
                RequireLos = GetAttributeAsNullable<bool>("RequireLos", false, null, null) ?? false;
                QuestId = GetAttributeAsNullable<int>("QuestId", false, ConstrainAs.QuestId(this), null) ?? 0;
                if (SuccessEvidence == SuccessEvidenceType.ObjectiveProgress &&
                    (QuestId <= 0 || ObjectiveIndex < 0 || ObjectiveIndex > 3))
                    IsAttributeProblem = true;
                QuestRequirementComplete = GetAttributeAsNullable<QuestCompleteRequirement>("QuestCompleteRequirement", false, null, null) ?? QuestCompleteRequirement.NotComplete;
                QuestRequirementInLog = GetAttributeAsNullable<QuestInLogRequirement>("QuestInLogRequirement", false, null, null) ?? QuestInLogRequirement.InLog;
                WaitTime = GetAttributeAsNullable<int>("WaitTime", false, ConstrainAs.Milliseconds, null) ?? 1500;
                IgnoreMobsInBlackspots = GetAttributeAsNullable<bool>("IgnoreMobsInBlackspots", false, null, null) ?? true;
                IgnoreCombat = GetAttributeAsNullable<bool>("IgnoreCombat", false, null, null) ?? false;

                MobAuraName = (tmpMobHasAuraId != 0) ? AuraNameFromId("HasAuraId", tmpMobHasAuraId) : null;
                MobAuraMissingName = (tmpMobHasAuraMissingId != 0) ? AuraNameFromId("HasAuraId", tmpMobHasAuraMissingId) : null;
            }

            catch (Exception except)
            {
                // Maintenance problems occur for a number of reasons.  The primary two are...
                // * Changes were made to the behavior, and boundary conditions weren't properly tested.
                // * The Honorbuddy core was changed, and the behavior wasn't adjusted for the new changes.
                // In any case, we pinpoint the source of the problem area here, and hopefully it
                // can be quickly resolved.
                LogMessage("error", "BEHAVIOR MAINTENANCE PROBLEM: " + except.Message
                                    + "\nFROM HERE:\n"
                                    + except.StackTrace + "\n");
                IsAttributeProblem = true;
            }
        }


        // Attributes provided by caller
        public double CollectionDistance { get; private set; }
        public int ItemId { get; private set; }
        public WoWPoint Location { get; private set; }
        public string MobAuraName { get; private set; }
        public string MobAuraMissingName { get; private set; }
        public double MobHpPercentLeft { get; private set; }
        public int[] MobIds { get; private set; }
        public ObjectType MobType { get; private set; }
        public NpcStateType NpcState { get; private set; }
        public NavigationType NavigationState { get; private set; }
        public int NumOfTimes { get; private set; }
        public SuccessEvidenceType SuccessEvidence { get; private set; }
        public int ObjectiveIndex { get; private set; }
        public int MaxAttempts { get; private set; }
        public int AcknowledgementTimeout { get; private set; }
        public int SubmissionRefusalTimeout { get; private set; }
        public int? InitialObjectiveCount { get; private set; }
        public bool AuthoritativeAttemptsExhausted { get; private set; }
        public int QuestId { get; private set; }
        public QuestCompleteRequirement QuestRequirementComplete { get; private set; }
        public QuestInLogRequirement QuestRequirementInLog { get; private set; }
        public double Range { get; private set; }
        public bool RequireLos { get; private set; }
        public bool WaitForNpcs { get; private set; }
        public int WaitTime { get; private set; }
        public bool IgnoreMobsInBlackspots { get; private set; }
        public bool IgnoreCombat { get; private set; }


        // Private variables for internal state
        private bool _isBehaviorDone;
        private bool _isDisposed;
        private readonly List<ulong> _npcAuraWait = new List<ulong>();
        private readonly List<ulong> _npcBlacklist = new List<ulong>();
        private Composite _root;
        private long _lastSubmissionUtc = -1;
        private long _submissionRefusalUtc = -1;

        // Private properties
        private int Counter { get; set; }
        private LocalPlayer Me { get { return (ObjectManager.Me); } }

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
            // InvocationCount is legacy local bookkeeping, never server acknowledgement.
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

        private static long UtcNowMilliseconds()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private RunStatus DeferAuthoritativeAttempt(string reason)
        {
            LogMessage("warning",
                "UseItemOn is deferring without authoritative {0} acknowledgement for quest {1}: {2}",
                SuccessEvidence, QuestId, reason);
            _isBehaviorDone = true;
            return RunStatus.Success;
        }

        private RunStatus DeferSubmissionRefusal(string reason)
        {
            LogMessage("warning",
                "UseItemOn is deferring because the container item submission could not be safely validated for quest {0}: {1}",
                QuestId, reason);
            _isBehaviorDone = true;
            return RunStatus.Success;
        }

        private int? ReadObjectiveCount()
        {
            if (QuestId <= 0 || ObjectiveIndex < 0 || ObjectiveIndex > 3)
                return null;
            var player = Me;
            PlayerQuest quest = player?.QuestLog?.GetQuestById((uint)QuestId);
            if (quest == null || !quest.GetData(out QuestDescriptorData data) ||
                data.ObjectivesDone == null || ObjectiveIndex >= data.ObjectivesDone.Length)
                return null;
            return data.ObjectivesDone[ObjectiveIndex];
        }

        private bool HasAuthoritativeSuccess()
        {
            if (SuccessEvidence == SuccessEvidenceType.InvocationCount)
                return false;

            bool questComplete = QuestId > 0 &&
                UtilIsProgressRequirementsMet(
                    QuestId,
                    QuestInLogRequirement.InLog,
                    QuestCompleteRequirement.Complete);

            if (SuccessEvidence == SuccessEvidenceType.QuestComplete)
                return IsAuthoritativeAcknowledged(SuccessEvidence, 0, null, questComplete);

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

        // DON'T EDIT THESE--they are auto-populated by Subversion
        public override string SubversionId { get { return ("$Id: UseItemOn.cs 229 2012-04-25 01:57:29Z natfoth $"); } }
        public override string SubversionRevision { get { return ("$Revision: 229 $"); } }


        ~UseItemOn()
        {
            Dispose(false);
        }


        public void Dispose(bool isExplicitlyInitiatedDispose)
        {
            if (!_isDisposed)
            {
                // NOTE: we should call any Dispose() method for any managed or unmanaged
                // resource, if that resource provides a Dispose() method.

                // Clean up managed resources, if explicit disposal...
                if (isExplicitlyInitiatedDispose)
                {
                    // empty, for now
                }

                // Clean up unmanaged resources (if any) here...
                TreeRoot.GoalText = string.Empty;
                TreeRoot.StatusText = string.Empty;

                // Call parent Dispose() (if it exists) here ...
                base.Dispose();
            }

            _isDisposed = true;
        }


        // May return 'null' if auraId is not valid.
        private string AuraNameFromId(string attributeName,
                                           int auraId)
        {
            string tmpString = null;

            try
            {
                tmpString = WoWSpell.FromId(auraId).Name;
            }
            catch
            {
                LogMessage("fatal", "Could not find {0}({0}).", attributeName, auraId);
                IsAttributeProblem = true;
            }

            return (tmpString);
        }


        /// <summary> Current object we should interact with.</summary>
        /// <value> The object.</value>
        private WoWObject CurrentObject
        {
            get
            {
                var me = Me;
                if (me == null || !me.IsValid || !me.IsAlive)
                    return null;
                WoWObject @object = null;

                switch (MobType)
                {
                    case ObjectType.GameObject:
                        @object = (ObjectManager.GetObjectsOfType<WoWGameObject>() ?? Enumerable.Empty<WoWGameObject>())
                                                .Where(obj => obj != null && obj.IsValid && obj.Guid != 0)
                                                .OrderBy(ret => ret.Distance)
                                                .FirstOrDefault(obj => !_npcBlacklist.Contains(obj.Guid)
                                                                        && obj.Distance < CollectionDistance
                                                                        && MobIds.Contains((int)obj.Entry));
                        break;

                    case ObjectType.Npc:
                        var baseTargets = (ObjectManager.GetObjectsOfType<WoWUnit>() ?? Enumerable.Empty<WoWUnit>())
                                                               .Where(target => target != null && target.IsValid && target.Guid != 0)
                                                               .OrderBy(target => target.Distance)
                                                               .Where(target => !_npcBlacklist.Contains(target.Guid) && !BehaviorBlacklist.Contains(target.Guid)
                                                                                && (target.Distance < CollectionDistance)
                                                                                && MobIds.Contains((int)target.Entry) && (!IgnoreMobsInBlackspots || (IgnoreMobsInBlackspots && !Targeting.IsTooNearBlackspot(ProfileManager.CurrentProfile.Blackspots, target.Location))));

                        var auraQualifiedTargets = baseTargets
                                                            .Where(target => (MobAuraName == null || target.HasAura(MobAuraName))
                                                                              && (MobAuraMissingName == null || !target.HasAura(MobAuraMissingName)));

                        var npcStateQualifiedTargets = auraQualifiedTargets
                                                            .Where(target => ((NpcState == NpcStateType.DontCare)
                                                                              || ((NpcState == NpcStateType.Dead) && target.Dead)
                                                                              || ((NpcState == NpcStateType.Alive) && target.IsAlive)
                                                                              || ((NpcState == NpcStateType.BelowHp) && target.IsAlive && (target.HealthPercent < MobHpPercentLeft))));

                        @object = npcStateQualifiedTargets.FirstOrDefault();
                        break;
                }

                if (@object != null)
                { LogMessage("debug", @object.Name); }

                return @object;
            }
        }

        private bool BlacklistIfPlayerNearby(WoWObject target)
        {
            WoWUnit nearestCompetingPlayer = ObjectManager.GetObjectsOfType<WoWUnit>(true, false)
                                                    .OrderBy(player => player.Location.Distance(target.Location))
                                                    .FirstOrDefault(player => player.IsPlayer
                                                                                && player.IsAlive
                                                                                && !player.IsInOurParty());

            // If player is too close to the target, ignore target for a bit...
            if ((nearestCompetingPlayer != null)
                && (nearestCompetingPlayer.Location.Distance(target.Location) <= 25))
            {
                BehaviorBlacklist.Add(target.Guid, TimeSpan.FromSeconds(90));
                return (true);
            }

            return (false);
        }

        private bool CanNavigateFully(WoWObject target)
        {
            if (Navigator.CanNavigateFully(Me.Location, target.Location))
            {
                return (true);
            }

            return (false);
        }

        

        public WoWItem Item
        {
            get
            {
                var me = StyxWoW.Me;
                if (me == null || !me.IsValid || !me.IsAlive)
                    return null;
                return me.CarriedItems?.FirstOrDefault(item => item != null && item.IsValid && item.Entry == ItemId);
            }
        }

        // One attempted use owns one actor, inventory item and recipient. Setup
        // callbacks must not silently substitute another same-entry object/item.
        private RunStatus UseCapturedItem()
        {
            var player = Me;
            var recipient = CurrentObject;
            var item = Item;
            // This branch already admitted an attempt. On revocation, consume
            // this tick without falling into the old roaming/waiting actions.
            if (player == null || recipient == null || item == null)
                return RunStatus.Success;

            ulong playerGuid = player.Guid;
            ulong recipientGuid = recipient.Guid;
            ulong itemGuid = item.Guid;
            uint recipientEntry = recipient.Entry;
            bool targeted = false;

            bool OwnsActorIdentity() => !_isDisposed && !_isBehaviorDone && playerGuid != 0
                && ReferenceEquals(Me, player) && player.IsValid && player.IsAlive
                && player.Guid == playerGuid;

            // Quest acceptance/completion can change during setup or item use.
            // Reuse the explicit profile's requirements, not a guessed recipe,
            // and fence the observation with the same actor/lifetime checks.
            bool OwnsActor() => OwnsActorIdentity()
                && UtilIsProgressRequirementsMet(QuestId, QuestRequirementInLog, QuestRequirementComplete)
                && OwnsActorIdentity();

            bool Admitted(bool requireSelectedTarget)
            {
                if (!OwnsActor() || recipientGuid == 0 || itemGuid == 0
                    || !recipient.IsValid || recipient.Guid != recipientGuid || recipient.Entry != recipientEntry
                    || !item.IsValid || item.Guid != itemGuid || item.Entry != ItemId || item.Cooldown != 0
                    || player.CarriedItems == null || !player.CarriedItems.Any(candidate => ReferenceEquals(candidate, item))
                    || !(ObjectManager.GetObjectsOfType<WoWObject>()?.Any(candidate => ReferenceEquals(candidate, recipient)) ?? false)
                    || MobIds == null || !MobIds.Contains((int)recipientEntry) || _npcBlacklist.Contains(recipientGuid))
                    return false;

                double distance = recipient.DistanceSqr;
                if (double.IsNaN(distance) || double.IsInfinity(distance)
                    || !(distance <= Range * Range) || !(distance < CollectionDistance * CollectionDistance))
                    return false;
                if (RequireLos && !recipient.InLineOfSight)
                    return false;

                if (MobType == ObjectType.GameObject)
                {
                    if (!(recipient is WoWGameObject)) return false;
                }
                else if (MobType == ObjectType.Npc && recipient is WoWUnit unit)
                {
                    if (BehaviorBlacklist.Contains(recipientGuid)
                        || (MobAuraName != null && !unit.HasAura(MobAuraName))
                        || (MobAuraMissingName != null && unit.HasAura(MobAuraMissingName))
                        || !(NpcState == NpcStateType.DontCare
                            || NpcState == NpcStateType.Dead && unit.Dead
                            || NpcState == NpcStateType.Alive && unit.IsAlive
                            || NpcState == NpcStateType.BelowHp && unit.IsAlive && unit.HealthPercent < MobHpPercentLeft)
                        || IgnoreMobsInBlackspots && Targeting.IsTooNearBlackspot(ProfileManager.CurrentProfile.Blackspots, unit.Location)
                        || requireSelectedTarget && (!ReferenceEquals(player.CurrentTarget, unit)
                            || player.CurrentTarget.Guid != recipientGuid))
                        return false;
                }
                else return false;
                // Generic container use is merchant-sensitive. Defer the attempt,
                // without closing another owner's UI or recording false progress.
                return !Styx.Logic.Inventory.Frames.Merchant.MerchantFrame.Instance.IsVisible
                    && OwnsActor();
            }

            if (!Admitted(false)) return RunStatus.Success;
            if (player.IsMoving)
            {
                WoWMovement.MoveStop();
                if (!Admitted(false)) return RunStatus.Success;
                StyxWoW.SleepForLagDuration();
                if (!Admitted(false)) return RunStatus.Success;
            }
            TreeRoot.StatusText = "Using item on \"" + recipient.Name + "\"";
            if (!Admitted(false)) return RunStatus.Success;
            if (recipient is WoWUnit target && !ReferenceEquals(player.CurrentTarget, target))
            {
                target.Target();
                targeted = true;
                if (!Admitted(true)) return RunStatus.Success;
                StyxWoW.SleepForLagDuration();
            }
            if (!Admitted(true)) return RunStatus.Success;
            WoWMovement.Face(recipientGuid);
            if (!Admitted(true) || HasAuthoritativeSuccess()) return RunStatus.Success;
            if (!item.TryUseContainerItem())
            {
                long now = UtcNowMilliseconds();
                if (_submissionRefusalUtc < 0)
                    _submissionRefusalUtc = now;

                if (IsAcknowledgementPending(
                        now,
                        _submissionRefusalUtc,
                        SubmissionRefusalTimeout))
                {
                    TreeRoot.StatusText =
                        "Waiting for a stable container item slot before submission";
                    return RunStatus.Success;
                }

                return DeferSubmissionRefusal(
                    "the item GUID/slot identity did not stabilize within the bounded local submission window");
            }

            _submissionRefusalUtc = -1;
            if (SuccessEvidence != SuccessEvidenceType.InvocationCount)
                _lastSubmissionUtc = UtcNowMilliseconds();

            // Invocation is not server quest credit. Retain the legacy local
            // repetition count, but never write into a disposed/replaced actor's
            // continuation. A legitimately consumed item need not remain in bags.
            if (!OwnsActor()) return RunStatus.Success;
            _npcBlacklist.Add(recipientGuid);
            Counter++;
            StyxWoW.SleepForLagDuration();
            if (!OwnsActor()) return RunStatus.Success;
            if (WaitTime < 100) WaitTime = 100;
            if (WaitTime > 100 && targeted && ReferenceEquals(player.CurrentTarget, recipient)
                && player.CurrentTarget.Guid == recipientGuid)
                player.ClearTarget();
            if (OwnsActor()) Thread.Sleep(WaitTime);
            return RunStatus.Success;
        }

        #region Overrides of CustomForcedBehavior

        protected override Composite CreateBehavior()
        {
            return _root ?? (_root =
            new PrioritySelector(

                new Decorator(
                    ret => SuccessEvidence == SuccessEvidenceType.InvocationCount && Counter >= NumOfTimes,
                    new Action(ret => _isBehaviorDone = true)),

                new Decorator(
                    ret => SuccessEvidence != SuccessEvidenceType.InvocationCount && HasAuthoritativeSuccess(),
                    new Action(ret => _isBehaviorDone = true)),

                new Decorator(
                    ret => SuccessEvidence != SuccessEvidenceType.InvocationCount &&
                           IsAcknowledgementPending(
                               UtcNowMilliseconds(),
                               _lastSubmissionUtc,
                               AcknowledgementTimeout),
                    new Action(ret =>
                    {
                        TreeRoot.StatusText = "Waiting for authoritative quest acknowledgement";
                        return RunStatus.Running;
                    })),

                new Decorator(
                    ret => SuccessEvidence != SuccessEvidenceType.InvocationCount &&
                           _lastSubmissionUtc >= 0 &&
                           !IsAcknowledgementPending(
                               UtcNowMilliseconds(),
                               _lastSubmissionUtc,
                               AcknowledgementTimeout) &&
                           Item == null,
                    new Action(ret => DeferAuthoritativeAttempt(
                        "the submitted item is no longer available after the bounded acknowledgement window"))),

                new Decorator(
                    ret => SuccessEvidence != SuccessEvidenceType.InvocationCount &&
                           (SuccessEvidence != SuccessEvidenceType.ObjectiveProgress || InitialObjectiveCount.HasValue) &&
                           Counter >= MaxAttempts,
                    new Action(ret =>
                    {
                        AuthoritativeAttemptsExhausted = true;
                        LogMessage("warning",
                            "UseItemOn exhausted {0} bounded attempt(s) without authoritative {1} acknowledgement for quest {2}; deferring.",
                            MaxAttempts, SuccessEvidence, QuestId);
                        _isBehaviorDone = true;
                        return RunStatus.Success;
                    })),

                new Decorator(
                    ret => SuccessEvidence == SuccessEvidenceType.ObjectiveProgress && !InitialObjectiveCount.HasValue,
                    new Action(ret =>
                    {
                        LogMessage("warning",
                            "UseItemOn cannot establish the initial objective count for quest {0} objective {1}; deferring without item use.",
                            QuestId, ObjectiveIndex);
                        _isBehaviorDone = true;
                        return RunStatus.Success;
                    })),

                    new PrioritySelector(
                        new Decorator(ret => CurrentObject != null &&
                            (CurrentObject.DistanceSqr > Range * Range ||
                             RequireLos && !CurrentObject.InLineOfSight),
                            new Switch<NavigationType>(ret => NavigationState,
                                new SwitchArgument<NavigationType>(
                                    NavigationType.CTM,
                                    new Sequence(
                                        new Action(ret => { TreeRoot.StatusText = "Moving to use item on - " + CurrentObject.Name; }),
                                        new Action(ret => WoWMovement.ClickToMove(CurrentObject.Location))
                                    )),
                                new SwitchArgument<NavigationType>(
                                    NavigationType.Mesh,
                                    new Sequence(
                                        new Action(delegate { TreeRoot.StatusText = "Moving to use item on \"" + CurrentObject.Name + "\""; }),
                                        new Action(ret => Navigator.MoveTo(CurrentObject.Location))
                                        )),
                                new SwitchArgument<NavigationType>(
                                    NavigationType.None,
                                    new Sequence(
                                        new Action(ret => { TreeRoot.StatusText = "Object is out of range, Skipping - " + CurrentObject.Name + " Distance: " + CurrentObject.Distance; }),
                                        new Action(ret => _isBehaviorDone = true)
                                    )))),

                        new Decorator(ret => CurrentObject != null && CurrentObject.DistanceSqr <= Range * Range && Item != null && Item.Cooldown == 0,
                            new Action(ret => UseCapturedItem())
                                    ),

                            new Decorator(
                                ret => Location.DistanceSqr(Me.Location) > 2 * 2,
                                new Sequence(
                                    new Action(delegate { TreeRoot.StatusText = "Moving to location " + Location; }),
                                    new Action(ret => Navigator.MoveTo(Location)))),

                            new Decorator(
                                 ret => !WaitForNpcs && CurrentObject == null,
                                 new Action(ret => _isBehaviorDone = true)),

                            new Action(ret => TreeRoot.StatusText = "Waiting for object to spawn")
                )));
        }


        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        public override bool IsDone
        {
            get
            {
                return (_isBehaviorDone     // local execution completion/deferral
                        || (SuccessEvidence != SuccessEvidenceType.InvocationCount && HasAuthoritativeSuccess())
                        || !UtilIsProgressRequirementsMet(QuestId, QuestRequirementInLog, QuestRequirementComplete));
            }
        }

        public override void OnStart()
        {
            // This reports problems, and stops BT processing if there was a problem with attributes...
            // We had to defer this action, as the 'profile line number' is not available during the element's
            // constructor call.
            OnStart_HandleAttributeProblem();

            _lastSubmissionUtc = -1;
            _submissionRefusalUtc = -1;

            if (!IsAttributeProblem && SuccessEvidence == SuccessEvidenceType.ObjectiveProgress)
                InitialObjectiveCount = ReadObjectiveCount();

            // If the quest is complete, this behavior is already done...
            // So we don't want to falsely inform the user of things that will be skipped.
            if (!IsDone)
            {
                PlayerQuest quest = StyxWoW.Me.QuestLog.GetQuestById((uint)QuestId);

                TreeRoot.GoalText = this.GetType().Name + ": " + ((quest != null) ? ("\"" + quest.Name + "\"") : "In Progress");
            }

            if (IgnoreCombat && TreeRoot.Current != null && TreeRoot.Current.Root != null && TreeRoot.Current.Root.LastStatus != RunStatus.Running)
            {
                var currentRoot = TreeRoot.Current.Root;
                if (currentRoot is GroupComposite)
                {
                    var root = (GroupComposite)currentRoot;
                    root.InsertChild(0, CreateBehavior());
                }
            }
        }

        #endregion
    }

    public static class WoWUnitExtensions
    {
        private static LocalPlayer Me { get { return (ObjectManager.Me); } }

        public static bool IsInOurParty(this WoWUnit wowUnit)
        {
            return ((Me.PartyMembers.FirstOrDefault(partyMember => (partyMember.Guid == wowUnit.Guid))) != null);
        }
    }

    class BehaviorBlacklist
    {
        static readonly Dictionary<ulong, BlacklistTime> SpellBlacklistDict = new Dictionary<ulong, BlacklistTime>();
        private BehaviorBlacklist()
        {
        }

        class BlacklistTime
        {
            public BlacklistTime(DateTime time, TimeSpan span)
            {
                TimeStamp = time;
                Duration = span;
            }
            public DateTime TimeStamp { get; private set; }
            public TimeSpan Duration { get; private set; }
        }

        static public bool Contains(ulong id)
        {
            RemoveIfExpired(id);
            return SpellBlacklistDict.ContainsKey(id);
        }

        static public void Add(ulong id, TimeSpan duration)
        {
            SpellBlacklistDict[id] = new BlacklistTime(DateTime.Now, duration);
        }

        static void RemoveIfExpired(ulong id)
        {
            if (SpellBlacklistDict.ContainsKey(id) &&
                SpellBlacklistDict[id].TimeStamp + SpellBlacklistDict[id].Duration <= DateTime.Now)
            {
                SpellBlacklistDict.Remove(id);
            }
        }
    }
}
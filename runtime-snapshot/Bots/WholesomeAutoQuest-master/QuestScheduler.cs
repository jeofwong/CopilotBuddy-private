using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Bots.Quest.QuestOrder;
using Styx.Helpers;
using Styx.Logic.Pathing;
using Styx.Logic.Questing;
using Styx.Logic.Questing.Recovery;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

#nullable disable

namespace WholesomeAQ
{
    public sealed class QuestSchedulerAcceptedQuest
    {
        public uint QuestId { get; init; }
        public bool IsCompleted { get; init; }
        public bool IsFailed { get; init; }
        public IReadOnlyList<int> ObjectiveCounts { get; init; } = Array.Empty<int>();
    }

    public sealed class QuestSchedulerSnapshot
    {
        public DateTime UtcNow { get; init; }
        public int PlayerLevel { get; init; }
        public int PlayerRaceId { get; init; }
        public int MapId { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        // Explicit pure snapshots retain their complete-input default. The live
        // producer below derives this flag from the actual raw/metadata owner.
        public bool HasCompleteQuestLog { get; init; } = true;
        public bool HasAuthoritativeCompletions { get; init; }
        public IReadOnlyCollection<uint> CompletedQuestIds { get; init; } = Array.Empty<uint>();
        public IReadOnlyList<QuestSchedulerAcceptedQuest> AcceptedQuests { get; init; } = Array.Empty<QuestSchedulerAcceptedQuest>();
        public int QuestLogCapacity { get; init; } = 25;
        public IReadOnlyDictionary<int, long> CarriedItemCounts { get; init; }
    }

    public class QuestScheduler
    {
        private const double EndpointCellSize = 80.0;
        private const string NavigationAssessmentRetry = "navigation-assessment-retry";
        private readonly DataLoader _dataLoader;
        private readonly ProfileBuilder _profileBuilder;
        private readonly WholesomeAQSettings _settings;
        private int _scanThreshold;
        private DateTime _lastScan = DateTime.MinValue;
        private QuestRecoveryContext _lastRecoveryContext;
        private ForcedBehavior _lastActivation;
        private QuestRecoveryKey _lastActivationKey;
        private bool _rebuildRequested;
        private PublishedWork _publishedWork;

        private sealed class PublishedWork
        {
            internal LocalPlayer Player;
            internal QuestLog Log;
            internal QuestLogSnapshot Observation;
            internal object OuterProfile, Profile;
            internal string Path;
            internal QuestScheduleResult Schedule;
            internal Func<bool> LeaseIsCurrent;
            internal Func<bool> Check;
        }

        // A stable delegate is the identity of one completed publication, not of
        // a Pending/Running refresh request. Capture it once per root driver tick.
        internal Func<bool> CaptureExecutionPermission() => _publishedWork?.Check;

        private bool HasPublicationOwner(PublishedWork work) =>
            ReferenceEquals(_publishedWork, work)
            && (work.LeaseIsCurrent == null || work.LeaseIsCurrent())
            && ReferenceEquals(LastSchedule, work.Schedule)
            && ReferenceEquals(ObjectManager.Me, work.Player)
            && ReferenceEquals(Styx.Logic.Profiles.ProfileManager.CurrentOuterProfile, work.OuterProfile)
            && ReferenceEquals(Styx.Logic.Profiles.ProfileManager.CurrentProfile, work.Profile)
            && string.Equals(Styx.Logic.Profiles.ProfileManager.XmlLocation, work.Path, StringComparison.Ordinal);

        private bool IsPublicationCurrent(PublishedWork work)
        {
            if (!ReferenceEquals(_publishedWork, work)) return false;
            bool current;
            try
            {
                current = HasPublicationOwner(work)
                    && work.Log.IsSnapshotCurrent(work.Observation)
                    && HasPublicationOwner(work);
            }
            catch (Exception error) when (error is not ThreadInterruptedException && error is not OperationCanceledException)
            {
                current = false;
            }
            // Do not revoke a new publication created by an observation callback.
            if (!ReferenceEquals(_publishedWork, work)) return false;
            if (!current)
                InvalidatePublishedWork("Published quest observations or owner changed; waiting for a fresh scan.");
            return current;
        }
        private static int _unknownNavigationFingerprintLogged;
        private static readonly TimeSpan ScanCooldown = TimeSpan.FromSeconds(10);

        public int ScanThreshold => _scanThreshold;
        public string CurrentProfilePath { get; private set; }
        public int LastQuestCount { get; private set; }
        public string LastStatus { get; private set; }
        public HashSet<int> ActiveQuestIds { get; private set; }
        public List<VendorEntry> CurrentVendors { get; set; }
        public QuestScheduleResult LastSchedule { get; private set; } = new QuestScheduleResult();
        public DateTime? EarliestRetryUtc => LastSchedule?.EarliestRetryUtc;

        public QuestScheduler(DataLoader dataLoader, ProfileBuilder profileBuilder, WholesomeAQSettings settings)
        {
            _dataLoader = dataLoader ?? throw new ArgumentNullException(nameof(dataLoader));
            _profileBuilder = profileBuilder ?? throw new ArgumentNullException(nameof(profileBuilder));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _scanThreshold = settings.ScanStartDistance;
        }

        public bool NeedNewScan() => DateTime.Now - _lastScan >= ScanCooldown;

        public bool ScanAndBuildProfile(LocalPlayer me) => ScanAndRefresh(me);

        public bool BuildForLogQuests(LocalPlayer me) => ScanAndRefresh(me);

        // Retain the generate-only API for standalone callers. The running bot uses
        // the lease-fenced overload and the host's actual load-acceptance result.
        public bool ScanAndRefresh(LocalPlayer me, string validatedGrindProfilePath = null) =>
            ScanAndRefresh(me, validatedGrindProfilePath, apply => { apply(); return true; }, null);

        // Preserve the original four-argument method, including reflection callers.
        // The owned entry has a distinct name to avoid ambiguous method lookup.
        internal bool ScanAndRefresh(
            LocalPlayer me,
            string validatedGrindProfilePath,
            Func<System.Action, bool> tryApplyPublication,
            Func<string, bool> tryLoadProfile) =>
            ScanAndRefreshOwned(me, validatedGrindProfilePath, tryApplyPublication, tryLoadProfile, null);

        internal bool ScanAndRefreshOwned(
            LocalPlayer me,
            string validatedGrindProfilePath,
            Func<System.Action, bool> tryApplyPublication,
            Func<string, bool> tryLoadProfile,
            Func<bool> isPublicationLeaseCurrent)
        {
            if (tryApplyPublication == null)
                throw new ArgumentNullException(nameof(tryApplyPublication));
            if (me == null)
            {
                tryApplyPublication(() => InvalidatePublishedWork("Quest observations unavailable: no player was supplied."));
                throw new ArgumentNullException(nameof(me));
            }

            int scanThreshold = 0;
            if (!tryApplyPublication(() =>
            {
                _lastScan = DateTime.Now;
                scanThreshold = _scanThreshold;
            }))
                return false;
            QuestDatabase db = _dataLoader.Database;
            if (db == null)
            {
                tryApplyPublication(() => InvalidatePublishedWork("No quest data loaded"));
                return false;
            }

            // QuestLog reads ObjectManager.Me, not the LocalPlayer argument. An old
            // wrapper must not borrow another player's log or fabricate an empty one.
            if (!ReferenceEquals(me, ObjectManager.Me) || ObjectManager.Wow == null ||
                !Styx.StyxWoW.IsInWorld || !me.IsValid)
            {
                tryApplyPublication(() => InvalidatePublishedWork("Quest observations unavailable: the current player is not ready."));
                return false;
            }

            // A refresh is not permission to keep executing the previous plan while
            // fresh identity/log/context reads can fail. Revoke before those reads;
            // do not revoke again in a late catch that may belong to an older refresh.
            if (!tryApplyPublication(() => InvalidatePublishedWork("Refreshing quest observations; prior work is not authorized.")))
                return false;
            QuestRecoveryRuntime.EnsureConfigured(
                _dataLoader.ExecutionFingerprint,
                NavigationProviderFingerprint());
            QuestLog questLog = me.QuestLog;
            QuestLogSnapshot observation = questLog.CaptureSnapshot();
            var accepted = observation.Quests
                .OrderBy(quest => quest.Id)
                .Select(quest => new QuestSchedulerAcceptedQuest
                {
                    QuestId = quest.Id,
                    IsCompleted = quest.IsCompleted,
                    IsFailed = observation.FailedQuestIds.Contains(quest.Id),
                    ObjectiveCounts = ReadObjectiveCounts(quest)
                })
                .ToArray();
            QuestRecoveryContext context = QuestRecoveryRuntime.Capture(
                accepted.SelectMany(quest => quest.ObjectiveCounts).ToArray());

            bool authoritative = me.QuestLog.TryGetAuthoritativeCompletedQuests(out var completed);
            var snapshot = new QuestSchedulerSnapshot
            {
                UtcNow = DateTime.UtcNow,
                PlayerLevel = me.Level,
                PlayerRaceId = (int)me.Race,
                MapId = (int)me.MapId,
                X = me.Location.X,
                Y = me.Location.Y,
                HasCompleteQuestLog = observation.IsComplete,
                HasAuthoritativeCompletions = authoritative,
                CompletedQuestIds = authoritative ? completed : Array.Empty<uint>(),
                AcceptedQuests = accepted,
                CarriedItemCounts = me.CarriedItems.GroupBy(item => (int)item.Entry)
                    .ToDictionary(group => group.Key, group => group.Sum(item => (long)item.StackCount))
            };

            // Raw observations are samples, not a native transaction/session lease.
            // Recheck the same sample at each fallible publication boundary. The
            // nested lease check also protects replacement work from reentrant
            // player/world observations; an obsolete failure must not revoke it.
            bool TryApplyObserved(Action apply)
            {
                bool applied = false;
                tryApplyPublication(() =>
                {
                    bool current = snapshot.HasCompleteQuestLog && questLog.IsSnapshotCurrent(observation);
                    tryApplyPublication(() =>
                    {
                        if (!current)
                        {
                            InvalidatePublishedWork("Quest observations incomplete or changed; waiting for a fresh scan.");
                            return;
                        }
                        apply();
                        applied = true;
                    });
                });
                return applied;
            }

            // Admission precedes recovery selection/marks and navigation probes.
            if (!TryApplyObserved(() => { }))
                return false;
            QuestScheduleResult candidate = MaterializeSchedule(
                db,
                snapshot,
                key => QuestRecoveryManager.Instance.Evaluate(key, context),
                _settings.MaxQuestsPerProfile,
                scanThreshold,
                _settings.MinQuestLevelOffset,
                validatedGrindProfilePath,
                QuestRecoveryManager.Instance.MarkCompleted,
                message => Logging.WriteDiagnostic($"[WholesomeAQ] {message}"),
                // Safety vetoes are per hotspot, including points without a native path probe.
                isKnownUnsafe: point => BlackspotManager.IsBlackspotted(
                    new WoWPoint((float)point.X, (float)point.Y, (float)point.Z)),
                navigationAssessment: point => AssessNavigation(point, me.Location),
                reportDataFailure: outcome => QuestRecoveryManager.Instance.Report(outcome, context));

            // Preparation can invoke external navigation/player owners. An obsolete
            // continuation must not change a replacement's scan state or output file.
            if (!TryApplyObserved(() => candidate = ApplyScanExpansionBeforeFallback(candidate)))
                return false;

            string path = null;
            bool hasWork = candidate.Selected.Count > 0 || candidate.FallbackMode == QuestFallbackMode.ValidatedGrind;
            if (candidate.Selected.Count > 0)
            {
                string xml = _profileBuilder.BuildProfileXml(
                    candidate.Plan, db, me.ZoneText, me.Name, me.Level, CurrentVendors,
                    _dataLoader.StrategyPack);
                if (!TryApplyObserved(() => path = _profileBuilder.WriteProfile(xml)))
                    return false;
            }
            else if (candidate.FallbackMode == QuestFallbackMode.ValidatedGrind)
            {
                path = candidate.ValidatedGrindProfilePath;
            }

            bool published = false;
            TryApplyObserved(() =>
            {
                // Null output is the builder's supported no-output mode, not an
                // instruction to reuse the prior profile or authorize its old child.
                if (hasWork && (string.IsNullOrWhiteSpace(path) ||
                    (tryLoadProfile != null && !tryLoadProfile(path))))
                    return;

                // Loading raises synchronous host events. They can stop/restart the
                // bot reentrantly, even while the refresh monitor is held. Recheck the
                // real lease after those events, rather than revoking in a late catch.
                TryApplyObserved(() =>
                {
                    _lastRecoveryContext = context;
                    LastStatus = candidate.Status;
                    LastQuestCount = candidate.Selected.Count;
                    ActiveQuestIds = new HashSet<int>(candidate.Selected.Select(item => (int)item.QuestId));
                    CurrentProfilePath = path;
                    LastSchedule = candidate;
                    // Generate-only callers do not establish host execution ownership.
                    if (hasWork && tryLoadProfile != null)
                    {
                        var work = new PublishedWork
                        {
                            Player = me, Log = questLog, Observation = observation,
                            OuterProfile = Styx.Logic.Profiles.ProfileManager.CurrentOuterProfile,
                            Profile = Styx.Logic.Profiles.ProfileManager.CurrentProfile,
                            Path = path, Schedule = candidate, LeaseIsCurrent = isPublicationLeaseCurrent
                        };
                        work.Check = () => IsPublicationCurrent(work);
                        _publishedWork = work; // Continuing execution permission is published last.
                    }
                    published = true;
                });
            });
            return published && hasWork;
        }

        internal void InvalidatePublishedWork(string status)
        {
            _publishedWork = null;
            // Revoke the execution gate first, including a child that is already Running.
            // Unknown is not an eligible grind fallback or an instruction to load an old XML.
            LastSchedule = new QuestScheduleResult
            {
                FallbackMode = QuestFallbackMode.TimedIdle,
                // A retry requests a fresh observation; it does not restore stale work.
                EarliestRetryUtc = DateTime.UtcNow.Add(ScanCooldown),
                Status = status
            };
            CurrentProfilePath = null;
            LastQuestCount = 0;
            LastStatus = status;
            _lastRecoveryContext = null;
            _lastActivation = null;
            _lastActivationKey = null;
            _rebuildRequested = false;

            // ActiveQuestIds also protects scheduled quest items at SellByQuality.
            // Keep that conservative protection until a successful observation replaces
            // it or the explicit lifecycle Reset runs. It is not execution permission.
        }

        internal QuestScheduleResult ApplyScanExpansionBeforeFallback(QuestScheduleResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));
            _rebuildRequested = false;
            if (result.Selected.Count > 0)
            {
                _scanThreshold = _settings.ScanStartDistance;
                return result;
            }
            if (_scanThreshold >= _settings.ScanMaxDistance)
                return result;

            int previous = _scanThreshold;
            _scanThreshold = Math.Min(
                _settings.ScanMaxDistance,
                _scanThreshold + Math.Max(1, _settings.ScanStep));
            return new QuestScheduleResult
            {
                Selected = result.Selected,
                Plan = result.Plan,
                EarliestRetryUtc = result.EarliestRetryUtc,
                FallbackMode = QuestFallbackMode.None,
                Status = $"{result.Status} Expanding scan radius from {previous} to {_scanThreshold} yards before fallback."
            };
        }

        public static QuestScheduleResult MaterializeSchedule(
            QuestDatabase db,
            QuestSchedulerSnapshot snapshot,
            Func<QuestRecoveryKey, QuestRecoveryDecision> evaluate,
            int maximum,
            int scanThreshold,
            int minQuestLevelOffset,
            string validatedGrindProfilePath = null,
            Action<uint> markCompleted = null,
            Action<string> log = null,
            Func<SpawnPoint, bool> isKnownUnsafe = null,
            Func<SpawnPoint, SpawnNavigationAssessment> navigationAssessment = null,
            Action<QuestAttemptOutcome> reportDataFailure = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (evaluate == null) throw new ArgumentNullException(nameof(evaluate));
            if (!snapshot.HasCompleteQuestLog || snapshot.AcceptedQuests == null)
            {
                return new QuestScheduleResult
                {
                    FallbackMode = QuestFallbackMode.TimedIdle,
                    EarliestRetryUtc = snapshot.UtcNow.Add(ScanCooldown),
                    Status = "Quest observations incomplete; waiting for a fresh scan."
                };
            }

            var completed = new HashSet<uint>(snapshot.CompletedQuestIds ?? Array.Empty<uint>());
            if (snapshot.HasAuthoritativeCompletions && markCompleted != null)
            {
                foreach (uint questId in completed.OrderBy(id => id))
                    markCompleted(questId);
            }

            var accepted = (snapshot.AcceptedQuests ?? Array.Empty<QuestSchedulerAcceptedQuest>())
                .GroupBy(quest => quest.QuestId)
                .ToDictionary(group => group.Key, group => group.Last());
            int questLogCapacity = Math.Max(1, snapshot.QuestLogCapacity);
            bool questLogFull = accepted.Count >= questLogCapacity;
            var quests = db.Quests.ToDictionary(quest => (uint)quest.Id);
            var candidates = new List<QuestWorkCandidate>();
            var candidatePlans = new Dictionary<QuestWorkCandidate, IReadOnlyList<QuestPlanEntry>>();
            var exclusions = new List<string>();
            var correctionAncestors = new HashSet<uint>();
            Func<SpawnPoint, SpawnNavigationAssessment> assessNavigation =
                CreateCachedNavigationAssessment(navigationAssessment, isKnownUnsafe);

            if (snapshot.HasAuthoritativeCompletions && !questLogFull)
            {
                int minimumLevel = Math.Max(1, snapshot.PlayerLevel - minQuestLevelOffset);
                foreach (QuestEntry descendant in db.Quests.OrderBy(quest => quest.Id))
                {
                    if (accepted.ContainsKey((uint)descendant.Id) || completed.Contains((uint)descendant.Id))
                        continue;
                    if (!CanRequestPickup(descendant, db, snapshot, scanThreshold, minimumLevel))
                        continue;
                    uint ancestor = FindAcceptedIncompleteAncestor(descendant, quests, accepted, completed);
                    if (ancestor == 0)
                        continue;
                    correctionAncestors.Add(ancestor);
                    log?.Invoke($"Quest chain correction: ancestor={ancestor}; descendant={descendant.Id}.");
                }
            }

            foreach (QuestSchedulerAcceptedQuest acceptedQuest in accepted.Values.OrderBy(quest => quest.QuestId))
            {
                if (!quests.TryGetValue(acceptedQuest.QuestId, out QuestEntry quest))
                {
                    var missingKey = QuestRecoveryKey.ForQuestStage(
                        acceptedQuest.QuestId,
                        acceptedQuest.IsCompleted ? QuestRecoveryStage.TurnIn : QuestRecoveryStage.Objective);
                    QuestRecoveryDecision missingDecision = evaluate(missingKey);
                    if (missingDecision.MayAttempt)
                        ReportDataOmission(missingKey, QuestFailureReason.InvalidQuestData,
                            "scheduler:accepted-quest-missing", reportDataFailure);
                    continue;
                }
                if (acceptedQuest.IsCompleted)
                {
                    AddRelationWork(
                        quest, QuestWorkStage.TurnIn, QuestRecoveryStage.TurnIn,
                        db.QuestEnders.Where(ender => ender.QuestId == quest.Id)
                            .Select(ender => new Relation(ender.EnderId, ender: ender)),
                        db, snapshot, evaluate, candidates, candidatePlans, exclusions, scanThreshold,
                        assessNavigation, reportDataFailure);
                }
                else
                {
                    AddObjectiveWork(
                        quest,
                        correctionAncestors.Contains(acceptedQuest.QuestId)
                            ? QuestWorkStage.AncestorCorrection
                            : QuestWorkStage.Objective,
                        acceptedQuest.ObjectiveCounts,
                        db, snapshot, evaluate, candidates, candidatePlans, exclusions, scanThreshold,
                        assessNavigation, reportDataFailure);
                }
            }

            if (snapshot.HasAuthoritativeCompletions && !questLogFull)
            {
                int minimumLevel = Math.Max(1, snapshot.PlayerLevel - minQuestLevelOffset);
                var claimedPositiveExclusiveGroups = new HashSet<int>();
                foreach (QuestEntry quest in db.Quests.OrderBy(quest => quest.QuestLevel).ThenBy(quest => quest.Id))
                {
                    uint questId = (uint)quest.Id;
                    if (accepted.ContainsKey(questId) || completed.Contains(questId))
                        continue;
                    if (snapshot.PlayerLevel < quest.MinLevel ||
                        (quest.QuestLevel > 0 && (quest.QuestLevel < minimumLevel || quest.QuestLevel > snapshot.PlayerLevel)) ||
                        !RaceAllowed(quest.AllowableRaces, snapshot.PlayerRaceId) ||
                        !Supported(quest))
                        continue;
                    if (!PositiveExclusiveGroupAvailable(
                        quest, db, accepted, completed, claimedPositiveExclusiveGroups))
                        continue;

                    // TrinityCore 3.3.5 primary: a negative direct PrevQuestID
                    // requires QUEST_STATUS_INCOMPLETE. Accepted ready/completed
                    // and failed parents do not unlock the child. Pinned
                    // AzerothCore WotLK is broader (non-NONE); keep that
                    // compatibility difference explicit rather than inferring a
                    // source core from the realm or dataset name.
                    if (quest.PrevQuestID < 0 &&
                        (quest.PrevQuestID == int.MinValue ||
                         !accepted.TryGetValue((uint)-quest.PrevQuestID, out QuestSchedulerAcceptedQuest activeParent) ||
                         activeParent.IsCompleted ||
                         activeParent.IsFailed))
                        continue;

                    uint ancestor = FindAcceptedIncompleteAncestor(quest, quests, accepted, completed);
                    if (ancestor != 0 || !PrerequisitesComplete(quest, quests, completed))
                        continue;

                    int plannedBefore = candidatePlans.Count;
                    AddRelationWork(
                        quest, QuestWorkStage.Pickup, QuestRecoveryStage.Pickup,
                        db.QuestGivers.Where(giver => giver.QuestId == quest.Id)
                            .Select(giver => new Relation(giver.GiverId, giver: giver)),
                        db, snapshot, evaluate, candidates, candidatePlans, exclusions, scanThreshold,
                        assessNavigation, reportDataFailure);
                    if (quest.ExclusiveGroup > 0 && candidatePlans.Count > plannedBefore)
                        claimedPositiveExclusiveGroups.Add(quest.ExclusiveGroup);
                }
            }

            QuestScheduleResult selected = QuestSchedulingPolicy.Select(
                candidates, maximum, snapshot.UtcNow, validatedGrindProfilePath);
            // Navigation-only retries wake an idle scheduler, never replace active quest work.
            DateTime? retryUtc = selected.Selected.Count == 0
                ? selected.EarliestRetryUtc
                : candidates.Where(candidate => candidate.Recovery.Status != NavigationAssessmentRetry)
                    .Select(candidate => candidate.Recovery.RetryUtc)
                    .Where(value => value.HasValue)
                    .OrderBy(value => value)
                    .FirstOrDefault();
            var selectedPlan = selected.Selected
                .SelectMany(candidate => candidatePlans.TryGetValue(candidate, out var plan)
                    ? plan
                    : Array.Empty<QuestPlanEntry>())
                .ToArray();
            string status = selected.Status;
            if (selected.Selected.Count > 0)
            {
                status += " " + string.Join(" ", selected.Selected.Select(candidate =>
                    $"selected quest={candidate.QuestId};stage={candidate.Stage};state={candidate.Recovery.State}."));
            }
            if (!snapshot.HasAuthoritativeCompletions)
                status += " completion-authority=unknown; deferred new pickup and prerequisite-negative scheduling.";
            if (questLogFull)
                status += $" quest-log-full={accepted.Count}/{questLogCapacity}; deferred new pickups while retaining accepted quest work.";
            if (exclusions.Count > 0)
                status += " " + string.Join(" ", exclusions.OrderBy(value => value, StringComparer.Ordinal));

            return new QuestScheduleResult
            {
                Selected = selected.Selected,
                Plan = selectedPlan,
                EarliestRetryUtc = retryUtc,
                FallbackMode = selected.FallbackMode,
                ValidatedGrindProfilePath = selected.ValidatedGrindProfilePath,
                Status = status
            };
        }

        public static QuestRecoveryKey EndpointKey(
            uint questId,
            QuestRecoveryStage stage,
            SpawnPoint point)
        {
            if (point == null) throw new ArgumentNullException(nameof(point));
            int cellX = (int)Math.Floor(point.X / EndpointCellSize);
            int cellY = (int)Math.Floor(point.Y / EndpointCellSize);
            return QuestRecoveryKey.ForEndpoint(
                questId, stage, point.Map,
                $"cell:{cellX.ToString(CultureInfo.InvariantCulture)}:{cellY.ToString(CultureInfo.InvariantCulture)}");
        }

        public static QuestRecoveryDecision BeginActivation(
            QuestRecoveryKey exactKey,
            Func<QuestRecoveryKey, QuestRecoveryDecision> tryBeginAttempt,
            Action<QuestRecoveryKey> clearStagePoi,
            Action requestRebuild)
        {
            if (exactKey == null) throw new ArgumentNullException(nameof(exactKey));
            if (tryBeginAttempt == null) throw new ArgumentNullException(nameof(tryBeginAttempt));
            QuestRecoveryDecision decision = tryBeginAttempt(exactKey);
            if (!decision.MayAttempt)
            {
                clearStagePoi?.Invoke(exactKey);
                requestRebuild?.Invoke();
            }
            return decision;
        }

        internal QuestRecoveryDecision ObserveActivation(
            ForcedBehavior behavior,
            Action<QuestRecoveryKey> clearStagePoi,
            Action requestRebuild)
        {
            if (_lastRecoveryContext == null)
                return null;
            return ObserveActivation(
                behavior,
                key => QuestRecoveryManager.Instance.TryBeginAttempt(key, _lastRecoveryContext),
                clearStagePoi,
                requestRebuild);
        }

        internal QuestRecoveryDecision ObserveActivation(
            ForcedBehavior behavior,
            Func<QuestRecoveryKey, QuestRecoveryDecision> tryBeginAttempt,
            Action<QuestRecoveryKey> clearStagePoi,
            Action requestRebuild)
        {
            try
            {
                if (behavior?.IsDone == true)
                    return null;
            }
            catch
            {
                // Preserve the prior activation path when completion cannot be read authoritatively.
            }

            QuestRecoveryKey exactKey = ActivationKey(behavior);
            if (exactKey == null)
            {
                _lastActivation = null;
                _lastActivationKey = null;
                return null;
            }
            if (ReferenceEquals(behavior, _lastActivation) && exactKey.Equals(_lastActivationKey))
                return null;

            _lastActivation = behavior;
            _lastActivationKey = exactKey;
            return BeginActivation(
                exactKey,
                tryBeginAttempt,
                clearStagePoi,
                () =>
                {
                    if (_rebuildRequested)
                        return;
                    _rebuildRequested = true;
                    requestRebuild?.Invoke();
                });
        }

        internal void ReleaseActivation(ForcedBehavior behavior)
        {
            if (!ReferenceEquals(behavior, _lastActivation))
                return;
            _lastActivation = null;
            _lastActivationKey = null;
        }

        internal static QuestRecoveryKey ActivationKey(ForcedBehavior behavior)
        {
            if (behavior is ForcedQuestPickUp pickup)
                return QuestRecoveryKey.ForNpc(pickup.QuestId, QuestRecoveryStage.Pickup, pickup.GiverId);
            if (behavior is ForcedQuestTurnIn turnIn)
                return QuestRecoveryKey.ForNpc(turnIn.QuestId, QuestRecoveryStage.TurnIn, turnIn.NpcId);
            if (behavior is ForcedQuestObjective objective && objective.Objective?.Quest != null)
                return QuestRecoveryKey.ForQuestStage(objective.Objective.Quest.Id, QuestRecoveryStage.Objective);
            return null;
        }

        public static void ReportEndpointUnreachable(
            QuestRecoveryKey failedEndpoint,
            QuestRecoveryStage questStage,
            IReadOnlyCollection<QuestRecoveryKey> knownEndpoints,
            IReadOnlyCollection<QuestRecoveryKey> alreadyTriedOrExcluded,
            Action<QuestAttemptOutcome> report)
        {
            if (failedEndpoint == null) throw new ArgumentNullException(nameof(failedEndpoint));
            if (failedEndpoint.Scope != QuestRecoveryScope.Endpoint)
                throw new ArgumentException("The failed key must identify one endpoint.", nameof(failedEndpoint));
            if (knownEndpoints == null) throw new ArgumentNullException(nameof(knownEndpoints));
            if (alreadyTriedOrExcluded == null) throw new ArgumentNullException(nameof(alreadyTriedOrExcluded));
            if (report == null) throw new ArgumentNullException(nameof(report));

            report(QuestAttemptOutcome.Failure(
                failedEndpoint,
                QuestFailureReason.EndpointUnreachable,
                "The selected endpoint was unreachable."));
            var exhausted = new HashSet<QuestRecoveryKey>(alreadyTriedOrExcluded) { failedEndpoint };
            if (knownEndpoints.Count > 0 && knownEndpoints.All(exhausted.Contains))
            {
                report(QuestAttemptOutcome.Failure(
                    QuestRecoveryKey.ForQuestStage(failedEndpoint.QuestId, questStage),
                    QuestFailureReason.NoNavigableHotspot,
                    "All known endpoint clusters were excluded or tried."));
            }
        }

        internal static string CreateNavigationProviderFingerprint(Type providerType, string meshRoot)
        {
            if (providerType == null || string.IsNullOrWhiteSpace(meshRoot) || !Directory.Exists(meshRoot))
                return "unknown";
            string version = providerType.Assembly.GetName().Version?.ToString();
            if (string.IsNullOrWhiteSpace(providerType.FullName) || string.IsNullOrWhiteSpace(version))
                return "unknown";
            long meshStamp = Directory.GetLastWriteTimeUtc(meshRoot).Ticks;
            string value = $"{providerType.FullName}|{version}|{Path.GetFullPath(meshRoot)}|{meshStamp}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }

        internal static string NavigationProviderFingerprint()
        {
            try
            {
                string fingerprint = CreateNavigationProviderFingerprint(
                    Navigator.NavigationProvider?.GetType(),
                    Path.Combine(Logging.ApplicationPath, "mmaps"));
                if (fingerprint != "unknown")
                    return fingerprint;
            }
            catch (Exception ex)
            {
                LogUnknownNavigationFingerprintOnce(ex.Message);
                return "unknown";
            }

            LogUnknownNavigationFingerprintOnce("provider or mesh-root stamp unavailable");
            return "unknown";
        }

        private static void LogUnknownNavigationFingerprintOnce(string reason)
        {
            if (Interlocked.Exchange(ref _unknownNavigationFingerprintLogged, 1) == 0)
                Logging.WriteDiagnostic($"[WholesomeAQ] Navigation fingerprint unknown: {reason}.");
        }

        private static void AddObjectiveWork(
            QuestEntry quest,
            QuestWorkStage workStage,
            IReadOnlyList<int> objectiveCounts,
            QuestDatabase db,
            QuestSchedulerSnapshot snapshot,
            Func<QuestRecoveryKey, QuestRecoveryDecision> evaluate,
            List<QuestWorkCandidate> candidates,
            Dictionary<QuestWorkCandidate, IReadOnlyList<QuestPlanEntry>> candidatePlans,
            List<string> exclusions,
            int scanThreshold,
            Func<SpawnPoint, SpawnNavigationAssessment> assessNavigation,
            Action<QuestAttemptOutcome> reportDataFailure)
        {
            var stageKey = QuestRecoveryKey.ForQuestStage((uint)quest.Id, QuestRecoveryStage.Objective);
            QuestRecoveryDecision stageDecision = evaluate(stageKey);
            if (!stageDecision.MayAttempt)
            {
                AddBlockedCandidate(quest, workStage, stageDecision, candidates, exclusions, stageKey);
                return;
            }
            if (quest.Objectives.Count == 0)
            {
                ReportDataOmission(stageKey, QuestFailureReason.InvalidQuestData,
                    "scheduler:no-objective-rows", reportDataFailure);
                return;
            }

            var objectiveWork = new List<(QuestObjective Objective, QuestRecoveryDecision Decision,
                IReadOnlyList<KeyValuePair<QuestRecoveryKey, IReadOnlyList<SpawnPoint>>> Clusters,
                IReadOnlyList<QuestEndpointCandidate> Endpoints)>();
            var allEndpoints = new List<QuestEndpointCandidate>();
            foreach (QuestObjective objective in quest.Objectives.OrderBy(value => value.Index))
            {
                // Already-satisfied work does not require usable collection-source
                // metadata. Do not turn an unused source into a quest-data failure.
                if (IsObjectiveComplete(objective, objectiveCounts, snapshot.CarriedItemCounts))
                    continue;
                if (!Supported(quest, objective))
                {
                    var unsupportedKey = QuestRecoveryKey.ForObjective((uint)quest.Id, objective.Index);
                    QuestRecoveryDecision unsupportedDecision = evaluate(unsupportedKey);
                    if (unsupportedDecision.MayAttempt)
                        ReportDataOmission(unsupportedKey, QuestFailureReason.UnsupportedObjective,
                            "scheduler:unsupported-objective", reportDataFailure);
                    continue;
                }
                var objectiveKey = QuestRecoveryKey.ForObjective((uint)quest.Id, objective.Index);
                QuestRecoveryDecision objectiveDecision = evaluate(objectiveKey);
                if (!objectiveDecision.MayAttempt)
                {
                    AddBlockedCandidate(quest, workStage, objectiveDecision, candidates, exclusions, objectiveKey);
                    continue;
                }

                SpawnPoint[] knownSpawns = GetObjectiveSpawns(objective, db).ToArray();
                if (knownSpawns.Length == 0)
                {
                    ReportDataOmission(objectiveKey, QuestFailureReason.InvalidQuestData,
                        "scheduler:no-known-hotspots", reportDataFailure);
                    AddObjectiveOmission(
                        quest, workStage, objective.Index, "no-known-hotspots", null, exclusions);
                    continue;
                }

                var inRangeSpawns = knownSpawns
                    .Where(point => InRange(point, snapshot, scanThreshold)).ToArray();
                if (inRangeSpawns.Length == 0)
                {
                    AddObjectiveOmission(
                        quest, workStage, objective.Index, "outside-scan-radius", null, exclusions);
                    continue;
                }

                var assessedClusters = AssessClusters(inRangeSpawns, assessNavigation);
                if (assessedClusters.Length == 0)
                {
                    AddNavigationRetry(quest, workStage, objectiveKey, snapshot, candidates, exclusions);
                    AddObjectiveOmission(
                        quest, workStage, objective.Index, "no-assessed-hotspots", null, exclusions);
                    continue;
                }
                var clusters = assessedClusters.Select(value => value.Cluster).ToArray();
                var endpoints = assessedClusters.Select(value =>
                {
                    var cluster = value.Cluster;
                    QuestRecoveryKey key = EndpointKey((uint)quest.Id, QuestRecoveryStage.Navigation, cluster.Value[0]);
                    QuestRecoveryDecision decision = evaluate(key);
                    if (!decision.MayAttempt)
                        AddBlockedCandidate(quest, workStage, decision, candidates, exclusions, key);
                    return new QuestEndpointCandidate
                    {
                        Key = key,
                        Point = cluster.Value[0],
                        Distance = cluster.Value.Min(point => Distance(point, snapshot)),
                        IsKnownReachable = value.Assessment.IsKnownReachable,
                        IsKnownSafe = value.Assessment.IsKnownSafe,
                        SafetyScore = value.Assessment.SafetyScore,
                        RequiresHalfOpen = stageDecision.State == QuestRecoveryState.HalfOpen ||
                                           objectiveDecision.State == QuestRecoveryState.HalfOpen ||
                                           decision.State == QuestRecoveryState.HalfOpen,
                        Recovery = decision
                    };
                }).ToArray();
                allEndpoints.AddRange(endpoints);
                objectiveWork.Add((objective, objectiveDecision, clusters, endpoints));
            }

            var selectedEndpoints = QuestSchedulingPolicy.Select(allEndpoints, maximum: 5);
            var selectedKeys = new HashSet<QuestRecoveryKey>(selectedEndpoints.Select(endpoint => endpoint.Key));
            var plan = new List<QuestPlanEntry>();
            bool requiresHalfOpen = selectedEndpoints.Any(endpoint => endpoint.RequiresHalfOpen);
            bool selectedOrdinary = selectedEndpoints.Any(endpoint => !endpoint.RequiresHalfOpen);
            foreach (var work in objectiveWork)
            {
                if (selectedOrdinary && work.Decision.State == QuestRecoveryState.HalfOpen)
                    continue;
                var hotspots = work.Clusters
                    .Where(cluster => selectedKeys.Contains(
                        EndpointKey((uint)quest.Id, QuestRecoveryStage.Navigation, cluster.Value[0])))
                    .SelectMany(cluster => cluster.Value)
                    .ToArray();
                if (hotspots.Length == 0)
                {
                    DateTime? retryUtc = work.Endpoints
                        .Select(endpoint => endpoint.Recovery.RetryUtc)
                        .Where(value => value.HasValue)
                        .OrderBy(value => value)
                        .FirstOrDefault();
                    AddObjectiveOmission(
                        quest, workStage, work.Objective.Index,
                        "no-selected-hotspots", retryUtc, exclusions);
                    continue;
                }
                plan.Add(new QuestPlanEntry
                {
                    Quest = quest,
                    Stage = workStage,
                    ObjectiveIndex = work.Objective.Index,
                    Hotspots = hotspots
                });
            }

            if (plan.Count > 0)
                AddEligibleCandidate(quest, workStage, stageDecision, plan, requiresHalfOpen, snapshot, candidates, candidatePlans);
        }

        private static void AddRelationWork(
            QuestEntry quest,
            QuestWorkStage workStage,
            QuestRecoveryStage recoveryStage,
            IEnumerable<Relation> relations,
            QuestDatabase db,
            QuestSchedulerSnapshot snapshot,
            Func<QuestRecoveryKey, QuestRecoveryDecision> evaluate,
            List<QuestWorkCandidate> candidates,
            Dictionary<QuestWorkCandidate, IReadOnlyList<QuestPlanEntry>> candidatePlans,
            List<string> exclusions,
            int scanThreshold,
            Func<SpawnPoint, SpawnNavigationAssessment> assessNavigation,
            Action<QuestAttemptOutcome> reportDataFailure)
        {
            var stageKey = QuestRecoveryKey.ForQuestStage((uint)quest.Id, recoveryStage);
            QuestRecoveryDecision stageDecision = evaluate(stageKey);
            if (!stageDecision.MayAttempt)
            {
                AddBlockedCandidate(quest, workStage, stageDecision, candidates, exclusions, stageKey);
                return;
            }

            var eligibleRelations = new List<(Relation Relation, QuestRecoveryDecision Decision, IReadOnlyList<SpawnPoint> Spawns)>();
            var allEndpoints = new List<QuestEndpointCandidate>();
            var distinctRelations = relations
                .OrderBy(value => value.Entry)
                .ThenBy(value => value.DisplayName, StringComparer.Ordinal)
                .GroupBy(value => (value.Type, value.Entry))
                .Select(group => group.First())
                .ToArray();
            if (distinctRelations.Length == 0)
            {
                ReportDataOmission(
                    stageKey,
                    QuestFailureReason.InvalidQuestData,
                    recoveryStage == QuestRecoveryStage.TurnIn
                        ? "scheduler:no-ender-relations"
                        : "scheduler:no-giver-relations",
                    reportDataFailure);
                return;
            }
            foreach (Relation relation in distinctRelations)
            {
                var relationKey = QuestRecoveryKey.ForNpc((uint)quest.Id, recoveryStage, (uint)relation.Entry);
                QuestRecoveryDecision relationDecision = evaluate(relationKey);
                if (!relationDecision.MayAttempt)
                {
                    AddBlockedCandidate(quest, workStage, relationDecision, candidates, exclusions, relationKey);
                    continue;
                }

                SpawnPoint[] knownSpawns = GetRelationSpawns(relation.Entry, relation.Type, db).ToArray();
                if (knownSpawns.Length == 0)
                {
                    ReportDataOmission(relationKey, QuestFailureReason.InvalidQuestData,
                        "scheduler:no-relation-spawns", reportDataFailure);
                    continue;
                }

                var inRangeSpawns = knownSpawns
                    .Where(point => InRange(point, snapshot, scanThreshold)).ToArray();
                if (inRangeSpawns.Length == 0)
                {
                    exclusions.Add($"excluded quest={quest.Id};stage={workStage};npc={relation.Entry};reason=outside-scan-radius;retry=context-change");
                    continue;
                }

                var assessedClusters = AssessClusters(inRangeSpawns, assessNavigation);
                var spawns = assessedClusters.SelectMany(value => value.Cluster.Value).ToArray();
                if (spawns.Length == 0)
                {
                    AddNavigationRetry(quest, workStage, relationKey, snapshot, candidates, exclusions);
                    continue;
                }
                eligibleRelations.Add((relation, relationDecision, spawns));
                foreach (var value in assessedClusters)
                {
                    var cluster = value.Cluster;
                    QuestRecoveryKey key = EndpointKey((uint)quest.Id, QuestRecoveryStage.Navigation, cluster.Value[0]);
                    QuestRecoveryDecision endpointDecision = evaluate(key);
                    if (!endpointDecision.MayAttempt)
                        AddBlockedCandidate(quest, workStage, endpointDecision, candidates, exclusions, key);
                    allEndpoints.Add(new QuestEndpointCandidate
                    {
                        Key = key,
                        Point = cluster.Value[0],
                        Distance = cluster.Value.Min(point => Distance(point, snapshot)),
                        IsKnownReachable = value.Assessment.IsKnownReachable,
                        IsKnownSafe = value.Assessment.IsKnownSafe,
                        SafetyScore = value.Assessment.SafetyScore,
                        RequiresHalfOpen = stageDecision.State == QuestRecoveryState.HalfOpen ||
                                           relationDecision.State == QuestRecoveryState.HalfOpen ||
                                           endpointDecision.State == QuestRecoveryState.HalfOpen,
                        Recovery = endpointDecision
                    });
                }
            }

            var selectedEndpoints = QuestSchedulingPolicy.Select(allEndpoints, maximum: 5);
            var selectedKeys = new HashSet<QuestRecoveryKey>(selectedEndpoints.Select(endpoint => endpoint.Key));
            var plan = new List<QuestPlanEntry>();
            bool requiresHalfOpen = selectedEndpoints.Any(endpoint => endpoint.RequiresHalfOpen);
            bool selectedOrdinary = selectedEndpoints.Any(endpoint => !endpoint.RequiresHalfOpen);
            foreach (var eligible in eligibleRelations)
            {
                if (selectedOrdinary && eligible.Decision.State == QuestRecoveryState.HalfOpen)
                    continue;
                var relationClusters = Cluster(eligible.Spawns)
                    .Where(cluster => selectedKeys.Contains(
                        EndpointKey((uint)quest.Id, QuestRecoveryStage.Navigation, cluster.Value[0])))
                    .ToArray();
                if (relationClusters.Length == 0)
                    continue;
                plan.Add(new QuestPlanEntry
                {
                    Quest = quest,
                    Stage = workStage,
                    Giver = eligible.Relation.Giver,
                    Ender = eligible.Relation.Ender,
                    Hotspots = relationClusters.SelectMany(cluster => cluster.Value).ToArray()
                });
            }

            if (plan.Count > 0)
                AddEligibleCandidate(quest, workStage, stageDecision, plan, requiresHalfOpen, snapshot, candidates, candidatePlans);
        }

        private static void AddEligibleCandidate(
            QuestEntry quest,
            QuestWorkStage workStage,
            QuestRecoveryDecision recovery,
            IReadOnlyList<QuestPlanEntry> plan,
            bool halfOpen,
            QuestSchedulerSnapshot snapshot,
            List<QuestWorkCandidate> candidates,
            Dictionary<QuestWorkCandidate, IReadOnlyList<QuestPlanEntry>> candidatePlans)
        {
            double distance = plan.SelectMany(entry => entry.Hotspots)
                .Select(point => Distance(point, snapshot))
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            var candidate = new QuestWorkCandidate
            {
                QuestId = (uint)quest.Id,
                Stage = halfOpen ? QuestWorkStage.HalfOpen : workStage,
                Distance = distance,
                ChainValue = quest.NextQuestID > 0 ? 1 : 0,
                SafetyScore = 0,
                Recovery = recovery
            };
            candidates.Add(candidate);
            candidatePlans[candidate] = plan;
        }

        private static void AddNavigationRetry(
            QuestEntry quest,
            QuestWorkStage workStage,
            QuestRecoveryKey key,
            QuestSchedulerSnapshot snapshot,
            List<QuestWorkCandidate> candidates,
            List<string> exclusions)
        {
            // A path or safety assessment describes this scan, not the validity of quest data.
            // Keep the endpoint excluded, but recheck without persisting a six-hour quarantine.
            AddBlockedCandidate(quest, workStage, new QuestRecoveryDecision
            {
                State = QuestRecoveryState.CoolingDown,
                MayAttempt = false,
                Status = NavigationAssessmentRetry,
                RetryUtc = snapshot.UtcNow.AddSeconds(30)
            }, candidates, exclusions, key);
        }

        private static void AddBlockedCandidate(
            QuestEntry quest,
            QuestWorkStage workStage,
            QuestRecoveryDecision decision,
            List<QuestWorkCandidate> candidates,
            List<string> exclusions,
            QuestRecoveryKey key)
        {
            candidates.Add(new QuestWorkCandidate
            {
                QuestId = (uint)quest.Id,
                Stage = workStage,
                Distance = double.MaxValue,
                Recovery = decision
            });
            exclusions.Add($"excluded quest={quest.Id};stage={workStage};{FormatKey(key)};retry={FormatRetry(decision.RetryUtc)};state={decision.State}.");
        }

        private static string FormatKey(QuestRecoveryKey key)
        {
            string value = $"scope={key.Scope}";
            if (key.Scope == QuestRecoveryScope.NpcRelation)
                value += $";npc={key.NpcEntry}";
            else if (key.Scope == QuestRecoveryScope.Endpoint)
                value += $";endpoint={key.Endpoint}";
            else if (key.Scope == QuestRecoveryScope.Objective)
                value += $";objective={key.ObjectiveIndex}";
            return value;
        }

        private static string FormatRetry(DateTime? retryUtc) =>
            retryUtc.HasValue ? retryUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : "context-change";

        private static void AddObjectiveOmission(
            QuestEntry quest,
            QuestWorkStage stage,
            int objectiveIndex,
            string reason,
            DateTime? retryUtc,
            List<string> exclusions) =>
            exclusions.Add(
                $"excluded quest={quest.Id};stage={stage};objective={objectiveIndex};reason={reason};retry={FormatRetry(retryUtc)}.");

        private static void ReportDataOmission(
            QuestRecoveryKey key,
            QuestFailureReason reason,
            string evidence,
            Action<QuestAttemptOutcome> reportDataFailure)
        {
            reportDataFailure?.Invoke(QuestAttemptOutcome.Failure(key, reason, evidence));
        }

        // A recovery cluster limits attempts; it does not authorize its other points.
        // Apply every point's veto before grouping, then rank representatives using only
        // their own evidence. Unprobed alternatives remain unknown and bounded by the
        // existing five-cluster execution policy.
        private static (KeyValuePair<QuestRecoveryKey, IReadOnlyList<SpawnPoint>> Cluster,
            SpawnNavigationAssessment Assessment)[] AssessClusters(
            IEnumerable<SpawnPoint> points,
            Func<SpawnPoint, SpawnNavigationAssessment> assessNavigation) =>
            points
                .OrderBy(point => point.Map)
                .ThenBy(point => point.X)
                .ThenBy(point => point.Y)
                .ThenBy(point => point.Z)
                .Select(point => (Point: point, Assessment: assessNavigation(point)))
                .Where(value => value.Assessment.IsKnownReachable != false
                                && value.Assessment.IsKnownSafe != false)
                .GroupBy(value => EndpointKey(0, QuestRecoveryStage.Navigation, value.Point))
                .OrderBy(group => group.Key.MapId)
                .ThenBy(group => group.Key.Endpoint, StringComparer.Ordinal)
                .Select(group =>
                {
                    var eligible = group
                        .OrderByDescending(value => value.Assessment.IsKnownReachable == true
                                                    && value.Assessment.IsKnownSafe == true)
                        .ThenByDescending(value => value.Assessment.SafetyScore)
                        .ThenBy(value => value.Point.X)
                        .ThenBy(value => value.Point.Y)
                        .ThenBy(value => value.Point.Z)
                        .DistinctBy(value => (value.Point.Map, value.Point.X, value.Point.Y, value.Point.Z))
                        .ToArray();
                    return (
                        Cluster: new KeyValuePair<QuestRecoveryKey, IReadOnlyList<SpawnPoint>>(
                            group.Key, eligible.Select(value => value.Point).ToArray()),
                        Assessment: eligible[0].Assessment);
                })
                .ToArray();

        private static IReadOnlyList<KeyValuePair<QuestRecoveryKey, IReadOnlyList<SpawnPoint>>> Cluster(
            IEnumerable<SpawnPoint> points) =>
            points
                .GroupBy(point => EndpointKey(0, QuestRecoveryStage.Navigation, point))
                .OrderBy(group => group.Key.MapId)
                .ThenBy(group => group.Key.Endpoint, StringComparer.Ordinal)
                .Select(group => new KeyValuePair<QuestRecoveryKey, IReadOnlyList<SpawnPoint>>(
                    group.Key, group
                        .OrderBy(point => point.X)
                        .ThenBy(point => point.Y)
                        .ThenBy(point => point.Z)
                        .DistinctBy(point => (point.Map, point.X, point.Y, point.Z))
                        .ToArray()))
                .ToArray();

        private static IEnumerable<SpawnPoint> GetObjectiveSpawns(QuestObjective objective, QuestDatabase db)
        {
            string key;
            Dictionary<string, List<SpawnPoint>> source;
            if (objective.Type == ObjectiveType.CollectFromGameObject)
            {
                key = objective.GameObjectId.ToString(CultureInfo.InvariantCulture);
                source = db.GameObjectSpawns;
            }
            else
            {
                key = objective.MobId.ToString(CultureInfo.InvariantCulture);
                source = db.CreatureSpawns;
            }
            return source.TryGetValue(key, out List<SpawnPoint> points)
                ? points
                : Enumerable.Empty<SpawnPoint>();
        }

        private static IEnumerable<SpawnPoint> GetRelationSpawns(
            int entry, QuestObjectType type, QuestDatabase db)
        {
            // Entry numbers are unique only within their declared object namespace.
            // Missing or invalid type evidence must not borrow another type's geometry.
            var source = type == QuestObjectType.Creature ? db.CreatureSpawns
                : type == QuestObjectType.GameObject ? db.GameObjectSpawns : null;
            string key = entry.ToString(CultureInfo.InvariantCulture);
            return source != null && source.TryGetValue(key, out List<SpawnPoint> points)
                && points != null ? points : Enumerable.Empty<SpawnPoint>();
        }

        private static bool InRange(SpawnPoint point, QuestSchedulerSnapshot snapshot, int scanThreshold) =>
            point.Map == snapshot.MapId && Distance(point, snapshot) <= scanThreshold;

        private static double Distance(SpawnPoint point, QuestSchedulerSnapshot snapshot)
        {
            double x = point.X - snapshot.X;
            double y = point.Y - snapshot.Y;
            return Math.Sqrt(x * x + y * y);
        }

        private static Func<SpawnPoint, SpawnNavigationAssessment> CreateCachedNavigationAssessment(
            Func<SpawnPoint, SpawnNavigationAssessment> navigationAssessment,
            Func<SpawnPoint, bool> isKnownUnsafe)
        {
            // Exact destinations own live evidence. Coarse cells only share a query
            // budget, never a safety/reachability result, including across floors.
            var cache = new Dictionary<(int Map, double X, double Y, double Z), SpawnNavigationAssessment>();
            var probedCells = new HashSet<QuestRecoveryKey>();
            return point =>
            {
                if (point == null) throw new ArgumentNullException(nameof(point));
                bool? knownSafe = point.IsKnownSafe == false ? false : (bool?)null;
                bool? knownReachable = point.IsKnownReachable == false ? false : (bool?)null;
                bool safetyQueryFailed = false;
                if (isKnownUnsafe != null)
                {
                    try
                    {
                        if (isKnownUnsafe(point))
                            knownSafe = false;
                    }
                    catch (Exception ex) when (ex is not ThreadInterruptedException && ex is not OperationCanceledException)
                    {
                        safetyQueryFailed = true;
                    }
                }

                // Explicit negative evidence is decisive and needs no native path query.
                if (knownSafe == false || knownReachable == false)
                    return new SpawnNavigationAssessment
                    {
                        IsKnownSafe = knownSafe,
                        IsKnownReachable = knownReachable,
                        SafetyScore = point.SafetyScore
                    };

                var locationKey = (point.Map, point.X, point.Y, point.Z);
                if (!cache.TryGetValue(locationKey, out SpawnNavigationAssessment live)
                    && probedCells.Add(EndpointKey(0, QuestRecoveryStage.Navigation, point)))
                {
                    try
                    {
                        live = navigationAssessment?.Invoke(point);
                    }
                    catch (Exception ex) when (ex is not ThreadInterruptedException && ex is not OperationCanceledException)
                    {
                        live = null;
                    }
                    // Cache failures too: no repeated native probe during the same scan.
                    cache[locationKey] = live;
                }

                // Exhausting a cell's query budget leaves other locations unknown.
                // A failed safety query must never erase an already established veto.
                return new SpawnNavigationAssessment
                {
                    IsKnownSafe = live?.IsKnownSafe == false
                        ? false
                        : safetyQueryFailed ? null : live?.IsKnownSafe,
                    IsKnownReachable = live?.IsKnownReachable,
                    SafetyScore = point.SafetyScore + (live?.SafetyScore ?? 0)
                };
            };
        }

        internal static SpawnNavigationAssessment AssessNavigation(
            SpawnPoint point,
            WoWPoint origin,
            Func<WoWPoint, bool> isKnownUnsafe = null)
        {
            if (point == null)
                throw new ArgumentNullException(nameof(point));
            var destination = new WoWPoint((float)point.X, (float)point.Y, (float)point.Z);
            bool unsafePoint;
            try
            {
                unsafePoint = isKnownUnsafe != null
                    ? isKnownUnsafe(destination)
                    : BlackspotManager.IsBlackspotted(destination);
            }
            catch (Exception ex) when (ex is not ThreadInterruptedException && ex is not OperationCanceledException)
            {
                return new SpawnNavigationAssessment();
            }
            if (unsafePoint)
                return new SpawnNavigationAssessment { IsKnownSafe = false };

            NavigationProvider provider = Navigator.NavigationProvider;
            if (provider == null)
                return new SpawnNavigationAssessment { IsKnownSafe = true };
            try
            {
                if (provider is MeshNavigator mesh)
                    return AssessMeshNavigation(mesh.FindPath(origin, destination), origin, destination);
                float? pathDistance = provider.PathDistance(origin, destination);
                if (!pathDistance.HasValue)
                {
                    return new SpawnNavigationAssessment
                    {
                        IsKnownSafe = true,
                        IsKnownReachable = false
                    };
                }

                double detour = Math.Max(0, pathDistance.Value - origin.Distance(destination));
                return new SpawnNavigationAssessment
                {
                    IsKnownSafe = true,
                    IsKnownReachable = true,
                    SafetyScore = (int)Math.Max(-100000, 1000 - Math.Min(101000, Math.Round(detour)))
                };
            }
            catch (Exception ex) when (ex is not ThreadInterruptedException && ex is not OperationCanceledException)
            {
                return new SpawnNavigationAssessment();
            }
        }

        internal static SpawnNavigationAssessment AssessMeshNavigation(
            Tripper.Navigation.PathFindResult path, WoWPoint origin, WoWPoint destination)
        {
            if (path == null || !path.Succeeded || path.Points == null || path.Points.Length == 0)
                return new SpawnNavigationAssessment { IsKnownSafe = true, IsKnownReachable = false };
            // A partial path establishes only that the current mesh query cannot reach the
            // endpoint. Execution can still recover a small disconnected start patch using
            // validated ground movement. Let that bounded attempt establish reachability.
            if (path.IsPartialPath)
                return new SpawnNavigationAssessment { IsKnownSafe = true };

            var start = new System.Numerics.Vector3(origin.X, origin.Y, origin.Z);
            var end = new System.Numerics.Vector3(destination.X, destination.Y, destination.Z);
            double distance = System.Numerics.Vector3.Distance(start, path.Points[0]) +
                System.Numerics.Vector3.Distance(path.Points[path.Points.Length - 1], end);
            for (int index = 1; index < path.Points.Length; index++)
                distance += System.Numerics.Vector3.Distance(path.Points[index - 1], path.Points[index]);
            double detour = Math.Max(0, distance - origin.Distance(destination));
            return new SpawnNavigationAssessment
            {
                IsKnownSafe = true,
                IsKnownReachable = true,
                SafetyScore = (int)Math.Max(-100000, 1000 - Math.Min(101000, Math.Round(detour)))
            };
        }

        private static bool Supported(QuestEntry quest) =>
            quest.Objectives.Count > 0 && quest.Objectives.All(objective => Supported(quest, objective));

        private static bool Supported(QuestEntry quest, QuestObjective objective) =>
            Supported(objective) &&
            // Both TrinityCore 3.3.5 and AzerothCore use SpecialFlags 0x20
            // for cast credit, not a kill. No item/interaction recipe is implied.
            // Keep the imported row intact; unsupported work is reported by its
            // existing objective owner, after satisfied counters are considered.
            (objective.Type != ObjectiveType.KillMob || (quest.SpecialFlags & 0x20) == 0);

        private static bool Supported(QuestObjective objective) =>
            (objective.Type == ObjectiveType.KillMob && objective.MobId > 0) ||
            (objective.Type == ObjectiveType.CollectItem && objective.ItemId > 0 && objective.MobId > 0) ||
            (objective.Type == ObjectiveType.CollectFromGameObject && objective.GameObjectId > 0) ||
            objective.Type == ObjectiveType.TurnInOnly;

        private static bool IsObjectiveComplete(
            QuestObjective objective,
            IReadOnlyList<int> objectiveCounts,
            IReadOnlyDictionary<int, long> carriedItemCounts)
        {
            // Match CollectItemObjective's completion rule for every source of the item.
            // Dataset alternative-source indexes are not live quest counter indexes.
            if (carriedItemCounts != null && objective.ItemId > 0 &&
                (objective.Type == ObjectiveType.CollectItem || objective.Type == ObjectiveType.CollectFromGameObject))
                return objective.CollectCount > 0 &&
                    carriedItemCounts.TryGetValue(objective.ItemId, out long count) && count >= objective.CollectCount;
            if (objectiveCounts == null || objective.Index < 0 || objective.Index >= objectiveCounts.Count)
                return false;
            int required = objective.Type == ObjectiveType.KillMob
                ? objective.KillCount
                : objective.Type == ObjectiveType.CollectItem || objective.Type == ObjectiveType.CollectFromGameObject
                    ? objective.CollectCount
                    : 0;
            return required > 0 && objectiveCounts[objective.Index] >= required;
        }

        private static bool RaceAllowed(int allowableRaces, int raceId) =>
            allowableRaces == 0 || allowableRaces == -1 ||
            (raceId > 0 && (allowableRaces & (1 << (raceId - 1))) != 0);

        private static bool CanRequestPickup(
            QuestEntry quest,
            QuestDatabase db,
            QuestSchedulerSnapshot snapshot,
            int scanThreshold,
            int minimumLevel) =>
            snapshot.PlayerLevel >= quest.MinLevel &&
            (quest.QuestLevel <= 0 ||
             (quest.QuestLevel >= minimumLevel && quest.QuestLevel <= snapshot.PlayerLevel)) &&
            RaceAllowed(quest.AllowableRaces, snapshot.PlayerRaceId) &&
            Supported(quest) &&
            db.QuestGivers
                .Where(giver => giver.QuestId == quest.Id)
                .SelectMany(giver => GetRelationSpawns(giver.GiverId, giver.GiverType, db))
                .Any(point => InRange(point, snapshot, scanThreshold));

        private static bool PositiveExclusiveGroupAvailable(
            QuestEntry quest,
            QuestDatabase db,
            IReadOnlyDictionary<uint, QuestSchedulerAcceptedQuest> accepted,
            HashSet<uint> completed,
            HashSet<int> claimed)
        {
            int group = quest.ExclusiveGroup;
            if (group <= 0)
                return true;
            if (claimed.Contains(group))
                return false;

            return !db.Quests.Any(other =>
                other.Id > 0 &&
                other.Id != quest.Id &&
                other.ExclusiveGroup == group &&
                (accepted.ContainsKey((uint)other.Id) || completed.Contains((uint)other.Id)));
        }

        private static bool PrerequisitesComplete(
            QuestEntry quest,
            IReadOnlyDictionary<uint, QuestEntry> quests,
            HashSet<uint> completed) =>
            BlockingPrerequisiteRoots(quest, quests, completed).Count == 0;

        private static IReadOnlyList<uint> BlockingPrerequisiteRoots(
            QuestEntry quest,
            IReadOnlyDictionary<uint, QuestEntry> quests,
            HashSet<uint> completed)
        {
            var blockers = new List<uint>();
            var seen = new HashSet<uint>();

            // Direct PrevQuestID keeps its own signed 3.3.5 contract. The
            // negative form is handled by the active-parent gate above; the
            // positive form independently requires rewarded history.
            if (quest.PrevQuestID > 0)
            {
                uint direct = (uint)quest.PrevQuestID;
                if (!completed.Contains(direct) && seen.Add(direct))
                    blockers.Add(direct);
            }

            // Pinned TrinityCore 3.3.5 treats DependentPreviousQuests as an
            // ordered OR. A rewarded ordinary/positive-group predecessor
            // satisfies the dependent gate immediately. A rewarded predecessor
            // in a negative ExclusiveGroup switches to each-from-all semantics;
            // a missing member fails that gate immediately rather than falling
            // through to a later alternative.
            foreach (int rawId in quest.PreviousQuestsIds)
            {
                if (rawId <= 0)
                    continue;

                uint id = (uint)rawId;
                if (!completed.Contains(id))
                    continue;

                // Unknown predecessor metadata cannot prove that this completed
                // quest is an ordinary alternative. Keep scanning for another
                // rewarded candidate with known metadata; otherwise fail closed.
                if (!quests.TryGetValue(id, out QuestEntry predecessor))
                    continue;

                if (predecessor.ExclusiveGroup >= 0)
                    return blockers;

                int group = predecessor.ExclusiveGroup;
                uint[] missingGroupMembers = quests.Values
                    .Where(candidate =>
                        candidate.Id > 0 &&
                        candidate.ExclusiveGroup == group &&
                        !completed.Contains((uint)candidate.Id))
                    .Select(candidate => (uint)candidate.Id)
                    .OrderBy(candidateId => candidateId)
                    .ToArray();

                if (missingGroupMembers.Length == 0)
                    return blockers;

                foreach (uint missing in missingGroupMembers)
                    if (seen.Add(missing))
                        blockers.Add(missing);

                return blockers;
            }

            // No known rewarded alternative satisfied the dependent gate.
            // Preserve source order and keep unknown completed candidates as
            // blockers: completion without predecessor metadata is not
            // permission to assume non-negative group semantics.
            foreach (int rawId in quest.PreviousQuestsIds)
            {
                if (rawId <= 0)
                    continue;
                uint id = (uint)rawId;
                if (seen.Add(id))
                    blockers.Add(id);
            }

            return blockers;
        }

        private static uint FindAcceptedIncompleteAncestor(
            QuestEntry quest,
            IReadOnlyDictionary<uint, QuestEntry> quests,
            IReadOnlyDictionary<uint, QuestSchedulerAcceptedQuest> accepted,
            HashSet<uint> completed)
        {
            var pending = new Queue<uint>();
            var seen = new HashSet<uint>();
            foreach (uint id in BlockingPrerequisiteRoots(quest, quests, completed))
                pending.Enqueue(id);

            while (pending.Count > 0)
            {
                uint id = pending.Dequeue();
                if (!seen.Add(id) || completed.Contains(id))
                    continue;
                if (accepted.TryGetValue(id, out var live) && !live.IsCompleted)
                    return id;
                if (quests.TryGetValue(id, out QuestEntry ancestor))
                {
                    foreach (uint previous in BlockingPrerequisiteRoots(ancestor, quests, completed))
                        pending.Enqueue(previous);
                }
            }
            return 0;
        }

        private static IReadOnlyList<int> ReadObjectiveCounts(PlayerQuest quest)
        {
            try
            {
                if (quest.GetData(out QuestDescriptorData data) && data.ObjectivesDone != null)
                    return data.ObjectivesDone.Select(value => (int)value).ToArray();
            }
            catch (Exception ex)
            {
                Logging.WriteDiagnostic($"[WholesomeAQ] Objective count capture failed for quest {quest.Id}: {ex.Message}");
            }
            return Array.Empty<int>();
        }

        public void Reset()
        {
            _publishedWork = null;
            _scanThreshold = _settings.ScanStartDistance;
            ActiveQuestIds = null;
            CurrentProfilePath = null;
            LastSchedule = new QuestScheduleResult();
            LastStatus = null;
            LastQuestCount = 0;
            _lastScan = DateTime.MinValue;
            _lastRecoveryContext = null;
            _lastActivation = null;
            _lastActivationKey = null;
            _rebuildRequested = false;
        }

        private sealed class Relation
        {
            public Relation(int entry, QuestGiverEntry giver = null, QuestEnderEntry ender = null)
            {
                Entry = entry;
                Giver = giver;
                Ender = ender;
            }

            public int Entry { get; }
            public QuestObjectType Type => Giver?.GiverType ?? Ender?.EnderType ?? (QuestObjectType)(-1);
            public QuestGiverEntry Giver { get; }
            public QuestEnderEntry Ender { get; }
            public string DisplayName => Giver?.GiverName ?? Ender?.EnderName ?? "";
        }
    }
}

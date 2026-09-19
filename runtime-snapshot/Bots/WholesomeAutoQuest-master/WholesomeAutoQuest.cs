using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Styx;
using Styx.Logic.POI;
using Styx.Helpers;
using Styx.Logic.AreaManagement;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Common;
using Styx.Logic.Inventory;
using Styx.Logic.Inventory.Frames.Merchant;
using Styx.Logic.Inventory.Frames.Trainer;
using Styx.Logic.Pathing;
using Styx.Logic.Profiles;
using Styx.Logic.Questing;
using Styx.Logic.Questing.Recovery;
using Styx.Combat.CombatRoutine;
using Styx.WoWInternals;
using Bots.Quest.QuestOrder;
using TreeSharp;
using Action = TreeSharp.Action;

#nullable disable

namespace WholesomeAQ
{
    internal sealed class WholesomeRecoveryConfigurationResult
    {
        public bool IsAvailable { get; init; }
        public bool DataReady { get; init; }
        public string DatasetFingerprint { get; init; } = "unknown";
        public string NavigationFingerprint { get; init; } = "unknown";
        public string Status { get; init; } = "";
    }

    internal readonly struct RefreshLease
    {
        public RefreshLease(long epoch, long runId)
        {
            Epoch = epoch;
            RunId = runId;
        }

        public long Epoch { get; }
        public long RunId { get; }
    }

    internal sealed class RefreshGate
    {
        private const int Idle = 0;
        private const int Pending = 1;
        private const int Running = 2;
        private const int Stopped = 3;
        private readonly object _sync = new();
        private int _state;
        private bool _rerun;
        private long _epoch;
        private long _nextRunId;

        internal object SynchronizationRoot => _sync;

        public bool TryRequest()
        {
            lock (_sync)
            {
                if (_state == Idle)
                {
                    _state = Pending;
                    return true;
                }
                if (_state == Running && !_rerun)
                {
                    _rerun = true;
                    return true;
                }
                return false;
            }
        }

        public bool TryRequest(RefreshLease lease)
        {
            lock (_sync)
            {
                if (!Matches(lease))
                    return false;
                if (_state == Idle)
                {
                    _state = Pending;
                    return true;
                }
                if (_state == Running && !_rerun)
                {
                    _rerun = true;
                    return true;
                }
                return false;
            }
        }

        public bool IsCurrent(RefreshLease lease)
        {
            lock (_sync)
                return Matches(lease) && _state != Stopped;
        }

        public bool TryApply(RefreshLease lease, System.Action apply)
        {
            if (apply == null)
                return false;
            lock (_sync)
            {
                if (_state != Running || !Matches(lease))
                    return false;
                apply();
                return true;
            }
        }

        public RefreshLease? Begin()
        {
            lock (_sync)
            {
                if (_state != Pending)
                    return null;
                _state = Running;
                return new RefreshLease(_epoch, ++_nextRunId);
            }
        }

        public void Complete(RefreshLease lease)
        {
            lock (_sync)
            {
                if (_state != Running || lease.Epoch != _epoch || lease.RunId != _nextRunId)
                    return;
                _state = _rerun ? Pending : Idle;
                _rerun = false;
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _state = Stopped;
                _rerun = false;
                _epoch++;
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                _state = Idle;
                _rerun = false;
                _epoch++;
            }
        }

        private bool Matches(RefreshLease lease) =>
            lease.Epoch == _epoch && lease.RunId == _nextRunId;
    }

    internal sealed class WholesomeLifecycleGate
    {
        private readonly RefreshGate _refreshGate;
        private readonly object _sync;
        private bool _started;
        private bool _subscribed;
        private bool _transitionInProgress;
        private long _epoch;

        public WholesomeLifecycleGate(RefreshGate refreshGate)
        {
            _refreshGate = refreshGate ?? throw new ArgumentNullException(nameof(refreshGate));
            _sync = _refreshGate.SynchronizationRoot;
        }

        public bool IsStopped
        {
            get
            {
                lock (_sync)
                    return !_started;
            }
        }

        public bool Start(System.Action subscribe, System.Action unsubscribe)
        {
            long epoch;
            lock (_sync)
            {
                while (_transitionInProgress)
                    Monitor.Wait(_sync);
                if (_started)
                    return false;
                _started = true;
                epoch = ++_epoch;
                _transitionInProgress = true;
                _refreshGate.Start();
            }

            bool subscribed = false;
            bool retained = false;
            try
            {
                subscribe?.Invoke();
                subscribed = true;
            }
            finally
            {
                lock (_sync)
                {
                    retained = subscribed && _started && _epoch == epoch;
                    if (retained)
                    {
                        _subscribed = true;
                    }
                    else if (_started && _epoch == epoch)
                    {
                        _started = false;
                        _epoch++;
                        _refreshGate.Stop();
                    }
                }

                try
                {
                    if (!retained)
                        unsubscribe?.Invoke();
                }
                finally
                {
                    lock (_sync)
                    {
                        _transitionInProgress = false;
                        Monitor.PulseAll(_sync);
                    }
                }
            }
            return retained;
        }

        public bool Stop(System.Action unsubscribe)
        {
            bool removeSubscription;
            lock (_sync)
            {
                if (!_started)
                    return false;
                _started = false;
                _epoch++;
                _refreshGate.Stop();
                if (_transitionInProgress)
                    return true;
                _transitionInProgress = true;
                removeSubscription = _subscribed;
                _subscribed = false;
            }

            try
            {
                if (removeSubscription)
                    unsubscribe?.Invoke();
            }
            finally
            {
                lock (_sync)
                {
                    _transitionInProgress = false;
                    Monitor.PulseAll(_sync);
                }
            }
            return true;
        }
    }

    internal sealed class QuestWorkSample
    {
        public QuestRecoveryKey Key { get; init; }
        public long AttemptGeneration { get; init; }
        public IReadOnlyList<int> ObjectiveCounts { get; init; } = Array.Empty<int>();
        public QuestRecoveryKey ClusterKey { get; init; }
        public IReadOnlyList<QuestRecoveryKey> KnownEndpointKeys { get; init; } = Array.Empty<QuestRecoveryKey>();
        public bool IsActiveWork { get; init; }
        public bool EndpointPathFailed { get; init; }
        public RouteFailureReason NavigationFailure { get; init; }
        public bool AllHotspotsUnavailable { get; init; }
        public bool DeathAttributable { get; init; }
        public bool CombatOwnedByQuest { get; init; }
    }

    internal sealed class QuestProgressUpdate
    {
        public bool MadeProgress { get; init; }
        public bool RequestAlternateCluster { get; init; }
        public bool NavigationDeferred { get; init; }
        public IReadOnlyList<int> ObjectiveCounts { get; init; } = Array.Empty<int>();
        public IReadOnlyList<QuestAttemptOutcome> Outcomes { get; init; } = Array.Empty<QuestAttemptOutcome>();
    }

    internal sealed class WholesomePickupMonitor
    {
        private QuestRecoveryKey _key;
        private long _generation;
        private long _lastInteractionCycle;
        private int _activeCycles;
        private bool _failureReported;

        public QuestAttemptOutcome Observe(
            QuestRecoveryKey key,
            long generation,
            long interactionCycle,
            QuestAttemptOutcome outcome,
            bool active)
        {
            if (key == null || generation <= 0)
                return null;
            if (_key == null || !_key.Equals(key) || _generation != generation)
            {
                Reset();
                _key = key;
                _generation = generation;
            }
            if (!active || outcome == null || interactionCycle <= 0 ||
                outcome.InteractionCycleId != interactionCycle ||
                interactionCycle <= _lastInteractionCycle || !outcome.Key.Equals(key) ||
                outcome.Reason is not (QuestFailureReason.PickupWrongQuestShown or
                    QuestFailureReason.PickupTargetNotOffered))
                return null;

            _lastInteractionCycle = interactionCycle;
            _activeCycles++;
            if (!outcome.IsFailureEpisode)
                return outcome;
            if (_activeCycles < 3 || _failureReported)
                return null;
            _failureReported = true;
            return outcome;
        }

        public void Reset()
        {
            _key = null;
            _generation = 0;
            _lastInteractionCycle = 0;
            _activeCycles = 0;
            _failureReported = false;
        }
    }

    internal sealed class WholesomeDeathMonitor
    {
        private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(10);
        private readonly IQuestRecoveryClock _clock;
        private object _owner;
        private QuestRecoveryKey _key;
        private long _generation;
        private QuestWorkSample _sample;
        private DateTime _capturedUtc;

        public WholesomeDeathMonitor(IQuestRecoveryClock clock = null)
        {
            _clock = clock ?? new SystemClock();
        }

        public void Capture(
            object owner,
            QuestRecoveryKey key,
            long generation,
            QuestWorkSample sample)
        {
            if (owner == null || key == null || generation <= 0 || sample == null ||
                !sample.IsActiveWork || !sample.CombatOwnedByQuest ||
                sample.AttemptGeneration != generation || !key.Equals(sample.Key))
            {
                Reset();
                return;
            }
            _owner = owner;
            _key = key;
            _generation = generation;
            _sample = sample;
            _capturedUtc = _clock.UtcNow;
        }

        public bool TryRecordDeath(
            object owner,
            QuestRecoveryKey key,
            long generation,
            out QuestWorkSample death)
        {
            bool exact = _sample != null && ReferenceEquals(owner, _owner) &&
                key != null && key.Equals(_key) && generation == _generation &&
                _clock.UtcNow - _capturedUtc >= TimeSpan.Zero &&
                _clock.UtcNow - _capturedUtc <= MaximumSnapshotAge;
            QuestWorkSample captured = _sample;
            Reset();
            if (!exact)
            {
                death = null;
                return false;
            }
            death = new QuestWorkSample
            {
                Key = captured.Key,
                AttemptGeneration = captured.AttemptGeneration,
                ObjectiveCounts = captured.ObjectiveCounts,
                ClusterKey = captured.ClusterKey,
                KnownEndpointKeys = captured.KnownEndpointKeys,
                IsActiveWork = captured.IsActiveWork,
                CombatOwnedByQuest = captured.CombatOwnedByQuest,
                DeathAttributable = true
            };
            return true;
        }

        public void Reset()
        {
            _owner = null;
            _key = null;
            _generation = 0;
            _sample = null;
            _capturedUtc = default;
        }

        private sealed class SystemClock : IQuestRecoveryClock
        {
            public DateTime UtcNow => DateTime.UtcNow;
        }
    }

    internal sealed class WholesomeProgressMonitor
    {
        private static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan DeathWindow = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(10);
        private readonly IQuestRecoveryClock _clock;
        private readonly HashSet<QuestRecoveryKey> _attemptedClusters = new();
        private readonly HashSet<QuestRecoveryKey> _failedEndpoints = new();
        private readonly List<DateTime> _attributableDeaths = new();
        private QuestRecoveryKey _key;
        private long _attemptGeneration;
        private IReadOnlyList<int> _counts = Array.Empty<int>();
        private DateTime _lastSampleUtc;
        private TimeSpan _activeWork;
        private bool _previousSampleActive;
        private bool _stageEpisodeReported;
        private bool _deathEpisodeReported;
        private bool _alternateRequested;

        public WholesomeProgressMonitor(IQuestRecoveryClock clock = null)
        {
            _clock = clock ?? new SystemClock();
        }

        public QuestProgressUpdate Sample(QuestWorkSample sample)
        {
            if (sample?.Key == null)
                return new QuestProgressUpdate();

            DateTime now = _clock.UtcNow;
            EnsureAttempt(sample.Key, sample.AttemptGeneration, sample.ObjectiveCounts, now);
            if (HasProgress(sample.ObjectiveCounts))
            {
                _counts = sample.ObjectiveCounts.ToArray();
                ResetEpisodeState(now);
                return new QuestProgressUpdate
                {
                    MadeProgress = true,
                    ObjectiveCounts = _counts
                };
            }

            if (sample.IsActiveWork && sample.EndpointPathFailed &&
                WholesomeAutoQuest.IsUnresolvedNavigationFailure(sample.NavigationFailure))
            {
                // A search prefix/resource limit is not a failed quest endpoint.
                // The consumer releases this exact stage through the core deferral owner.
                _lastSampleUtc = now;
                _previousSampleActive = false;
                return new QuestProgressUpdate { NavigationDeferred = true };
            }

            TimeSpan sampleGap = now - _lastSampleUtc;
            if (_previousSampleActive && sample.IsActiveWork &&
                sampleGap > TimeSpan.Zero && sampleGap <= MaximumSampleGap)
                _activeWork += sampleGap;
            _lastSampleUtc = now;
            _previousSampleActive = sample.IsActiveWork;
            if (sample.IsActiveWork && sample.ClusterKey != null)
                _attemptedClusters.Add(sample.ClusterKey);

            var outcomes = new List<QuestAttemptOutcome>();
            bool requestAlternate = false;
            if (sample.IsActiveWork && !_alternateRequested &&
                _activeWork >= TimeSpan.FromMinutes(4))
            {
                _alternateRequested = true;
                requestAlternate = true;
            }
            if (sample.IsActiveWork && sample.EndpointPathFailed &&
                sample.ClusterKey?.Scope == QuestRecoveryScope.Endpoint)
            {
                bool firstEndpointFailure = _failedEndpoints.Add(sample.ClusterKey);
                outcomes.Add(CreateOutcome(
                    sample.ClusterKey,
                    QuestFailureReason.PathGenerationFailed,
                    firstEndpointFailure,
                    "The active generated endpoint has no navigation path."));

                QuestRecoveryKey[] known = sample.KnownEndpointKeys
                    .Where(endpoint => endpoint != null && endpoint.Scope == QuestRecoveryScope.Endpoint)
                    .Distinct()
                    .ToArray();
                if (known.Length > 0 && known.All(_failedEndpoints.Contains))
                {
                    outcomes.Add(CreateStageOutcome(
                        sample.Key,
                        QuestFailureReason.NoNavigableHotspot,
                        "All active generated-area hotspot clusters have failed navigation."));
                }
                else
                {
                    // Keep the stage ownership while trying its next endpoint. Rebuilding here
                    // would make the scheduler reject this same stage as already Attempting.
                    requestAlternate = true;
                }
            }

            if (sample.IsActiveWork && sample.AllHotspotsUnavailable)
            {
                outcomes.Add(CreateStageOutcome(
                    sample.Key,
                    QuestFailureReason.NoNavigableHotspot,
                    "The active generated area contains no available hotspots."));
            }
            else if (sample.IsActiveWork &&
                     _activeWork >= NoProgressLimit)
            {
                outcomes.Add(CreateStageOutcome(
                    sample.Key,
                    QuestFailureReason.NoObjectiveProgress,
                    $"No objective counter or item progress after {_activeWork.TotalMinutes:F1} active minutes across {_attemptedClusters.Count} clusters."));
            }

            return new QuestProgressUpdate
            {
                RequestAlternateCluster = requestAlternate,
                Outcomes = outcomes
            };
        }

        public QuestProgressUpdate RecordDeath(QuestWorkSample sample)
        {
            if (sample?.Key == null || !sample.DeathAttributable)
                return new QuestProgressUpdate();

            DateTime now = _clock.UtcNow;
            EnsureAttempt(sample.Key, sample.AttemptGeneration, sample.ObjectiveCounts, now);
            _attributableDeaths.RemoveAll(value => now - value > DeathWindow);
            _attributableDeaths.Add(now);
            if (_attributableDeaths.Count < 3)
                return new QuestProgressUpdate();

            bool failureEpisode = !_deathEpisodeReported;
            _deathEpisodeReported = true;
            return new QuestProgressUpdate
            {
                Outcomes = new[]
                {
                    CreateOutcome(
                        sample.Key,
                        QuestFailureReason.RepeatedDeaths,
                        failureEpisode,
                        $"{_attributableDeaths.Count} attributable deaths occurred inside 15 minutes without objective progress.")
                }
            };
        }

        public void Reset()
        {
            _key = null;
            _attemptGeneration = 0;
            _counts = Array.Empty<int>();
            _lastSampleUtc = default;
            ResetEpisodeState(default);
        }

        private void EnsureAttempt(
            QuestRecoveryKey key,
            long attemptGeneration,
            IReadOnlyList<int> counts,
            DateTime now)
        {
            if (_key != null && _key.Equals(key) && _attemptGeneration == attemptGeneration)
                return;
            _key = key;
            _attemptGeneration = attemptGeneration;
            _counts = (counts ?? Array.Empty<int>()).ToArray();
            ResetEpisodeState(now);
        }

        private bool HasProgress(IReadOnlyList<int> counts) =>
            (counts ?? Array.Empty<int>())
                .Select((count, index) => count > (index < _counts.Count ? _counts[index] : 0))
                .Any(progressed => progressed);

        private void ResetEpisodeState(DateTime now)
        {
            _activeWork = TimeSpan.Zero;
            _lastSampleUtc = now;
            _previousSampleActive = false;
            _attemptedClusters.Clear();
            _failedEndpoints.Clear();
            _attributableDeaths.Clear();
            _stageEpisodeReported = false;
            _deathEpisodeReported = false;
            _alternateRequested = false;
        }

        private QuestAttemptOutcome CreateStageOutcome(
            QuestRecoveryKey key,
            QuestFailureReason reason,
            string evidence)
        {
            bool failureEpisode = !_stageEpisodeReported;
            _stageEpisodeReported = true;
            return CreateOutcome(key, reason, failureEpisode, evidence);
        }

        private static QuestAttemptOutcome CreateOutcome(
            QuestRecoveryKey key,
            QuestFailureReason reason,
            bool failureEpisode,
            string evidence) => new()
        {
            Key = key,
            Kind = failureEpisode ? QuestAttemptOutcomeKind.Failure : QuestAttemptOutcomeKind.Observation,
            Reason = reason,
            IsFailureEpisode = failureEpisode,
            Evidence = evidence
        };

        private sealed class SystemClock : IQuestRecoveryClock
        {
            public DateTime UtcNow => DateTime.UtcNow;
        }
    }

    internal sealed class WholesomeAttemptOwnership
    {
        private readonly object _sync = new();
        private object _owner;
        private QuestRecoveryKey _key;
        private long _generation;

        public void Begin(object owner, QuestRecoveryKey key, QuestRecoveryDecision decision)
        {
            if (owner == null || key == null || decision == null ||
                !decision.MayAttempt || decision.State != QuestRecoveryState.Attempting ||
                decision.AttemptGeneration <= 0)
                return;
            lock (_sync)
            {
                _owner = owner;
                _key = key;
                _generation = decision.AttemptGeneration;
            }
        }

        public bool TryComplete(object owner, string evidence, out QuestAttemptOutcome success)
        {
            lock (_sync)
            {
                if (_owner == null || !ReferenceEquals(owner, _owner) || _key == null || _generation <= 0)
                {
                    success = null;
                    return false;
                }
                success = QuestAttemptOutcome.Success(_key, _generation, evidence);
                ClearCore();
                return true;
            }
        }

        public bool TryGet(object owner, out QuestRecoveryKey key)
        {
            return TryGet(owner, out key, out _);
        }

        public bool TryGet(object owner, out QuestRecoveryKey key, out long generation)
        {
            lock (_sync)
            {
                if (_owner == null || !ReferenceEquals(owner, _owner) || _key == null)
                {
                    key = null;
                    generation = 0;
                    return false;
                }
                key = _key;
                generation = _generation;
                return true;
            }
        }

        public bool TryBind(object owner, QuestAttemptOutcome outcome, out QuestAttemptOutcome bound)
        {
            if (outcome == null)
            {
                bound = null;
                return false;
            }
            lock (_sync)
            {
                if (_owner == null || !ReferenceEquals(owner, _owner) || _key == null || _generation <= 0)
                {
                    bound = null;
                    return false;
                }
                bound = new QuestAttemptOutcome
                {
                    Key = outcome.Key,
                    AttemptKey = _key,
                    AttemptGeneration = _generation,
                    Kind = outcome.Kind,
                    Reason = outcome.Reason,
                    IsFailureEpisode = outcome.IsFailureEpisode,
                    Evidence = outcome.Evidence,
                    ObservedQuestId = outcome.ObservedQuestId,
                    OfferedQuestIds = outcome.OfferedQuestIds,
                    ObjectiveCounts = outcome.ObjectiveCounts,
                    InteractionCycleId = outcome.InteractionCycleId
                };
                return true;
            }
        }

        public bool Release(object owner, long generation)
        {
            lock (_sync)
            {
                if (_owner == null || !ReferenceEquals(owner, _owner) || generation != _generation)
                    return false;
                ClearCore();
                return true;
            }
        }

        public bool TryGet(out object owner, out QuestRecoveryKey key)
        {
            return TryGet(out owner, out key, out _);
        }

        public bool TryGet(out object owner, out QuestRecoveryKey key, out long generation)
        {
            lock (_sync)
            {
                owner = _owner;
                key = _key;
                generation = _generation;
                return owner != null && key != null && generation > 0;
            }
        }

        public bool TryGetChanged(
            object currentOwner,
            out object priorOwner,
            out QuestRecoveryKey key,
            out long generation)
        {
            lock (_sync)
            {
                priorOwner = _owner;
                key = _key;
                generation = _generation;
                return priorOwner != null && !ReferenceEquals(priorOwner, currentOwner) &&
                    key != null && generation > 0;
            }
        }

        public void Clear(object owner = null)
        {
            lock (_sync)
            {
                if (owner == null || ReferenceEquals(owner, _owner))
                    ClearCore();
            }
        }

        private void ClearCore()
        {
            _owner = null;
            _key = null;
            _generation = 0;
        }
    }

    // TreeSharp.Decorator checks its predicate only when Execute starts. This gate must
    // also revoke an already-running child when recovery removes the selected work.
    internal sealed class WholesomeExecutionGate : Decorator
    {
        public WholesomeExecutionGate(CanRunDecoratorDelegate allow, Composite child) : base(allow, child) { }

        public override RunStatus Tick(object context)
        {
            if (!CanRun(context))
            {
                Stop(context);
                LastStatus = RunStatus.Failure;
                return RunStatus.Failure;
            }
            return base.Tick(context);
        }
    }

    public class WholesomeAutoQuest : Bots.Quest.QuestBot
    {
        private DataLoader _dataLoader;
        private VendorDataLoader _vendorLoader;
        private QuestScheduler _scheduler;
        private ProfileBuilder _profileBuilder;
        private WholesomeAQSettings _settings = new WholesomeAQSettings();
        private bool _initialized;
        private bool _dataReady;
        private bool _vendorDataReady;
        private DateTime _lastScanTime = DateTime.MinValue;
        private string _profilePath;
        private string _vendorBlacklistPath;
        private readonly RefreshGate _refreshGate = new();
        private readonly WholesomeLifecycleGate _lifecycle;
        private readonly WholesomeProgressMonitor _progressMonitor = new();
        private Func<Styx.Logic.AreaManagement.GrindArea> _activeGrindArea = () => StyxWoW.AreaManager.CurrentGrindArea;
        private readonly WholesomePickupMonitor _pickupMonitor = new();
        private readonly WholesomeDeathMonitor _deathMonitor = new();
        private readonly WholesomeAttemptOwnership _attemptOwnership = new();
        private Composite _root;
        private bool _stopped = true;
        private double _lastX, _lastY, _lastZ;
        private double _anchorX, _anchorY, _anchorZ;
        private bool _anchorSet;
        private DateTime _lastMovedTime = DateTime.UtcNow;
        private readonly Guid _vendorTravelOwner = Guid.NewGuid();
        private DateTime _lastVendorObservationUtc = DateTime.MinValue;
        private uint _lastVendorMap;
        private uint _lastVendorEntry;
        private WoWPoint _lastVendorDestination;
        private HashSet<int> _lastReadyQuestIds;
        private QuestLogSnapshot _lastReadyQuestSnapshot;
        private bool _wasStuck;
        private bool _stuckLogged;
        private DateTime _nextProgressSampleUtc = DateTime.MinValue;
        private volatile bool _restingPaused;
        private DateTime _restStartTime = DateTime.MinValue;
        private DateTime _restTimeoutEnd = DateTime.MinValue;
        private DateTime _lastFacingLog = DateTime.MinValue;

        public override string Name => "Wholesome Auto Quest";
        public override bool RequiresProfile => false;

        public WholesomeAutoQuest()
        {
            _lifecycle = new WholesomeLifecycleGate(_refreshGate);
        }

        public override Composite Root => _root ??= new WholesomeExecutionGate(
            _ => !_stopped && WholesomeRestPolicy.ShouldRunQuestRoot(_restingPaused),
            new Bots.Quest.PublishedQuestRoot(() => _scheduler?.CaptureExecutionPermission()));

        internal static bool ShouldExecuteQuestRoot(bool stopped, QuestScheduleResult schedule)
        {
            if (stopped)
                return false;
            if (schedule == null)
                return true;
            return schedule.Selected.Count > 0 || schedule.FallbackMode == QuestFallbackMode.ValidatedGrind;
        }

        public override Form ConfigurationForm
        {
            get
            {
                WholesomeRecoveryConfigurationResult recovery = EnsureRecoveryConfigured();
                return new SettingsForm(_settings, Log,
                    forceStop: () => TreeRoot.Stop(),
                    resume: () => RequestRefresh("User requested a scheduler refresh."),
                    recoveryChanged: OnRecoverySettingsChanged,
                    recoveryAvailable: recovery.IsAvailable,
                    recoveryUnavailableReason: recovery.Status);
            }
        }

        public override void Start()
        {
            if (!_stopped)
                return;
            _profilePath = FindProfilePath();
            _profileBuilder = new ProfileBuilder(_profilePath);
            WholesomeRecoveryConfigurationResult recovery = EnsureRecoveryConfigured();
            _dataReady = recovery.DataReady;
            _vendorLoader = new VendorDataLoader();
            _vendorDataReady = _vendorLoader.Load();
            _vendorBlacklistPath = Path.Combine(Path.GetDirectoryName(_profilePath), "vendor_blacklist.txt");
            LoadVendorBlacklist();
            _scheduler = new QuestScheduler(_dataLoader, _profileBuilder, _settings);
            _initialized = true;
            _lastScanTime = DateTime.MinValue;
            ResetRecoveryLifecycleState();
            _stopped = false;
            _lifecycle.Start(
                () => BotEvents.Player.OnPlayerDied += OnPlayerDied,
                () => BotEvents.Player.OnPlayerDied -= OnPlayerDied);

            try
            {
                base.Start();
            }
            catch
            {
                _stopped = true;
                _lifecycle.Stop(() => BotEvents.Player.OnPlayerDied -= OnPlayerDied);
                throw;
            }

            if (StyxWoW.Me != null)
            {
                bool ranged = StyxWoW.Me.Class == WoWClass.Hunter
                    || StyxWoW.Me.Class == WoWClass.Mage
                    || StyxWoW.Me.Class == WoWClass.Priest
                    || StyxWoW.Me.Class == WoWClass.Warlock;
                int dist = ranged ? 30 : 24;
                CharacterSettings.Instance.PullDistance = dist;
                Log($"Class {StyxWoW.Me.Class} → PullDistance set to {dist}");
            }

            RequestRefresh("Initial scheduler build.");
            RunPendingRefresh();

            Log(_dataReady ? "Started with quest data loaded." : "Started. No quest data loaded.");
        }

        private WholesomeRecoveryConfigurationResult EnsureRecoveryConfigured()
        {
            return EnsureRecoveryConfigured(
                QuestRecoveryManager.Instance,
                _dataLoader ?? new DataLoader(),
                QuestScheduler.NavigationProviderFingerprint,
                CreateCurrentRecoveryEnvironment,
                Log);
        }

        internal WholesomeRecoveryConfigurationResult EnsureRecoveryConfigured(
            QuestRecoveryManager manager,
            DataLoader loader,
            Func<string> navigationFingerprint,
            Func<string, string, QuestRecoveryEnvironment> environmentFactory,
            System.Action<string> log)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            if (loader == null)
                throw new ArgumentNullException(nameof(loader));

            _dataLoader ??= loader;
            bool dataReady = false;
            string dataset = _dataLoader.ExecutionFingerprint;
            try
            {
                dataReady = _dataLoader.Load() != null;
                dataset = _dataLoader.ExecutionFingerprint;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Quest recovery data fingerprint initialization failed: {ex.Message}");
            }

            string navigation = "unknown";
            try
            {
                navigation = navigationFingerprint?.Invoke() ?? "unknown";
            }
            catch (Exception ex)
            {
                log?.Invoke($"Quest recovery navigation fingerprint initialization failed: {ex.Message}");
            }

            QuestRecoveryEnvironment environment;
            try
            {
                environment = environmentFactory?.Invoke(dataset, navigation);
            }
            catch (Exception ex)
            {
                string status = $"Quest recovery configuration failed: {ex.Message}";
                log?.Invoke(status);
                return new WholesomeRecoveryConfigurationResult
                {
                    DataReady = dataReady,
                    DatasetFingerprint = dataset,
                    NavigationFingerprint = navigation,
                    Status = status
                };
            }

            if (environment == null)
            {
                const string status = "Quest recovery is unavailable until a character and realm are loaded.";
                log?.Invoke(status);
                return new WholesomeRecoveryConfigurationResult
                {
                    DataReady = dataReady,
                    DatasetFingerprint = dataset,
                    NavigationFingerprint = navigation,
                    Status = status
                };
            }

            try
            {
                manager.Configure(environment);
                return new WholesomeRecoveryConfigurationResult
                {
                    IsAvailable = true,
                    DataReady = dataReady,
                    DatasetFingerprint = dataset,
                    NavigationFingerprint = navigation,
                    Status = "Quest recovery state loaded."
                };
            }
            catch (Exception ex)
            {
                string status = $"Quest recovery configuration failed: {ex.Message}";
                log?.Invoke(status);
                return new WholesomeRecoveryConfigurationResult
                {
                    DataReady = dataReady,
                    DatasetFingerprint = dataset,
                    NavigationFingerprint = navigation,
                    Status = status
                };
            }
        }

        private static QuestRecoveryEnvironment CreateCurrentRecoveryEnvironment(
            string dataset,
            string navigation)
        {
            var player = StyxWoW.Me;
            if (player == null || string.IsNullOrWhiteSpace(player.Name) ||
                string.IsNullOrWhiteSpace(player.RealmName))
                return null;

            string coreVersion = typeof(QuestRecoveryManager).Assembly
                .GetName().Version?.ToString() ?? "unknown";
            return new QuestRecoveryEnvironment(
                Settings.SettingsDirectory,
                player.Name,
                player.RealmName,
                dataset,
                coreVersion,
                navigation);
        }

        public override void Stop()
        {
            _stopped = true;
            _lifecycle.Stop(() => BotEvents.Player.OnPlayerDied -= OnPlayerDied);
            try
            {
                AbandonOwnedAttempt(
                    _attemptOwnership,
                    (key, generation) => QuestRecoveryManager.Instance.AbandonAttempt(key, generation));
            }
            catch (Exception ex)
            {
                Log($"Recovery attempt abandon failed during Stop: {ex.Message}");
            }
            ResetRecoveryLifecycleState();
            _scheduler?.Reset();
            try
            {
                QuestRecoveryManager.Instance.Flush();
            }
            catch (Exception ex)
            {
                Log($"Recovery state flush failed during Stop: {ex.Message}");
            }
            base.Stop();
        }

        internal static bool AbandonOwnedAttempt(
            WholesomeAttemptOwnership ownership,
            Func<QuestRecoveryKey, long, bool> abandon)
        {
            if (ownership == null || abandon == null ||
                !ownership.TryGet(out _, out QuestRecoveryKey key, out long generation))
                return false;
            return abandon(key, generation);
        }

        internal static bool FinalizeChangedOwner(
            WholesomeAttemptOwnership ownership,
            object currentOwner,
            Func<QuestRecoveryKey, long, bool> abandon)
        {
            if (ownership == null || abandon == null ||
                !ownership.TryGetChanged(
                    currentOwner, out object priorOwner,
                    out QuestRecoveryKey key, out long generation))
                return false;
            bool finalized = abandon(key, generation);
            ownership.Release(priorOwner, generation);
            return finalized;
        }

        internal void ResetRecoveryLifecycleState()
        {
            _lastReadyQuestIds = null;
            _lastReadyQuestSnapshot = null;
            _progressMonitor.Reset();
            _pickupMonitor.Reset();
            _deathMonitor.Reset();
            _attemptOwnership.Clear();
            _nextProgressSampleUtc = DateTime.MinValue;
            VendorSafetyPolicy.Travel.Reset(_vendorTravelOwner);
            _lastVendorObservationUtc = DateTime.MinValue;
            _lastMovedTime = DateTime.UtcNow;
            _wasStuck = false;
            _stuckLogged = false;
        }

        private bool DoScan(QuestScheduler scheduler, RefreshLease lease)
        {
            try
            {
                bool refreshed = false;
                bool runAgain = RunLeaseFencedRefresh(
                    _refreshGate,
                    lease,
                    () =>
                    {
                        if (_stopped || !_initialized || !_dataReady || scheduler == null ||
                            !StyxWoW.IsInWorld || StyxWoW.Me == null)
                        {
                            // This callback is fenced by the current refresh lease. Do not
                            // leave a prior plan running when the scan itself is skipped.
                            _refreshGate.TryApply(lease, () =>
                                scheduler?.InvalidatePublishedWork("Quest observations unavailable; waiting for the current world and data."));
                            return false;
                        }
                        // Vendor discovery is part of this lease's fallible observation phase.
                        // Revoke before it runs; a late catch could revoke a replacement lease.
                        if (!_refreshGate.TryApply(lease, () =>
                        {
                            scheduler.InvalidatePublishedWork("Refreshing quest observations; waiting for vendor and quest data.");
                            _lastScanTime = DateTime.Now;
                        }))
                            return false;
                        if (_vendorDataReady)
                        {
                            var bl = _settings.BlacklistedVendors;
                            var vendors = _vendorLoader.GetNearestVendors(StyxWoW.Me, "Repair", 3, bl)
                                .Concat(_vendorLoader.GetNearestVendors(StyxWoW.Me, "Food", 3, bl))
                                .Concat(_vendorLoader.GetNearestVendors(StyxWoW.Me, "Train", 2, bl))
                                .ToList();
                            if (!_refreshGate.TryApply(lease, () => scheduler.CurrentVendors = vendors))
                                return false;
                        }
                        refreshed = scheduler.ScanAndRefreshOwned(
                            StyxWoW.Me, null,
                            apply => _refreshGate.TryApply(lease, apply),
                            path => ProfileManager.TryLoadNew(path, true),
                            () => !_stopped && ReferenceEquals(_scheduler, scheduler)
                                && _refreshGate.IsCurrent(lease));
                        return !refreshed && scheduler.LastSchedule?.FallbackMode == QuestFallbackMode.None;
                    },
                    () =>
                    {
                        if (refreshed)
                        {
                            Log($"Profile refreshed - {scheduler.LastStatus}");
                            LogFarAwayQuests();
                        }
                        else
                        {
                            TreeRoot.StatusText = scheduler.LastStatus ?? "Wholesome quest recovery is idle.";
                        }
                    });
                return runAgain;
            }
            catch (Exception ex) when (ex is not ThreadInterruptedException && ex is not OperationCanceledException)
            {
                Log($"Scan error: {ex.Message}");
                return false;
            }
        }

        internal static bool RunLeaseFencedRefresh(
            RefreshGate gate,
            RefreshLease lease,
            Func<bool> scan,
            System.Action apply)
        {
            if (gate == null || scan == null || apply == null || !gate.IsCurrent(lease))
                return false;
            bool runAgain = scan();
            if (!gate.TryApply(lease, apply))
                return false;
            return gate.IsCurrent(lease) && runAgain;
        }

        protected override void OnNoMoreNodes(object sender, EventArgs e)
        {
            // A generated profile is one scheduler batch, not the end of questing.
            // Defer rebuilding until Pulse, after the current order finishes advancing.
            RequestRefresh("Generated quest profile exhausted; queuing one scheduler rebuild.");
        }

        private void RequestRefresh(string reason)
        {
            if (_stopped || !_refreshGate.TryRequest())
                return;
            Log(reason);
        }

        private void RunPendingRefresh()
        {
            if (_stopped || StyxWoW.Me?.Combat == true)
                return;
            RefreshLease? lease = _refreshGate.Begin();
            if (!lease.HasValue)
                return;
            QuestScheduler scheduler = _scheduler;

            bool runAgain = false;
            try
            {
                runAgain = DoScan(scheduler, lease.Value);
            }
            finally
            {
                _refreshGate.Complete(lease.Value);
            }
            if (runAgain)
                _refreshGate.TryRequest(lease.Value);
        }

        private void MaybeRequestTimedRetry()
        {
            DateTime? retryUtc = _scheduler?.EarliestRetryUtc;
            if (retryUtc.HasValue && DateTime.UtcNow >= retryUtc.Value)
                RequestRefresh("Quest recovery retry is due; queuing one scheduler rebuild.");
        }

        public override void Pulse()
        {
            // Do not carry ready-history authority across an observed world/run gap,
            // including early returns below. This is not a continuous session lease.
            if (_stopped || !StyxWoW.IsInGame || StyxWoW.Me == null || !TreeRoot.IsRunning)
            {
                _lastReadyQuestIds = null;
                _lastReadyQuestSnapshot = null;
            }
            if (_stopped)
                return;

            if (VendorSafetyPolicy.Travel.ReleaseExpired(_vendorTravelOwner, DateTime.UtcNow))
                RequestRefresh("Temporary vendor travel retry is due; queuing one scheduler rebuild.");
            CapturePreDeathAttribution();
            MaybeRequestTimedRetry();
            ObserveRecoveryActivation();
            ObserveQuestProgress();
            CompleteCurrentStageBeforeTick();
            RunPendingRefresh();
            if (StyxWoW.IsInGame && StyxWoW.Me != null)
            {
                PoiType poiType = BotPoi.Current.Type;
                bool hasPendingLoot = poiType == PoiType.Loot
                    || poiType == PoiType.Skin
                    || poiType == PoiType.Harvest
                    || Styx.Logic.LootTargeting.Instance.FirstObject != null;
                var immediateTarget = Styx.Logic.Targeting.Instance.FirstUnit;
                bool hasImmediateThreat = WholesomeRestPolicy.HasImmediateThreat(
                    StyxWoW.Me.Combat,
                    immediateTarget != null
                    && immediateTarget.IsHostile
                    && immediateTarget.IsTargetingMeOrPet);

                // Consumables cannot start in water or an unknown liquid observation.
                // Release only the routine rest pause so movement/air recovery is not
                // held behind a thirty-second wait for an impossible food/drink aura.
                bool canRestHere = !LiquidEnvironment.IsPlayerInLiquid(StyxWoW.Me);
                if (!canRestHere)
                    _restingPaused = false;

                if (_restingPaused)
                {
                    TimeSpan elapsed = DateTime.Now - _restStartTime;
                    bool usesMana = StyxWoW.Me.MaxMana > 0;
                    if (WholesomeRestPolicy.ShouldYieldRest(
                            StyxWoW.Me.HealthPercent,
                            hasPendingLoot,
                            hasImmediateThreat))
                    {
                        Log("Useful work available — ending routine rest");
                        _restingPaused = false;
                    }
                    else if (WholesomeRestPolicy.IsRecovered(
                                 StyxWoW.Me.HealthPercent,
                                 StyxWoW.Me.ManaPercent,
                                 usesMana,
                                 _settings))
                    {
                        Log($"Rest done — HP={StyxWoW.Me.HealthPercent:F0}% MP={StyxWoW.Me.ManaPercent:F0}%");
                        _restingPaused = false;
                    }
                    else if (elapsed.TotalSeconds >= 30)
                    {
                        Log("Rest timeout after 30s — resuming");
                        _restingPaused = false;
                        _restTimeoutEnd = DateTime.Now.AddSeconds(10);
                    }
                    else
                    {
                        return;
                    }
                }

                if (StyxWoW.Me.Combat && StyxWoW.Me.CurrentTarget != null
                    && StyxWoW.Me.Class != WoWClass.Hunter
                    && StyxWoW.Me.Class != WoWClass.Mage
                    && StyxWoW.Me.Class != WoWClass.Priest
                    && StyxWoW.Me.Class != WoWClass.Warlock
                    && StyxWoW.Me.LastRedErrorMessage != null
                    && StyxWoW.Me.LastRedErrorMessage.IndexOf("wrong", StringComparison.OrdinalIgnoreCase) >= 0
                    && (DateTime.Now - _lastFacingLog).TotalSeconds >= 10)
                {
                    Logging.Write(Color.Yellow, "Facing wrong way, rotating");
                    StyxWoW.Me.SetFacing(StyxWoW.Me.CurrentTarget);
                    _lastFacingLog = DateTime.Now;
                }

                if (canRestHere && !_restingPaused && !StyxWoW.Me.Combat && DateTime.Now > _restTimeoutEnd)
                {
                    bool usesMana = StyxWoW.Me.MaxMana > 0;
                    if (WholesomeRestPolicy.ShouldStartRest(
                            StyxWoW.Me.HealthPercent,
                            StyxWoW.Me.ManaPercent,
                            usesMana,
                            hasPendingLoot,
                            hasImmediateThreat,
                            _settings))
                    {
                        bool lowHealth = StyxWoW.Me.HealthPercent <= _settings.RestHealthPercent;
                        bool lowMana = usesMana && StyxWoW.Me.ManaPercent <= _settings.RestManaPercent;
                        if (StyxWoW.Me.Dead || StyxWoW.Me.IsGhost)
                            return;

                        bool usedFood = false;
                        bool usedDrink = false;

                        if (lowHealth && !StyxWoW.Me.HasAura("Food"))
                        {
                            var food = Consumable.GetBestFood(false);
                            if (food != null)
                            {
                                Rest.FeedImmediate();
                                usedFood = true;
                            }
                        }

                        if (lowMana && !StyxWoW.Me.HasAura("Drink"))
                        {
                            var drink = Consumable.GetBestDrink(false);
                            if (drink != null)
                            {
                                Rest.DrinkImmediate();
                                usedDrink = true;
                            }
                        }

                        _restStartTime = DateTime.Now;
                        _restingPaused = true;
                        Navigator.PlayerMover.MoveStop();
                        Log($"Rest: HP={StyxWoW.Me.HealthPercent:F0}% MP={StyxWoW.Me.ManaPercent:F0}% — paused{(usedFood ? " (ate)" : "")}{(usedDrink ? " (drank)" : "")}");
                        return;
                    }
                }
            }

            base.Pulse();
            ObserveVendorTravelContext();

            if (StyxWoW.IsInGame && StyxWoW.Me != null && !StyxWoW.Me.Combat && TreeRoot.IsRunning)
            {

                var loc = StyxWoW.Me.Location;

                if (!_anchorSet)
                {
                    _anchorX = loc.X;
                    _anchorY = loc.Y;
                    _anchorZ = loc.Z;
                    _anchorSet = true;
                }

                double dx = loc.X - _anchorX;
                double dy = loc.Y - _anchorY;
                double dz = loc.Z - _anchorZ;
                if (dx * dx + dy * dy + dz * dz > 25.0)
                {
                    _anchorX = loc.X;
                    _anchorY = loc.Y;
                    _anchorZ = loc.Z;
                }

                if (Math.Abs(loc.X - _lastX) > 0.1 || Math.Abs(loc.Y - _lastY) > 0.1 || Math.Abs(loc.Z - _lastZ) > 0.1)
                {
                    _lastX = loc.X;
                    _lastY = loc.Y;
                    _lastZ = loc.Z;
                    _lastMovedTime = DateTime.UtcNow;
                    _wasStuck = false;
                    _stuckLogged = false;
                }
                else
                {
                    double stuckSec = (DateTime.UtcNow - _lastMovedTime).TotalSeconds;

                    if (stuckSec > 30 && !_wasStuck)
                    {
                        if (TryRecoverFailedVendorTravel(loc, TimeSpan.FromSeconds(stuckSec)))
                        {
                            _wasStuck = true;
                            _stuckLogged = true;
                        }
                        else if (TryRecoverFailedPickupTravel(loc, stuckSec))
                        {
                            _wasStuck = true;
                            _stuckLogged = true;
                        }
                        else if (!_stuckLogged)
                        {
                            _stuckLogged = true;
                            Log($"Bot running but not moving for {stuckSec:F0}s");
                        }
                    }
                }
            }

            if (StyxWoW.IsInGame && StyxWoW.Me != null && TreeRoot.IsRunning)
            {
                var poi = BotPoi.Current;
                var pickup = QuestOrder.Instance?.CurrentBehavior as ForcedQuestPickUp;
                if (pickup != null &&
                    _attemptOwnership.TryGet(
                        pickup,
                        out QuestRecoveryKey pickupOwner,
                        out long pickupGeneration))
                {
                    bool activePickup = IsPickupRecoveryActive(
                        pickup,
                        pickupOwner,
                        pickupGeneration,
                        QuestRecoveryManager.Instance.OwnsAttempt(
                            pickupOwner,
                            pickupGeneration),
                        poi,
                        StyxWoW.IsInWorld,
                        StyxWoW.Me.Dead,
                        StyxWoW.Me.IsGhost,
                        StyxWoW.Me.OnTaxi,
                        StyxWoW.Me.IsOnTransport,
                        _restingPaused ||
                            StyxWoW.Me.HasAura("Food") || StyxWoW.Me.HasAura("Drink"),
                        TreeRoot.IsPaused && !_restingPaused,
                        StyxWoW.Me.Combat);
                    if (pickup.TryConsumeOutcome(out QuestAttemptOutcome producedOutcome))
                    {
                        QuestAttemptOutcome pickupOutcome = _pickupMonitor.Observe(
                            pickupOwner,
                            pickupGeneration,
                            pickup.InteractionCycleId,
                            producedOutcome,
                            activePickup);
                        if (pickupOutcome != null)
                        {
                            ReportRecoveryOutcome(pickup, pickupOutcome);
                            if (pickupOutcome.IsFailureEpisode)
                            {
                                RequestRefresh($"Quest {pickup.QuestId} pickup mismatch at giver {pickup.GiverId}; queuing one scheduler rebuild.");
                            }
                        }
                    }
                }
                else
                {
                    _pickupMonitor.Reset();
                }

                if (ObserveReadyQuestLog())
                    return;
            }

            if (_settings.SellWhite || _settings.SellGreen || _settings.SellBlue)
                SellByQuality();
        }

        private bool ObserveReadyQuestLog()
        {
            var previous = _lastReadyQuestSnapshot;
            var previousReady = _lastReadyQuestIds;
            // Clear before observing so failures/cancellation cannot retain authority.
            _lastReadyQuestSnapshot = null;
            _lastReadyQuestIds = null;
            if (!StyxWoW.IsInGame || StyxWoW.Me == null)
                return false;

            var current = StyxWoW.Me.QuestLog.CaptureSnapshot();
            if (!current.IsIdentityComplete)
                return false;

            _lastReadyQuestSnapshot = current;
            _lastReadyQuestIds = new HashSet<int>(current.ReadyQuestIds.Select(id => (int)id));
            if (previous == null || previousReady == null || !previous.HasSameOwner(current))
                return false;

            var accepted = new HashSet<uint>(current.AcceptedQuestIds);
            var departed = previousReady.Where(id => !accepted.Contains((uint)id)).ToList();
            if (departed.Count == 0)
                return false;

            // A raw departure requests fresh scheduling. It does not prove that a
            // server turn-in succeeded or grant completed-quest history authority.
            Log($"Previously ready quest(s) left the observed log: {string.Join(",", departed)} — triggering rescan");
            RequestRefresh("Previously ready quest left the observed log; queuing one scheduler rebuild.");
            return true;
        }

        private void ObserveRecoveryActivation()
        {
            if (!ShouldExecuteQuestRoot(_stopped, _scheduler?.LastSchedule))
                return;
            var behavior = QuestOrder.Instance?.CurrentBehavior;
            CompleteChangedStageWhenProven(behavior);
            FinalizeChangedOwner(
                _attemptOwnership,
                behavior,
                (key, generation) => QuestRecoveryManager.Instance.AbandonAttempt(key, generation));
            QuestRecoveryDecision decision = _scheduler?.ObserveActivation(
                behavior,
                key => TryClearDeniedRecoveryPoi(
                    behavior!,
                    key,
                    QuestOrder.Instance?.CurrentBehavior!,
                    BotPoi.Current,
                    () => BotPoi.Clear("Wholesome recovery activation claim denied")),
                () => RequestRefresh("Recovery activation claim was denied; queuing one scheduler rebuild."));
            if (decision?.MayAttempt == true)
                _attemptOwnership.Begin(behavior, QuestScheduler.ActivationKey(behavior), decision);
        }

        internal static string PickupOutcomeFingerprint(QuestAttemptOutcome outcome) =>
            outcome == null
                ? ""
                : $"{outcome.Kind}|{outcome.IsFailureEpisode}|{outcome.Reason}|{outcome.Evidence}";

        private void CompleteChangedStageWhenProven(ForcedBehavior currentBehavior)
        {
            if (!_attemptOwnership.TryGet(out object owner, out QuestRecoveryKey key) ||
                ReferenceEquals(owner, currentBehavior) || StyxWoW.Me == null)
                return;

            bool completed = key.Stage == QuestRecoveryStage.Pickup && StyxWoW.Me.QuestLog.ContainsQuest(key.QuestId);
            if (!completed && key.Stage == QuestRecoveryStage.TurnIn &&
                StyxWoW.Me.QuestLog.TryGetAuthoritativeCompletedQuests(out var completedIds))
                completed = completedIds.Contains(key.QuestId);
            if (!completed || !_attemptOwnership.TryComplete(owner, $"{key.Stage} stage completed.", out var success))
                return;

            QuestRecoveryManager.Instance.Report(success, QuestRecoveryRuntime.Capture());
            _scheduler?.ReleaseActivation(owner as ForcedBehavior);
            RequestRefresh($"Quest {key.QuestId} {key.Stage} completed; queuing one scheduler rebuild.");
        }

        private void CompleteCurrentStageBeforeTick()
        {
            ForcedBehavior behavior = QuestOrder.Instance?.CurrentBehavior;
            if (behavior == null || !_attemptOwnership.TryGet(behavior, out QuestRecoveryKey key) || StyxWoW.Me == null)
                return;

            bool behaviorDone;
            try
            {
                behaviorDone = behavior.IsDone;
            }
            catch
            {
                return;
            }
            bool accepted = StyxWoW.Me.QuestLog.ContainsQuest(key.QuestId);
            bool authoritativeCompleted = StyxWoW.Me.QuestLog.TryGetAuthoritativeCompletedQuests(out var completedIds) &&
                completedIds.Contains(key.QuestId);
            if (!IsCompletedOwnedStage(behavior, key, accepted, authoritativeCompleted, behaviorDone) ||
                !_attemptOwnership.TryComplete(behavior, $"{key.Stage} behavior completed.", out var success))
                return;

            QuestRecoveryManager.Instance.Report(success, QuestRecoveryRuntime.Capture());
            _scheduler?.ReleaseActivation(behavior);
            RequestRefresh($"Quest {key.QuestId} {key.Stage} completed; queuing one scheduler rebuild.");
        }

        internal static bool IsCompletedOwnedStage(
            ForcedBehavior behavior,
            QuestRecoveryKey ownerKey,
            bool questAccepted,
            bool authoritativeCompleted,
            bool behaviorDone)
        {
            QuestRecoveryKey activeKey = QuestScheduler.ActivationKey(behavior);
            if (!behaviorDone || activeKey == null || ownerKey == null || !activeKey.Equals(ownerKey))
                return false;
            return ownerKey.Stage switch
            {
                QuestRecoveryStage.Pickup => questAccepted,
                QuestRecoveryStage.Objective => questAccepted,
                QuestRecoveryStage.TurnIn => authoritativeCompleted,
                _ => false
            };
        }

        private void ObserveQuestProgress()
        {
            if (!ShouldExecuteQuestRoot(_stopped, _scheduler?.LastSchedule))
                return;
            if (DateTime.UtcNow < _nextProgressSampleUtc)
                return;
            _nextProgressSampleUtc = DateTime.UtcNow.AddSeconds(2);

            ForcedQuestObjective behavior = QuestOrder.Instance?.CurrentBehavior as ForcedQuestObjective;
            if (behavior?.Objective?.Quest == null ||
                !_attemptOwnership.TryGet(behavior, out QuestRecoveryKey ownerKey, out long attemptGeneration))
                return;

            QuestWorkSample sample = CreateLiveWorkSample(behavior, ownerKey, attemptGeneration);
            QuestProgressUpdate update = _progressMonitor.Sample(sample);
            ProcessProgressUpdate(behavior, sample, update);
        }

        private QuestWorkSample CreateLiveWorkSample(
            ForcedQuestObjective behavior,
            QuestRecoveryKey ownerKey,
            long attemptGeneration)
        {
            var me = StyxWoW.Me;
            uint questId = behavior.Objective.Quest.Id;
            IReadOnlyList<int> counts = ReadObjectiveCounts(behavior.Objective.Quest);
            var area = StyxWoW.AreaManager.CurrentGrindArea;
            var knownEndpoints = new List<QuestRecoveryKey>();
            QuestRecoveryKey clusterKey = null;
            bool allHotspotsUnavailable = false;
            bool endpointPathFailed = false;
            RouteFailureReason navigationFailure = RouteFailureReason.None;

            if (me != null && area != null)
            {
                allHotspotsUnavailable = area.Hotspots.Count == 0 && area.CircledHotspots.Count == 0;
                foreach (var hotspot in area.Hotspots)
                {
                    knownEndpoints.Add(QuestScheduler.EndpointKey(
                        questId,
                        QuestRecoveryStage.Navigation,
                        new SpawnPoint { Map = (int)me.MapId, X = hotspot.Position.X, Y = hotspot.Position.Y, Z = hotspot.Position.Z }));
                }

                if (!allHotspotsUnavailable)
                {
                    try
                    {
                        WoWPoint current = area.CurrentHotSpot.Position;
                        if (current != WoWPoint.Zero)
                        {
                            clusterKey = QuestScheduler.EndpointKey(
                                questId,
                                QuestRecoveryStage.Navigation,
                                new SpawnPoint { Map = (int)me.MapId, X = current.X, Y = current.Y, Z = current.Z });
                            if (Navigator.NavigationProvider is MeshNavigator meshNavigator)
                            {
                                endpointPathFailed = HasFailedActiveEndpoint(
                                    me.Combat,
                                    BotPoi.Current.Type,
                                    me.Location,
                                    current,
                                    meshNavigator.LastMoveDestination,
                                    meshNavigator.HasActivePath,
                                    meshNavigator.PathPrecision,
                                    meshNavigator.LastMoveResult,
                                    meshNavigator.LastMoveAttemptUtc,
                                    DateTime.UtcNow);
                                if (endpointPathFailed)
                                    navigationFailure = meshNavigator.LastRouteFailure;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Active objective endpoint assessment unavailable: {ex.Message}");
                    }
                }
            }

            bool questCombat = me?.Combat == true && IsQuestCombatTarget(questId, me.CurrentTarget?.Entry ?? 0);
            return CreateWorkSample(
                behavior,
                ownerKey,
                BotPoi.Current,
                StyxWoW.IsInWorld,
                me?.Dead == true,
                me?.IsGhost == true,
                me?.OnTaxi == true,
                me?.IsOnTransport == true ||
                    Navigator.NavigationProvider is MeshNavigator transit && transit.IsRidingElevator,
                _restingPaused || me?.HasAura("Food") == true || me?.HasAura("Drink") == true,
                TreeRoot.IsPaused && !_restingPaused,
                me?.Combat == true,
                questCombat,
                counts,
                clusterKey,
                knownEndpoints,
                endpointPathFailed,
                allHotspotsUnavailable,
                deathAttributable: false,
                attemptGeneration,
                navigationFailure);
        }

        internal static bool HasFailedActiveEndpoint(
            bool inCombat,
            PoiType poiType,
            WoWPoint playerLocation,
            WoWPoint endpoint,
            WoWPoint navigatorDestination,
            bool hasActivePath,
            float pathPrecision,
            MoveResult? lastMoveResult = null,
            DateTime? lastMoveUtc = null,
            DateTime? nowUtc = null)
        {
            // QuestOrder can retain the previous pickup/turn-in POI for one stage
            // transition. The navigator destination match below still proves that
            // this failed movement belongs to the active objective endpoint.
            if (poiType != PoiType.None && poiType != PoiType.Hotspot &&
                poiType != PoiType.QuestPickUp && poiType != PoiType.QuestTurnIn)
                return false;
            return HasFreshFailedMoveToEndpoint(
                inCombat, playerLocation, endpoint, navigatorDestination, hasActivePath,
                pathPrecision, lastMoveResult, lastMoveUtc, nowUtc);
        }

        internal static bool HasFailedPickupTravel(
            bool inCombat,
            PoiType poiType,
            WoWPoint playerLocation,
            WoWPoint endpoint,
            WoWPoint navigatorDestination,
            bool hasActivePath,
            float pathPrecision,
            MoveResult? lastMoveResult = null,
            DateTime? lastMoveUtc = null,
            DateTime? nowUtc = null)
        {
            if (poiType != PoiType.QuestPickUp)
                return false;
            return HasFreshFailedMoveToEndpoint(
                inCombat, playerLocation, endpoint, navigatorDestination, hasActivePath,
                pathPrecision, lastMoveResult, lastMoveUtc, nowUtc);
        }

        private static bool HasFreshFailedMoveToEndpoint(
            bool inCombat,
            WoWPoint playerLocation,
            WoWPoint endpoint,
            WoWPoint navigatorDestination,
            bool hasActivePath,
            float pathPrecision,
            MoveResult? lastMoveResult,
            DateTime? lastMoveUtc,
            DateTime? nowUtc)
        {
            // Read the completed movement attempt, not path-array shape. A path can be
            // empty while throttled, successfully exhausted, cleared or superseded.
            DateTime now = nowUtc ?? DateTime.UtcNow;
            if (lastMoveResult is not (MoveResult.Failed or MoveResult.PathGenerationFailed) ||
                !lastMoveUtc.HasValue || lastMoveUtc.Value > now ||
                now - lastMoveUtc.Value > TimeSpan.FromSeconds(5))
                return false;
            if (inCombat || hasActivePath || endpoint == WoWPoint.Zero || navigatorDestination == WoWPoint.Zero)
            {
                return false;
            }

            float precision = Math.Max(1f, pathPrecision);
            float precisionSqr = precision * precision;
            bool playerAtEndpoint = playerLocation.Distance2DSqr(endpoint) <= precisionSqr &&
                Math.Abs(playerLocation.Z - endpoint.Z) < 4.5f;
            bool navigatorTargetsEndpoint = navigatorDestination.Distance2DSqr(endpoint) <= precisionSqr &&
                Math.Abs(navigatorDestination.Z - endpoint.Z) < 4.5f;
            return !playerAtEndpoint && navigatorTargetsEndpoint;
        }

        // Do not charge a new trip for time spent on another POI, paused, loading,
        // in combat or intentionally resting. A long sampling gap is unknown time.
        private void ObserveVendorTravelContext()
        {
            var me = StyxWoW.Me;
            var poi = BotPoi.Current;
            DateTime now = DateTime.UtcNow;
            if (me == null || !StyxWoW.IsInWorld || poi == null || !VendorSafetyPolicy.IsService(poi.Type))
            {
                _lastVendorObservationUtc = DateTime.MinValue;
                return;
            }
            bool newContext = _lastVendorObservationUtc == DateTime.MinValue
                || now < _lastVendorObservationUtc || now - _lastVendorObservationUtc > TimeSpan.FromSeconds(5)
                || me.MapId != _lastVendorMap || poi.Entry != _lastVendorEntry
                || !VendorTravelBackoff.SameEndpoint(poi.Location, _lastVendorDestination);
            if (newContext || TreeRoot.IsPaused || !TreeRoot.IsRunning || me.Combat
                || me.Dead || me.IsGhost || me.OnTaxi || me.IsOnTransport
                || Navigator.IsRidingElevator || _restingPaused)
            {
                _lastMovedTime = now;
                _wasStuck = false;
                _stuckLogged = false;
            }
            _lastVendorObservationUtc = now;
            _lastVendorMap = me.MapId;
            _lastVendorEntry = poi.Entry;
            _lastVendorDestination = poi.Location;
        }

        private bool TryRecoverFailedVendorTravel(WoWPoint playerLocation, TimeSpan stationaryFor)
        {
            var me = StyxWoW.Me;
            var poi = BotPoi.Current;
            if (me == null || poi == null || poi.Entry > int.MaxValue || !VendorSafetyPolicy.IsService(poi.Type)
                || Navigator.NavigationProvider is not MeshNavigator mesh)
                return false;
            bool intentionalRest = _restingPaused || me.HasAura("Food") || me.HasAura("Drink");
            bool frameOpen = MerchantFrame.Instance.IsVisible || TrainerFrame.Instance.IsVisible
                || Styx.Logic.Inventory.Frames.Gossip.GossipFrame.Instance.IsVisible;
            if (intentionalRest || frameOpen) _lastMovedTime = DateTime.UtcNow;
            var sample = new VendorTravelObservation
            {
                NowUtc = DateTime.UtcNow, MapId = me.MapId, Entry = (int)poi.Entry, Type = poi.Type,
                PlayerLocation = playerLocation, Destination = poi.Location,
                LastMoveDestination = mesh.LastMoveDestination, LastMoveResult = mesh.LastMoveResult,
                LastMoveAttemptUtc = mesh.LastMoveAttemptUtc, StationaryFor = stationaryFor,
                IsActiveWorld = !_stopped && StyxWoW.IsInWorld && TreeRoot.IsRunning,
                IsPaused = TreeRoot.IsPaused, IsCombat = me.Combat, IsDead = me.Dead || me.IsGhost,
                IsResting = intentionalRest, IsOnTaxi = me.OnTaxi, IsOnTransport = me.IsOnTransport,
                IsElevatorTransit = mesh.IsRidingElevator, HasActivePath = mesh.HasActivePath,
                IsServiceFrameOpen = frameOpen
            };
            if (!VendorSafetyPolicy.Travel.TryDefer(_vendorTravelOwner, sample)) return false;
            Log($"Vendor travel deferred for 120s: {poi.Name} (Entry:{poi.Entry}), map={me.MapId}, "
                + $"destination={poi.Location}; movement={mesh.LastMoveResult}, reason={mesh.LastRouteFailure}. No saved blacklist change.");
            BotPoi.Clear("Wholesome temporary vendor travel retry");
            RequestRefresh("Vendor travel temporarily deferred; selecting another eligible endpoint.");
            return true;
        }

        private bool TryRecoverFailedPickupTravel(WoWPoint playerLocation, double stalledSeconds)
        {
            var pickup = QuestOrder.Instance?.CurrentBehavior as ForcedQuestPickUp;
            if (pickup == null || Navigator.NavigationProvider is not MeshNavigator meshNavigator ||
                !_attemptOwnership.TryGet(pickup, out QuestRecoveryKey pickupOwner, out long pickupGeneration) ||
                !QuestRecoveryManager.Instance.OwnsAttempt(pickupOwner, pickupGeneration))
            {
                return false;
            }

            var me = StyxWoW.Me;
            if (_stopped || me == null || !StyxWoW.IsInWorld || TreeRoot.IsPaused ||
                me.Dead || me.IsGhost || me.OnTaxi || me.IsOnTransport ||
                meshNavigator.IsRidingElevator || _restingPaused ||
                me.HasAura("Food") || me.HasAura("Drink"))
                return false;

            BotPoi poi = BotPoi.Current;
            if (!HasFailedPickupTravel(
                    StyxWoW.Me?.Combat == true,
                    poi.Type,
                    playerLocation,
                    poi.Location,
                    meshNavigator.LastMoveDestination,
                    meshNavigator.HasActivePath,
                    meshNavigator.PathPrecision,
                    meshNavigator.LastMoveResult,
                    meshNavigator.LastMoveAttemptUtc,
                    DateTime.UtcNow))
            {
                return false;
            }

            if (IsUnresolvedNavigationFailure(meshNavigator.LastRouteFailure))
                return TryDeferCurrentNavigation(pickup, pickupOwner, pickupGeneration,
                    QuestRecoveryRuntime.Capture(), meshNavigator.LastRouteFailure);

            var outcome = QuestAttemptOutcome.Failure(
                pickupOwner,
                QuestFailureReason.EndpointUnreachable,
                $"Movement to quest giver {pickup.GiverId} failed after {stalledSeconds:F0}s without progress.");
            ReportRecoveryOutcome(pickup, outcome);
            Log($"Quest pickup route to {pickup.GiverName} (Entry:{pickup.GiverId}) failed after {stalledSeconds:F0}s; selecting alternate work.");
            RequestRefresh($"Quest {pickup.QuestId} pickup giver was unreachable; queuing one scheduler rebuild.");
            return true;
        }

        internal static bool IsUnresolvedNavigationFailure(RouteFailureReason reason) =>
            reason is RouteFailureReason.SearchResourceLimit or RouteFailureReason.PartialPath or
                RouteFailureReason.VerticalAccessUnresolved or RouteFailureReason.PathSearchFailed;

        private bool TryDeferCurrentNavigation(
            ForcedBehavior behavior,
            QuestRecoveryKey key,
            long generation,
            QuestRecoveryContext context,
            RouteFailureReason reason)
        {
            if (_stopped || !IsUnresolvedNavigationFailure(reason) ||
                !ReferenceEquals(QuestOrder.Instance?.CurrentBehavior, behavior) ||
                !_attemptOwnership.TryGet(behavior, out QuestRecoveryKey ownedKey, out long ownedGeneration) ||
                !ownedKey.Equals(key) || ownedGeneration != generation)
                return false;
            var result = QuestRecoveryManager.Instance.TryDeferNavigationAttempt(
                key, generation, context,
                $"Navigation deferred: {reason}; incomplete search does not establish endpoint/quest failure.");
            if (!result.Accepted || !_attemptOwnership.Release(behavior, generation))
                return false;
            _scheduler?.ReleaseActivation(behavior);
            TryClearRecoveryOutcomePoi(behavior, key, QuestOrder.Instance?.CurrentBehavior,
                BotPoi.Current, () => BotPoi.Clear("Wholesome navigation retry deferred"));
            RequestRefresh($"Quest {key.QuestId} navigation is unresolved ({reason}); retry {result.Decision.RetryUtc:O}; selecting alternate eligible work without a failure episode.");
            return true;
        }

        private void ProcessProgressUpdate(
            ForcedQuestObjective behavior,
            QuestWorkSample sample,
            QuestProgressUpdate update)
        {
            if (update.NavigationDeferred)
            {
                TryDeferCurrentNavigation(behavior, sample.Key, sample.AttemptGeneration,
                    QuestRecoveryRuntime.Capture(sample.ObjectiveCounts), sample.NavigationFailure);
                return;
            }
            if (update.RequestAlternateCluster)
            {
                bool advanced = false;
                try
                {
                    var area = _activeGrindArea();
                    advanced = area != null &&
                        area.TryAdvanceCurrentHotspot(out var previous, out var current);
                }
                catch (Exception ex)
                {
                    Log($"Alternate objective cluster request failed: {ex.Message}");
                }
                if (!advanced)
                    Log($"Quest {sample.Key.QuestId} has no alternate active hotspot; retaining the current recovery attempt until its bounded deadline.");
            }
            QuestRecoveryContext context = QuestRecoveryRuntime.Capture(update.MadeProgress
                ? update.ObjectiveCounts
                : sample.ObjectiveCounts);
            if (update.MadeProgress)
            {
                if (_attemptOwnership.TryComplete(behavior, "Objective counter or required item count increased.", out var success))
                    QuestRecoveryManager.Instance.Report(success, context);
                QuestRecoveryManager.Instance.ReportProgress(sample.Key, update.ObjectiveCounts, context);
                _scheduler?.ReleaseActivation(behavior);
                if (IsCompletedOwnedStage(
                        behavior,
                        sample.Key,
                        questAccepted: true,
                        authoritativeCompleted: false,
                        behaviorDone: behavior.IsDone))
                    RequestRefresh($"Quest {sample.Key.QuestId} objective completed; queuing one scheduler rebuild.");
            }

            QuestAttemptOutcome stageFailure = update.Outcomes
                .FirstOrDefault(outcome => outcome.IsFailureEpisode && outcome.Key.Equals(sample.Key));
            QuestAttemptOutcome[] subordinateFailures = update.Outcomes
                .Where(outcome => outcome.IsFailureEpisode && !outcome.Key.Equals(sample.Key))
                .ToArray();
            var reportedFailures = new Dictionary<QuestAttemptOutcome, QuestRecoveryDecision>();
            if (stageFailure != null)
            {
                QuestAttemptOutcome[] sequence = subordinateFailures
                    .Concat(new[] { stageFailure })
                    .ToArray();
                if (ReportOwnedFailures(
                        _attemptOwnership,
                        behavior,
                        sequence,
                        (IReadOnlyList<QuestAttemptOutcome> bound, out IReadOnlyList<QuestRecoveryDecision> accepted) =>
                            QuestRecoveryManager.Instance.TryReportGeneratedFailures(bound, context, out accepted),
                        out IReadOnlyList<QuestRecoveryDecision> decisions))
                {
                    for (int index = 0; index < sequence.Length; index++)
                        reportedFailures[sequence[index]] = decisions[index];
                    _scheduler?.ReleaseActivation(behavior);
                }
                else
                {
                    RecoverRejectedOwnedFailures(
                        _attemptOwnership,
                        behavior,
                        (key, generation) => QuestRecoveryManager.Instance.AbandonAttempt(key, generation),
                        () => _scheduler?.ReleaseActivation(behavior),
                        () => RequestRefresh($"Quest {sample.Key.QuestId} generated recovery outcome was superseded; queuing one scheduler rebuild."));
                    return;
                }
            }
            else
            {
                foreach (QuestAttemptOutcome subordinate in subordinateFailures)
                {
                    if (_attemptOwnership.TryBind(behavior, subordinate, out QuestAttemptOutcome bound))
                        reportedFailures[subordinate] = QuestRecoveryManager.Instance.Report(bound, context);
                }
            }

            foreach (QuestAttemptOutcome original in update.Outcomes)
            {
                QuestAttemptOutcome outcome = original;
                QuestRecoveryDecision decision;
                if (reportedFailures.TryGetValue(original, out decision))
                { }
                else
                {
                    if (original.IsFailureEpisode)
                    {
                        outcome = new QuestAttemptOutcome
                        {
                            Key = original.Key,
                            Kind = QuestAttemptOutcomeKind.Observation,
                            Reason = original.Reason,
                            IsFailureEpisode = false,
                            Evidence = original.Evidence,
                            ObservedQuestId = original.ObservedQuestId,
                            OfferedQuestIds = original.OfferedQuestIds,
                            ObjectiveCounts = original.ObjectiveCounts,
                            InteractionCycleId = original.InteractionCycleId
                        };
                    }
                    decision = QuestRecoveryManager.Instance.Report(outcome, context);
                }
                if (!outcome.IsFailureEpisode)
                    continue;
                Log($"Recovery quest={outcome.Key.QuestId};stage={outcome.Key.Stage};scope={outcome.Key.Scope};reason={outcome.Reason};episode={outcome.IsFailureEpisode};state={decision.State}.");
                TryClearRecoveryOutcomePoi(
                    behavior,
                    outcome.Key,
                    QuestOrder.Instance?.CurrentBehavior,
                    BotPoi.Current,
                    () => BotPoi.Clear("Wholesome quest recovery outcome"));
                if (RequiresSchedulerRefresh(outcome))
                    RequestRefresh($"Quest {outcome.Key.QuestId} recovery outcome requires one scheduler rebuild.");
            }
        }

        internal static bool RequiresSchedulerRefresh(QuestAttemptOutcome outcome)
        {
            return outcome?.IsFailureEpisode == true && outcome.Key.Scope != QuestRecoveryScope.Endpoint;
        }

        internal delegate bool TryGeneratedFailureReporter(
            IReadOnlyList<QuestAttemptOutcome> outcomes,
            out IReadOnlyList<QuestRecoveryDecision> decisions);

        internal static bool ReportOwnedFailures(
            WholesomeAttemptOwnership ownership,
            object owner,
            IReadOnlyList<QuestAttemptOutcome> outcomes,
            TryGeneratedFailureReporter report,
            out IReadOnlyList<QuestRecoveryDecision> decisions)
        {
            decisions = Array.Empty<QuestRecoveryDecision>();
            if (ownership == null || outcomes == null || outcomes.Count == 0 || report == null)
                return false;

            var bound = new List<QuestAttemptOutcome>(outcomes.Count);
            foreach (QuestAttemptOutcome outcome in outcomes)
            {
                if (!ownership.TryBind(owner, outcome, out QuestAttemptOutcome generated) ||
                    !generated.IsFailureEpisode)
                    return false;
                bound.Add(generated);
            }
            if (!report(bound, out decisions))
            {
                decisions ??= Array.Empty<QuestRecoveryDecision>();
                return false;
            }
            decisions ??= Array.Empty<QuestRecoveryDecision>();
            return decisions.Count == bound.Count &&
                   ownership.Release(owner, bound[^1].AttemptGeneration);
        }

        internal static bool RecoverRejectedOwnedFailures(
            WholesomeAttemptOwnership ownership,
            object owner,
            Func<QuestRecoveryKey, long, bool> abandon,
            System.Action releaseActivation,
            System.Action requestRefresh)
        {
            if (ownership == null || abandon == null ||
                !ownership.TryGet(owner, out QuestRecoveryKey key, out long generation))
                return false;
            abandon(key, generation);
            if (!ownership.Release(owner, generation))
                return false;
            releaseActivation?.Invoke();
            requestRefresh?.Invoke();
            return true;
        }

        internal static bool ReleaseManuallyExcludedOwnership(
            WholesomeAttemptOwnership ownership,
            uint questId,
            System.Action<object> releaseActivation)
        {
            if (ownership == null ||
                !ownership.TryGet(out object owner, out QuestRecoveryKey key, out long generation) ||
                key.QuestId != questId ||
                !ownership.Release(owner, generation))
                return false;
            releaseActivation?.Invoke(owner);
            return true;
        }

        internal static bool ReportOwnedFailure(
            WholesomeAttemptOwnership ownership,
            object owner,
            QuestAttemptOutcome outcome,
            Func<QuestAttemptOutcome, QuestRecoveryDecision> report)
        {
            if (ownership == null || report == null ||
                !ownership.TryBind(owner, outcome, out QuestAttemptOutcome bound) ||
                !bound.IsFailureEpisode)
                return false;

            report(bound);
            return ownership.Release(owner, bound.AttemptGeneration);
        }

        private void ReportRecoveryOutcome(ForcedBehavior behavior, QuestAttemptOutcome outcome)
        {
            QuestRecoveryContext context = QuestRecoveryRuntime.Capture();
            QuestRecoveryDecision decision;
            if (outcome.IsFailureEpisode)
            {
                decision = null;
                if (!ReportOwnedFailure(
                        _attemptOwnership,
                        behavior,
                        outcome,
                        bound => decision = QuestRecoveryManager.Instance.Report(bound, context)))
                    return;
            }
            else
            {
                if (!_attemptOwnership.TryBind(behavior, outcome, out QuestAttemptOutcome bound))
                    return;
                outcome = bound;
                decision = QuestRecoveryManager.Instance.Report(outcome, context);
            }
            Log($"Recovery quest={outcome.Key.QuestId};stage={outcome.Key.Stage};scope={outcome.Key.Scope};reason={outcome.Reason};episode={outcome.IsFailureEpisode};state={decision.State}.");
            if (outcome.IsFailureEpisode)
            {
                _scheduler?.ReleaseActivation(behavior);
                TryClearRecoveryOutcomePoi(
                    behavior,
                    outcome.Key,
                    QuestOrder.Instance?.CurrentBehavior,
                    BotPoi.Current,
                    () => BotPoi.Clear("Wholesome quest recovery outcome"));
            }
        }

        private bool IsQuestCombatTarget(uint questId, uint targetEntry)
        {
            if (targetEntry == 0 || _dataLoader?.Database == null)
                return false;
            QuestEntry quest = _dataLoader.Database.Quests.FirstOrDefault(entry => entry.Id == questId);
            return quest?.Objectives.Any(objective =>
                objective.MobId == targetEntry || objective.GameObjectId == targetEntry) == true;
        }

        private void CapturePreDeathAttribution()
        {
            ForcedQuestObjective behavior = QuestOrder.Instance?.CurrentBehavior as ForcedQuestObjective;
            var me = StyxWoW.Me;
            if (behavior?.Objective?.Quest == null || me == null ||
                !_attemptOwnership.TryGet(
                    behavior,
                    out QuestRecoveryKey ownerKey,
                    out long attemptGeneration) ||
                !QuestRecoveryManager.Instance.OwnsAttempt(ownerKey, attemptGeneration))
            {
                _deathMonitor.Reset();
                return;
            }

            bool questCombat = me.Combat && IsQuestCombatTarget(
                behavior.Objective.Quest.Id,
                me.CurrentTarget?.Entry ?? 0);
            QuestWorkSample sample = CreateWorkSample(
                behavior,
                ownerKey,
                BotPoi.Current,
                StyxWoW.IsInWorld,
                me.Dead,
                me.IsGhost,
                me.OnTaxi,
                me.IsOnTransport,
                _restingPaused || me.HasAura("Food") || me.HasAura("Drink"),
                TreeRoot.IsPaused && !_restingPaused,
                me.Combat,
                questCombat,
                ReadObjectiveCounts(behavior.Objective.Quest),
                clusterKey: null,
                knownEndpointKeys: null,
                endpointPathFailed: false,
                allHotspotsUnavailable: false,
                deathAttributable: false,
                attemptGeneration);
            _deathMonitor.Capture(behavior, ownerKey, attemptGeneration, sample);
        }

        private static IReadOnlyList<int> ReadObjectiveCounts(PlayerQuest quest)
        {
            try
            {
                if (quest != null && quest.GetData(out QuestDescriptorData data) && data.ObjectivesDone != null)
                    return data.ObjectivesDone.Select(value => (int)value).ToArray();
            }
            catch (Exception ex)
            {
                Logging.WriteDiagnostic($"[WholesomeAQ] Objective progress capture failed for quest {quest?.Id}: {ex.Message}");
            }
            return Array.Empty<int>();
        }

        internal static bool TryClearDeniedRecoveryPoi(
            ForcedBehavior deniedBehavior,
            QuestRecoveryKey deniedKey,
            ForcedBehavior currentBehavior,
            BotPoi currentPoi,
            System.Action clearPoi)
        {
            if (deniedBehavior == null || deniedKey == null || currentPoi == null || clearPoi == null)
                return false;
            if (!ReferenceEquals(deniedBehavior, currentBehavior))
                return false;
            QuestRecoveryKey currentKey = QuestScheduler.ActivationKey(currentBehavior);
            if (currentKey == null || !currentKey.Equals(deniedKey))
                return false;
            if (!IsOwnedRecoveryPoi(currentBehavior, deniedKey, currentPoi))
                return false;

            clearPoi();
            return true;
        }

        internal static bool TryClearRecoveryOutcomePoi(
            ForcedBehavior failedBehavior,
            QuestRecoveryKey failedKey,
            ForcedBehavior currentBehavior,
            BotPoi currentPoi,
            System.Action clearPoi) =>
            TryClearDeniedRecoveryPoi(
                failedBehavior,
                failedKey,
                currentBehavior,
                currentPoi,
                clearPoi);

        private static bool IsOwnedRecoveryPoi(
            ForcedBehavior behavior,
            QuestRecoveryKey key,
            BotPoi poi)
        {
            if (behavior is ForcedQuestPickUp pickup)
            {
                if (poi.Type != PoiType.QuestPickUp)
                    return false;
                var node = poi.AsPickUp;
                if (node != null)
                    return node.QuestId == key.QuestId
                           && node.GiverId == key.NpcEntry
                           && Near(node.GiverLocation, pickup.GiverLocation);
                return poi.Entry == key.NpcEntry && Near(poi.Location, pickup.GiverLocation);
            }
            if (behavior is ForcedQuestTurnIn turnIn)
            {
                if (poi.Type != PoiType.QuestTurnIn)
                    return false;
                var node = poi.AsTurnIn;
                if (node != null)
                    return node.QuestId == key.QuestId
                           && node.TurnInId == key.NpcEntry
                           && Near(node.TurnInLocation, turnIn.Location);
                return poi.Entry == key.NpcEntry && Near(poi.Location, turnIn.Location);
            }
            if (behavior is ForcedQuestObjective objective && objective.Objective?.Quest != null)
            {
                if (poi.Type == PoiType.Quest)
                    return poi.Entry == key.QuestId;
                return false;
            }
            return false;
        }

        private static bool Near(WoWPoint first, WoWPoint second) =>
            first != WoWPoint.Zero && second != WoWPoint.Zero && first.Distance(second) <= 5;

        internal static QuestWorkSample CreateWorkSample(
            ForcedBehavior currentBehavior,
            QuestRecoveryKey attemptOwner,
            BotPoi currentPoi,
            bool inWorld,
            bool dead,
            bool ghost,
            bool onTaxi,
            bool onTransport,
            bool resting,
            bool userPaused,
            bool inCombat,
            bool combatOwnedByQuest,
            IReadOnlyList<int> objectiveCounts,
            QuestRecoveryKey clusterKey,
            IReadOnlyList<QuestRecoveryKey> knownEndpointKeys,
            bool endpointPathFailed,
            bool allHotspotsUnavailable,
            bool deathAttributable,
            long attemptGeneration = 0,
            RouteFailureReason navigationFailure = RouteFailureReason.None)
        {
            QuestRecoveryKey activeKey = QuestScheduler.ActivationKey(currentBehavior);
            bool exactObjectiveOwner = activeKey != null &&
                activeKey.Stage == QuestRecoveryStage.Objective &&
                attemptOwner != null && activeKey.Equals(attemptOwner);
            PoiType poiType = currentPoi?.Type ?? PoiType.None;
            bool excludedPoi = poiType is PoiType.Buy or PoiType.Sell or PoiType.Repair or
                PoiType.Train or PoiType.Mail or PoiType.Fly or PoiType.InnKeeper or PoiType.Corpse;
            bool active = exactObjectiveOwner && inWorld && !dead && !ghost && !onTaxi &&
                !onTransport && !resting && !userPaused && !excludedPoi &&
                (!inCombat || combatOwnedByQuest);

            return new QuestWorkSample
            {
                Key = activeKey,
                AttemptGeneration = attemptGeneration,
                ObjectiveCounts = objectiveCounts ?? Array.Empty<int>(),
                ClusterKey = clusterKey,
                KnownEndpointKeys = knownEndpointKeys ?? Array.Empty<QuestRecoveryKey>(),
                IsActiveWork = active,
                CombatOwnedByQuest = exactObjectiveOwner && active && combatOwnedByQuest,
                EndpointPathFailed = exactObjectiveOwner && endpointPathFailed,
                NavigationFailure = exactObjectiveOwner && endpointPathFailed
                    ? navigationFailure : RouteFailureReason.None,
                AllHotspotsUnavailable = exactObjectiveOwner && allHotspotsUnavailable,
                DeathAttributable = exactObjectiveOwner && deathAttributable && !excludedPoi
            };
        }

        internal static bool IsPickupRecoveryActive(
            ForcedBehavior currentBehavior,
            QuestRecoveryKey attemptOwner,
            long attemptGeneration,
            bool managerOwnsAttempt,
            BotPoi currentPoi,
            bool inWorld,
            bool dead,
            bool ghost,
            bool onTaxi,
            bool onTransport,
            bool resting,
            bool userPaused,
            bool inCombat)
        {
            QuestRecoveryKey activeKey = QuestScheduler.ActivationKey(currentBehavior);
            return attemptGeneration > 0 && managerOwnsAttempt && activeKey != null &&
                activeKey.Stage == QuestRecoveryStage.Pickup &&
                attemptOwner != null && activeKey.Equals(attemptOwner) &&
                inWorld && !dead && !ghost && !onTaxi && !onTransport &&
                !resting && !userPaused && !inCombat &&
                IsOwnedRecoveryPoi(currentBehavior, activeKey, currentPoi);
        }

        private void OnPlayerDied()
        {
            if (!_attemptOwnership.TryGet(
                    out object owner,
                    out QuestRecoveryKey ownerKey,
                    out long attemptGeneration) ||
                owner is not ForcedQuestObjective behavior ||
                !_deathMonitor.TryRecordDeath(
                    owner,
                    ownerKey,
                    attemptGeneration,
                    out QuestWorkSample sample))
                return;
            ProcessProgressUpdate(behavior, sample, _progressMonitor.RecordDeath(sample));
        }

        private bool _lastFrameVisible;

        private void SellByQuality()
        {
            if (!MerchantFrame.Instance.IsVisible)
            {
                _lastFrameVisible = false;
                return;
            }

            if (!_lastFrameVisible)
            {
                _lastFrameVisible = true;
                Log("Arrived at vendor — merchant frame opened");
            }

            ItemQuality mask = ItemQuality.None;
            if (_settings.SellWhite) mask |= ItemQuality.Common;
            if (_settings.SellGreen) mask |= ItemQuality.Uncommon;
            if (_settings.SellBlue) mask |= ItemQuality.Rare;

            if (mask == ItemQuality.None) return;

            // Selling is destructive. A scheduler batch is not the full live quest log.
            var me = StyxWoW.Me;
            var database = _dataLoader?.Database;
            if (me == null || !me.IsValid || !me.IsAlive || database?.Quests == null)
                return;
            var questLog = me.QuestLog;
            var observation = questLog.CaptureSnapshot();
            if (observation == null || !observation.IsComplete)
                return;
            var accepted = observation.Quests;

            var protectedIds = new HashSet<uint>(ProtectedItemsManager.GetAllItemIds());
            var protectedNames = ProtectedItemsManager.GetAllItemNames();
            var acceptedIds = new HashSet<int>();
            foreach (var current in accepted)
            {
                if (current == null || current.Id == 0 || current.Id > int.MaxValue)
                    return;
                acceptedIds.Add((int)current.Id);
            }
            // Keep the old upcoming/scheduled protection as well as deferred and
            // completed-but-not-turned-in quests. Do not protect unrelated dataset rows.
            var protectedQuestIds = new HashSet<int>(acceptedIds);
            if (_scheduler?.ActiveQuestIds != null)
                protectedQuestIds.UnionWith(_scheduler.ActiveQuestIds);
            foreach (int qId in protectedQuestIds)
            {
                var matches = database.Quests.Where(q => q != null && q.Id == qId).ToArray();
                if (matches.Length != 1 || matches[0].Objectives == null
                    || matches[0].Objectives.Any(obj => obj == null))
                {
                    // Missing/ambiguous accepted data is not permission to sell its items.
                    if (acceptedIds.Contains(qId)) return;
                    continue;
                }
                var quest = matches[0];
                if (quest.StartItem > 0) protectedIds.Add((uint)quest.StartItem);
                foreach (var obj in quest.Objectives)
                    if (obj.ItemId > 0) protectedIds.Add((uint)obj.ItemId);
            }

            var bestFood = Consumable.GetBestFood(false);
            if (bestFood != null)
                protectedIds.Add(bestFood.Entry);
            var bestDrink = Consumable.GetBestDrink(false);
            if (bestDrink != null)
                protectedIds.Add(bestDrink.Entry);

            foreach (var item in me.BagItems)
            {
                if (item == null) return;
                if (item.ItemClass == WoWItemClass.Projectile
                 || item.ItemClass == WoWItemClass.Quiver
                 || item.ItemClass == WoWItemClass.Reagent
                 || item.ItemClass == WoWItemClass.Key)
                    protectedIds.Add(item.Entry);
            }

            // Recheck the immediate dispatch boundary after observing inventory.
            if (!ReferenceEquals(me, StyxWoW.Me) || !me.IsValid || !me.IsAlive
                || !MerchantFrame.Instance.IsVisible
                || !questLog.IsSnapshotCurrent(observation))
                return;
            MerchantFrame.Instance.SellItemQualities(mask, protectedNames, protectedIds);
        }

        private static string FindProfilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "Bots", "WholesomeAutoQuest", "WHOLESOME_AUTOQUESTER.xml");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return path;
        }

        private void LogFarAwayQuests()
        {
            var db = _dataLoader?.Database;
            if (db == null || _scheduler?.ActiveQuestIds == null) return;

            foreach (int qId in _scheduler.ActiveQuestIds)
            {
                var quest = db.Quests.FirstOrDefault(q => q.Id == qId);
                if (quest == null) continue;

                var givers = db.QuestGivers.Where(g => g.QuestId == qId).ToList();
                var enders = db.QuestEnders.Where(e => e.QuestId == qId).ToList();
                if (givers.Count == 0 || enders.Count == 0) continue;

                foreach (var giver in givers)
                {
                    string gKey = giver.GiverId.ToString();
                    var gSpawns = giver.GiverType == QuestObjectType.GameObject
                        ? (db.GameObjectSpawns.TryGetValue(gKey, out var gs) ? gs : null)
                        : (db.CreatureSpawns.TryGetValue(gKey, out var cs) ? cs : null);
                    if (gSpawns == null || gSpawns.Count == 0) continue;

                    foreach (var ender in enders)
                    {
                        string eKey = ender.EnderId.ToString();
                        var eSpawns = ender.EnderType == QuestObjectType.GameObject
                            ? (db.GameObjectSpawns.TryGetValue(eKey, out var es) ? es : null)
                            : (db.CreatureSpawns.TryGetValue(eKey, out var ces) ? ces : null);
                        if (eSpawns == null || eSpawns.Count == 0) continue;

                        var g0 = gSpawns[0];
                        var e0 = eSpawns[0];

                        double dx = g0.X - e0.X;
                        double dy = g0.Y - e0.Y;
                        double dz = g0.Z - e0.Z;
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist > 5000.0)
                        {
                            string extra = g0.Map != e0.Map
                                ? $" (different maps: {g0.Map} vs {e0.Map})" : "";
                            Log($"FAR: {quest.Name} ({qId}) — giver {giver.GiverName}({giver.GiverId}) to ender {ender.EnderName}({ender.EnderId}) = {dist:F0}yd{extra}");
                        }
                    }
                }
            }
        }

        internal void Log(string message)
        {
            Logging.Write(System.Drawing.Color.CornflowerBlue, $"[{Name}] {message}");
        }

        private void LoadVendorBlacklist()
        {
            if (!File.Exists(_vendorBlacklistPath)) return;
            try
            {
                string text = File.ReadAllText(_vendorBlacklistPath).Trim();
                _settings.VendorBlacklistText = text;
                SyncVendorBlacklist();
                Log($"Loaded {_settings.BlacklistedVendors.Count} blacklisted vendors");
            }
            catch (Exception ex)
            {
                Log($"Failed to load vendor blacklist: {ex.Message}");
            }
        }

        private void SaveVendorBlacklist()
        {
            SyncVendorBlacklist();
            try
            {
                string dir = Path.GetDirectoryName(_vendorBlacklistPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_vendorBlacklistPath, _settings.VendorBlacklistText);
            }
            catch (Exception ex)
            {
                Log($"Failed to save vendor blacklist: {ex.Message}");
            }
        }

        private void SyncVendorBlacklist()
        {
            foreach (int entry in _settings.BlacklistedVendors)
                Styx.Logic.Profiles.VendorManager.RejectVendor(entry, "Wholesome saved vendor blacklist");
        }

        private void OnRecoverySettingsChanged()
        {
            var manuallyExcluded = QuestRecoveryManager.Instance.GetEntries()
                .Where(record => record.State == QuestRecoveryState.ManualBlacklist)
                .Select(record => record.Key.QuestId)
                .Distinct()
                .ToArray();
            foreach (uint questId in manuallyExcluded)
            {
                ReleaseManuallyExcludedOwnership(
                    _attemptOwnership,
                    questId,
                    owner => _scheduler?.ReleaseActivation(owner as ForcedBehavior));
            }
            RequestRefresh("Manual quest exclusions changed; queuing one scheduler rebuild.");
        }
    }
}

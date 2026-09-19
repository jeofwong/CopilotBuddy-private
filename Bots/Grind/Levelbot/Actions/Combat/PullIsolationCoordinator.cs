using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.Logic.POI;
using Styx.Logic.Profiles;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;

namespace Levelbot.Actions.Combat
{
    public sealed class PullIsolationObservation
    {
        public ulong Guid { get; set; }
        public WoWPoint Location { get; set; }
        public float AggroRange { get; set; }
        public bool IsEngaged { get; set; }
        public bool IsAttackable { get; set; }
    }

    /// <summary>
    /// LevelBot-owned dense-pack isolation. World risk, approach and retreat remain
    /// here; the active combat routine owns only the class-specific ranged opener.
    /// </summary>
    public static class PullIsolationCoordinator
    {
        private const float SocialRadius = 10f;
        private const float AggroPadding = 2f;
        private const float RetreatDistance = 10f;
        private const double SeparationDistance = 10d;
        private const double MeleeStopDistance = 7d;
        private static readonly TimeSpan EngagementTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RetreatTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan UnsafeTargetBackoff = TimeSpan.FromSeconds(15);

        private sealed class PullPlan
        {
            public ulong TargetGuid;
            public uint TargetEntry;
            public WoWPoint PackOrigin;
            public WoWPoint PullPoint;
            public WoWPoint RetreatAnchor;
            public double PullRange;
            public IIsolationPullProvider Provider;
            public Composite Opener;
            public bool OpenerStarted;
            public bool AwaitingEngagement;
            public bool Retreating;
            public DateTime DeadlineUtc;
        }

        private static PullPlan _plan;

        public static Composite CreatePreCombatBehavior() =>
            new TreeSharp.Action(context => TickPreCombat(context));

        public static Composite CreateRetreatBehavior() =>
            new TreeSharp.Action(context => TickRetreat(context));

        public static void Reset()
        {
            Reset(null, clearMovement: true);
        }

        internal static bool IsContextEligible(
            bool isDungeon,
            bool isBattleground,
            bool isArena,
            bool targetIsPlayer,
            bool targetIsElite,
            bool onTransport)
        {
            return !isDungeon && !isBattleground && !isArena &&
                   !targetIsPlayer && !targetIsElite && !onTransport;
        }

        internal static WoWPoint ComputeRetreatAnchor(
            WoWPoint playerLocation,
            WoWPoint targetLocation,
            float retreatDistance)
        {
            float dx = playerLocation.X - targetLocation.X;
            float dy = playerLocation.Y - targetLocation.Y;
            float dz = playerLocation.Z - targetLocation.Z;
            float length = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length <= 0.001f || retreatDistance <= 0)
                return playerLocation;

            float scale = retreatDistance / length;
            return new WoWPoint(
                playerLocation.X + dx * scale,
                playerLocation.Y + dy * scale,
                playerLocation.Z + dz * scale);
        }

        internal static bool HasDensePack(
            WoWPoint targetLocation,
            WoWPoint approachPoint,
            IReadOnlyList<PullIsolationObservation> observations,
            ulong targetGuid,
            float socialRadius,
            float aggroPadding)
        {
            if (observations == null)
                return false;

            foreach (PullIsolationObservation observation in observations)
            {
                if (observation == null || observation.Guid == 0 || observation.Guid == targetGuid ||
                    !observation.IsAttackable || observation.IsEngaged)
                    continue;

                if (observation.Location.Distance(targetLocation) <= socialRadius)
                    return true;

                float radius = Math.Max(0f, observation.AggroRange) + Math.Max(0f, aggroPadding);
                if (radius > 0f && observation.Location.Distance(approachPoint) <= radius)
                    return true;
            }
            return false;
        }

        internal static bool ShouldContinueRetreat(
            bool sameTarget,
            bool targetAlive,
            int engagedCount,
            bool safeAnchor,
            double targetDistanceFromPack,
            double targetDistanceToPlayer,
            double requiredSeparation,
            double meleeStopDistance)
        {
            return sameTarget && targetAlive && engagedCount == 1 && safeAnchor &&
                   targetDistanceFromPack < requiredSeparation &&
                   targetDistanceToPlayer > meleeStopDistance;
        }

        private static RunStatus TickPreCombat(object context)
        {
            LocalPlayer me = StyxWoW.Me;
            if (me == null || !me.IsValid || !me.IsAlive)
            {
                Reset(context, clearMovement: true);
                return RunStatus.Failure;
            }

            if (_plan != null)
            {
                WoWUnit owned = me.CurrentTarget;
                if (!OwnsTarget(owned))
                {
                    Reset(context, clearMovement: true);
                    return RunStatus.Failure;
                }

                if (DateTime.UtcNow >= _plan.DeadlineUtc)
                    return RejectCurrentTarget(context, owned, "isolation pull timed out before engagement");

                if (_plan.AwaitingEngagement)
                {
                    if (owned.IsTargetingMeOrPet || owned.IsTargetingAnyMinion || owned.Combat || me.Combat)
                    {
                        _plan.Retreating = true;
                        _plan.AwaitingEngagement = false;
                        _plan.DeadlineUtc = DateTime.UtcNow.Add(RetreatTimeout);
                        return TickRetreat(context);
                    }

                    TreeRoot.StatusText = "Waiting for isolated target to engage";
                    return RunStatus.Running;
                }

                return ContinueApproachAndOpen(context, me, owned);
            }

            if (BotPoi.Current.Type != PoiType.Kill)
                return RunStatus.Failure;

            WoWUnit target = me.CurrentTarget;
            IIsolationPullProvider provider = RoutineManager.Current as IIsolationPullProvider;
            if (target == null || provider == null)
                return RunStatus.Failure;

            if (!target.IsValid || !target.IsAlive || !target.Attackable || !target.IsHostile ||
                target.TaggedByOther || target.Fleeing || target.Guid == 0)
                return RunStatus.Failure;

            var map = me.CurrentMap;
            if (map == null || !IsContextEligible(
                    map.IsDungeon,
                    map.IsBattleground,
                    map.IsArena,
                    target.IsPlayer,
                    target.Elite,
                    me.IsOnTransport))
                return RunStatus.Failure;

            // First slice is intentionally solo/melee-target oriented. Ranged casters
            // and group coordination need separate threat/LOS policies.
            if (me.IsInParty || me.IsInRaid || target.MaxMana > 1)
                return RunStatus.Failure;

            double practicalRange = provider.IsolationPullDistance;
            if (double.IsNaN(practicalRange) || double.IsInfinity(practicalRange) ||
                practicalRange < 8d || practicalRange > 40d)
                return RunStatus.Failure;

            WoWPoint pullPoint = me.Location;
            if (target.Distance > practicalRange)
                pullPoint = WoWMathHelper.CalculatePointFrom(
                    me.Location, target.Location, (float)(practicalRange - 1d));

            PullIsolationObservation[] observations = ObserveWorld(target.Guid);
            if (!HasDensePack(
                    target.Location,
                    pullPoint,
                    observations,
                    target.Guid,
                    SocialRadius,
                    AggroPadding))
                return RunStatus.Failure;

            if (!IsRoutePointSafe(me.Location, pullPoint, target.Guid, observations))
                return RejectCurrentTarget(context, target, "dense-pack pull point crosses another aggro envelope");

            WoWPoint retreatAnchor;
            if (!TryFindRetreatAnchor(pullPoint, target.Location, target.Guid, observations, out retreatAnchor))
                return RejectCurrentTarget(context, target, "no safe retreat anchor was available");

            _plan = new PullPlan
            {
                TargetGuid = target.Guid,
                TargetEntry = target.Entry,
                PackOrigin = target.Location,
                PullPoint = pullPoint,
                RetreatAnchor = retreatAnchor,
                PullRange = practicalRange,
                Provider = provider,
                DeadlineUtc = DateTime.UtcNow.Add(EngagementTimeout)
            };

            Logging.Write("[PullIsolation] Dense pack around {0}; ranged pull at up to {1:0.#} yd, then retreating about {2:0.#} yd.",
                target.Name, practicalRange, RetreatDistance);
            return ContinueApproachAndOpen(context, me, target);
        }

        private static RunStatus ContinueApproachAndOpen(object context, LocalPlayer me, WoWUnit target)
        {
            if (_plan == null || !OwnsTarget(target))
                return RunStatus.Failure;

            double distanceToPullPoint = me.Location.Distance(_plan.PullPoint);
            if (distanceToPullPoint > Math.Max(1.5, Navigator.PathPrecision))
            {
                if (!IsCurrentRouteSafe(me.Location, _plan.PullPoint, target.Guid))
                    return RejectCurrentTarget(context, target, "approach route became unsafe");

                TreeRoot.StatusText = "Moving to safe ranged pull point";
                MoveResult move = Navigator.MoveTo(_plan.PullPoint);
                if (move == MoveResult.Failed || move == MoveResult.PathGenerationFailed)
                    return RejectCurrentTarget(context, target, "safe ranged pull point became unreachable");
                return RunStatus.Running;
            }

            if (target.Distance > _plan.PullRange || !target.InLineOfSpellSight)
                return RejectCurrentTarget(context, target, "safe pull point does not provide valid ranged line of sight");

            if (!TryRefreshRetreatAnchor(me, target))
                return RejectCurrentTarget(context, target, "retreat anchor became unsafe before the opener");

            if (me.IsMoving)
            {
                Navigator.Clear();
                WoWMovement.MoveStop();
                TreeRoot.StatusText = "Stopping at safe ranged pull point";
                return RunStatus.Running;
            }

            if (_plan.Opener == null)
            {
                _plan.Opener = _plan.Provider?.CreateIsolationPullBehavior();
                if (_plan.Opener == null)
                    return RejectCurrentTarget(context, target, "combat routine did not provide a ranged opener");
            }

            if (!_plan.OpenerStarted)
            {
                _plan.Opener.Start(context);
                _plan.OpenerStarted = true;
            }

            RunStatus status = _plan.Opener.Tick(context);
            if (status == RunStatus.Running)
                return RunStatus.Running;
            if (status != RunStatus.Success)
                return RejectCurrentTarget(context, target, "class ranged opener was unavailable");

            _plan.OpenerStarted = false;
            _plan.AwaitingEngagement = true;
            _plan.DeadlineUtc = DateTime.UtcNow.Add(EngagementTimeout);
            TreeRoot.StatusText = "Ranged pull sent; waiting to peel target from pack";
            return RunStatus.Running;
        }

        private static RunStatus TickRetreat(object context)
        {
            PullPlan plan = _plan;
            LocalPlayer me = StyxWoW.Me;
            if (plan == null || me == null || !me.IsValid || !me.IsAlive)
                return RunStatus.Failure;

            WoWUnit target = me.CurrentTarget;
            if (plan.AwaitingEngagement)
            {
                if (!OwnsTarget(target))
                {
                    Reset(context, clearMovement: true);
                    return RunStatus.Failure;
                }

                bool targetEngaged = target.IsTargetingMeOrPet || target.IsTargetingAnyMinion ||
                                     target.Combat || me.Combat;
                if (!targetEngaged)
                    return RunStatus.Running;

                plan.AwaitingEngagement = false;
                plan.Retreating = true;
                plan.DeadlineUtc = DateTime.UtcNow.Add(RetreatTimeout);
            }

            if (!plan.Retreating)
                return RunStatus.Failure;

            bool sameTarget = OwnsTarget(target);
            int engagedCount = Targeting.GetAggroOnMeWithin(me.Location, 50f);
            bool safeAnchor = IsCurrentRouteSafe(me.Location, plan.RetreatAnchor, plan.TargetGuid);
            double fromPack = target?.Location.Distance(plan.PackOrigin) ?? double.MaxValue;
            double toPlayer = target?.Distance ?? 0d;

            if (DateTime.UtcNow >= plan.DeadlineUtc ||
                !ShouldContinueRetreat(
                    sameTarget,
                    target?.IsAlive == true,
                    engagedCount,
                    safeAnchor,
                    fromPack,
                    toPlayer,
                    SeparationDistance,
                    MeleeStopDistance))
            {
                string reason = engagedCount > 1
                    ? "additional aggro detected"
                    : fromPack >= SeparationDistance
                        ? "target separated from pack"
                        : toPlayer <= MeleeStopDistance
                            ? "target reached close combat"
                            : "retreat ownership ended";
                Logging.Write("[PullIsolation] Ending retreat for {0}: {1}.",
                    target?.Name ?? "target", reason);
                Reset(context, clearMovement: true);
                return RunStatus.Failure;
            }

            if (me.Location.Distance(plan.RetreatAnchor) <= Math.Max(1.5, Navigator.PathPrecision))
            {
                TreeRoot.StatusText = "Holding safe pull anchor while target closes";
                return RunStatus.Running;
            }

            TreeRoot.StatusText = "Retreating to isolate pulled target";
            MoveResult move = Navigator.MoveTo(plan.RetreatAnchor);
            if (move == MoveResult.Failed || move == MoveResult.PathGenerationFailed)
            {
                Reset(context, clearMovement: true);
                return RunStatus.Failure;
            }

            return RunStatus.Running;
        }

        private static bool OwnsTarget(WoWUnit target)
        {
            return _plan != null && target != null && target.IsValid && target.Guid != 0 &&
                   target.Guid == _plan.TargetGuid && target.Entry == _plan.TargetEntry;
        }

        private static PullIsolationObservation[] ObserveWorld(ulong targetGuid)
        {
            try
            {
                return ObjectManager.CachedUnits
                    .Where(unit => unit != null && unit.Guid != 0 && unit.Guid != targetGuid)
                    .Select(unit => new PullIsolationObservation
                    {
                        Guid = unit.Guid,
                        Location = unit.Location,
                        AggroRange = unit.MyAggroRange,
                        IsEngaged = unit.Combat || unit.Aggro || unit.PetAggro ||
                                    unit.IsTargetingMeOrPet || unit.IsTargetingAnyMinion ||
                                    unit.TappedByAllThreatLists,
                        IsAttackable = unit.IsValid && unit.IsAlive && unit.Attackable &&
                                       unit.IsHostile && !unit.IsPlayer && !unit.IsPet &&
                                       !unit.IsNonCombatPet && !unit.IsCritter
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[PullIsolation] World observation failed: {0}", ex.Message);
                return null;
            }
        }

        private static bool TryFindRetreatAnchor(
            WoWPoint pullPoint,
            WoWPoint targetLocation,
            ulong targetGuid,
            IReadOnlyList<PullIsolationObservation> observations,
            out WoWPoint anchor)
        {
            WoWPoint straight = ComputeRetreatAnchor(pullPoint, targetLocation, RetreatDistance);
            if (IsRoutePointSafe(pullPoint, straight, targetGuid, observations))
            {
                anchor = straight;
                return true;
            }

            float awayHeading = (float)Math.Atan2(
                pullPoint.Y - targetLocation.Y,
                pullPoint.X - targetLocation.X);
            foreach (float degrees in new[] { 30f, -30f, 60f, -60f })
            {
                WoWPoint candidate = pullPoint.RayCast(
                    awayHeading + WoWMathHelper.DegreesToRadians(degrees),
                    RetreatDistance);
                candidate.Z = pullPoint.Z;
                if (IsRoutePointSafe(pullPoint, candidate, targetGuid, observations))
                {
                    anchor = candidate;
                    return true;
                }
            }

            anchor = pullPoint;
            return false;
        }

        private static bool TryRefreshRetreatAnchor(LocalPlayer me, WoWUnit target)
        {
            if (_plan == null)
                return false;

            PullIsolationObservation[] current = ObserveWorld(target.Guid);
            if (IsRoutePointSafe(me.Location, _plan.RetreatAnchor, target.Guid, current))
                return true;

            WoWPoint replacement;
            if (!TryFindRetreatAnchor(me.Location, target.Location, target.Guid, current, out replacement))
                return false;
            _plan.RetreatAnchor = replacement;
            return true;
        }

        private static bool IsCurrentRouteSafe(WoWPoint from, WoWPoint to, ulong targetGuid) =>
            IsRoutePointSafe(from, to, targetGuid, ObserveWorld(targetGuid));

        private static bool IsRoutePointSafe(
            WoWPoint from,
            WoWPoint to,
            ulong targetGuid,
            IReadOnlyList<PullIsolationObservation> observations)
        {
            if (!IsFinitePoint(from) || !IsFinitePoint(to))
                return false;

            try
            {
                if (!Navigator.CanNavigateFully(from, to))
                    return false;
            }
            catch
            {
                return false;
            }

            if (Targeting.IsTooNearBlackspot(ProfileManager.CurrentProfile?.Blackspots, to))
                return false;

            if (observations == null)
                return false;

            foreach (PullIsolationObservation observation in observations)
            {
                if (observation == null || observation.Guid == 0 || observation.Guid == targetGuid ||
                    !observation.IsAttackable || observation.IsEngaged)
                    continue;

                float radius = Math.Max(0f, observation.AggroRange) + AggroPadding;
                if (radius <= 0f)
                    continue;
                if (observation.Location.Distance(to) <= radius ||
                    DistanceToSegment(observation.Location, from, to) <= radius)
                    return false;
            }
            return true;
        }

        private static double DistanceToSegment(WoWPoint point, WoWPoint start, WoWPoint end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double dz = end.Z - start.Z;
            double lengthSqr = dx * dx + dy * dy + dz * dz;
            if (lengthSqr <= 0.000001)
                return point.Distance(start);

            double t = ((point.X - start.X) * dx +
                        (point.Y - start.Y) * dy +
                        (point.Z - start.Z) * dz) / lengthSqr;
            t = Math.Max(0d, Math.Min(1d, t));
            var closest = new WoWPoint(
                (float)(start.X + t * dx),
                (float)(start.Y + t * dy),
                (float)(start.Z + t * dz));
            return point.Distance(closest);
        }

        private static bool IsFinitePoint(WoWPoint point)
        {
            return !float.IsNaN(point.X) && !float.IsNaN(point.Y) && !float.IsNaN(point.Z) &&
                   !float.IsInfinity(point.X) && !float.IsInfinity(point.Y) && !float.IsInfinity(point.Z);
        }

        private static RunStatus RejectCurrentTarget(object context, WoWUnit target, string reason)
        {
            if (target != null && target.Guid != 0)
            {
                Logging.Write("[PullIsolation] Deferring {0}: {1}.", target.Name, reason);
                Blacklist.Add(target.Guid, UnsafeTargetBackoff);
                if (BotPoi.Current.Type == PoiType.Kill && BotPoi.Current.Guid == target.Guid)
                    BotPoi.Clear("Dense-pack isolation deferred target");
                if (StyxWoW.Me?.CurrentTarget?.Guid == target.Guid)
                    StyxWoW.Me.ClearTarget();
            }
            Reset(context, clearMovement: true);
            return RunStatus.Success;
        }

        private static void Reset(object context, bool clearMovement)
        {
            PullPlan prior = _plan;
            _plan = null;
            if (prior?.OpenerStarted == true && prior.Opener != null)
            {
                try { prior.Opener.Stop(context); }
                catch (Exception ex) { Logging.WriteDebug("[PullIsolation] Opener cleanup failed: {0}", ex.Message); }
            }

            if (clearMovement)
            {
                try { Navigator.Clear(); }
                catch { }
            }
        }
    }
}

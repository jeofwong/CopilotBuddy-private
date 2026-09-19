using System;
using System.Collections.Generic;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic.Combat
{
    /// <summary>
    /// One engagement owner shared by Combat Bot and runtime routines. Original
    /// 3.3.5a object/threat observations only; selected targets are not permission.
    /// </summary>
    public static class GroupCombatSafety
    {
        public static bool IsRestricted =>
            string.Equals(BotManager.Current?.Name, "Combat Bot", StringComparison.OrdinalIgnoreCase)
            && StyxWoW.Me != null && StyxWoW.Me.CurrentMap.IsDungeon;

        public static bool MayAttack(WoWUnit target) => target != null
            && (!IsRestricted || IsEngagedWithGroup(target));

        public static bool MayAttackCurrentTarget() => MayAttack(StyxWoW.Me?.CurrentTarget);

        public static bool IsEngagedWithGroup(WoWUnit target)
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsValid || !me.IsAlive
                || target == null || !target.IsValid || !target.IsAlive
                || target.IsFriendly || !target.CanSelect || !target.Attackable || !target.Combat)
                return false;

            // Both the enemy's current combat state and its link to our group must
            // be observed. A tank in another fight can select an untouched next pack.
            if (target.Aggro || target.PetAggro || target.IsTargetingMeOrPet
                || target.IsTargetingAnyMinion || target.IsTargetingMyPartyMember
                || target.IsTargetingMyRaidMember || target.TaggedByMe)
                return true;

            if (HasPositiveThreat(target, me)) return true;
            IEnumerable<WoWPlayer> roster = me.IsInRaid ? me.RaidMembers
                : me.IsInParty ? me.PartyMembers : Array.Empty<WoWPlayer>();
            foreach (var member in roster)
                if (member != null && member.IsValid && member.IsAlive && HasPositiveThreat(target, member))
                    return true;
            return false;
        }

        private static bool HasPositiveThreat(WoWUnit enemy, WoWUnit member)
        {
            // The host exposes a signed client field through uint. Do not interpret
            // negative/wrapped values or the default empty entry as positive threat.
            uint value = enemy.GetThreatInfoFor(member).ThreatValue;
            return value > 0 && value <= int.MaxValue;
        }
    }
}

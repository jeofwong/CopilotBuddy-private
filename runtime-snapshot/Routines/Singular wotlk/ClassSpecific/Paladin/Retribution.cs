using System;
using System.Linq;
using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;
using Singular.Settings;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Combat;

using TreeSharp;

namespace Singular.ClassSpecific.Paladin
{
    public class Retribution
    {

        #region Properties & Fields

        // WotLK QC: Removed T13 (Dragon Soul, Cata 4.3) dead code — doesn't exist in WotLK, was never referenced.

        #endregion

        #region Heal
        [Class(WoWClass.Paladin)]
        [Spec(TalentSpec.RetributionPaladin)]
        [Behavior(BehaviorType.Heal)]
        [Context(WoWContext.All)]
        public static Composite CreateRetributionPaladinHeal()
        {
            return new PrioritySelector(
                //Spell.WaitForCast(),
                // Lay on Hands: emergency heal, gives Forbearance — only use if no Forbearance active
                Spell.Cast("Lay on Hands", ret => StyxWoW.Me,
                           ret => StyxWoW.Me.HealthPercent <= SingularSettings.Instance.Paladin.LayOnHandsHealth &&
                                  !StyxWoW.Me.HasAura("Forbearance")),
                Common.CreatePaladinDispelBehavior(),
                // Holy Light: primary heal (big, slow) — uses HolyLightHealth threshold
                Spell.Heal("Holy Light", ret => StyxWoW.Me,
                           ret => StyxWoW.Me.HealthPercent <= GetRetributionHealThreshold(SingularSettings.Instance.Paladin.HolyLightHealth)),
                // Flash of Light: fast cheap fallback — uses FlashOfLightHealth threshold
                Spell.Heal("Flash of Light", ret => StyxWoW.Me,
                           ret => StyxWoW.Me.HealthPercent <= GetRetributionHealThreshold(SingularSettings.Instance.Paladin.FlashOfLightHealth)));
        }

        [Class(WoWClass.Paladin)]
        [Spec(TalentSpec.RetributionPaladin)]
        [Behavior(BehaviorType.Rest)]
        [Context(WoWContext.All)]
        public static Composite CreateRetributionPaladinRest()
        {
            return new PrioritySelector( // use ooc heals if we have mana to
                new Decorator(ret => !StyxWoW.Me.HasAura("Drink") && !StyxWoW.Me.HasAura("Food"),
                    CreateRetributionPaladinHeal()),
                // Rest up damnit! Do this first, so we make sure we're fully rested.
                Rest.CreateDefaultRestBehaviour(),
                // Can we res people?
                Spell.Resurrect("Redemption"));
        }
        #endregion

        #region Normal Rotation

        // Optional LevelBot dense-pack opener. This is intentionally not part
        // of the normal Ret rotation: it provides one ranged damage submission,
        // with no taunt and no melee closing, after LevelBot validates the pull point.
        public static Composite CreateRetributionPaladinIsolationPull()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Spell.WaitForCast(false, false),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.Cast("Exorcism", ret => IsValidIsolationPullTarget())
            );
        }

        private static bool IsValidIsolationPullTarget()
        {
            var me = StyxWoW.Me;
            var target = me?.CurrentTarget;
            return me != null && target != null &&
                   SingularRoutine.CurrentWoWContext == WoWContext.Normal &&
                   TalentManager.CurrentSpec == TalentSpec.RetributionPaladin &&
                   target.IsValid && target.IsAlive && !target.IsPlayer && !target.Elite &&
                   !me.IsMoving && !me.IsOnTransport &&
                   target.Distance >= 7 && target.Distance <= 30;
        }

        [Class(WoWClass.Paladin)]
        [Spec(TalentSpec.RetributionPaladin)]
        [Behavior(BehaviorType.Pull)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Normal)]
        public static Composite CreateRetributionPaladinNormalPullAndCombat()
        {
            return new PrioritySelector(

                Safers.EnsureTarget(),
                Spell.WaitForCast(false, false),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Helpers.Common.CreateAutoAttack(true),
                Helpers.Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),

                // Defensive
                Spell.BuffSelf("Hand of Freedom",
                    ret => StyxWoW.Me.HasAuraWithMechanic(WoWSpellMechanic.Dazed,
                                                          WoWSpellMechanic.Disoriented,
                                                          WoWSpellMechanic.Frozen,
                                                          WoWSpellMechanic.Incapacitated,
                                                          WoWSpellMechanic.Rooted,
                                                          WoWSpellMechanic.Slowed,
                                                          WoWSpellMechanic.Snared)),

                    Spell.BuffSelf("Divine Shield", ret => StyxWoW.Me.HealthPercent <= 20 && !StyxWoW.Me.HasAura("Forbearance") && (!StyxWoW.Me.HasAura("Horde Flag") && !StyxWoW.Me.HasAura("Alliance Flag"))),
                    Spell.BuffSelf("Divine Protection", ret => StyxWoW.Me.HealthPercent <= SingularSettings.Instance.Paladin.DivineProtectionHealthRet && !StyxWoW.Me.HasAura("Forbearance")),

                    CreateRetributionSealBehavior(),
                    CreateManaRecoveryBehavior(),

                    //7	Blow buffs seperatly.  No reason for stacking while grinding.
                    Spell.BuffSelf("Avenging Wrath", ret => Unit.NearbyUnfriendlyUnits.Count(u => u.Distance <= 8) >= 3),
                    Spell.BuffSelf("Blood Fury", ret => SpellManager.HasSpell("Blood Fury") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),
                    Spell.BuffSelf("Berserking", ret => SpellManager.HasSpell("Berserking") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),
                    Spell.BuffSelf("Lifeblood", ret => SpellManager.HasSpell("Lifeblood") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),

                    //Exo is above HoW if we're fighting Undead / Demon
                    CreateExorcismBehavior(requireProc: true, undeadOrDemon: true),
                    //Hammer of Wrath if target < 20% HP
                    Spell.Cast("Hammer of Wrath", ret => StyxWoW.Me.CurrentTarget is { } target && target.HealthPercent <= 20), // WotLK: Sanctified Wrath does not unlock HoW above 20% (Cata-only)
                    CreateMeleeStrikeBehavior(),
                    CreateRetributionJudgementBehavior(),
                    // Disjoint windows prevent the same proc from bypassing an earlier retry guard.
                    CreateExorcismBehavior(requireProc: true, undeadOrDemon: false),
                    CreateExorcismBehavior(),
                    Spell.Cast("Holy Wrath", ret => HasHolyWrathTarget()),
                //consecration,not_flying=1,if=mana>16000
                    CreateConsecrationBehavior(),

                    // Move to melee is LAST. Period.
                    Movement.CreateMoveToMeleeBehavior(true)
                );
        }

        #endregion

        #region Battleground Rotation

        [Class(WoWClass.Paladin)]
        [Spec(TalentSpec.RetributionPaladin)]
        [Behavior(BehaviorType.Pull)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Battlegrounds)]

        public static Composite CreateRetributionPaladinPvPPullAndCombat()
        {
            HealerManager.NeedHealTargeting = true;
            return new PrioritySelector(
                    Safers.EnsureTarget(),
                    Movement.CreateMoveToLosBehavior(),
                    Movement.CreateFaceTargetBehavior(),
                    Helpers.Common.CreateAutoAttack(true),
                    Helpers.Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),

                   // Defensive
                    Spell.BuffSelf("Hand of Freedom",
                    ret => !StyxWoW.Me.Auras.Values.Any(a => a.Name.Contains("Hand of") && a.CreatorGuid == StyxWoW.Me.Guid) &&
                           StyxWoW.Me.HasAuraWithMechanic(WoWSpellMechanic.Dazed,
                                                          WoWSpellMechanic.Disoriented,
                                                          WoWSpellMechanic.Frozen,
                                                          WoWSpellMechanic.Incapacitated,
                                                          WoWSpellMechanic.Rooted,
                                                          WoWSpellMechanic.Slowed,
                                                          WoWSpellMechanic.Snared)),

                    Spell.BuffSelf("Divine Shield", ret => StyxWoW.Me.HealthPercent <= 20 && !StyxWoW.Me.HasAura("Forbearance") && (!StyxWoW.Me.HasAura("Horde Flag") && !StyxWoW.Me.HasAura("Alliance Flag"))),
                    Spell.BuffSelf("Divine Protection", ret => StyxWoW.Me.HealthPercent <= SingularSettings.Instance.Paladin.DivineProtectionHealthRet && !StyxWoW.Me.HasAura("Forbearance")),

                    //  Buffs
                    CreateRetributionSealBehavior(),
                    CreateManaRecoveryBehavior(),

                    Spell.BuffSelf("Avenging Wrath", ret => StyxWoW.Me.CurrentTarget is { } target && target.Distance <= 8),
                    Spell.BuffSelf("Blood Fury", ret => SpellManager.HasSpell("Blood Fury") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),
                    Spell.BuffSelf("Berserking", ret => SpellManager.HasSpell("Berserking") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),
                    Spell.BuffSelf("Lifeblood", ret => SpellManager.HasSpell("Lifeblood") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),

                    //Hammer of Wrath if target < 20% HP
                    Spell.Cast("Hammer of Wrath", ret => StyxWoW.Me.CurrentTarget is { } target && target.HealthPercent <= 20), // WotLK: Sanctified Wrath does not unlock HoW above 20% (Cata-only)
                    // Only an observed instant proc belongs ahead of the melee strikes.
                    CreateExorcismBehavior(requireProc: true),

                    CreateMeleeStrikeBehavior(throttleCrusaderStrike: true),
                    CreateRetributionJudgementBehavior(),
                    CreateExorcismBehavior(),
                    Spell.Cast("Holy Wrath", ret => HasHolyWrathTarget()),
                    CreateConsecrationBehavior(),

                Movement.CreateMoveToMeleeBehavior(true)
                );
        }

        #endregion

        #region Instance Rotation

        [Class(WoWClass.Paladin)]
        [Spec(TalentSpec.RetributionPaladin)]
        [Behavior(BehaviorType.Pull)]
        [Behavior(BehaviorType.Combat)]
        [Context(WoWContext.Instances)]
        public static Composite CreateRetributionPaladinInstancePullAndCombat()
        {
            return new PrioritySelector(
                    Safers.EnsureTarget(),
                    Movement.CreateMoveToLosBehavior(),
                    Movement.CreateFaceTargetBehavior(),
                    Helpers.Common.CreateAutoAttack(true),
                    Helpers.Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),

                    // Defensive
                    Spell.BuffSelf("Hand of Freedom",
                        ret => !StyxWoW.Me.Auras.Values.Any(a => a.Name.Contains("Hand of") && a.CreatorGuid == StyxWoW.Me.Guid) &&
                                StyxWoW.Me.HasAuraWithMechanic(WoWSpellMechanic.Dazed,
                                                               WoWSpellMechanic.Disoriented,
                                                               WoWSpellMechanic.Frozen,
                                                               WoWSpellMechanic.Incapacitated,
                                                               WoWSpellMechanic.Rooted,
                                                               WoWSpellMechanic.Slowed,
                                                               WoWSpellMechanic.Snared)),

                    Spell.BuffSelf("Divine Shield", ret => StyxWoW.Me.HealthPercent <= 20 && !StyxWoW.Me.HasAura("Forbearance") && (!StyxWoW.Me.HasAura("Horde Flag") && !StyxWoW.Me.HasAura("Alliance Flag"))),
                    Spell.BuffSelf("Divine Protection", ret => StyxWoW.Me.HealthPercent <= SingularSettings.Instance.Paladin.DivineProtectionHealthRet && !StyxWoW.Me.HasAura("Forbearance")),

                    CreateRetributionSealBehavior(),
                    CreateManaRecoveryBehavior(),

                    Spell.BuffSelf("Avenging Wrath", ret => StyxWoW.Me.CurrentTarget is { } target && target.IsBoss()),
                    Spell.BuffSelf("Blood Fury", ret => SpellManager.HasSpell("Blood Fury") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),
                    Spell.BuffSelf("Berserking", ret => SpellManager.HasSpell("Berserking") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),
                    Spell.BuffSelf("Lifeblood", ret => SpellManager.HasSpell("Lifeblood") && StyxWoW.Me.ActiveAuras.ContainsKey("Avenging Wrath")),

                    // Preserve the earlier undead/demon instant-proc window, not a hard cast.
                    CreateExorcismBehavior(requireProc: true, undeadOrDemon: true),
                    //Hammer of Wrath if target < 20% HP
                    Spell.Cast("Hammer of Wrath", ret => StyxWoW.Me.CurrentTarget is { } target && target.HealthPercent <= 20), // WotLK: Sanctified Wrath does not unlock HoW above 20% (Cata-only)
                    CreateExorcismBehavior(requireProc: true, undeadOrDemon: false),

                    CreateMeleeStrikeBehavior(),
                //judgement - simplified for WotLK
                    CreateRetributionJudgementBehavior(),
                    CreateExorcismBehavior(),
                //holy_wrath
                    Spell.Cast("Holy Wrath", ret => HasHolyWrathTarget()),
                //consecration,not_flying=1,if=mana>16000
                    CreateConsecrationBehavior(),

                    // Move to melee is LAST. Period.
                    Movement.CreateMoveToMeleeBehavior(true)
                );
        }

        #endregion




        // Ret policies share the existing behavior-tree dispatch, rather than a
        // second rotation engine. Choices are observed again after cast setup.
        private sealed class TacticsChoice
        {
            private readonly object player;
            private readonly object target;
            private readonly string spell;
            private readonly ulong playerGuid, targetGuid;
            private readonly TalentSpec specialization;

            internal TacticsChoice(Func<string> choose)
            {
                player = StyxWoW.Me;
                target = StyxWoW.Me?.CurrentTarget;
                playerGuid = StyxWoW.Me?.Guid ?? 0UL;
                targetGuid = StyxWoW.Me?.CurrentTarget?.Guid ?? 0UL;
                specialization = TalentManager.CurrentSpec;
                spell = choose();
            }

            private bool SameOwner => ReferenceEquals(StyxWoW.Me, player)
                && ReferenceEquals(StyxWoW.Me?.CurrentTarget, target)
                && (StyxWoW.Me?.Guid ?? 0UL) == playerGuid
                && (StyxWoW.Me?.CurrentTarget?.Guid ?? 0UL) == targetGuid
                && TalentManager.CurrentSpec == specialization;

            internal bool IsCurrent(string expected, Func<string> choose) =>
                spell == expected && SameOwner && choose() == expected && SameOwner;
        }

        private static Composite CreateTacticsBehavior(bool self, Func<string> choose, params string[] spells)
        {
            return new PrioritySelector(_ => new TacticsChoice(choose),
                spells.Select(name => Spell.Cast(name,
                    context => context is TacticsChoice choice && choice.IsCurrent(name, choose)
                        ? (self ? StyxWoW.Me : StyxWoW.Me.CurrentTarget) : null,
                    _ => true)).ToArray());
        }

        internal static Composite CreateRetributionSealBehavior() =>
            CreateTacticsBehavior(true, SelectRetributionSeal,
                "Seal of Command", "Seal of Corruption", "Seal of Justice", "Seal of Light",
                "Seal of Righteousness", "Seal of Vengeance", "Seal of Wisdom");

        private static string SelectRetributionSeal()
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsValid || !me.IsAlive || me.Mounted || me.IsOnTransport
                || me.IsCasting || me.IsChanneling || me.HasAura("Food") || me.HasAura("Drink"))
                return null;

            string wanted;
            PaladinSeal configured = SingularSettings.Instance.Paladin.Seal;
            if (configured != PaladinSeal.Auto)
            {
                // A deliberate recovery/control seal must not fight precombat Auto.
                wanted = "Seal of " + configured;
                if (!SpellManager.HasSpell(wanted)) return null;
            }
            else
            {
                var target = me.CurrentTarget;
                // Player combat values immediate damage and controlled CC. The
                // dungeon area guard is not an arena safety observation. Keep
                // automatic cleave/DoT seals out when Righteousness is learned;
                // explicit Command remains available for a deliberate assignment.
                if (target != null && target.IsPlayer && SpellManager.HasSpell("Seal of Righteousness"))
                    return me.HasAura("Seal of Righteousness") ? null : "Seal of Righteousness";
                string stacking = SpellManager.HasSpell("Seal of Corruption") ? "Seal of Corruption"
                    : SpellManager.HasSpell("Seal of Vengeance") ? "Seal of Vengeance" : null;
                bool safeCleave = target == null || Unit.IsAreaEffectSafe("Divine Storm", target);
                int nearby = Unit.NearbyUnfriendlyUnits.Count(u => u.IsValid && u.IsAlive && u.Distance <= 8);
                bool boss = target != null && target.IsBoss();
                bool command = SpellManager.HasSpell("Seal of Command") && safeCleave
                    && ((!boss && (nearby >= 3 || nearby >= 2 && me.HasAura("Seal of Command"))) || stacking == null);
                wanted = command ? "Seal of Command" : stacking
                    ?? (SpellManager.HasSpell("Seal of Righteousness") ? "Seal of Righteousness" : null);
                // Damage seals stay preferred in groups; mana is recovered with
                // judgements/Plea, not an automatic DPS-to-Wisdom seal swap.
            }
            return wanted != null && !me.HasAura(wanted) ? wanted : null;
        }

        private static Composite CreateRetributionJudgementBehavior() =>
            CreateTacticsBehavior(false, SelectRetributionJudgement,
                "Judgement of Wisdom", "Judgement of Light", "Judgement of Justice");

        private static string SelectRetributionJudgement()
        {
            var me = StyxWoW.Me;
            var target = me?.CurrentTarget;
            if (me == null || target == null || !me.IsValid || !me.IsAlive || !target.IsValid || !target.IsAlive)
                return null;
            if ((target.Fleeing || target.IsPlayer && (target.IsMoving || target.HasAura("Horde Flag") || target.HasAura("Alliance Flag")))
                && SpellManager.HasSpell("Judgement of Justice"))
                return "Judgement of Justice";

            bool grouped = me.IsInParty || me.IsInRaid;
            // Auras belong to their caster. Our own Wisdom is not a partner's
            // coverage, and expired/unknown-owner observations cannot cover it.
            var auras = target.GetAllAuras().ToArray();
            bool externalWisdom = auras.Any(a => a != null && a.Name == "Judgement of Wisdom"
                && a.IsActive && a.TimeLeft > TimeSpan.FromSeconds(2) && a.CreatorGuid != 0 && a.CreatorGuid != me.Guid);
            bool externalLight = auras.Any(a => a != null && a.Name == "Judgement of Light"
                && a.IsActive && a.TimeLeft > TimeSpan.FromSeconds(2) && a.CreatorGuid != 0 && a.CreatorGuid != me.Guid);
            bool preferLight = grouped ? externalWisdom && !externalLight : me.HealthPercent < 70 && me.ManaPercent >= 40;
            string preferred = preferLight ? "Judgement of Light" : "Judgement of Wisdom";
            string fallback = preferLight ? "Judgement of Wisdom" : "Judgement of Light";
            return SpellManager.HasSpell(preferred) ? preferred
                : SpellManager.HasSpell(fallback) ? fallback : null;
        }

        private static Composite CreateManaRecoveryBehavior() => new PrioritySelector(
            // Talented damaging judgements also restore mana. Keep the cheap
            // judgement available before spending the last mana on melee strikes.
            new Decorator(_ => StyxWoW.Me != null && StyxWoW.Me.ManaPercent <= 15,
                CreateRetributionJudgementBehavior()),
            Spell.BuffSelf("Divine Plea", _ => StyxWoW.Me != null
                && StyxWoW.Me.ManaPercent < SingularSettings.Instance.Paladin.DivinePleaMana
                && StyxWoW.Me.HealthPercent > 70));

        private static Composite CreateConsecrationBehavior() =>
            CreateTacticsBehavior(false, SelectConsecration, "Consecration");

        private static string SelectConsecration()
        {
            var me = StyxWoW.Me;
            var target = me?.CurrentTarget;
            if (me == null || target == null || !me.IsValid || !me.IsAlive
                || !target.IsValid || !target.IsAlive || me.Mounted || me.IsOnTransport
                || me.IsMoving || target.IsMoving || me.IsCasting || me.IsChanneling
                || target.Distance > Spell.MeleeRange
                || me.ManaPercent <= SingularSettings.Instance.Paladin.DivinePleaMana
                || !SpellManager.HasSpell("Consecration")
                || !Unit.IsAreaEffectSafe("Consecration", target))
                return null;

            // A sustained boss can use the ground damage even without adds. This
            // is a conservative filler, not a forecast of target lifetime or DPS.
            // Keep the configured pack threshold and reserve recovery mana; the
            // real spell layer still checks current cost, cooldown and safety.
            int nearby = Unit.NearbyUnfriendlyUnits.Count(u => u.IsValid && u.IsAlive && u.Distance <= 8);
            return target.IsBoss() || nearby >= SingularSettings.Instance.Paladin.ConsecrationCount
                ? "Consecration" : null;
        }

        private static bool HasHolyWrathTarget() =>
            Unit.NearbyUnfriendlyUnits.Any(u => u.IsValid && u.IsAlive && u.Distance <= 10 && u.IsUndeadOrDemon());

        private static int GetRetributionHealThreshold(int configured)
        {
            var me = StyxWoW.Me;
            return me != null && me.Combat && (me.IsInParty || me.IsInRaid)
                ? Math.Min(configured, SingularSettings.Instance.Paladin.RetributionHealHealth) : configured;
        }

        // Independent ready attacks must not disable one another merely because
        // another spell is known or the target count crosses an arbitrary gate.
        // Spell.Cast retains the real spellbook, cooldown, range and area-safety
        // checks. Preserve the existing CS-before-DS order and PvP retry throttle.
        private static Composite CreateMeleeStrikeBehavior(bool throttleCrusaderStrike = false)
        {
            Composite crusaderStrike = Spell.Cast("Crusader Strike", ret =>
                StyxWoW.Me.CurrentTarget is { } target && target.IsWithinMeleeRange);
            return new PrioritySelector(
                throttleCrusaderStrike ? new Throttle(1, crusaderStrike) : crusaderStrike,
                Spell.Cast("Divine Storm", ret =>
                    StyxWoW.Me.CurrentTarget is { } target && target.Distance <= 8));
        }

        // Use one policy in every active context. Each priority window owns a
        // disjoint set of proc states, so an unconfirmed attempt cannot immediately
        // fall through to another Exorcism node with a fresh retry budget.
        private static Composite CreateExorcismBehavior(bool requireProc = false, bool? undeadOrDemon = null)
        {
            return CreateExorcismRetryBehavior(Spell.Cast("Exorcism", ret =>
            {
                var player = StyxWoW.Me;
                var target = player?.CurrentTarget;
                if (target == null)
                    return false;
                bool proc = player.ActiveAuras.ContainsKey("The Art of War");
                return proc == requireProc
                       && (!undeadOrDemon.HasValue || target.IsUndeadOrDemon() == undeadOrDemon.Value)
                       && (proc || !player.IsMoving)
                       && ShouldCastExorcism(SpellManager.HasSpell("The Art of War"), proc,
                           target.IsWithinMeleeRange, player.IsAutoAttacking);
            }));
        }

        private static Composite CreateExorcismRetryBehavior(Composite cast)
        {
            // Cast reports dispatch, not server acceptance. Yield to other attacks and
            // melee movement between attempts when range/LOS checks disagree with the client.
            return new Throttle(3, cast);
        }

        private static bool ShouldCastExorcism(
            bool knowsArtOfWar,
            bool hasArtOfWarProc,
            bool targetInMeleeRange,
            bool autoAttacking)
        {
            // AutoAttack may already be enabled while approaching at range.
            // Protect an active melee opportunity, not that flag in isolation.
            // A real proc remains usable if passive-talent discovery is incomplete.
            return hasArtOfWarProc || (!knowsArtOfWar && !(targetInMeleeRange && autoAttacking));
        }
    }
}
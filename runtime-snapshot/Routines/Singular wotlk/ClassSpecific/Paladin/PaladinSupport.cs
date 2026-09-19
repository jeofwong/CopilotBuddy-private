using System;
using System.Collections.Generic;
using System.Linq;
using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;
using Singular.Settings;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Combat;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;

namespace Singular.ClassSpecific.Paladin
{
    // WotLK 3.3.5a support decisions. No modern role, aura or spellbook Lua APIs.
    public partial class Common
    {
        private sealed class SupportAction
        {
            internal string Spell;
            internal WoWPlayer Target;
            internal Func<WoWPlayer, string> Revalidate;
        }

        private static bool CanMaintainSupport()
        {
            var me = StyxWoW.Me;
            return me != null && me.IsValid && me.IsAlive && !me.Mounted && !me.IsOnTransport
                && !me.IsCasting && !me.IsChanneling && !me.HasAura("Food") && !me.HasAura("Drink");
        }

        private static bool IsSupportRecipient(WoWPlayer player) =>
            player != null && player.IsValid && player.IsAlive && player.IsFriendly
            && (player.IsMe || player.DistanceSqr < 40 * 40 && player.InLineOfSpellSight);

        private static IEnumerable<WoWPlayer> SupportRecipients(bool includeGroup)
        {
            var me = StyxWoW.Me;
            if (me == null) yield break;
            if (IsSupportRecipient(me)) yield return me;
            if (!includeGroup) yield break;
            // Roster membership, not inferred class/spec or inspect-derived healing roles.
            var members = me.IsInRaid ? me.RaidMembers : me.IsInParty ? me.PartyMembers : Enumerable.Empty<WoWPlayer>();
            var seen = new HashSet<ulong> { me.Guid };
            foreach (var player in members)
                if (IsSupportRecipient(player) && seen.Add(player.Guid)) yield return player;
        }

        private static bool IsCurrentRecipient(WoWPlayer player, bool includeGroup) =>
            IsSupportRecipient(player) && SupportRecipients(includeGroup).Any(p => p.Guid == player.Guid);

        private static WoWAura[] SupportAuras(WoWPlayer player) =>
            player.GetAllAuras().Where(a => a != null && a.IsActive).ToArray();

        private enum PallyPowerReadStatus
        {
            Absent,
            Verified,
            Uncertain
        }

        private sealed class PallyPowerAssignment
        {
            internal PallyPowerReadStatus Status;
            internal string Blessing;
            internal string Aura;
        }

        // PallyPower v3.2.21 Wrath mapping, corroborated by the reviewed W61
        // upload and public source d8f7a78de637e73d7d3e44a67721623f1d3fcd07.
        // Raw slot numbers are never interpreted unless the loaded addon itself
        // reports IsWrath and the explicit "Wrath" tables are readable.
        private static readonly string[] PallyPowerWrathBlessings =
        {
            null, "Blessing of Wisdom", "Blessing of Might",
            "Blessing of Kings", "Blessing of Sanctuary"
        };

        private static readonly string[] PallyPowerWrathAuras =
        {
            null, "Devotion Aura", "Retribution Aura", "Concentration Aura",
            "Shadow Resistance Aura", "Frost Resistance Aura", "Fire Resistance Aura",
            "Crusader Aura"
        };

        private static int PallyPowerClassIndex(WoWClass wowClass)
        {
            switch (wowClass)
            {
                case WoWClass.Warrior: return 1;
                case WoWClass.Rogue: return 2;
                case WoWClass.Priest: return 3;
                case WoWClass.Druid: return 4;
                case WoWClass.Paladin: return 5;
                case WoWClass.Hunter: return 6;
                case WoWClass.Mage: return 7;
                case WoWClass.Warlock: return 8;
                case WoWClass.Shaman: return 9;
                case WoWClass.DeathKnight: return 10;
                default: return 0;
            }
        }

        private static PallyPowerAssignment ReadPallyPowerAssignment(WoWPlayer player)
        {
            if (player == null || string.IsNullOrEmpty(player.Name))
                return new PallyPowerAssignment { Status = PallyPowerReadStatus.Uncertain };

            int classIndex = PallyPowerClassIndex(player.Class);
            if (classIndex == 0)
                return new PallyPowerAssignment { Status = PallyPowerReadStatus.Uncertain };

            string target = Lua.Escape(player.Name);
            string query =
                "local classIndex=" + classIndex + ";local targetName=\"" + target + "\";" +
                "if type(IsAddOnLoaded)=='function' and not IsAddOnLoaded('PallyPower') then return '0' end;" +
                "if type(GetAddOnMetadata)~='function' then return '2' end;" +
                "local ppVersion=GetAddOnMetadata('PallyPower','Version');if ppVersion~='v3.2.21' then return '2' end;" +
                "local pp=PallyPower;if type(pp)~='table' then return '2' end;" +
                "if pp.IsWrath~=true then return '2' end;" +
                "local a=PallyPower_Assignments and PallyPower_Assignments[\"Wrath\"];" +
                "local n=PallyPower_NormalAssignments and PallyPower_NormalAssignments[\"Wrath\"];" +
                "local u=PallyPower_AuraAssignments and PallyPower_AuraAssignments[\"Wrath\"];" +
                "if type(a)~='table' or type(n)~='table' or type(u)~='table' or type(pp.player)~='string' then return '2' end;" +
                "local p=pp.player;local classSlot=0;local normalSlot=0;local auraSlot=tonumber(u[p]) or 0;" +
                "if type(a[p])=='table' then classSlot=tonumber(a[p][classIndex]) or 0 end;" +
                "if type(n[p])=='table' and type(n[p][classIndex])=='table' then normalSlot=tonumber(n[p][classIndex][targetName]) or 0 end;" +
                "return '1',tostring(classSlot),tostring(normalSlot),tostring(auraSlot)";

            List<string> values;
            try
            {
                values = Lua.GetReturnValues(query);
            }
            catch
            {
                return new PallyPowerAssignment { Status = PallyPowerReadStatus.Uncertain };
            }

            if (values == null || values.Count == 0)
                return new PallyPowerAssignment { Status = PallyPowerReadStatus.Uncertain };
            if (values[0] == "0")
                return new PallyPowerAssignment { Status = PallyPowerReadStatus.Absent };
            if (values[0] != "1" || values.Count != 4)
                return new PallyPowerAssignment { Status = PallyPowerReadStatus.Uncertain };

            int classSlot, normalSlot, auraSlot;
            if (!int.TryParse(values[1], out classSlot) ||
                !int.TryParse(values[2], out normalSlot) ||
                !int.TryParse(values[3], out auraSlot) ||
                classSlot < 0 || classSlot >= PallyPowerWrathBlessings.Length ||
                normalSlot < 0 || normalSlot >= PallyPowerWrathBlessings.Length ||
                auraSlot < 0 || auraSlot >= PallyPowerWrathAuras.Length)
                return new PallyPowerAssignment { Status = PallyPowerReadStatus.Uncertain };

            int blessingSlot = normalSlot > 0 ? normalSlot : classSlot;
            return new PallyPowerAssignment
            {
                Status = PallyPowerReadStatus.Verified,
                Blessing = PallyPowerWrathBlessings[blessingSlot],
                Aura = PallyPowerWrathAuras[auraSlot]
            };
        }

        private static bool MatchesBlessing(WoWAura aura, string name) =>
            aura.Name == name || aura.Name == "Greater " + name;

        private static string SelectBlessing(WoWPlayer player)
        {
            string normal = SelectNormalBlessing(player);
            return normal == null ? null : SelectBlessingVariant(player, normal);
        }

        private static string SelectNormalBlessing(WoWPlayer player)
        {
            if (!CanMaintainSupport() || !IsCurrentRecipient(player, true)) return null;
            var auras = SupportAuras(player).Where(a => a.TimeLeft > TimeSpan.Zero).ToArray();
            var paladinSettings = SingularSettings.Instance.Paladin;
            var setting = paladinSettings.Blessings;
            bool battleShout = auras.Any(a => a.Name == "Battle Shout");
            string[] order;
            bool assignmentControlled = false;
            if (setting != PaladinBlessings.Auto)
                order = new[] { "Blessing of " + setting };
            else
            {
                if (paladinSettings.UsePallyPowerAssignments)
                {
                    PallyPowerAssignment assignment = ReadPallyPowerAssignment(player);
                    if (assignment.Status == PallyPowerReadStatus.Uncertain)
                        return null;
                    if (assignment.Status == PallyPowerReadStatus.Verified)
                    {
                        assignmentControlled = true;
                        if (string.IsNullOrEmpty(assignment.Blessing))
                            return null;
                        order = new[] { assignment.Blessing };
                    }
                    else
                        order = null;
                }
                else
                    order = null;

                if (!assignmentControlled)
                {
                    bool caster = player.Class == WoWClass.Mage || player.Class == WoWClass.Priest
                        || player.Class == WoWClass.Warlock || player.HasAura("Moonkin Form") || player.HasAura("Tree of Life")
                        || player.IsMe && TalentManager.CurrentSpec == TalentSpec.HolyPaladin;
                    // Ret is a known damage role only for our own character. Do not
                    // invent a teammate's spec/tank assignment from its class alone.
                    bool groupedRet = player.IsMe && TalentManager.CurrentSpec == TalentSpec.RetributionPaladin
                        && (StyxWoW.Me.IsInParty || StyxWoW.Me.IsInRaid)
                        && SingularRoutine.CurrentWoWContext != WoWContext.Battlegrounds;
                    order = caster
                        ? new[] { "Blessing of Kings", "Blessing of Wisdom" }
                        : groupedRet && !battleShout
                            ? new[] { "Blessing of Might", "Blessing of Kings", "Blessing of Wisdom" }
                            : new[] { "Blessing of Kings", "Blessing of Might", "Blessing of Wisdom" };

                    // Preserve a unique, useful contribution rather than fighting an
                    // existing assignment on every pulse. Expired or duplicated buffs
                    // do not freeze selection. Explicit settings and a verified
                    // PallyPower assignment bypass this Auto contribution rule.
                    foreach (string retained in new[] { "Blessing of Kings", "Blessing of Might" })
                    {
                        if (retained == "Blessing of Might" && (caster || battleShout)) continue;
                        var owners = auras.Where(a => MatchesBlessing(a, retained)).ToArray();
                        if (owners.Any(a => a.CreatorGuid == StyxWoW.Me.Guid)
                            && !owners.Any(a => a.CreatorGuid != 0 && a.CreatorGuid != StyxWoW.Me.Guid))
                            return null;
                    }
                }
            }
            foreach (string name in order)
            {
                // Flat attack-power coverage is not the separate percentage-AP
                // category (Trueshot/Unleashed Rage/Abomination's Might).
                // Explicit preference is not permission to fight existing coverage.
                // Effective aura rank/talent strength is not observed here: defer
                // Might conservatively and reconsider when the covering aura expires.
                if (name == "Blessing of Might" && battleShout) continue;
                if (name == "Blessing of Wisdom" && player.MaxMana <= 0) continue;
                var coverage = auras.Where(a => MatchesBlessing(a, name)).ToArray();
                bool external = coverage.Any(a => a.CreatorGuid != 0 && a.CreatorGuid != StyxWoW.Me.Guid);
                // Our only useful contribution must not be replaced on the next pulse.
                // Multiple same-name owners are retained by GetAllAuras, not a name-keyed dictionary.
                if (coverage.Any(a => a.CreatorGuid == StyxWoW.Me.Guid) && !external) return null;
                if (coverage.Length != 0) continue;
                if (SpellManager.HasSpell(name) && SpellManager.CanCast(name, player)) return name;
            }
            return null;
        }

        private static string SelectBlessingVariant(WoWPlayer player, string normal)
        {
            var me = StyxWoW.Me;
            string greater = "Greater " + normal;
            if (!SingularSettings.Instance.Paladin.UseGreaterBlessings || me == null
                || me.Guid == 0 || player.Guid == 0 || me.Combat
                || !SpellManager.HasSpell(greater) || !SpellManager.CanCast(greater, player)
                || !HasGreaterBlessingReagents(greater))
                return normal;

            // Greater blessings can reach other group members of the selected
            // class. Do not silently replace an assignment or borrow coverage
            // from an unobservable member. The normal action remains available.
            var roster = me.IsInRaid ? me.RaidMembers : me.IsInParty ? me.PartyMembers : Enumerable.Empty<WoWPlayer>();
            if (roster == null) return normal;
            var members = roster.Take(41).ToArray();
            if (members.Length > 40 || members.Any(p => p == null || p.Guid == 0)) return normal;
            var seen = new HashSet<ulong>();
            foreach (var member in new[] { me }.Cast<WoWPlayer>().Concat(members))
            {
                if (member.Class != player.Class || !seen.Add(member.Guid)) continue;
                if (!IsSupportRecipient(member)) return normal;
                var auras = SupportAuras(member).Where(a => a.TimeLeft > TimeSpan.Zero).ToArray();
                if (auras.Any(a => a.CreatorGuid == me.Guid
                    && (a.Name.StartsWith("Blessing of ", StringComparison.Ordinal)
                        || a.Name.StartsWith("Greater Blessing of ", StringComparison.Ordinal))
                    && !MatchesBlessing(a, normal)))
                    return normal;
                // Evaluate only the underlying single-target policy here, not
                // this variant selector recursively. Covered/discordant members
                // deny a mass rebuff, including Might versus active Battle Shout.
                if (SelectNormalBlessing(member) != normal) return normal;
            }
            return greater;
        }

        private static bool HasGreaterBlessingReagents(string name)
        {
            WoWSpell spell;
            if (!SpellManager.Spells.TryGetValue(name, out spell) || spell == null) return false;
            var data = spell.InternalInfo;
            if (data.Reagent == null || data.ReagentCount == null
                || data.Reagent.Length != 8 || data.ReagentCount.Length != 8) return false;
            var needed = new Dictionary<uint, long>();
            for (int index = 0; index < data.Reagent.Length; index++)
            {
                int id = data.Reagent[index];
                uint count = data.ReagentCount[index];
                if (id <= 0)
                {
                    if (count != 0) return false;
                    continue;
                }
                if (count == 0) return false;
                long prior;
                needed.TryGetValue((uint)id, out prior);
                needed[(uint)id] = prior + count;
            }
            // Missing/zeroed metadata is not proof that a Greater buff is free.
            // Sum repeated reagent entries before comparing actual carried stock.
            return needed.Count > 0 && needed.All(item =>
                StyxWoW.Me.GetCarriedItemCount(item.Key) >= item.Value);
        }

        private static SupportAction FindBlessingAction() => FindSupportAction(true, SelectBlessing);

        private static SupportAction FindSupportAction(bool includeGroup, Func<WoWPlayer, string> choose)
        {
            if (!CanMaintainSupport()) return null;
            foreach (var player in SupportRecipients(includeGroup))
            {
                string spell = choose(player);
                if (spell != null) return new SupportAction { Spell = spell, Target = player, Revalidate = choose };
            }
            return null;
        }

        private static Composite CreateSupportBehavior(Func<SupportAction> choose, params string[] spellNames)
        {
            return new Throttle(2, new PrioritySelector(_ => choose(),
                spellNames.Select(name => Spell.Cast(name,
                    context => ValidSupportAction(context, name) ? ((SupportAction)context).Target : null,
                    context => ValidSupportAction(context, name))).ToArray()));
        }

        private static bool ValidSupportAction(object context, string spell)
        {
            var action = context as SupportAction;
            return action != null && action.Spell == spell && CanMaintainSupport()
                && IsSupportRecipient(action.Target) && action.Revalidate(action.Target) == spell;
        }

        private static string SelectAura(WoWPlayer player)
        {
            if (!CanMaintainSupport() || player == null || !player.IsMe || player.Guid == 0) return null;
            var paladinSettings = SingularSettings.Instance.Paladin;
            var setting = paladinSettings.Aura;
            string[] order;
            bool assignmentControlled = false;
            if (setting != PaladinAura.Auto)
                order = new[] { setting == PaladinAura.Resistance ? "Shadow Resistance Aura" : setting + " Aura" };
            else
            {
                if (paladinSettings.UsePallyPowerAssignments)
                {
                    PallyPowerAssignment assignment = ReadPallyPowerAssignment(player);
                    if (assignment.Status == PallyPowerReadStatus.Uncertain)
                        return null;
                    if (assignment.Status == PallyPowerReadStatus.Verified)
                    {
                        assignmentControlled = true;
                        if (string.IsNullOrEmpty(assignment.Aura))
                            return null;
                        order = new[] { assignment.Aura };
                    }
                    else
                        order = null;
                }
                else
                    order = null;

                if (!assignmentControlled)
                {
                    if (TalentManager.CurrentSpec == TalentSpec.HolyPaladin)
                        order = new[] { "Concentration Aura", "Devotion Aura", "Retribution Aura" };
                    else if (TalentManager.CurrentSpec == TalentSpec.ProtectionPaladin && (StyxWoW.Me.IsInParty || StyxWoW.Me.IsInRaid))
                        order = new[] { "Devotion Aura", "Retribution Aura", "Concentration Aura" };
                    else
                        order = new[] { "Retribution Aura", "Devotion Aura", "Concentration Aura" };
                }
            }
            var auras = SupportAuras(player);
            if (setting == PaladinAura.Auto && !assignmentControlled)
            {
                // Preserve a useful contribution even when a preferred aura briefly
                // disappears. For known duplicate casters, one stable GUID ordering
                // keeps every Paladin from switching away at the same time.
                foreach (string name in order)
                {
                    var coverage = auras.Where(a => a.Name == name).ToArray();
                    if (coverage.Any(a => a.CreatorGuid == player.Guid)
                        && !coverage.Any(a => a.CreatorGuid != 0 && a.CreatorGuid < player.Guid))
                        return null;
                }
            }
            foreach (string name in order)
            {
                var coverage = auras.Where(a => a.Name == name).ToArray();
                bool external = coverage.Any(a => a.CreatorGuid != 0 && a.CreatorGuid != player.Guid);
                if (coverage.Any(a => a.CreatorGuid == player.Guid) && !external) return null;
                if (coverage.Length != 0) continue;
                if (SpellManager.HasSpell(name) && SpellManager.CanCast(name, player)) return name;
            }
            return null;
        }

        [Class(WoWClass.Paladin)]
        [Spec(TalentSpec.RetributionPaladin)]
        [Spec(TalentSpec.HolyPaladin)]
        [Spec(TalentSpec.ProtectionPaladin)]
        [Spec(TalentSpec.Lowbie)]
        [Behavior(BehaviorType.CombatBuffs)]
        [Context(WoWContext.All)]
        public static Composite CreatePaladinCombatAuras() => CreatePaladinAuraBehavior();

        internal static Composite CreatePaladinAuraBehavior() => CreateSupportBehavior(
            () => FindSupportAction(false, SelectAura), "Devotion Aura", "Retribution Aura", "Concentration Aura",
            "Shadow Resistance Aura", "Frost Resistance Aura", "Fire Resistance Aura", "Crusader Aura");

        // Removal can punish the dispeller or trigger a position-sensitive encounter mechanic.
        // This conservative list is NOT a complete encounter policy. Disable automatic dispels
        // for assignments that require the raid leader's timing rather than a generic decision.
        private static readonly HashSet<int> ManualDispelEffects = new HashSet<int>
        {
            28169, // Grobbulus: Mutating Injection
            70337, 73912, 73913, 73914, // Lich King: Necrotic Plague variants
            30108, 30404, 30405, 47841, 47843, // Unstable Affliction ranks
            34914, 34916, 34917, 48159, 48160 // Vampiric Touch ranks
        };

        private static string SelectDispel(WoWPlayer player)
        {
            var settings = SingularSettings.Instance.Paladin;
            if (!settings.DispelDebuffs || !CanMaintainSupport() || !IsCurrentRecipient(player, settings.DispelParty))
                return null;
            var harmful = SupportAuras(player).Where(a => a.IsHarmful && a.Spell != null).ToArray();
            bool SafeFor(params WoWDispelType[] types) =>
                harmful.Any(a => types.Contains(a.Spell.DispelType))
                && !harmful.Any(a => types.Contains(a.Spell.DispelType) && ManualDispelEffects.Contains(a.SpellId));
            // Purify cannot incidentally dispel unsafe Magic while curing Disease/Poison.
            // Cleanse can, so its complete removal mask must be safe, not just one debuff.
            bool purify = SafeFor(WoWDispelType.Disease, WoWDispelType.Poison)
                && SpellManager.HasSpell("Purify") && SpellManager.CanCast("Purify", player);
            bool cleanse = SafeFor(WoWDispelType.Disease, WoWDispelType.Poison, WoWDispelType.Magic)
                && SpellManager.HasSpell("Cleanse") && SpellManager.CanCast("Cleanse", player);
            if (cleanse && harmful.Any(a => a.Spell.DispelType == WoWDispelType.Magic)) return "Cleanse";
            return purify ? "Purify" : cleanse ? "Cleanse" : null;
        }

        public static Composite CreatePaladinDispelBehavior() => CreateSupportBehavior(
            () => FindSupportAction(SingularSettings.Instance.Paladin.DispelParty, SelectDispel), "Purify", "Cleanse");
    }
}

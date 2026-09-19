using System;
using System.Collections.Generic;
using Styx;
using Styx.WoWInternals.WoWObjects;

namespace SmartLootRoller
{
    public static class PawnScorer
    {
        public static Dictionary<string, float> ParseWeights(string weightsString)
        {
            var dict = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(weightsString))
                return dict;

            var pairs = weightsString.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var kvp = pair.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (kvp.Length == 2)
                {
                    if (float.TryParse(kvp[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float val))
                    {
                        dict[kvp[0].Trim()] = val;
                    }
                }
            }
            return dict;
        }

        private static readonly Dictionary<string, string> LuaStatMap = new Dictionary<string, string>
        {
            {"ITEM_MOD_AGILITY_SHORT", "Agility"},
            {"ITEM_MOD_STRENGTH_SHORT", "Strength"},
            {"ITEM_MOD_INTELLECT_SHORT", "Intellect"},
            {"ITEM_MOD_SPIRIT_SHORT", "Spirit"},
            {"ITEM_MOD_STAMINA_SHORT", "Stamina"},
            {"ITEM_MOD_DEFENSE_SKILL_RATING_SHORT", "DefenseRating"},
            {"ITEM_MOD_DODGE_RATING_SHORT", "DodgeRating"},
            {"ITEM_MOD_PARRY_RATING_SHORT", "ParryRating"},
            {"ITEM_MOD_BLOCK_RATING_SHORT", "BlockRating"},
            {"ITEM_MOD_BLOCK_VALUE_SHORT", "BlockValue"},
            {"ITEM_MOD_HIT_MELEE_RATING_SHORT", "HitRating"},
            {"ITEM_MOD_HIT_RANGED_RATING_SHORT", "HitRating"},
            {"ITEM_MOD_HIT_SPELL_RATING_SHORT", "HitRating"},
            {"ITEM_MOD_HIT_RATING_SHORT", "HitRating"},
            {"ITEM_MOD_CRIT_MELEE_RATING_SHORT", "CritRating"},
            {"ITEM_MOD_CRIT_RANGED_RATING_SHORT", "CritRating"},
            {"ITEM_MOD_CRIT_SPELL_RATING_SHORT", "CritRating"},
            {"ITEM_MOD_CRIT_RATING_SHORT", "CritRating"},
            {"ITEM_MOD_HASTE_MELEE_RATING_SHORT", "HasteRating"},
            {"ITEM_MOD_HASTE_RANGED_RATING_SHORT", "HasteRating"},
            {"ITEM_MOD_HASTE_SPELL_RATING_SHORT", "HasteRating"},
            {"ITEM_MOD_HASTE_RATING_SHORT", "HasteRating"},
            {"ITEM_MOD_EXPERTISE_RATING_SHORT", "ExpertiseRating"},
            {"ITEM_MOD_ATTACK_POWER_SHORT", "AttackPower"},
            {"ITEM_MOD_FERAL_ATTACK_POWER_SHORT", "FeralAttackPower"},
            {"ITEM_MOD_ARMOR_PENETRATION_RATING_SHORT", "ArmorPenetrationRating"},
            {"ITEM_MOD_SPELL_POWER_SHORT", "SpellPower"},
            {"ITEM_MOD_SPELL_HEALING_DONE_SHORT", "SpellPower"},
            {"ITEM_MOD_SPELL_DAMAGE_DONE_SHORT", "SpellPower"},
            {"ITEM_MOD_MANA_REGENERATION_SHORT", "Mp5"}
        };

        public static float CalculateScore(WoWItem item, Dictionary<string, float> weights)
        {
            if (item == null || item.ItemInfo == null) return 0f;
            return CalculateScore(item.ItemInfo, item.Link, weights);
        }

        public static float CalculateScore(ItemInfo info, string itemLink, Dictionary<string, float> weights)
        {
            if (info == null || weights == null || weights.Count == 0)
                return 0f;

            float score = 0f;
            bool statsParsed = false;

            try
            {
                string lua = string.Format(@"
                    local stats = GetItemStats('{0}')
                    if not stats then return '' end
                    local str = ''
                    for k, v in pairs(stats) do
                        str = str .. k .. ':' .. v .. ','
                    end
                    return str
                ", itemLink.Replace("'", "\\'"));

                string statString = Styx.WoWInternals.Lua.GetReturnVal<string>(lua, 0);
                if (!string.IsNullOrEmpty(statString))
                {
                    statsParsed = true;
                    string[] pairs = statString.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string pair in pairs)
                    {
                        string[] kv = pair.Split(':');
                        if (kv.Length == 2)
                        {
                            string luaStatName = kv[0];
                            if (float.TryParse(kv[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float statValue))
                            {
                                if (LuaStatMap.TryGetValue(luaStatName, out string pawnStatName))
                                {
                                    if (weights.TryGetValue(pawnStatName, out float weight))
                                    {
                                        score += statValue * weight;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback will happen below
            }

            if (!statsParsed)
            {
                // Base Stats (Fallback)
                var stats = info.GetItemStats();
                if (stats != null)
                {
                    foreach (var stat in stats)
                    {
                        string statName = stat.Key.ToString();
                        if (weights.TryGetValue(statName, out float weight))
                        {
                            score += stat.Value * weight;
                        }
                    }
                }
            }

            // Armor
            if (weights.TryGetValue("Armor", out float armorWeight) && info.Armor > 0)
            {
                score += info.Armor * armorWeight;
            }

            // Weapon DPS & Feral AP
            if (info.ItemClass == WoWItemClass.Weapon && info.DPS > 0)
            {
                float dps = info.DPS;
                
                if (weights.TryGetValue("WeaponDps", out float dpsWeight))
                {
                    score += dps * dpsWeight;
                }

                if (weights.TryGetValue("FeralAttackPower", out float feralWeight))
                {
                    // WotLK Feral AP formula approx: (DPS - 54.8) * 14
                    float feralAp = Math.Max(0, (dps - 54.8f) * 14f);
                    score += feralAp * feralWeight;
                }
            }

            return score;
        }

        public static bool IsUsable(ItemInfo info, string allowedArmor, string allowedWeapons)
        {
            if (info == null) return false;

            // Rings, Trinkets, Necks, Cloaks are usable by everyone
            if (info.InventoryType == InventoryType.Finger ||
                info.InventoryType == InventoryType.Trinket ||
                info.InventoryType == InventoryType.Neck ||
                info.InventoryType == InventoryType.Cloak ||
                info.InventoryType == InventoryType.Holdable)
            {
                return true;
            }

            if (info.ItemClass == WoWItemClass.Armor)
            {
                // Librams, Totems, Idols, Sigils are Relics
                if (info.InventoryType == InventoryType.Relic)
                    return true;

                string[] allowedArmorArr = allowedArmor.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                string[] allowedWeaponsArr = allowedWeapons.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                // Offhands (Shields)
                if (info.InventoryType == InventoryType.Shield)
                {
                    foreach (var a in allowedArmorArr) { if (a.Trim().Equals("Shield", StringComparison.OrdinalIgnoreCase)) return true; }
                    foreach (var w in allowedWeaponsArr) { if (w.Trim().Equals("Shield", StringComparison.OrdinalIgnoreCase)) return true; }
                    return false;
                }

                string subclass = info.ArmorClass.ToString();
                foreach (var a in allowedArmorArr) { if (a.Trim().Equals(subclass, StringComparison.OrdinalIgnoreCase)) return true; }
                
                return false;
            }

            if (info.ItemClass == WoWItemClass.Weapon)
            {
                string subclass = info.WeaponClass.ToString();
                string[] allowedWeaponsArr = allowedWeapons.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var w in allowedWeaponsArr) { if (w.Trim().Equals(subclass, StringComparison.OrdinalIgnoreCase)) return true; }
                return false;
            }

            return true; // Default to true for items we don't know how to filter (e.g. quest items)
        }

        // Explicitly missing observations are not a zero-value loadout. Keep this
        // decision local to the current call so hydrated items can be reconsidered.
        // Null entries are legitimate empty slots in WoWBag.Items; a non-null
        // item without metadata is different and cannot authorize replacement.
        private static bool TryObserveEquipment(
            out WoWItem[] items, out WoWItem mainHand, out WoWItem offHand)
        {
            items = null;
            mainHand = null;
            offHand = null;
            var player = StyxWoW.Me;
            var equipment = player?.Inventory?.Equipped;
            if (equipment == null)
                return false;

            var observedItems = equipment.Items;
            if (observedItems == null)
                return false;
            items = new List<WoWItem>(observedItems).ToArray();
            mainHand = equipment.MainHand;
            offHand = equipment.OffHand;
            if ((mainHand != null && mainHand.ItemInfo == null) ||
                (offHand != null && offHand.ItemInfo == null))
                return false;
            foreach (var item in items)
                if (item != null && item.ItemInfo == null)
                    return false;
            return true;
        }

        // Match the host's physical slot aliases, not just the inventory-type
        // spelling. This does not grant class eligibility or change hand loadouts.
        private static InventoryType ComparableSlot(InventoryType type)
        {
            switch (type)
            {
                case InventoryType.Robe:
                    return InventoryType.Chest;
                case InventoryType.Thrown:
                case InventoryType.RangedRight:
                case InventoryType.Relic:
                    return InventoryType.Ranged;
                default:
                    return type;
            }
        }

        public static float GetMinEquippedScore(InventoryType invType, Dictionary<string, float> weights)
        {
            if (!TryObserveEquipment(out var equippedItems, out var mhItem, out var ohItem))
                return float.NaN;

            // --- SPECIAL CASE: WEAPONS AND OFFHANDS ---
            bool isWeaponSlot = invType == InventoryType.Weapon || invType == InventoryType.WeaponMainHand || 
                                invType == InventoryType.WeaponOffHand || invType == InventoryType.TwoHandWeapon || 
                                invType == InventoryType.Shield || invType == InventoryType.Holdable;

            if (isWeaponSlot)
            {
                float mhScore = (mhItem != null && mhItem.ItemInfo != null) ? CalculateScore(mhItem, weights) : 0f;
                float ohScore = (ohItem != null && ohItem.ItemInfo != null) ? CalculateScore(ohItem, weights) : 0f;
                
                bool hasTwoHander = (mhItem != null && mhItem.ItemInfo != null && mhItem.ItemInfo.InventoryType == InventoryType.TwoHandWeapon);

                float totalEquipped = mhScore + ohScore;

                if (invType == InventoryType.TwoHandWeapon)
                {
                    // A 2H weapon replaces BOTH hands, so it must beat the sum of both hands.
                    return totalEquipped;
                }
                else if (invType == InventoryType.WeaponMainHand || invType == InventoryType.Weapon)
                {
                    if (hasTwoHander)
                    {
                        // Equipping a 1H will unequip the 2H, leaving the offhand empty.
                        // We must compare the new 1H against the 2H alone.
                        // (If the player has an offhand in their bags, they will equip it on the NEXT pulse if it's an upgrade).
                        return mhScore; 
                    }
                    else
                    {
                        // It replaces the MainHand. (Or worst 1H if DW, but we will simplify to replacing MH score).
                        return mhScore;
                    }
                }
                else if (invType == InventoryType.WeaponOffHand || invType == InventoryType.Shield || invType == InventoryType.Holdable)
                {
                    if (hasTwoHander)
                    {
                        // Equipping an offhand unequips the 2H, leaving the main hand empty.
                        // It must beat the 2H alone to be considered an upgrade right now.
                        return mhScore;
                    }
                    else
                    {
                        // Replaces the offhand
                        return ohScore;
                    }
                }
            }

            // --- DEFAULT CASE: ARMOR, RINGS, TRINKETS ---
            var scores = new List<float>();
            var comparableSlot = ComparableSlot(invType);
            foreach (var item in equippedItems)
            {
                if (item != null && item.ItemInfo != null)
                {
                    if (ComparableSlot(item.ItemInfo.InventoryType) == comparableSlot)
                    {
                        scores.Add(CalculateScore(item, weights));
                    }
                }
            }

            if (scores.Count == 0)
                return 0f;

            int expectedSlots = 1;
            if (invType == InventoryType.Finger || invType == InventoryType.Trinket)
            {
                expectedSlots = 2;
            }

            if (scores.Count < expectedSlots)
                return 0f; 

            float minScore = float.MaxValue;
            foreach (var s in scores)
            {
                if (s < minScore) minScore = s;
            }

            return minScore;
        }

        public static bool IsSlotEmpty(InventoryType invType)
        {
            if (!TryObserveEquipment(out var equippedItems, out var mainHand, out var offHand))
                return false;
            
            // A two-hander displaces both hands. An empty main-hand alone
            // cannot bypass comparison with a valuable off-hand.
            if (invType == InventoryType.TwoHandWeapon)
                return mainHand == null && offHand == null;

            if (invType == InventoryType.WeaponMainHand || invType == InventoryType.Weapon)
            {
                var mh = mainHand;
                return mh == null || mh.ItemInfo == null;
            }
            if (invType == InventoryType.Shield || invType == InventoryType.WeaponOffHand || invType == InventoryType.Holdable)
            {
                var mh = mainHand;
                if (mh != null && mh.ItemInfo != null && mh.ItemInfo.InventoryType == InventoryType.TwoHandWeapon)
                {
                    return false; // The offhand is effectively 'occupied' by the 2H weapon
                }

                var oh = offHand;
                return oh == null || oh.ItemInfo == null;
            }

            int count = 0;
            var comparableSlot = ComparableSlot(invType);
            int maxAllowed = (invType == InventoryType.Finger || invType == InventoryType.Trinket || invType == InventoryType.Weapon) ? 2 : 1;

            foreach (var item in equippedItems)
            {
                if (item != null && item.ItemInfo != null && ComparableSlot(item.ItemInfo.InventoryType) == comparableSlot)
                    count++;
            }

            return count < maxAllowed;
        }

        public static float GetMinEquippedItemLevel(InventoryType invType)
        {
            if (!TryObserveEquipment(out var equippedItems, out var mainHand, out var offHand))
                return float.NaN;
            List<float> ilvls = new List<float>();
            var comparableSlot = ComparableSlot(invType);

            if (invType == InventoryType.TwoHandWeapon || invType == InventoryType.WeaponMainHand || invType == InventoryType.Weapon)
            {
                // Retain the existing main-hand tie convention, but do not
                // lose the only displaced item when the main-hand is empty.
                var mh = invType == InventoryType.TwoHandWeapon ? mainHand ?? offHand : mainHand;
                if (mh != null && mh.ItemInfo != null) ilvls.Add(mh.ItemInfo.Level);
            }
            else if (invType == InventoryType.Shield || invType == InventoryType.WeaponOffHand || invType == InventoryType.Holdable)
            {
                // Off-hand replacement of a two-hander already uses the
                // main-hand score; its level comparison must use that item too.
                var oh = mainHand != null && mainHand.ItemInfo != null &&
                    mainHand.ItemInfo.InventoryType == InventoryType.TwoHandWeapon ? mainHand : offHand;
                if (oh != null && oh.ItemInfo != null) ilvls.Add(oh.ItemInfo.Level);
            }
            else
            {
                foreach (var item in equippedItems)
                {
                    if (item != null && item.ItemInfo != null && ComparableSlot(item.ItemInfo.InventoryType) == comparableSlot)
                    {
                        ilvls.Add(item.ItemInfo.Level);
                    }
                }
            }

            if (ilvls.Count == 0) return 0f;
            ilvls.Sort();
            return ilvls[0];
        }
    }
}
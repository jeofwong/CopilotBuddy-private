using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Styx;
using Styx.Helpers;
using Styx.Logic.BehaviorTree;
using Styx.Plugins.PluginClass;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace SmartLootRoller
{
    public class SmartLootRoller : HBPlugin
    {
        private static readonly List<Tuple<string, LuaEventHandlerDelegate>> _detachedHandlers = new List<Tuple<string, LuaEventHandlerDelegate>>();
        private DateTime _lastBagScan = DateTime.MinValue;

        #region HBPlugin Overrides

        public override string Name => "SmartLootRoller";
        public override string Author => "MattB";
        public override Version Version => new Version(2, 0, 0);
        public override bool WantButton => true;
        public override string ButtonText => "Settings";

        public override void OnButtonPress()
        {
            new FormSettings().ShowDialog();
        }

        public override void Initialize()
        {
            Lua.Events.AttachEvent("START_LOOT_ROLL", HandleLootRoll);
            Lua.Events.AttachEvent("CONFIRM_LOOT_ROLL", HandleConfirmLootRoll);
            Lua.Events.AttachEvent("CONFIRM_DISENCHANT_ROLL", HandleConfirmLootRoll);
            Logging.Write("[SmartLootRoller] Plugin Initialized. Event handlers attached.");
        }

        public override void Dispose()
        {
            Lua.Events.DetachEvent("START_LOOT_ROLL", HandleLootRoll);
            Lua.Events.DetachEvent("CONFIRM_LOOT_ROLL", HandleConfirmLootRoll);
            Lua.Events.DetachEvent("CONFIRM_DISENCHANT_ROLL", HandleConfirmLootRoll);
            ReattachOtherHandlers();
            Logging.Write("[SmartLootRoller] Plugin Disposed. Event handlers detached.");
        }

        public override void Pulse()
        {
            if (TreeRoot.IsRunning)
            {
                DetachOtherHandlers();

                if (DateTime.Now - _lastBagScan > TimeSpan.FromSeconds(5))
                {
                    _lastBagScan = DateTime.Now;
                    AutoEquipUpgrades();
                }
            }
        }

        private void AutoEquipUpgrades()
        {
            try
            {
                var settings = SmartLootRollerSettings.Instance;
                if (!settings.AutoEquipUpgrades) return;

                var weights = settings.GetWeightsDictionary();
                if (weights.Count == 0) return;

                var me = StyxWoW.Me;
                if (me == null || me.BagItems == null) return;

                foreach (var item in me.BagItems)
                {
                    if (item == null || item.ItemInfo == null) continue;

                    // Cannot equip if level too high
                    if (me.Level < item.ItemInfo.RequiredLevel) continue;

                    // Only consider actual Weapons and Armor
                    if (item.ItemInfo.ItemClass != WoWItemClass.Armor && item.ItemInfo.ItemClass != WoWItemClass.Weapon)
                        continue;

                    // Bind on Equip handling
                    if (item.ItemInfo.Bond == WoWItemBondType.OnEquip)
                    {
                        if (item.ItemInfo.Quality == WoWItemQuality.Uncommon && !settings.AutoEquipBoEGreens)
                            continue;
                        if (item.ItemInfo.Quality == WoWItemQuality.Rare && !settings.AutoEquipBoEBlues)
                            continue;
                        if (item.ItemInfo.Quality == WoWItemQuality.Epic || item.ItemInfo.Quality == WoWItemQuality.Legendary)
                            continue; // Never auto-equip BoE epics to prevent huge gold loss
                    }

                    // Check if the player actually has the proficiency and level to equip it
                    if (!item.Usable)
                        continue;

                    if (PawnScorer.IsUsable(item.ItemInfo, settings.AllowedArmor, settings.AllowedWeapons))
                    {
                        float bagScore = PawnScorer.CalculateScore(item, weights);
                        float equippedScore = PawnScorer.GetMinEquippedScore(item.ItemInfo.InventoryType, weights);
                        bool isSlotEmpty = PawnScorer.IsSlotEmpty(item.ItemInfo.InventoryType);
                        
                        bool isUpgrade = false;

                        if (isSlotEmpty)
                        {
                            isUpgrade = true; // Always equip into empty slots!
                        }
                        else if (bagScore > equippedScore * 1.01f)
                        {
                            isUpgrade = true; // Clear Pawn score upgrade
                        }
                        else if (bagScore == equippedScore) // Handles 0 == 0 ties
                        {
                            // Tie-breaker: Item Level
                            float equippedIlvl = PawnScorer.GetMinEquippedItemLevel(item.ItemInfo.InventoryType);
                            if (item.ItemInfo.Level > equippedIlvl)
                            {
                                isUpgrade = true;
                            }
                        }

                        if (isUpgrade)
                        {
                            Logging.Write("[SmartLootRoller] Auto-equipping UPGRADE: '{0}' (Score: {1:F1} > Equipped: {2:F1})", item.Name, bagScore, equippedScore);
                            item.UseContainerItem();
                            
                            // Automatically accept the 'Equipping this item will bind it to you' popup
                            Styx.WoWInternals.Lua.DoString("if StaticPopup1 and StaticPopup1:IsVisible() and StaticPopup1.which == 'EQUIP_BIND' then StaticPopup1Button1:Click() end");
                            
                            return; // Equip only one item per pulse to avoid confusion/spam
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.WriteDiagnostic("[SmartLootRoller] Exception in AutoEquipUpgrades: {0}", ex.Message);
            }
        }

        #endregion

        #region Loot Roll Event Handlers

        private void HandleLootRoll(object sender, LuaEventArgs e)
        {
            try
            {
                var settings = SmartLootRollerSettings.Instance;
                if (!settings.RollForLoot)
                    return;

                if (e.Args.Length < 1)
                    return;

                string rollId = e.Args[0].ToString();
                string itemLink = Lua.GetReturnVal<string>("return GetLootRollItemLink(" + rollId + ")", 0);
                if (string.IsNullOrEmpty(itemLink))
                {
                    Logging.WriteDebug("[SmartLootRoller] GetLootRollItemLink returned empty for roll ID {0}", rollId);
                    return;
                }

                string[] splitted = itemLink.Split(':');
                uint itemId;
                if (splitted.Length < 2 || !uint.TryParse(splitted[1], out itemId) || itemId == 0)
                {
                    Logging.Write("[SmartLootRoller] Parsing ItemLink failed! Link: {0}", itemLink);
                    return;
                }

                ItemInfo rollItemInfo = ItemInfo.FromId(itemId);
                if (rollItemInfo == null)
                {
                    Logging.Write("[SmartLootRoller] Retrieving item info failed for Item ID {0}", itemId);
                    return;
                }

                bool matchesCriteria = false;
                bool comparisonKnown = true;

                // --- NEW PAWN SCORING LOGIC ---
                if (PawnScorer.IsUsable(rollItemInfo, settings.AllowedArmor, settings.AllowedWeapons))
                {
                    var weights = settings.GetWeightsDictionary();
                    float droppedScore = PawnScorer.CalculateScore(rollItemInfo, itemLink, weights);
                    
                    if (droppedScore > 0)
                    {
                        float equippedScore = PawnScorer.GetMinEquippedScore(rollItemInfo.InventoryType, weights);
                        // Missing equipment is not a confirmed non-upgrade. Preserve
                        // Greed/Pass fallback, but never infer permission to disenchant.
                        comparisonKnown = !float.IsNaN(equippedScore) && !float.IsInfinity(equippedScore);

                        // Give a tiny 1% buffer to avoid rolling on sidegrades, unless equipped is 0.
                        if (!comparisonKnown)
                        {
                            Logging.Write("[SmartLootRoller] Equipment comparison unavailable; deferring Need and Disenchant.");
                        }
                        else if (equippedScore == 0 || droppedScore > equippedScore * 1.01f)
                        {
                            matchesCriteria = true;
                            Logging.Write("[SmartLootRoller] Item '{0}' is an UPGRADE! (Score: {1:F1} > Equipped: {2:F1})", rollItemInfo.Name, droppedScore, equippedScore);
                        }
                        else
                        {
                            Logging.Write("[SmartLootRoller] Item '{0}' is NOT an upgrade. (Score: {1:F1} <= Equipped: {2:F1})", rollItemInfo.Name, droppedScore, equippedScore);
                        }
                    }
                }

                // Original 3.3.5 exposes roll availability separately from item stats.
                // A score upgrade cannot override an unavailable Need option; use
                // the configured nonmatching fallback, including explicit DE policy.
                if (matchesCriteria && settings.MatchRule == MatchRollType.Need &&
                    !Lua.GetReturnVal<bool>("return GetLootRollItemInfo(" + rollId + ")", 5))
                    matchesCriteria = false;

                // 4. Determine Roll Type and Execute Roll
                int rollType = (int)NoMatchRollType.Greed;

                if (matchesCriteria)
                {
                    // Saved enum values are not Lua roll codes: legacy Pass=3
                    // must remain readable, but code 3 means Disenchant to the client.
                    rollType = settings.MatchRule == MatchRollType.Need ? 1
                        : settings.MatchRule == MatchRollType.Greed ? 2 : 0;
                    Logging.Write("[SmartLootRoller] Item '{0}' MATCHES your criteria. Rolling {1}.", rollItemInfo.Name, settings.MatchRule);
                }
                else
                {
                    bool canDisenchant = Lua.GetReturnVal<bool>("return GetLootRollItemInfo(" + rollId + ")", 7);
                    if (comparisonKnown && settings.RollForLootDE && canDisenchant)
                    {
                        rollType = 3; // Disenchant
                        Logging.Write("[SmartLootRoller] Item '{0}' does NOT match. Rolling Disenchant.", rollItemInfo.Name);
                    }
                    else
                    {
                        // Unknown saved values also fail closed to Pass, never DE.
                        rollType = settings.NoMatchRule == NoMatchRollType.Greed ? 2 : 0;
                        Logging.Write("[SmartLootRoller] Item '{0}' does NOT match. Rolling {1}.", rollItemInfo.Name, settings.NoMatchRule);
                    }
                }

                if (rollType == 2 &&
                    !Lua.GetReturnVal<bool>("return GetLootRollItemInfo(" + rollId + ")", 6))
                {
                    rollType = 0;
                    Logging.Write("[SmartLootRoller] Greed is unavailable; passing.");
                }
                Lua.DoString("RollOnLoot(" + rollId + ", " + rollType + ")");
            }
            catch (Exception ex)
            {
                Logging.Write("[SmartLootRoller] Exception in HandleLootRoll: {0}", ex.Message);
            }
        }

        private void HandleConfirmLootRoll(object sender, LuaEventArgs e)
        {
            try
            {
                var settings = SmartLootRollerSettings.Instance;
                if (!settings.RollForLoot)
                    return;

                double rollId = (double)e.Args[0];
                double rollType = (double)e.Args[1];
                Lua.DoString("ConfirmLootRoll({0},{1})", rollId, rollType);
            }
            catch (Exception ex)
            {
                Logging.Write("[SmartLootRoller] Exception in HandleConfirmLootRoll: {0}", ex.Message);
            }
        }

        #endregion

        #region Reflection Event Hijacking (Bypass conflicting loot handlers)

        private void DetachOtherHandlers()
        {
            try
            {
                var luaEventsInstance = Lua.Events;
                if (luaEventsInstance == null)
                    return;

                var fieldInfo = typeof(LuaEvents).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(f => f.FieldType == typeof(Dictionary<string, LuaEventHandlerDelegate>));
                if (fieldInfo == null)
                    return;

                var eventHandlers = fieldInfo.GetValue(luaEventsInstance) as Dictionary<string, LuaEventHandlerDelegate>;
                if (eventHandlers == null)
                    return;

                lock (eventHandlers)
                {
                    string[] eventNames = { "START_LOOT_ROLL", "CONFIRM_LOOT_ROLL", "CONFIRM_DISENCHANT_ROLL" };
                    foreach (var eventName in eventNames)
                    {
                        if (eventHandlers.ContainsKey(eventName))
                        {
                            var combined = eventHandlers[eventName];
                            if (combined != null)
                            {
                                var toDetach = new List<LuaEventHandlerDelegate>();
                                foreach (var d in combined.GetInvocationList())
                                {
                                    if (d.Method.DeclaringType != null && 
                                        !d.Method.DeclaringType.FullName.Contains("SmartLootRoller"))
                                    {
                                        toDetach.Add((LuaEventHandlerDelegate)d);
                                    }
                                }

                                foreach (var handler in toDetach)
                                {
                                    Lua.Events.DetachEvent(eventName, handler);
                                    Logging.Write("[SmartLootRoller] Dynamically detached external handler from {0} ({1}.{2}) to prevent conflict.", 
                                        eventName, handler.Method.DeclaringType.FullName, handler.Method.Name);

                                    lock (_detachedHandlers)
                                    {
                                        if (!_detachedHandlers.Any(t => t.Item2 == handler && t.Item1 == eventName))
                                        {
                                            _detachedHandlers.Add(Tuple.Create(eventName, handler));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Write("[SmartLootRoller] Warning: Failed to dynamically detach conflicting handlers: {0}", ex.Message);
            }
        }

        private void ReattachOtherHandlers()
        {
            lock (_detachedHandlers)
            {
                foreach (var tuple in _detachedHandlers)
                {
                    try
                    {
                        Lua.Events.AttachEvent(tuple.Item1, tuple.Item2);
                        Logging.Write("[SmartLootRoller] Reattached external handler to {0} ({1}.{2}).", 
                            tuple.Item1, tuple.Item2.Method.DeclaringType.FullName, tuple.Item2.Method.Name);
                    }
                    catch (Exception ex)
                    {
                        Logging.Write("[SmartLootRoller] Error reattaching handler: {0}", ex.Message);
                    }
                }
                _detachedHandlers.Clear();
            }
        }

        #endregion
    }
}

#nullable disable
using System;
using System.Collections.Generic;
using System.Text;
using GreenMagic;
using Styx.Logic.Combat;
using Styx.Helpers;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic.Inventory.Frames.Merchant
{
    /// <summary>
    /// Represents the merchant/vendor frame.
    /// Addresses from HB 3.3.5a.
    /// </summary>
    public class MerchantFrame : Frame
    {
        public MerchantFrame() : base("MerchantFrame")
        {
        }

        static MerchantFrame()
        {
            Instance = new MerchantFrame();
        }

        /// <summary>
        /// The merchant NPC.
        /// Address: 12559336U (0xBF9BE8)
        /// </summary>
        public WoWUnit Merchant
        {
            get
            {
                ulong guid = ObjectManager.Wow.Read<ulong>(12559336U);
                return ObjectManager.GetObjectByGuid<WoWUnit>(guid);
            }
        }

        /// <summary>
        /// Number of buyback items.
        /// Address: 12559348U (0xBF9BF4)
        /// </summary>
        public int NumBuybackItems
        {
            get
            {
                return ObjectManager.Wow.Read<int>(12559348U);
            }
        }

        // The original client marks both quest items and quest-starting IDs.
        // Failed/missing observation is pending, never permission to sell.
        // Share this boundary without replacing either seller's existing guards.
        private const string QuestItemSaleGuardLua =
            "if not skip then " +
            "if type(GetContainerItemQuestInfo)~='function' then return 'ok',2 end " +
            "local queried,isQuestItem,questId=pcall(GetContainerItemQuestInfo,b,s) " +
            "if not queried then return 'ok',2 end " +
            "if isQuestItem or questId then skip=true end end ";

        private readonly MerchantSaleAttemptGate _saleAttemptGate = new MerchantSaleAttemptGate();

        // Original 3.3.5 GetItemInfo returns the vendor price in slot eleven.
        // Unknown is not zero, and zero-value items must not mask later stacks.
        private const string SaleValueGuardLua =
            "if not skip then " +
            "if type(sellPrice)~='number' or sellPrice~=sellPrice or sellPrice<0 or sellPrice==math.huge then return 'ok',2 end " +
            "if sellPrice==0 then skip=true end end ";

        /// <summary>
        /// Sells all items matching the specified qualities.
        /// </summary>
        public void SellItemQualities(ItemQuality qualities, IEnumerable<string> nameExceptions, IEnumerable<uint> idExceptions)
        {
            if (qualities == ItemQuality.None)
                return;

            BuildSaleFilters(
                qualities, nameExceptions, idExceptions,
                out string exceptions, out string qualityCondition);

            // Bulk callers (including Wholesome) share the stepped seller's
            // retry receipts. Preserve earlier attempts when a later observation
            // is pending or a Lua call fails; none is proof of server acceptance.
            _saleAttemptGate.ExecuteBatch(string.Format(
                "if not MerchantFrame or not MerchantFrame:IsShown() then return 'ok',3 end " +
                "if type(UnitGUID)~='function' then return 'ok',2 end " +
                "local player=UnitGUID('player') local merchant=UnitGUID('npc') " +
                "if type(player)~='string' or player=='' or type(merchant)~='string' or merchant=='' then return 'ok',2 end " +
                "local submitted={{}} local budget=saleAttemptBudget or 256 " +
                "local function sellPass() " +
                "{0}for b=0,4 do for s=1,GetContainerNumSlots(b) do " +
                "local itemLink=GetContainerItemLink(b,s) if itemLink then " +
                "local name,_,quality,_,_,_,_,_,_,_,sellPrice=GetItemInfo(itemLink) " +
                "if not name or quality==nil then return 'ok',2 end " +
                "if name and quality~=nil then local id=tonumber(string.match(itemLink,'item:(%d+)')) " +
                "name=string.lower(name) if {1} then local skip=false " +
                "if itemExceptions then for i=1,#itemExceptions do " +
                "if (itemExceptions[i].i and id==itemExceptions[i].i) or " +
                "(itemExceptions[i].n and name==itemExceptions[i].n) then skip=true break end end end " +
                SaleValueGuardLua +
                QuestItemSaleGuardLua +
                "if not skip then local _,count,locked=GetContainerItemInfo(b,s) " +
                "if not locked then " +
                "if type(count)~='number' or count~=count or count<1 or count==math.huge or count~=math.floor(count) then return 'ok',2 end " +
                "local token=player..':'..merchant..':'..b..':'..s..':'..count..':'..itemLink " +
                "if #token>2048 then return 'ok',2 end " +
                "if not blockedSaleStacks or not blockedSaleStacks[token] then " +
                "if #submitted>=budget then return 'ok',4 end " +
                "if not MerchantFrame or not MerchantFrame:IsShown() then return 'ok',3 end " +
                "if UnitGUID('player')~=player or UnitGUID('npc')~=merchant then return 'ok',2 end " +
                "submitted[#submitted+1]=token " +
                "if blockedSaleStacks then blockedSaleStacks[token]=true end " +
                "UseContainerItem(b,s) end end end end end end end end " +
                "return 'ok',0 end " +
                "local ok,marker,status=pcall(sellPass) " +
                "if not ok then return 'ok',2,unpack(submitted) end " +
                "return marker,status,unpack(submitted)",
                exceptions, qualityCondition), Lua.GetReturnValues, Environment.TickCount64);
        }

        /// <summary>
        /// Submits at most one eligible stack. 0 = no currently eligible stack,
        /// 1 = request submitted (not acknowledged), 2 = observation pending,
        /// 3 = merchant closed, 4 = bounded retry deferral, -1 = unknown result.
        /// </summary>
        public int SellNextItemQualities(ItemQuality qualities, IEnumerable<string> nameExceptions, IEnumerable<uint> idExceptions)
        {
            if (qualities == ItemQuality.None)
                return 0;

            return _saleAttemptGate.Execute(
                BuildSellNextItemLua(qualities, nameExceptions, idExceptions),
                Lua.GetReturnValues, Environment.TickCount64);
        }

        internal static bool TryParseSellStepResult(IReadOnlyList<string> values, out int result)
        {
            result = -1;
            return values != null &&
                   values.Count >= 2 &&
                   string.Equals(values[0], "ok", StringComparison.Ordinal) &&
                   int.TryParse(values[1], out result);
        }

        internal static string BuildSellNextItemLua(ItemQuality qualities, IEnumerable<string> nameExceptions, IEnumerable<uint> idExceptions)
        {
            if (qualities == ItemQuality.None)
                return "return 'ok',0";

            BuildSaleFilters(
                qualities, nameExceptions, idExceptions,
                out string exceptions, out string qualityCondition);

            return string.Format(
                "if not MerchantFrame or not MerchantFrame:IsShown() then return 'ok',3 end " +
                "if type(UnitGUID)~='function' then return 'ok',2 end " +
                "local player=UnitGUID('player') local merchant=UnitGUID('npc') " +
                "if type(player)~='string' or player=='' or type(merchant)~='string' or merchant=='' then return 'ok',2 end " +
                "{0}for b=0,4 do for s=1,GetContainerNumSlots(b) do " +
                "local itemLink=GetContainerItemLink(b,s) if itemLink then " +
                "local name,_,quality,_,_,_,_,_,_,_,sellPrice=GetItemInfo(itemLink) " +
                "if not name or quality==nil then return 'ok',2 end " +
                "local id=tonumber(string.match(itemLink,'item:(%d+)')) name=string.lower(name) " +
                "if {1} then local skip=false if itemExceptions then for i=1,#itemExceptions do " +
                "if (itemExceptions[i].i and id==itemExceptions[i].i) or " +
                "(itemExceptions[i].n and name==itemExceptions[i].n) then skip=true break end end end " +
                SaleValueGuardLua +
                QuestItemSaleGuardLua +
                "if not skip then local _,count,locked=GetContainerItemInfo(b,s) " +
                "if locked then return 'ok',2 end " +
                "if type(count)~='number' or count~=count or count<1 or count==math.huge or count~=math.floor(count) then return 'ok',2 end " +
                "local token=player..':'..merchant..':'..b..':'..s..':'..count..':'..itemLink " +
                "if not blockedSaleStacks or not blockedSaleStacks[token] then " +
                "if not MerchantFrame or not MerchantFrame:IsShown() then return 'ok',3 end " +
                "if UnitGUID('player')~=player or UnitGUID('npc')~=merchant then return 'ok',2 end " +
                "UseContainerItem(b,s) return 'ok',1,token end end end end end end " +
                "return 'ok',0",
                exceptions, qualityCondition);
        }

        private static void BuildSaleFilters(
            ItemQuality qualities,
            IEnumerable<string> nameExceptions,
            IEnumerable<uint> idExceptions,
            out string exceptions,
            out string qualityCondition)
        {

            List<string> qualityConditions = new List<string>();
            if ((qualities & ItemQuality.Poor) != ItemQuality.None)
                qualityConditions.Add("quality == 0");
            if ((qualities & ItemQuality.Common) != ItemQuality.None)
                qualityConditions.Add("quality == 1");
            if ((qualities & ItemQuality.Uncommon) != ItemQuality.None)
                qualityConditions.Add("quality == 2");
            if ((qualities & ItemQuality.Rare) != ItemQuality.None)
                qualityConditions.Add("quality == 3");
            if ((qualities & ItemQuality.Epic) != ItemQuality.None)
                qualityConditions.Add("quality == 4");

            StringBuilder qualityBuilder = new StringBuilder();
            qualityBuilder.Append("(" + qualityConditions[0]);
            for (int i = 1; i < qualityConditions.Count; i++)
            {
                qualityBuilder.Append(" or " + qualityConditions[i]);
            }
            qualityBuilder.Append(")");
            qualityCondition = qualityBuilder.ToString();

            StringBuilder exceptionsBuilder = new StringBuilder();
            if (nameExceptions != null || idExceptions != null)
            {
                HashSet<string> nameSet = new HashSet<string>();
                HashSet<uint> idSet = new HashSet<uint>();
                exceptionsBuilder.Append("local itemExceptions = {");
                bool hasExceptions = false;

                if (nameExceptions != null)
                {
                    foreach (string name in nameExceptions)
                    {
                        string lowerName = name.ToLower();
                        if (!string.IsNullOrEmpty(lowerName) && !nameSet.Contains(lowerName))
                        {
                            exceptionsBuilder.Append("{n=\"" + Lua.Escape(lowerName) + "\"},");
                            hasExceptions = true;
                            nameSet.Add(lowerName);
                        }
                    }
                }

                if (idExceptions != null)
                {
                    foreach (uint id in idExceptions)
                    {
                        if (!idSet.Contains(id))
                        {
                            exceptionsBuilder.Append("{i=" + id + "},");
                            hasExceptions = true;
                            idSet.Add(id);
                        }
                    }
                }

                if (hasExceptions)
                {
                    exceptionsBuilder.Remove(exceptionsBuilder.Length - 1, 1);
                }
                exceptionsBuilder.Append("}");
            }
            exceptions = exceptionsBuilder + " ";
        }

        public void Close()
        {
            Hide();
        }

        public new void Hide()
        {
            Lua.DoString("CloseMerchant()");
        }

        /// <summary>
        /// Number of items the merchant sells.
        /// Address: 12559344U (0xBF9BF0)
        /// </summary>
        public int MerchantNumItems
        {
            get
            {
                return ObjectManager.Wow.Read<int>(12559344U);
            }
        }

        /// <summary>
        /// Merchant item count reported by the client API. This is authoritative for
        /// WotLK because the native merchant-array layout differs between HB ports.
        /// </summary>
        public int LuaMerchantNumItems => Lua.GetReturnVal<int>("return GetMerchantNumItems()", 0U);

        private static bool CanAfford(int stack, WoWItem item)
        {
            return item.ItemInfo.BuyPrice * stack <= ObjectManager.Me.Coinage;
        }

        public void BuyItem(WoWItem item, int stackCount)
        {
            if (CanAfford(stackCount, item))
            {
                int? index = GetMerchantIndex(item.Entry);
                if (index.HasValue)
                {
                    Logging.Write("Buying {0} {1}", stackCount, item.Name);
                    Lua.DoString("BuyMerchantItem(" + index.Value + "," + stackCount + ")");
                }
            }
            else
            {
                Logging.Write("Not enough money to buy {0}", item.Name);
            }
        }

        public void BuyItem(uint itemId, int stackCount)
        {
            int? index = GetMerchantIndex(itemId);
            if (index.HasValue)
            {
                MerchantItem merchantItem = GetMerchantItemAtIndex(index.Value - 1);
                if ((long)stackCount * (long)merchantItem.BuyPrice > (long)ObjectManager.Me.Coinage)
                {
                    Logging.Write("Not enough money to buy item {0}", itemId);
                    return;
                }
                Lua.DoString("BuyMerchantItem(" + index.Value + "," + stackCount + ")");
            }
        }

        /// <summary>
        /// Buys an item by merchant index and amount.
        /// Returns true if the purchase was made, false if not enough money.
        /// </summary>
        public bool BuyItem(int index, int amount)
        {
            if (index <= 0 || amount <= 0)
                return false;

            MerchantItem merchantItem = GetMerchantItemAtIndex(index - 1);
            if ((long)amount * (long)merchantItem.BuyPrice > (long)ObjectManager.Me.Coinage)
            {
                Logging.Write("Not enough money to buy {0}", amount);
                return false;
            }

            Lua.DoString("BuyMerchantItem(" + index + "," + amount + ")");
            return true;
        }

        public void SellItem(WoWItem item)
        {
            Lua.DoString(
                "for b=0,4 do if GetBagName(b) then for s=1, GetContainerNumSlots(b) do local itemLink = GetContainerItemLink(b, s) if itemLink then local _, stackCount = GetContainerItemInfo(b, s)\tif string.find(itemLink, \"{0}\") and stackCount == {1} then UseContainerItem(b, s)\tend\tend\tend end end",
                item.Name,
                item.StackCount);
        }

        public void RepairAllItems()
        {
            RepairAllItems(false);
        }

        public void RepairAllItems(bool useGuildBankFunds)
        {
            Logging.WriteDiagnostic("Repairing all items");
            Lua.DoString("RepairAllItems(" + (useGuildBankFunds ? "1" : "nil") + ")");
        }

        /// <summary>
        /// Gets all items the merchant sells.
        /// </summary>
        public List<MerchantItem> MerchantItems
        {
            get
            {
                List<MerchantItem> list = new List<MerchantItem>();
                if (IsVisible)
                {
                    for (int i = 0; i < MerchantNumItems; i++)
                    {
                        MerchantItem item = GetMerchantItemAtIndex(i);
                        if (!list.Contains(item))
                        {
                            list.Add(item);
                        }
                    }
                }
                return list;
            }
        }

        /// <summary>
        /// Gets all merchant items as an array.
        /// </summary>
        public MerchantItem[] GetAllMerchantItems()
        {
            return MerchantItems.ToArray();
        }

        private int? GetMerchantIndex(uint itemId)
        {
            if (itemId == 0)
                return null;

            for (int i = 0; i < MerchantNumItems; i++)
            {
                MerchantItem item = GetMerchantItemAtIndex(i);
                if ((long)item.ItemId == (long)itemId)
                {
                    return item.Index;
                }
            }
            return null;
        }

        /// <summary>
        /// Gets merchant item at specified index.
        /// Base address: 12554536U (0xBF87A8)
        /// Item size: 32 bytes
        /// </summary>
        private static MerchantItem GetMerchantItemAtIndex(int index)
        {
            uint offset = (uint)(32 * index);
            uint itemAddress = 12554536U + offset;
            return new MerchantItem(itemAddress, index);
        }

        /// <summary>
        /// Gets a buyback item by index.
        /// Address: 12554488U (0xBF8778)
        /// </summary>
        public WoWItem GetBuybackItem(int index)
        {
            if (index < 0 || index >= 12)
                throw new ArgumentOutOfRangeException("index");

            WoWBag inventory = StyxWoW.Me.Inventory;
            uint slotId = ObjectManager.Wow.Read<uint>((uint)(12554488 + index * 4));
            return inventory.GetItemBySlot(slotId);
        }

        /// <summary>
        /// Gets the best drink the merchant sells that the player can use.
        /// Returns -1 if none found.
        /// </summary>
        public int GetBestDrinkFromVendor()
        {
            try
            {
                return GetBestDrinkCandidateFromVendor()?.MerchantIndex ?? -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Gets the best food the merchant sells that the player can use.
        /// Returns -1 if none found.
        /// </summary>
        public int GetBestFoodFromVendor()
        {
            try
            {
                return GetBestFoodCandidateFromVendor()?.MerchantIndex ?? -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Gets the best drink using the player's maximum mana as the useful capacity.
        /// </summary>
        public ConsumableCandidate GetBestDrinkCandidateFromVendor()
        {
            return GetBestConsumableFromVendor(
                ConsumableKind.Drink,
                StyxWoW.Me?.MaxMana ?? 0);
        }

        /// <summary>
        /// Gets the best food using the player's maximum health as the useful capacity.
        /// </summary>
        public ConsumableCandidate GetBestFoodCandidateFromVendor()
        {
            return GetBestConsumableFromVendor(
                ConsumableKind.Food,
                StyxWoW.Me?.MaxHealth ?? 0);
        }

        /// <summary>
        /// Buys a catalog candidate without reading the unreliable native merchant array.
        /// </summary>
        public bool BuyConsumable(ConsumableCandidate candidate, int amount)
        {
            if (candidate == null || candidate.MerchantIndex <= 0 || amount <= 0)
                return false;

            if ((ulong)amount * candidate.BuyPrice > StyxWoW.Me.Coinage)
            {
                Logging.Write("Not enough money to buy {0}x {1}", amount, candidate.Name);
                return false;
            }

            Lua.DoString("BuyMerchantItem(" + candidate.MerchantIndex + "," + amount + ")");
            return true;
        }

        private ConsumableCandidate GetBestConsumableFromVendor(ConsumableKind kind, int capacity)
        {
            return ConsumableVendorPolicy.SelectBest(
                GetConsumableCandidatesFromVendor(),
                kind,
                StyxWoW.Me?.Level ?? 1,
                capacity);
        }

        private List<ConsumableCandidate> GetConsumableCandidatesFromVendor()
        {
            var candidates = new List<ConsumableCandidate>();
            int itemCount = LuaMerchantNumItems;

            for (int merchantIndex = 1; merchantIndex <= itemCount; merchantIndex++)
            {
                try
                {
                    List<string> values = Lua.GetReturnValues(BuildMerchantScanLua(merchantIndex));
                    if (values == null || values.Count < 4)
                        continue;

                    uint itemId = ConsumableVendorPolicy.ParseItemId(values[0]);
                    if (itemId == 0)
                        continue;

                    string name = values[1] ?? string.Empty;
                    ulong.TryParse(values[2], out ulong buyPrice);
                    ConsumableTooltipInfo tooltipInfo = ConsumableVendorPolicy.ParseTooltip(values[3]);

                    ItemInfo itemInfo = ItemInfo.FromId(itemId);
                    int requiredLevel = itemInfo?.RequiredLevel ?? 0;
                    if (tooltipInfo.Kind == ConsumableKind.None && itemInfo != null)
                        tooltipInfo = GetConsumableInfoFromItemInfo(itemInfo);

                    if (!ConsumableVendorPolicy.ShouldProtectFromSale(tooltipInfo.Kind))
                        continue;

                    candidates.Add(new ConsumableCandidate(
                        merchantIndex,
                        itemId,
                        name,
                        requiredLevel,
                        tooltipInfo.HealthRestored,
                        tooltipInfo.ManaRestored,
                        buyPrice,
                        tooltipInfo.Kind));
                }
                catch (Exception ex)
                {
                    Logging.WriteDebug("Could not inspect merchant item {0}: {1}", merchantIndex, ex.Message);
                }
            }

            return candidates;
        }

        private static string BuildMerchantScanLua(int merchantIndex)
        {
            return string.Format(
                "local i={0} " +
                "local link=GetMerchantItemLink(i) or '' " +
                "local name,_,price=GetMerchantItemInfo(i) " +
                "if not CopilotBuddyMerchantScanTooltip then " +
                "CopilotBuddyMerchantScanTooltip=CreateFrame('GameTooltip','CopilotBuddyMerchantScanTooltip',UIParent,'GameTooltipTemplate') " +
                "CopilotBuddyMerchantScanTooltip:SetOwner(UIParent,'ANCHOR_NONE') end " +
                "local tip=CopilotBuddyMerchantScanTooltip tip:ClearLines() tip:SetMerchantItem(i) " +
                "local lines={{}} for n=1,tip:NumLines() do " +
                "local region=_G[tip:GetName()..'TextLeft'..n] " +
                "if region then local text=region:GetText() if text then table.insert(lines,text) end end end " +
                "return link,name or '',tostring(price or 0),table.concat(lines,' ')",
                merchantIndex);
        }

        private static ConsumableTooltipInfo GetConsumableInfoFromItemInfo(ItemInfo itemInfo)
        {
            ConsumableKind kind = ConsumableKind.None;
            int healthRestored = 0;
            int manaRestored = 0;

            foreach (int spellId in itemInfo.SpellId ?? Array.Empty<int>())
            {
                if (spellId == 0)
                    continue;

                WoWSpell spell = WoWSpell.FromId(spellId);
                if (spell == null)
                    continue;

                if (spell.Name == "Food" || spell.Name == "Refreshment")
                {
                    kind |= ConsumableKind.Food;
                    healthRestored = Math.Max(
                        healthRestored,
                        GetPeriodicRestoration(spell, WoWApplyAuraType.PeriodicHeal));
                }

                if (spell.Name == "Drink" || spell.Name == "Refreshment")
                {
                    kind |= ConsumableKind.Drink;
                    manaRestored = Math.Max(
                        manaRestored,
                        GetPeriodicRestoration(spell, WoWApplyAuraType.PeriodicEnergize));
                }
            }

            return new ConsumableTooltipInfo(kind, healthRestored, manaRestored);
        }

        private static int GetPeriodicRestoration(WoWSpell spell, WoWApplyAuraType auraType)
        {
            int total = 0;
            foreach (SpellEffect effect in spell.SpellEffects)
            {
                if (effect == null || effect.AuraType != auraType)
                    continue;

                int ticks = effect.Amplitude > 0 && spell.BaseDuration > 0
                    ? Math.Max(1, spell.BaseDuration / (int)effect.Amplitude)
                    : 1;
                total = Math.Max(total, (Math.Abs(effect.BasePoints) + 1) * ticks);
            }
            return total;
        }

        /// <summary>
        /// Gets a merchant item by its index.
        /// </summary>
        public MerchantItem GetMerchantItemByIndex(int index)
        {
            if (index < 0 || index >= MerchantNumItems)
                return null;
            return GetMerchantItemAtIndex(index);
        }

        /// <summary>
        /// Returns true if the buy operation was successful.
        /// </summary>
        public bool BuyItem(int index, int amount, out bool success)
        {
            if (index < 0 || amount <= 0)
            {
                success = false;
                return false;
            }
            Lua.DoString("BuyMerchantItem(" + index + "," + amount + ")");
            success = true;
            return true;
        }

        /// <summary>
        /// FEAT-32: Buys an item by name from the merchant.
        /// Iterates through merchant items, finds by name, buys the requested amount.
        /// Returns true if the item was found and purchased.
        /// </summary>
        public bool BuyItem(string name, int amount = 1)
        {
            if (string.IsNullOrEmpty(name) || amount <= 0)
                return false;

            int numItems = MerchantNumItems;
            for (int i = 0; i < numItems; i++)
            {
                var merchantItem = GetMerchantItemAtIndex(i);
                if (merchantItem != null && 
                    string.Equals(merchantItem.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if ((long)amount * (long)merchantItem.BuyPrice >= (long)StyxWoW.Me.Coinage)
                        return false;
                    for (int j = 0; j < amount; j++)
                    {
                        BuyItem(merchantItem.Index, 1);
                    }
                    return true;
                }
            }
            return false;
        }

        public static readonly MerchantFrame Instance;
    }
}

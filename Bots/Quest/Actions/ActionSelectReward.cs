// Decompiled with JetBrains decompiler
// Type: Bots.Quest.Actions.ActionSelectReward
// Assembly: Honorbuddy, Version=2.0.0.5999, Culture=neutral, PublicKeyToken=50a565ab5c01ae50
// Based on HB 4.3.4 ActionSelectReward

using System;
using System.Collections.Generic;
using Styx.Helpers;
using Styx.Logic.Inventory;
using Styx.Logic.Inventory.Frames.Quest;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Action = TreeSharp.Action;

#nullable disable
namespace Bots.Quest.Actions;

/// <summary>
/// Selects a quest reward only from a complete live original-client choice observation.
/// Cache metadata may be stale or incomplete, so an unknown live choice identity defers
/// instead of clicking an arbitrary index.
/// </summary>
public class ActionSelectReward : Action
{
    private const int MaximumRewardChoices = 64;
    private readonly WeightSetEx _weightSet = WeightSetEx.CurrentWeightSet;

    internal sealed class LiveRewardChoice
    {
        public int Index { get; set; }
        public uint ItemId { get; set; }
        public int Count { get; set; }
        public string ItemLink { get; set; }
    }

    internal static bool TryObserveLiveChoices(
        Func<int> observeCount,
        Func<int, string> observeLink,
        Func<int, int?> observeStackCount,
        out List<LiveRewardChoice> choices)
    {
        if (observeCount == null) throw new ArgumentNullException(nameof(observeCount));
        if (observeLink == null) throw new ArgumentNullException(nameof(observeLink));
        if (observeStackCount == null) throw new ArgumentNullException(nameof(observeStackCount));

        choices = new List<LiveRewardChoice>();
        int count = observeCount();
        if (count < 0 || count > MaximumRewardChoices)
            return false;

        for (int index = 0; index < count; index++)
        {
            string itemLink = observeLink(index);
            uint itemId = ConsumableVendorPolicy.ParseItemId(itemLink);
            int? stackCount = observeStackCount(index);
            if (itemId == 0 || string.IsNullOrEmpty(itemLink) ||
                !stackCount.HasValue || stackCount.Value <= 0)
            {
                choices.Clear();
                return false;
            }

            choices.Add(new LiveRewardChoice
            {
                Index = index,
                ItemId = itemId,
                Count = stackCount.Value,
                ItemLink = itemLink
            });
        }

        return true;
    }

    protected override RunStatus Run(object context)
    {
        if (!TryObserveLiveChoices(
            () => Lua.GetReturnVal<int>("return GetNumQuestChoices()", 0U),
            index => Lua.GetReturnVal<string>(
                string.Format("return GetQuestItemLink('choice', {0})", index + 1), 0U),
            index =>
            {
                int count = Lua.GetReturnVal<int>(
                    string.Format("return select(3, GetQuestItemInfo('choice', {0}))", index + 1), 0U);
                return count;
            },
            out List<LiveRewardChoice> choices))
        {
            Logging.Write("Quest reward choices could not be observed completely; deferring reward selection.");
            return RunStatus.Failure;
        }

        if (choices.Count == 0)
        {
            Logging.Write("No live quest reward choices are currently available; deferring reward selection.");
            return RunStatus.Failure;
        }

        float bestScore = float.MinValue;
        int bestIndex = -1;
        string bestName = "";

        // First pass: stat-weight evaluation on the exact live choice identity.
        foreach (LiveRewardChoice choice in choices)
        {
            ItemInfo itemInfo = ItemInfo.FromId(choice.ItemId);
            if (itemInfo == null || !ObjectManager.Me.CanEquipItem(itemInfo))
                continue;

            ItemStats itemStats = new ItemStats(choice.ItemLink);
            float score = _weightSet.EvaluateItem(itemInfo, itemStats);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = choice.Index;
                bestName = itemInfo.Name;
            }
        }

        // Second pass: vendor sell-price fallback, still using only the live choice set.
        if (bestIndex == -1)
        {
            float bestValue = float.MinValue;
            foreach (LiveRewardChoice choice in choices)
            {
                ItemInfo itemInfo = ItemInfo.FromId(choice.ItemId);
                if (itemInfo == null)
                    continue;

                float sellValue = (float)(itemInfo.SellPrice * choice.Count);
                Logging.Write("{0}{1} sells for {2}",
                    itemInfo.Name,
                    choice.Count > 1 ? ("x" + choice.Count) : "",
                    sellValue);

                if (sellValue > bestValue)
                {
                    bestName = itemInfo.Name;
                    bestValue = sellValue;
                    bestIndex = choice.Index;
                }
            }
        }

        if (bestIndex == -1)
        {
            Logging.Write("Live quest reward choices have no usable item identity; deferring reward selection.");
            return RunStatus.Failure;
        }

        Logging.Write("Choosing {0}", bestName);
        QuestFrame.Instance.SelectQuestReward(bestIndex);
        return RunStatus.Success;
    }
}

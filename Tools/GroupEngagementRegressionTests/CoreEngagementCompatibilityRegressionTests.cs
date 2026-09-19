using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Singular.Helpers;
using Styx;
using Styx.Logic;
using Styx.Logic.Combat;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

// Core reference: TrinityCore 3.3.5 8fda442f, Unit::SetStunned clears
// the displayed target, not the combat relationship. These tests execute
// the tracked GroupCombatSafety and Unit policies with controlled client
// observations. They do not execute a server, GatherBuddy, or a native stun.
internal static class CoreEngagementCompatibilityRegressionTests
{
    private sealed class AssertionFailure : Exception
    {
        internal AssertionFailure(string message) : base(message) { }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>();
        foreach (string context in new[] { "solo", "party", "raid" })
        {
            string mode = context;
            void Add(string name, Action<LocalPlayer, WoWUnit> test) =>
                cases.Add((mode + ": " + name, () =>
                {
                    Reset(mode);
                    var enemy = new WoWUnit { Guid = 100, Combat = true };
                    ObjectManager.Objects.Add(enemy);
                    StyxWoW.Me.CurrentTarget = enemy;
                    test(StyxWoW.Me, enemy);
                }));

            Add("positive self threat survives an empty displayed target", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 1;
                Check(enemy.CurrentTarget == null && enemy.IsEligibleDungeonCombatTarget(), "positive threat was discarded");
                Check(ReferenceEquals(me.CurrentTarget, enemy), "eligibility changed our selected target");
            });
            Add("displayed target loss does not clear the engagement", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 20;
                enemy.CurrentTarget = me;
                enemy.IsTargetingMeOrPet = true;
                Check(enemy.IsEligibleDungeonCombatTarget(), "initial engagement");
                enemy.CurrentTarget = null;
                enemy.IsTargetingMeOrPet = false;
                Check(enemy.IsEligibleDungeonCombatTarget(), "stun-shaped observation ended engagement");
                Check(ReferenceEquals(me.CurrentTarget, enemy) && enemy.Combat, "read-only policy mutated combat or selection");
            });
            Add("displayed target restoration remains eligible", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 20;
                Check(enemy.IsEligibleDungeonCombatTarget(), "stunned engagement");
                enemy.CurrentTarget = me;
                enemy.IsTargetingMeOrPet = true;
                Check(enemy.IsEligibleDungeonCombatTarget(), "restored target was rejected");
            });
            Add("zero threat without a relationship stays unknown", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 0;
                Check(!enemy.IsEligibleDungeonCombatTarget(), "zero was treated as positive threat");
            });
            Add("wrapped threat cannot authorize an unrelated target", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = uint.MaxValue;
                Check(!enemy.IsEligibleDungeonCombatTarget(), "wrapped signed threat was accepted");
            });
            Add("stun aura alone never grants engagement", (me, enemy) =>
            {
                enemy.Auras["Hammer of Justice"] = new WoWAura { Name = "Hammer of Justice", CreatorGuid = 900 };
                Check(!enemy.IsEligibleDungeonCombatTarget(), "unrelated crowd control was treated as our combat");
            });
            Add("enemy combat reset revokes retained threat", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 20;
                Check(enemy.IsEligibleDungeonCombatTarget(), "initial engagement");
                enemy.Combat = false;
                Check(!enemy.IsEligibleDungeonCombatTarget(), "reset enemy borrowed historical threat");
            });
            Add("invalid enemy remains denied", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 20;
                enemy.IsValid = false;
                Check(!enemy.IsEligibleDungeonCombatTarget(), "invalid enemy accepted");
            });
            Add("dead enemy remains denied", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 20;
                enemy.IsAlive = false;
                Check(!enemy.IsEligibleDungeonCombatTarget(), "dead enemy accepted");
            });
            Add("invalid observer cannot authorize retained threat", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 20;
                me.IsValid = false;
                Check(!GroupCombatSafety.IsEngagedWithGroup(enemy), "invalid player supplied combat authority");
            });
            Add("dead observer cannot authorize retained threat", (me, enemy) =>
            {
                enemy.Threats[me.Guid] = 20;
                me.IsAlive = false;
                Check(!GroupCombatSafety.IsEngagedWithGroup(enemy), "dead player supplied combat authority");
            });
            Add("invalid observer cannot bypass through aggro flags", (me, enemy) =>
            {
                enemy.Aggro = true;
                me.IsValid = false;
                Check(!GroupCombatSafety.IsEngagedWithGroup(enemy), "invalid player borrowed an aggro flag");
            });
            if (mode != "solo")
            {
                Add("positive member threat survives a missing displayed target", (me, enemy) =>
                {
                    var member = new WoWPlayer { Guid = 40, IsFriendly = true };
                    var roster = mode == "raid" ? me.RaidMembers : me.PartyMembers;
                    roster.Add(member);
                    enemy.Threats[member.Guid] = 20;
                    Check(enemy.IsEligibleDungeonCombatTarget(), "member threat was lost");
                    roster.Clear();
                    Check(!enemy.IsEligibleDungeonCombatTarget(), "departed member retained permission");
                });
            }
        }

        int passed = 0, assertions = 0, unexpected = 0;
        var oldPlayer = StyxWoW.Me;
        var oldBot = BotManager.Current;
        var oldLeader = RaFHelper.Leader;
        var oldObjects = ObjectManager.Objects.ToArray();
        var oldTanks = Group.Tanks.ToArray();
        try
        {
            foreach (var item in cases)
            {
                try { item.Test(); passed++; Console.WriteLine("PASS core engagement: " + item.Name); }
                catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL core engagement assertion: " + item.Name + ": " + error.Message); }
                catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR core engagement fixture: " + item.Name + ": " + error); }
            }
        }
        finally
        {
            StyxWoW.Me = oldPlayer;
            BotManager.Current = oldBot;
            RaFHelper.Leader = oldLeader;
            ObjectManager.Objects.Clear(); ObjectManager.Objects.AddRange(oldObjects);
            Group.Tanks.Clear(); Group.Tanks.AddRange(oldTanks);
        }
        Console.WriteLine($"Core engagement compatibility scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; linked production policy; controlled client observations; no server/game attached.");
        if (assertions + unexpected != 0)
            throw new InvalidOperationException($"Core engagement regressions: assertions={assertions}; unexpected={unexpected}");
    }

    private static void Reset(string mode)
    {
        StyxWoW.Me = new LocalPlayer { Guid = 1, IsFriendly = true, Combat = true,
            IsInParty = mode == "party", IsInRaid = mode == "raid" };
        BotManager.Current = new(); RaFHelper.Leader = null;
        Group.Tanks.Clear(); ObjectManager.Objects.Clear();
    }
    private static void Check(bool valid, string why) { if (!valid) throw new AssertionFailure(why); }
}

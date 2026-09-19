using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Singular.ClassSpecific.Paladin;
using Singular.Settings;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.WoWInternals.WoWObjects;

// Exact PallyPower v3.2.21/Wrath table shape is represented only by controlled
// Lua return values. No addon Lua is executed and no SavedVariables are written.
internal static class PallyPowerAssignmentRegressionTests
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>
        {
            ("bridge disabled keeps existing Auto policy and does not query addon", () =>
            {
                Setup(false); Know("Blessing of Kings", "Blessing of Sanctuary");
                Fixture.LuaResult = _ => Row(1,4,0,0);
                Check(Bless(StyxWoW.Me) == "Blessing of Kings", "disabled bridge changed Auto blessing");
                Check(Fixture.LuaQueries.Count == 0, "disabled bridge queried PallyPower");
            }),
            ("verified Wrath slot4 maps to Sanctuary", () =>
            {
                Setup(); Know("Blessing of Kings", "Blessing of Sanctuary");
                Fixture.LuaResult = _ => Row(1,4,0,0);
                Check(Bless(StyxWoW.Me) == "Blessing of Sanctuary", "Wrath slot4 was not Sanctuary");
            }),
            ("verified per-player normal override supersedes class assignment", () =>
            {
                Setup(); Know("Blessing of Kings", "Blessing of Might");
                Fixture.LuaResult = _ => Row(1,3,2,0);
                Check(Bless(StyxWoW.Me) == "Blessing of Might", "normal exception did not override class assignment");
            }),
            ("verified explicit none does not invent an Auto blessing", () =>
            {
                Setup(); Know("Blessing of Kings", "Blessing of Might");
                Fixture.LuaResult = _ => Row(1,0,0,0);
                Check(Bless(StyxWoW.Me) == null, "PallyPower none was replaced by Singular Auto");
            }),
            ("addon absent falls back to existing Auto policy", () =>
            {
                Setup(); Know("Blessing of Kings");
                Fixture.LuaResult = _ => new(){"0"};
                Check(Bless(StyxWoW.Me) == "Blessing of Kings", "absent addon disabled existing Auto support");
            }),
            ("present non-Wrath state defers instead of interpreting raw slot4", () =>
            {
                Setup(); Know("Blessing of Kings", "Blessing of Sanctuary");
                Fixture.LuaResult = _ => new(){"2"};
                Check(Bless(StyxWoW.Me) == null, "unknown flavor activated or bypassed assignment safety");
            }),
            ("malformed present state defers instead of guessing", () =>
            {
                Setup(); Know("Blessing of Kings");
                Fixture.LuaResult = _ => new(){"1","4"};
                Check(Bless(StyxWoW.Me) == null, "malformed assignment fell back to guessed Auto behavior");
            }),
            ("Lua observation failure defers without escaping support owner", () =>
            {
                Setup(); Know("Blessing of Kings");
                Fixture.LuaResult = _ => throw new InvalidOperationException("controlled query failure");
                Check(Bless(StyxWoW.Me) == null, "query failure was treated as permission to guess");
            }),
            ("assigned unlearned blessing does not fall back to another family", () =>
            {
                Setup(); Know("Blessing of Kings");
                Fixture.LuaResult = _ => Row(1,4,0,0);
                Check(Bless(StyxWoW.Me) == null, "unlearned assigned Sanctuary fell back to Kings");
            }),
            ("assigned Might still respects Battle Shout equivalence", () =>
            {
                Setup(); Know("Blessing of Might", "Blessing of Kings");
                Fixture.Aura(StyxWoW.Me, "Battle Shout", 99);
                Fixture.LuaResult = _ => Row(1,2,0,0);
                Check(Bless(StyxWoW.Me) == null, "assignment bypassed observed flat-AP coverage");
            }),
            ("manual Singular blessing remains explicit and bypasses PallyPower read", () =>
            {
                Setup(); Know("Blessing of Kings", "Blessing of Sanctuary");
                SingularSettings.Instance.Paladin.Blessings = PaladinBlessings.Kings;
                Fixture.LuaResult = _ => Row(1,4,0,0);
                Check(Bless(StyxWoW.Me) == "Blessing of Kings", "manual Singular setting lost precedence");
                Check(Fixture.LuaQueries.Count == 0, "manual Singular setting still queried assignment source");
            }),
            ("Wrath class mapping uses DeathKnight index10", () =>
            {
                Setup(); Know("Blessing of Might");
                var dk=Fixture.Add(WoWClass.DeathKnight); dk.Name="Death Knight Fixture";
                Fixture.LuaResult = code =>
                {
                    Check(code.Contains("classIndex=10", StringComparison.Ordinal), "DeathKnight did not map to PallyPower class index10");
                    return Row(1,2,0,0);
                };
                Check(Bless(dk) == "Blessing of Might", "DeathKnight assignment was not applied");
            }),
            ("unsupported raw blessing slot defers instead of cross-flavor mapping", () =>
            {
                Setup(); Know("Blessing of Kings");
                Fixture.LuaResult = _ => Row(1,6,0,0);
                Check(Bless(StyxWoW.Me) == null, "unsupported raw slot was interpreted");
            }),
            ("verified Wrath aura slot5 maps to Frost Resistance Aura", () =>
            {
                Setup(); Know("Frost Resistance Aura", "Devotion Aura");
                Fixture.LuaResult = _ => Row(1,0,0,5);
                Check(Aura(StyxWoW.Me) == "Frost Resistance Aura", "Wrath aura slot5 was not Frost Resistance");
            }),
            ("verified aura none does not invent an Auto aura", () =>
            {
                Setup(); Know("Devotion Aura");
                Fixture.LuaResult = _ => Row(1,0,0,0);
                Check(Aura(StyxWoW.Me) == null, "PallyPower aura none was replaced by Auto");
            }),
            ("addon absent keeps existing Auto aura", () =>
            {
                Setup(); Know("Devotion Aura");
                Fixture.LuaResult = _ => new(){"0"};
                Check(Aura(StyxWoW.Me) == "Devotion Aura", "absent addon disabled normal Auto aura");
            }),
            ("manual Singular aura remains explicit without PallyPower read", () =>
            {
                Setup(); Know("Devotion Aura", "Frost Resistance Aura");
                SingularSettings.Instance.Paladin.Aura = PaladinAura.Devotion;
                Fixture.LuaResult = _ => Row(1,0,0,5);
                Check(Aura(StyxWoW.Me) == "Devotion Aura", "manual aura lost precedence");
                Check(Fixture.LuaQueries.Count == 0, "manual aura queried PallyPower");
            }),
            ("read-only query pins reviewed addon version before interpreting slots", () =>
            {
                Setup(); Know("Blessing of Kings");
                Fixture.LuaResult = code =>
                {
                    Check(code.Contains("GetAddOnMetadata('PallyPower','Version')", StringComparison.Ordinal),
                        "query did not observe loaded PallyPower version");
                    Check(code.Contains("v3.2.21", StringComparison.Ordinal),
                        "query did not pin the reviewed v3.2.21 mapping");
                    return Row(1,3,0,0);
                };
                Check(Bless(StyxWoW.Me) == "Blessing of Kings", "version-pinned query control failed");
            }),
            ("read-only query pins the Wrath table name", () =>
            {
                Setup(); Know("Blessing of Kings");
                Fixture.LuaResult = code =>
                {
                    Check(code.Contains("[\"Wrath\"]", StringComparison.Ordinal), "query followed addon flavor table indirectly");
                    return Row(1,3,0,0);
                };
                Check(Bless(StyxWoW.Me) == "Blessing of Kings", "Wrath query control failed");
            })
        };

        int passed=0, assertions=0, unexpected=0;
        foreach (var item in cases)
        {
            Fixture.Reset();
            try { item.Test(); passed++; Console.WriteLine("PASS PallyPower bridge: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL PallyPower bridge: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR PallyPower bridge: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"PallyPower assignment scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; linked Paladin support; controlled read-only Lua observation; no addon/client attached.");
        if(assertions+unexpected!=0) throw new InvalidOperationException("PallyPower assignment regression");
    }

    private static void Setup(bool enabled=true)
    {
        SingularSettings.Instance.Paladin.UsePallyPowerAssignments=enabled;
        SingularSettings.Instance.Paladin.Blessings=PaladinBlessings.Auto;
        SingularSettings.Instance.Paladin.Aura=PaladinAura.Auto;
    }
    private static List<string> Row(int state,int classSlot,int normalSlot,int auraSlot) =>
        new(){state.ToString(),classSlot.ToString(),normalSlot.ToString(),auraSlot.ToString()};
    private static void Know(params string[] spells) { foreach(var spell in spells) Fixture.Known.Add(spell); }
    private static string? Bless(WoWPlayer player) => Invoke("SelectNormalBlessing", player);
    private static string? Aura(WoWPlayer player) => Invoke("SelectAura", player);
    private static string? Invoke(string name, WoWPlayer player) =>
        (string?)typeof(Common).GetMethod(name,Hidden)!.Invoke(null,new object[]{player});
    private static void Check(bool valid,string reason) { if(!valid) throw new AssertionFailure(reason); }
}

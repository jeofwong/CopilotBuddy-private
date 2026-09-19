using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Styx.Combat.CombatRoutine;

// Full tracked Singular compilation, then actual CLR registration attributes.
// The optional isolation opener is not an ordinary replacement combat rotation.
internal static class RetributionRotationRegistrationRegressionTests
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class Failure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        Type fixtureType = typeof(SingularBehaviorCountRegressionTests)
            .GetNestedType("Fixture", BindingFlags.NonPublic)!;
        using var fixture = (IDisposable)Activator.CreateInstance(fixtureType, true)!;
        var assembly = ((Type)fixtureType.GetField("factories", Hidden)!.GetValue(fixture)!).Assembly;
        Type owner = assembly.GetType("Singular.ClassSpecific.Paladin.Retribution", true)!;
        MethodInfo normal = owner.GetMethod("CreateRetributionPaladinNormalPullAndCombat", Hidden)!;
        MethodInfo isolation = owner.GetMethod("CreateRetributionPaladinIsolationPull", Hidden)!;
        MethodInfo instance = owner.GetMethod("CreateRetributionPaladinInstancePullAndCombat", Hidden)!;
        MethodInfo pvp = owner.GetMethod("CreateRetributionPaladinPvPPullAndCombat", Hidden)!;
        Type specType = assembly.GetType("Singular.Managers.TalentSpec", true)!;
        Type contextType = assembly.GetType("Singular.WoWContext", true)!;
        Type behaviorType = assembly.GetType("Singular.BehaviorType", true)!;
        int spec = Convert.ToInt32(Enum.Parse(specType, "RetributionPaladin"));

        var cases = new List<(string Name, Action Test)>();
        cases.Add(("all four tracked Ret factories remain present", () =>
            Check(normal != null && isolation != null && instance != null && pvp != null,
                "a full or optional tracked Ret factory is missing")));
        foreach (string value in new[] { "Pull", "Combat" })
        {
            string role = value;
            int wantedRole = Convert.ToInt32(Enum.Parse(behaviorType, role));
            cases.Add(("Normal " + role + " is registered on the complete Ret rotation", () =>
                Check(Matches(normal, spec, Convert.ToInt32(Enum.Parse(contextType, "Normal")), wantedRole),
                    "the complete Normal Ret rotation lost its automatic " + role + " registration")));
            cases.Add(("optional isolation helper cannot replace automatic " + role, () =>
                Check(!HasAttributeValue(isolation, "Behavior", "Type", value => (value & wantedRole) != 0),
                    "the Exorcism-only isolation helper was registered as ordinary " + role)));
            cases.Add(("Instances " + role + " retains its complete Ret factory", () =>
                Check(Matches(instance, spec, Convert.ToInt32(Enum.Parse(contextType, "Instances")), wantedRole),
                    "Instances Ret registration changed")));
            cases.Add(("Battlegrounds " + role + " retains its complete Ret factory", () =>
                Check(Matches(pvp, spec, Convert.ToInt32(Enum.Parse(contextType, "Battlegrounds")), wantedRole),
                    "Battlegrounds Ret registration changed")));
        }

        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var c in cases)
        {
            try { c.Test(); passed++; Console.WriteLine("PASS Ret rotation registration: " + c.Name); }
            catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL Ret rotation registration: " + c.Name + ": " + e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR Ret rotation registration: " + c.Name + ": " + e); }
        }
        Console.WriteLine($"Ret rotation registration scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; full tracked Singular compilation and CLR attributes; no rotation tick or game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Ret rotation registration regression");
    }

    private static bool Matches(MethodInfo method, int spec, int context, int role) =>
        method != null
        && HasAttributeValue(method, "Class", "SpecificClass", value => value == (int)WoWClass.Paladin)
        && HasAttributeValue(method, "Spec", "SpecificSpec", value => value == spec)
        && HasAttributeValue(method, "Context", "SpecificContext", value => (value & context) != 0)
        && HasAttributeValue(method, "Behavior", "Type", value => (value & role) != 0);

    private static bool HasAttributeValue(MethodInfo method, string kind, string property, Func<int, bool> matches) =>
        method != null && method.GetCustomAttributes(false)
            .Where(a => a.GetType().FullName == "Singular.Dynamics." + kind + "Attribute")
            .Any(a => matches(Convert.ToInt32(a.GetType().GetProperty(property, Hidden)!.GetValue(a))));

    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new Failure(reason);
    }
}

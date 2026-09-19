using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Styx.Helpers;
using Styx.Logic.Inventory.Frames.Merchant;

// Structural checks of the actual C# script builder, not a native transaction.
// The separate controlled Lua probe is not original-client acceptance.
internal static class MerchantStepContextRegressionTests
{
    private sealed class AssertionFailure : Exception
    {
        internal AssertionFailure(string message) : base(message) { }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        MethodInfo method = typeof(MerchantFrame).GetMethod("BuildSellNextItemLua",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Actual seller script builder missing");
        string Build(ItemQuality quality) => (string)(method.Invoke(null,
            new object[] { quality, Array.Empty<string>(), Array.Empty<uint>() })
            ?? throw new InvalidOperationException("Actual builder returned null"));
        string script = Build(ItemQuality.Poor);
        const string visible = "if not MerchantFrame or not MerchantFrame:IsShown() then return 'ok',3 end";
        const string identity = "if UnitGUID('player')~=player or UnitGUID('npc')~=merchant then return 'ok',2 end";
        int selected = script.LastIndexOf("GetContainerItemInfo(b,s)", StringComparison.Ordinal);
        int submit = script.IndexOf("UseContainerItem(b,s)", StringComparison.Ordinal);
        int finalVisible = script.LastIndexOf(visible, StringComparison.Ordinal);
        int finalIdentity = script.LastIndexOf(identity, StringComparison.Ordinal);
        int exclusion = script.LastIndexOf("if not blockedSaleStacks or not blockedSaleStacks[token] then", StringComparison.Ordinal);
        var cases = new List<(string Name, Action Test)>
        {
            ("initial visibility admission is retained", () => Check(script.IndexOf(visible, StringComparison.Ordinal) >= 0 && script.IndexOf(visible, StringComparison.Ordinal) < selected, "initial merchant guard missing")),
            ("merchant visibility is checked after candidate observations", () => Check(selected >= 0 && finalVisible > selected && finalVisible < submit, "late merchant visibility was not revalidated")),
            ("captured identity guard follows final visibility before submission", () => Check(selected >= 0 && finalVisible > selected && finalIdentity > finalVisible && finalIdentity < submit, "late player/merchant identity was not revalidated")),
            ("existing retry exclusion still encloses final admission", () => Check(selected >= 0 && exclusion > selected && exclusion < finalVisible && finalVisible < submit, "final guard bypasses retry exclusion")),
            ("submitted request retains its receipt rather than claiming acknowledgement", () => Check(script.Contains("UseContainerItem(b,s) return 'ok',1,token", StringComparison.Ordinal) && !script.Contains("CloseMerchant", StringComparison.Ordinal), "receipt or interface ownership contract changed")),
            ("empty quality retains no-op", () => Check(Build(ItemQuality.None) == "return 'ok',0", "empty-mask behavior changed"))
        };
        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS merchant step context structure: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL merchant step context structure: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR merchant step context structure: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"Merchant step-context structure scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual generated source; structural only, no Lua/native sale.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Merchant step-context structural regression");
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid) throw new AssertionFailure(reason);
    }
}

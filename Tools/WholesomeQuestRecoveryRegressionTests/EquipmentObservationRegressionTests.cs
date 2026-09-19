using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Reuse the existing controlled item/world fixture, but compile all four real
// SmartLoot owners. Neither scoring nor roll/equip decisions are substituted.
internal static class EquipmentObservationRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        string? root = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CopilotBuddy.csproj"))) { root = dir.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");
        string fixture = (string)typeof(SmartLootDecisionRegressionTests).GetField("Boundary", flags)!.GetRawConstantValue()!;
        string temp = Path.Combine(Path.GetTempPath(), "cb-equipment-observation-" + Guid.NewGuid().ToString("N"));
        bool logging = Styx.Helpers.Logging.FileLogging;
        Directory.CreateDirectory(temp);
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            foreach (string name in new[] { "SmartLootRoller.cs", "SmartLootRollerSettings.cs", "PawnScorer.cs", "StatWeightsPresets.cs" })
                File.Copy(Path.Combine(root, "runtime-snapshot", "Plugins", "SmartLootRoller", name), Path.Combine(temp, name));
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), fixture + Environment.NewLine + Cases);
            Type compilerType = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(compilerType, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                compilerType.GetMethod("AddReference", flags)!.Invoke(compiler, new object[] { path });
            var result = (CompilerResults)compilerType.GetMethod("Compile", flags)!.Invoke(compiler, null)!;
            var errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Actual equipment-owner compile failed: " + string.Join(";", errors.Select(e => e.ToString())));
            var assembly = (Assembly)compilerType.GetProperty("CompiledAssembly", flags)!.GetValue(compiler)!;
            try { assembly.GetType("EquipmentCases", true)!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = logging; Directory.Delete(temp, true); }
    }
    private const string Cases = """
/* Compiled fixture extension, not another module initializer. */ public static class EquipmentCases
{
    private sealed class Failure(string text) : Exception(text) { }
    public static void Run()
    {
        var cases = new List<(string Name, Action Test)>();
        foreach (string value in new[] { "player", "inventory", "equipment", "items", "listed metadata", "main-hand metadata", "off-hand metadata" })
        {
            string state = value;
            void Add(string name, Action<SmartLootRollerSettings> body) => cases.Add((state + ": " + name, () => { var s = Reset(); Missing(state); body(s); }));
            Add("unknown equipment score is not comparable", s => Check(float.IsNaN(PawnScorer.GetMinEquippedScore(InventoryType.Neck, s.GetWeightsDictionary())), "unknown equipment was valued as a known zero"));
            Add("unknown equipment is not an empty slot", s => Check(!SlotEmpty(), "unknown equipment authorized empty-slot insertion"));
            Add("unknown equipment level is not a known zero", s => Check(float.IsNaN(ItemLevel()), "unknown equipment level enabled tie-breaking"));
            Add("unknown equipment cannot authorize Need", s => { s.NoMatchRule = NoMatchRollType.Pass; Roll(); Check(LootCases.Scripts.SequenceEqual(new[] { "RollOnLoot(42, 0)" }), "unknown comparison authorized Need"); });
            Add("unknown equipment cannot authorize auto-equip", s => { Equip(); Check(!LootCases.Scripts.Contains("EQUIP"), "unknown slot caused item use"); });
            Add("unknown comparison cannot choose Disenchant over Greed", s => {
                s.NoMatchRule = NoMatchRollType.Greed; s.RollForLootDE = true; LootCases.CanDisenchant = true;
                Roll(); Check(LootCases.Scripts.SequenceEqual(new[] { "RollOnLoot(42, 2)" }), "unresolved comparison entered destructive fallback");
            });
            Add("unknown comparison cannot choose Disenchant over Pass", s => {
                s.NoMatchRule = NoMatchRollType.Pass; s.RollForLootDE = true; LootCases.CanDisenchant = true;
                Roll(); Check(LootCases.Scripts.SequenceEqual(new[] { "RollOnLoot(42, 0)" }), "unresolved comparison ignored configured Pass");
            });
            Add("unknown comparison with unavailable Greed still cannot Disenchant", s => {
                s.NoMatchRule = NoMatchRollType.Greed; s.RollForLootDE = true; LootCases.CanDisenchant = true; LootCases.CanGreed = false;
                Roll(); Check(LootCases.Scripts.SequenceEqual(new[] { "RollOnLoot(42, 0)" }), "unavailable Greed escalated to Disenchant");
            });
        }
        cases.Add(("known empty inventory retains a zero baseline", () => { var s = Reset(); Check(PawnScorer.GetMinEquippedScore(InventoryType.Neck, s.GetWeightsDictionary()) == 0 && SlotEmpty(), "known empty slot was blocked"); }));
        cases.Add(("known empty slot still equips", () => { Reset(); Equip(); Check(LootCases.Scripts.Count(x => x == "EQUIP") == 1, "known empty equip control failed"); }));
        cases.Add(("known occupied slot retains weighted comparison", () => { var s = Reset(); Item(InventoryType.Neck); Check(PawnScorer.GetMinEquippedScore(InventoryType.Neck, s.GetWeightsDictionary()) == 20 && !SlotEmpty(), "known occupied slot changed score"); }));
        cases.Add(("known zero-score item is not unknown", () => { var s = Reset(); Item(InventoryType.Neck); s.ClearWeights(); Check(PawnScorer.GetMinEquippedScore(InventoryType.Neck, s.GetWeightsDictionary()) == 0 && !SlotEmpty(), "known zero was confused with unknown"); }));
        cases.Add(("one known ring leaves one available slot", () => { var s = Reset(); Item(InventoryType.Finger); Check(PawnScorer.GetMinEquippedScore(InventoryType.Finger, s.GetWeightsDictionary()) == 0 && PawnScorer.IsSlotEmpty(InventoryType.Finger), "second ring slot lost"); }));
        cases.Add(("two known rings compare the replacement slot", () => { var s = Reset(); Item(InventoryType.Finger); Item(InventoryType.Finger); Check(PawnScorer.GetMinEquippedScore(InventoryType.Finger, s.GetWeightsDictionary()) == 20 && !PawnScorer.IsSlotEmpty(InventoryType.Finger), "full ring comparison changed"); }));
        cases.Add(("two-hand candidate retains both-hand cost", () => { var s = Reset(); var e = StyxWoW.Me.Inventory.Equipped; e.MainHand = Item(InventoryType.WeaponMainHand); e.OffHand = Item(InventoryType.Shield); Check(PawnScorer.GetMinEquippedScore(InventoryType.TwoHandWeapon, s.GetWeightsDictionary()) == 40, "both-hand replacement was undercounted"); }));
        cases.Add(("two-hander keeps its off-hand occupied", () => { Reset(); var e = StyxWoW.Me.Inventory.Equipped; e.MainHand = Item(InventoryType.TwoHandWeapon); Check(!PawnScorer.IsSlotEmpty(InventoryType.Shield), "two-hander off-hand treated as empty"); }));
        cases.Add(("metadata recovery restores comparison without sticky quarantine", () => { var s = Reset(); var item = Item(InventoryType.Neck); var info = item.ItemInfo; item.ItemInfo = null!; _ = PawnScorer.GetMinEquippedScore(InventoryType.Neck, s.GetWeightsDictionary()); item.ItemInfo = info; Check(PawnScorer.GetMinEquippedScore(InventoryType.Neck, s.GetWeightsDictionary()) == 20, "metadata recovery stayed blocked"); }));
        int pass = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); pass++; Console.WriteLine("PASS equipment observation: " + item.Name); }
            catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL equipment observation assertion: " + item.Name + ": " + e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR equipment observation fixture: " + item.Name + ": " + e); }
        }
        Console.WriteLine($"Equipment observation scenarios: {pass}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual scorer and roll/equip owners; reused controlled observations; no live equip or roll.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Equipment observation regressions");
    }
    private static SmartLootRollerSettings Reset()
    {
        var s = (SmartLootRollerSettings)Invoke(typeof(LootCases).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)!, null)!;
        s.AutoEquipUpgrades = true;
        StyxWoW.Me.BagItems.Add(new WoWItem { ItemInfo = ItemInfo.Current });
        return s;
    }
    private static WoWItem Item(InventoryType type)
    {
        var item = new WoWItem { ItemInfo = new ItemInfo { InventoryType = type, ItemClass = WoWItemClass.Armor, Level = 10 } };
        StyxWoW.Me.Inventory.Equipped.Items.Add(item);
        return item;
    }
    private static void Missing(string state)
    {
        switch (state)
        {
            case "player": StyxWoW.Me = null!; break;
            case "inventory": StyxWoW.Me.Inventory = null!; break;
            case "equipment": StyxWoW.Me.Inventory.Equipped = null!; break;
            case "items": StyxWoW.Me.Inventory.Equipped.Items = null!; break;
            case "listed metadata": Item(InventoryType.Neck).ItemInfo = null!; break;
            case "main-hand metadata": StyxWoW.Me.Inventory.Equipped.MainHand = new WoWItem { ItemInfo = null! }; break;
            case "off-hand metadata": StyxWoW.Me.Inventory.Equipped.OffHand = new WoWItem { ItemInfo = null! }; break;
        }
    }
    private static bool SlotEmpty()
    {
        try { return PawnScorer.IsSlotEmpty(InventoryType.Neck); }
        catch (NullReferenceException) { throw new Failure("unknown items made the public emptiness query throw"); }
    }
    private static float ItemLevel()
    {
        try { return PawnScorer.GetMinEquippedItemLevel(InventoryType.Neck); }
        catch (NullReferenceException) { throw new Failure("unknown items made the public item-level query throw"); }
    }
    private static void Roll() => Invoke(typeof(LootCases).GetMethod("Roll", BindingFlags.Static | BindingFlags.NonPublic)!, null);
    private static void Equip() => Invoke(typeof(SmartLootRoller.SmartLootRoller).GetMethod("AutoEquipUpgrades", BindingFlags.Instance | BindingFlags.NonPublic)!, new SmartLootRoller.SmartLootRoller());
    private static object? Invoke(MethodInfo method, object? instance)
    {
        try { return method.Invoke(instance, null); }
        catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    private static void Check(bool value, string why) { if (!value) throw new Failure(why); }
}
""";
}

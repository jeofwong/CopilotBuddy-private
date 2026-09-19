using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Reuse the existing controlled item/world fixture, but compile all four real
// SmartLoot owners. Neither scoring nor roll/equip decisions are substituted.
internal static class EquipmentSlotAliasRegressionTests
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
        string temp = Path.Combine(Path.GetTempPath(), "cb-equipment-slot-" + Guid.NewGuid().ToString("N"));
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
            try { assembly.GetType("EquipmentSlotCases", true)!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = logging; Directory.Delete(temp, true); }
    }
    private const string Cases = """
/* Compiled fixture extension, not another module initializer. */ public static class EquipmentSlotCases
{
    private sealed class Failure(string text) : Exception(text) { }
    public static void Run()
    {
        var cases = new List<(string Name, Action Test)>();
        // These aliases are already grouped by the actual host's
        // InventoryManager.GetInventorySlotsByEquipSlot. They are slot identity,
        // not permission for a class to equip every member of a slot family.
        foreach (var family in new[] {
            new[] { InventoryType.Chest, InventoryType.Robe },
            new[] { InventoryType.Ranged, InventoryType.Thrown, InventoryType.RangedRight, InventoryType.Relic } })
        foreach (var proposed in family)
        foreach (var current in family)
        {
            var candidate = proposed; var equipped = current;
            void Add(string name, Action<SmartLootRollerSettings> test) => cases.Add(($"{candidate} over {equipped}: {name}", () => { var s = Reset(candidate, equipped); test(s); }));
            Add("same physical slot retains equipped score", s => Check(PawnScorer.GetMinEquippedScore(candidate, s.GetWeightsDictionary()) == 20, "slot alias was scored as empty"));
            Add("same physical slot is occupied", s => Check(!PawnScorer.IsSlotEmpty(candidate), "slot alias authorized empty-slot equip"));
            Add("same physical slot retains item level", s => Check(PawnScorer.GetMinEquippedItemLevel(candidate) == 100, "slot alias lost item-level comparison"));
        }
        foreach (var entry in new[] { (InventoryType.Chest, InventoryType.Robe), (InventoryType.Robe, InventoryType.Chest) })
        {
            var pair = entry;
            cases.Add(($"{pair.Item1} over {pair.Item2}: lower/equal item cannot authorize Need", () => {
                var s = Reset(pair.Item1, pair.Item2); s.NoMatchRule = NoMatchRollType.Pass; Roll();
                Check(LootCases.Scripts.SequenceEqual(new[] { "RollOnLoot(42, 0)" }), "same-slot sidegrade caused Need");
            }));
            cases.Add(($"{pair.Item1} over {pair.Item2}: empty-slot shortcut cannot equip downgrade", () => {
                Reset(pair.Item1, pair.Item2); Equip(); Check(!LootCases.Scripts.Contains("EQUIP"), "same-slot downgrade equipped");
            }));
        }
        foreach (var entry in new[] { (InventoryType.Chest, InventoryType.Neck), (InventoryType.Neck, InventoryType.Robe),
            (InventoryType.Relic, InventoryType.Chest), (InventoryType.Chest, InventoryType.Ranged), (InventoryType.Finger, InventoryType.Trinket) })
        {
            var pair = entry;
            cases.Add(($"unrelated {pair.Item2} cannot occupy {pair.Item1}", () => {
                var s = Reset(pair.Item1, pair.Item2);
                Check(PawnScorer.GetMinEquippedScore(pair.Item1, s.GetWeightsDictionary()) == 0 &&
                    PawnScorer.IsSlotEmpty(pair.Item1) && PawnScorer.GetMinEquippedItemLevel(pair.Item1) == 0, "unrelated slots were merged");
            }));
        }
        cases.Add(("legitimate null empty-slot placeholders retain known-empty admission", () => {
            var s = Reset(InventoryType.Neck, InventoryType.Neck); var items = StyxWoW.Me.Inventory.Equipped.Items;
            items.Clear(); items.Add(null!); items.Add(null!);
            Check(PawnScorer.GetMinEquippedScore(InventoryType.Neck, s.GetWeightsDictionary()) == 0 && PawnScorer.IsSlotEmpty(InventoryType.Neck), "null empty placeholders became unknown metadata");
        }));
        cases.Add(("null placeholders do not hide a known equipped item", () => {
            var s = Reset(InventoryType.Neck, InventoryType.Neck); var items = StyxWoW.Me.Inventory.Equipped.Items;
            items.Insert(0, null!); items.Add(null!);
            Check(PawnScorer.GetMinEquippedScore(InventoryType.Neck, s.GetWeightsDictionary()) == 20 && !PawnScorer.IsSlotEmpty(InventoryType.Neck), "null placeholders hid the occupied slot");
        }));
        int pass = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); pass++; Console.WriteLine("PASS equipment slot: " + item.Name); }
            catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL equipment slot assertion: " + item.Name + ": " + e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR equipment slot fixture: " + item.Name + ": " + e); }
        }
        Console.WriteLine($"Equipment slot scenarios: {pass}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual four SmartLoot owners; controlled observations; host slot aliases, not class eligibility or live equip.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Equipment slot regressions");
    }
    private static SmartLootRollerSettings Reset(InventoryType candidate, InventoryType equipped)
    {
        var s = (SmartLootRollerSettings)Invoke(typeof(LootCases).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)!, null)!;
        s.AutoEquipUpgrades = true;
        ItemInfo.Current.InventoryType = candidate; ItemInfo.Current.ArmorClass = WoWItemArmorClass.Plate; ItemInfo.Current.Level = 1;
        StyxWoW.Me.BagItems.Add(new WoWItem { ItemInfo = ItemInfo.Current });
        StyxWoW.Me.Inventory.Equipped.Items.Add(new WoWItem { ItemInfo = new ItemInfo { InventoryType = equipped, ItemClass = WoWItemClass.Armor, ArmorClass = WoWItemArmorClass.Plate, Level = 100 } });
        return s;
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

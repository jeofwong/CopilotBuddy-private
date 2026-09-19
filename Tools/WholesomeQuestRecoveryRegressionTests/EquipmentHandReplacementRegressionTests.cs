using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Use the same external observations as the existing SmartLoot tests. Compile
// all four real owners; no substitute scorer, equip policy or roll decision.
internal static class EquipmentHandReplacementRegressionTests
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
        string temp = Path.Combine(Path.GetTempPath(), "cb-hand-replacement-" + Guid.NewGuid().ToString("N"));
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
            if (errors.Length != 0) throw new InvalidOperationException("Actual hand-owner compile failed: " + string.Join(";", errors.Select(e => e.ToString())));
            var assembly = (Assembly)compilerType.GetProperty("CompiledAssembly", flags)!.GetValue(compiler)!;
            try { assembly.GetType("EquipmentHandCases", true)!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = logging; Directory.Delete(temp, true); }
    }
    private const string Cases = """
/* Compiled fixture extension, not another initializer. */ public static class EquipmentHandCases
{
    private sealed class Failure(string text) : Exception(text) { }
    public static void Run()
    {
        var cases = new List<(string Name, Action Test)>();
        foreach (var value in new[] { InventoryType.WeaponOffHand, InventoryType.Shield, InventoryType.Holdable })
        {
            var occupied = value;
            void Add(string name, Action<SmartLootRollerSettings> run) => cases.Add(($"two-hand over off-hand-only {occupied}: {name}", () => {
                var s = Reset(InventoryType.TwoHandWeapon, 10, 120); Install(null, Item(occupied, 100, 100)); run(s);
            }));
            Add("off-hand contribution is not an empty loadout", s => Check(!PawnScorer.IsSlotEmpty(InventoryType.TwoHandWeapon), "off-hand was ignored by empty-slot shortcut"));
            Add("score retains the displaced off-hand", s => Check(PawnScorer.GetMinEquippedScore(InventoryType.TwoHandWeapon, s.GetWeightsDictionary()) == 100, "displaced off-hand score was lost"));
            Add("level comparison retains the only displaced item", s => Check(PawnScorer.GetMinEquippedItemLevel(InventoryType.TwoHandWeapon) == 100, "occupied off-hand was treated as level zero"));
            Add("higher-level score downgrade never auto-equips", s => { Equip(); ExpectEquip(false); });
            Add("lower-level score tie never auto-equips", s => { SetCandidatePower(100); ItemInfo.Current.Level = 80; Equip(); ExpectEquip(false); });
            Add("genuine weighted upgrade remains eligible", s => { SetCandidatePower(150); Equip(); ExpectEquip(true); });
        }
        foreach (var value in new[] { InventoryType.Shield, InventoryType.Holdable })
        {
            var proposed = value;
            void Add(string name, Action<SmartLootRollerSettings> run) => cases.Add(($"{proposed} over two-hander: {name}", () => {
                var s = Reset(proposed, 100, 80); Install(Item(InventoryType.TwoHandWeapon, 100, 100), null); run(s);
            }));
            Add("score compares the displaced main-hand", s => Check(PawnScorer.GetMinEquippedScore(proposed, s.GetWeightsDictionary()) == 100, "main-hand score was lost"));
            Add("level compares the same displaced main-hand", s => Check(PawnScorer.GetMinEquippedItemLevel(proposed) == 100, "level compared an empty off-hand instead"));
            Add("two-handed weapon still occupies the off-hand", s => Check(!PawnScorer.IsSlotEmpty(proposed), "two-hander did not occupy off-hand"));
            Add("lower-level score tie never auto-equips", s => { Equip(); ExpectEquip(false); });
            Add("same-level score tie never auto-equips", s => { ItemInfo.Current.Level = 100; Equip(); ExpectEquip(false); });
            Add("higher-level score tie retains existing policy", s => { ItemInfo.Current.Level = 101; Equip(); ExpectEquip(true); });
            Add("genuine weighted upgrade retains existing policy", s => { SetCandidatePower(150); Equip(); ExpectEquip(true); });
        }
        cases.Add(("off-hand weapon level query follows displaced two-hander", () => {
            Reset(InventoryType.TwoHandWeapon, 10, 1); Install(Item(InventoryType.TwoHandWeapon, 100, 100), null);
            Check(PawnScorer.GetMinEquippedItemLevel(InventoryType.WeaponOffHand) == 100, "off-hand weapon query lost main-hand level");
        }));
        cases.Add(("both known-empty hands retain empty-slot admission", () => {
            var s = Reset(InventoryType.TwoHandWeapon, 10, 1); Install(null, null);
            Check(PawnScorer.IsSlotEmpty(InventoryType.TwoHandWeapon) && PawnScorer.GetMinEquippedScore(InventoryType.TwoHandWeapon, s.GetWeightsDictionary()) == 0 && PawnScorer.GetMinEquippedItemLevel(InventoryType.TwoHandWeapon) == 0, "known empty hands were rejected");
            Equip(); ExpectEquip(true);
        }));
        cases.Add(("missing off-hand metadata remains unknown not empty", () => {
            var s = Reset(InventoryType.TwoHandWeapon, 10, 1); Install(null, new WoWItem { ItemInfo = null! });
            Check(!PawnScorer.IsSlotEmpty(InventoryType.TwoHandWeapon) && float.IsNaN(PawnScorer.GetMinEquippedScore(InventoryType.TwoHandWeapon, s.GetWeightsDictionary())), "missing metadata gained empty permission");
            Equip(); ExpectEquip(false);
        }));
        cases.Add(("main-hand-only two-hander comparison retains level", () => {
            Reset(InventoryType.TwoHandWeapon, 10, 1); Install(Item(InventoryType.WeaponMainHand, 100, 100), null);
            Check(!PawnScorer.IsSlotEmpty(InventoryType.TwoHandWeapon) && PawnScorer.GetMinEquippedItemLevel(InventoryType.TwoHandWeapon) == 100, "ordinary main-hand comparison changed");
        }));
        foreach (var value in new[] { InventoryType.Weapon, InventoryType.WeaponMainHand })
        {
            var proposed = value;
            cases.Add(($"{proposed} does not consume an unrelated off-hand", () => {
                var s = Reset(InventoryType.TwoHandWeapon, 10, 1); Install(null, Item(InventoryType.Shield, 100, 100));
                Check(PawnScorer.IsSlotEmpty(proposed) && PawnScorer.GetMinEquippedScore(proposed, s.GetWeightsDictionary()) == 0 && PawnScorer.GetMinEquippedItemLevel(proposed) == 0, "one-hand target incorrectly inherited two-hand replacement semantics");
            }));
        }
        int pass = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); pass++; Console.WriteLine("PASS equipment hand: " + item.Name); }
            catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL equipment hand assertion: " + item.Name + ": " + e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR equipment hand fixture: " + item.Name + ": " + e); }
        }
        Console.WriteLine($"Equipment hand scenarios: {pass}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual scorer and equip consumer; controlled observations; not class proficiency, gear optimization or live equip.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Equipment hand regressions");
    }
    private static SmartLootRollerSettings Reset(InventoryType candidate, float score, int level)
    {
        var s = (SmartLootRollerSettings)Invoke(typeof(LootCases).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)!, null)!;
        s.ClearWeights(); s.Weight_WeaponDps = 1; s.Weight_Armor = 1; s.AutoEquipUpgrades = true;
        s.AllowedArmor = "Plate,Shield";
        var item = Item(candidate, score, level); ItemInfo.Current = item.ItemInfo; StyxWoW.Me.BagItems.Add(item);
        Check(PawnScorer.IsUsable(item.ItemInfo, s.AllowedArmor, s.AllowedWeapons), "candidate admission control was not established");
        return s;
    }
    private static WoWItem Item(InventoryType type, float score, int level)
    {
        bool armor = type == InventoryType.Shield || type == InventoryType.Holdable;
        return new WoWItem { ItemInfo = new ItemInfo { InventoryType = type, Level = level,
            ItemClass = armor ? WoWItemClass.Armor : WoWItemClass.Weapon,
            WeaponClass = type == InventoryType.TwoHandWeapon ? WoWItemWeaponClass.SwordTwoHand : WoWItemWeaponClass.Sword,
            ArmorClass = WoWItemArmorClass.Plate,
            Armor = armor ? score : 0, DPS = armor ? 0 : score } };
    }
    private static void SetCandidatePower(float value)
    {
        if (ItemInfo.Current.ItemClass == WoWItemClass.Armor) ItemInfo.Current.Armor = value;
        else ItemInfo.Current.DPS = value;
    }
    private static void Install(WoWItem? main, WoWItem? off)
    {
        var equipment = StyxWoW.Me.Inventory.Equipped;
        equipment.MainHand = main; equipment.OffHand = off; equipment.Items.Clear();
        if (main != null) equipment.Items.Add(main);
        if (off != null) equipment.Items.Add(off);
    }
    private static void Equip() => Invoke(typeof(SmartLootRoller.SmartLootRoller).GetMethod("AutoEquipUpgrades", BindingFlags.Instance | BindingFlags.NonPublic)!, new SmartLootRoller.SmartLootRoller());
    private static void ExpectEquip(bool expected) => Check(LootCases.Scripts.Contains("EQUIP") == expected, "unexpected auto-equip decision: " + string.Join(";", LootCases.Scripts));
    private static object? Invoke(MethodInfo method, object? instance)
    {
        try { return method.Invoke(instance, null); }
        catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    private static void Check(bool value, string why) { if (!value) throw new Failure(why); }
}
""";
}

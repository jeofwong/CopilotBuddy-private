// Controlled external observations and dispatch only. Common, Retribution,
// Throttle, attributes and TreeSharp below are linked, unchanged production code.
using Styx.Combat.CombatRoutine;
using Styx.Logic.Combat;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;

internal static class Fixture
{
    internal static readonly HashSet<string> Known = new(StringComparer.Ordinal);
    internal static readonly HashSet<string> Unavailable = new(StringComparer.Ordinal);
    internal static readonly List<(string Spell, ulong Target)> Attempts = new();
    internal static readonly List<Exception> Errors = new();
    internal static readonly Dictionary<string, WoWSpell> Metadata = new();
    internal static readonly List<string> LuaQueries = new();
    internal static Func<string, List<string>>? LuaResult;
    internal static int DefaultRestCalls;
    internal static RunStatus DefaultRestResult = RunStatus.Failure;
    internal static void Reset()
    {
        Known.Clear(); Unavailable.Clear(); Attempts.Clear(); Errors.Clear(); Metadata.Clear();
        LuaQueries.Clear(); LuaResult = null;
        DefaultRestCalls = 0; DefaultRestResult = RunStatus.Failure;
        Singular.Managers.TankManager.Instance.FirstUnit = null;
        Singular.Managers.TankManager.Instance.NeedToTaunt.Clear();
        Singular.Settings.SingularSettings.Instance.EnableTaunting = false;
        Styx.StyxWoW.Me = new LocalPlayer { Guid = 1, Class = WoWClass.Paladin, Name = "SelfPaladin" };
        Singular.Settings.SingularSettings.Instance.Paladin = new();
        Singular.Managers.TalentManager.CurrentSpec = Singular.Managers.TalentSpec.RetributionPaladin;
        Singular.Helpers.Unit.NearbyUnfriendlyUnits.Clear();
    }
    internal static WoWPlayer Add(WoWClass kind = WoWClass.Warrior, bool raid = false)
    {
        var me = Styx.StyxWoW.Me;
        var p = new WoWPlayer { Guid = (ulong)(me.PartyMembers.Count + me.RaidMembers.Count + 10), Class = kind,
            Name = "Member" + (me.PartyMembers.Count + me.RaidMembers.Count + 10),
            MaxMana = kind is WoWClass.Warrior or WoWClass.Rogue or WoWClass.DeathKnight ? 0 : 100 };
        if (raid) { me.IsInRaid = true; me.RaidMembers.Add(p); }
        else { me.IsInParty = true; me.PartyMembers.Add(p); }
        return p;
    }
    internal static void Aura(WoWUnit p, string name, ulong owner, int id = 1, WoWDispelType dispel = WoWDispelType.None)
        => p.ObservedAuras.Add(new WoWAura { Name = name, CreatorGuid = owner, SpellId = id,
            IsHarmful = dispel != WoWDispelType.None, Spell = new WoWSpell { DispelType = dispel } });
    internal static Composite Nothing() => new TreeSharp.Action(_ => RunStatus.Failure);
    internal static Composite Submit(string name, Func<object, WoWUnit?> select, Func<object, bool>? requires = null, bool buff = false)
        => new TreeSharp.Action(context =>
        {
            var target = select(context);
            if (target == null || (requires != null && !requires(context)) || !SpellManager.CanCast(name, target))
                return RunStatus.Failure;
            if (buff && target.HasMyAura(name)) return RunStatus.Failure;
            Attempts.Add((name, target.Guid));
            return RunStatus.Success;
        });
    internal static RunStatus Tick(Composite root)
    {
        root.Start(null!);
        RunStatus status;
        int ticks = 0;
        do
        {
            status = root.Tick(null!);
            if (++ticks > 200) throw new InvalidOperationException("Unexpected blocking/never-ending support behavior");
        } while (status == RunStatus.Running);
        root.Stop(null!);
        if (Errors.Count != 0) throw new InvalidOperationException("Swallowed exception: " + Errors[0]);
        return status;
    }
}
namespace Styx.Combat.CombatRoutine
{
    public enum WoWClass { None, Warrior, Paladin, Hunter, Rogue, Priest, DeathKnight, Shaman, Mage, Warlock, Druid }
}
namespace Styx.Logic.Combat
{
    public enum WoWSpellMechanic { Dazed, Disoriented, Frozen, Incapacitated, Rooted, Slowed, Snared }
    public sealed class SpellRecord { public int[]? Reagent = new int[8]; public uint[]? ReagentCount = new uint[8]; }
    public class WoWSpell { public WoWDispelType DispelType { get; set; } public SpellRecord InternalInfo { get; } = new(); }
    public class WoWAura
    {
        public string Name { get; set; } = "";
        public ulong CreatorGuid { get; set; }
        public int SpellId { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsHarmful { get; set; }
        public bool IsPassive { get; set; }
        public TimeSpan TimeLeft { get; set; } = TimeSpan.FromMinutes(10);
        public WoWSpell? Spell { get; set; } = new();
    }
    public static class SpellManager
    {
        public static Dictionary<string, WoWSpell> Spells => Fixture.Metadata;
        public static bool HasSpell(string name) => Fixture.Known.Contains(name);
        public static bool CanCast(string name, WoWUnit target, bool checkRange = true, bool checkMovement = false)
            => HasSpell(name) && !Fixture.Unavailable.Contains(name) && target.IsValid && target.IsAlive &&
                (target.IsMe || target.Distance < 40 && target.InLineOfSpellSight);
    }
}
namespace Styx.WoWInternals.WoWObjects
{
    public class MapState { public bool IsBattleground { get; set; } public bool IsInstance { get; set; } }
    public class WoWUnit
    {
        public MapState CurrentMap { get; } = new();
        public bool IsInInstance => CurrentMap.IsInstance;
        public ulong Guid { get; set; }
        public uint Entry { get; set; } = 1;
        public string Name { get; set; } = "";
        public bool IsValid { get; set; } = true;
        public bool IsAlive { get; set; } = true;
        public bool IsFriendly { get; set; } = true;
        public bool IsGhost { get; set; }
        public bool Dead => !IsAlive;
        public bool IsMe => ReferenceEquals(this, Styx.StyxWoW.Me);
        public bool Combat { get; set; }
        public bool Mounted { get; set; }
        public bool IsOnTransport { get; set; }
        public bool IsCasting { get; set; }
        public bool IsChanneling { get; set; }
        public bool IsMoving { get; set; }
        public bool IsPlayer { get; set; }
        public bool Elite { get; set; }
        public bool Fleeing { get; set; }
        public bool IsAutoAttacking { get; set; }
        public float Distance { get; set; } = 5;
        public float DistanceSqr => Distance * Distance;
        public bool InLineOfSpellSight { get; set; } = true;
        public bool IsWithinMeleeRange => Distance <= 5;
        public int MaxMana { get; set; } = 100;
        public double ManaPercent { get; set; } = 100;
        public double HealthPercent { get; set; } = 100;
        public int Level { get; set; } = 80;
        public WoWClass Class { get; set; }
        public WoWUnit? CurrentTarget { get; set; }
        public List<WoWAura> ObservedAuras { get; } = new();
        public Dictionary<string, WoWAura> Auras => ObservedAuras.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.Last());
        public Dictionary<string, WoWAura> ActiveAuras => Auras;
        public Dictionary<string, WoWAura> Debuffs => Auras.Where(a => a.Value.IsHarmful).ToDictionary(a => a.Key, a => a.Value);
        public IEnumerable<WoWAura> GetAllAuras() => ObservedAuras;
        public bool HasAura(string name) => ObservedAuras.Any(a => a.Name == name && a.IsActive);
        public bool HasMyAura(string name) => ObservedAuras.Any(a => a.Name == name && a.IsActive && a.CreatorGuid == Styx.StyxWoW.Me.Guid);
        public bool HasAuraWithMechanic(params WoWSpellMechanic[] _) => false;
        public bool IsBoss() => false;
        public bool IsUndeadOrDemon() => false;
    }
    public class WoWPlayer : WoWUnit
    {
        public bool IsInParty { get; set; }
        public bool IsInRaid { get; set; }
        public List<WoWPlayer> PartyMembers { get; } = new();
        public List<WoWPlayer> RaidMembers { get; } = new();
    }
    public sealed class LocalPlayer : WoWPlayer
    {
        public Dictionary<uint, int> ItemCounts { get; } = new();
        public int GetCarriedItemCount(uint id) => ItemCounts.TryGetValue(id, out int count) ? count : 0;
    }
}
namespace Styx { public static class StyxWoW { public static LocalPlayer Me { get; set; } = new(); } }
namespace Styx.WoWInternals
{
    public static class Lua
    {
        public static List<string> GetReturnValues(string code)
        {
            Fixture.LuaQueries.Add(code);
            return Fixture.LuaResult?.Invoke(code) ?? new List<string>();
        }
        public static string Escape(string value) => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
namespace Styx.Helpers { public static class Logging { public static void WriteException(Exception ex) => Fixture.Errors.Add(ex); } }
namespace Singular.Managers
{
    public enum TalentSpec { RetributionPaladin, HolyPaladin, ProtectionPaladin, Lowbie }
    public static class TalentManager { public static TalentSpec CurrentSpec { get; set; } }
    public static class HealerManager { public static bool NeedHealTargeting { get; set; } }
    public sealed class TankManager
    {
        public static TankManager Instance { get; } = new();
        public WoWUnit? FirstUnit { get; set; }
        public List<WoWUnit> NeedToTaunt { get; } = new();
    }
}
namespace Singular.Dynamics
{
    public enum BehaviorType { Heal, Rest, Pull, Combat, PreCombatBuffs, CombatBuffs, PullBuffs }
    public enum WoWContext { All, Normal, Battlegrounds, Instances }
}
namespace Singular.Settings
{
    internal class PaladinSettings
    {
        public Singular.ClassSpecific.Paladin.PaladinSeal Seal { get; set; }
        public Singular.ClassSpecific.Paladin.PaladinAura Aura { get; set; }
        public Singular.ClassSpecific.Paladin.PaladinBlessings Blessings { get; set; }
        public bool UseGreaterBlessings { get; set; }
        public bool UsePallyPowerAssignments { get; set; }
        public bool DispelDebuffs { get; set; } = true;
        public bool DispelParty { get; set; } = true;
        public int LayOnHandsHealth => 15;
        public int HolyLightHealth { get; set; } = 30;
        public int FlashOfLightHealth => 50;
        public int DivineProtectionHealthRet => 20;
        public int DivineProtectionHealthProt => 20;
        public int ProtConsecrationCount => 3;
        public bool AvengersPullOnly { get; set; }
        public int ConsecrationCount => 3;
        public int DivinePleaMana => 30;
        public int RetributionHealHealth => 30;
    }
    internal class SingularSettings
    {
        public static SingularSettings Instance { get; } = new();
        public PaladinSettings Paladin { get; set; } = new();
        public bool EnableTaunting { get; set; }
    }
}
namespace Singular.Helpers
{
    public static class Unit { public static List<WoWUnit> NearbyUnfriendlyUnits { get; } = new(); public static IEnumerable<WoWUnit> UnfriendlyUnitsNearTarget(float range) => NearbyUnfriendlyUnits; public static bool IsAreaEffectSafe(string name, WoWUnit target) => true; }
    public static class Safers { public static Composite EnsureTarget() => Fixture.Nothing(); }
    public static class Common
    {
        public static Composite CreateAutoAttack(bool _) => Fixture.Nothing();
        public static Composite CreateInterruptSpellCast(Func<object, WoWUnit?> _) => Fixture.Nothing();
    }
    public static class Movement
    {
        public static Composite CreateMoveToLosBehavior() => Fixture.Nothing();
        public static Composite CreateFaceTargetBehavior() => Fixture.Nothing();
        public static Composite CreateMoveToMeleeBehavior(bool _) => Fixture.Nothing();
        public static Composite CreateMoveToTargetBehavior(bool _, float range) => Fixture.Nothing();
    }
    public static class Rest
    {
        public static Composite CreateDefaultRestBehaviour() => new TreeSharp.Action(_ =>
        { Fixture.DefaultRestCalls++; return Fixture.DefaultRestResult; });
    }
    public static class Spell
    {
        public const float MeleeRange = 5;
        public static Composite WaitForCast(bool _ = true, bool __ = true) => Fixture.Nothing();
        public static Composite Resurrect(string _) => Fixture.Nothing();
        public static Composite Cast(string name, Func<object, bool>? requires = null) => Fixture.Submit(name, _ => Styx.StyxWoW.Me.CurrentTarget, requires);
        public static Composite Cast(string name, Func<object, WoWUnit?> select, Func<object, bool> requires) => Fixture.Submit(name, select, requires);
        public static Composite BuffSelf(string name, Func<object, bool>? requires = null) => Fixture.Submit(name, _ => Styx.StyxWoW.Me, requires, true);
        public static Composite Heal(string name, Func<object, WoWUnit?> select, Func<object, bool> requires) => Fixture.Submit(name, select, requires);
    }
}

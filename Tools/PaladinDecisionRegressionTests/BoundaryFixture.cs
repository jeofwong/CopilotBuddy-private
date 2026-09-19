// Only world observation, native spell dispatch and unrelated movement/target
// helpers are controlled here. Retribution, attributes, Throttle and TreeSharp
// are linked production source, not a rewritten rotation or behavior tree.
using TreeSharp;

internal static class Fixture
{
    internal static readonly HashSet<string> Known = new();
    internal static readonly HashSet<string> Ready = new();
    internal static readonly List<string> Trace = new();
    internal static readonly List<Exception> Exceptions = new();
    internal static bool AreaSafe = true;
    internal static bool ApplyEffects;
    internal static System.Action? BeforeDispatch;
    internal static string? Selected;
    internal static void Reset(int count = 1, int level = 80)
    {
        Known.Clear(); Ready.Clear(); Trace.Clear(); Exceptions.Clear();
        AreaSafe = true; Selected = null; ApplyEffects = false; BeforeDispatch = null;
        Singular.Settings.SingularSettings.Instance.Paladin.Reset();
        Singular.Managers.TalentManager.CurrentSpec = Singular.Managers.TalentSpec.RetributionPaladin;
        Styx.StyxWoW.Me = new Styx.Player { Level = level, CurrentTarget = new Styx.UnitState() };
        Singular.Helpers.Unit.NearbyUnfriendlyUnits.Clear();
        for (int i = 0; i < count; i++) Singular.Helpers.Unit.NearbyUnfriendlyUnits.Add(new Styx.UnitState());
    }
    internal static Composite Nothing() => new TreeSharp.Action(_ => RunStatus.Failure);
    internal static Composite Attempt(string spell, Func<object, Styx.UnitState?> select, Func<object, bool>? requires) =>
        new TreeSharp.Action(context =>
        {
            var target = select(context);
            string? reason = target == null ? "no-target" : requires != null && !requires(context) ? "requirements"
                : !Known.Contains(spell) ? "unknown" : !Ready.Contains(spell) ? "unavailable"
                : (spell is "Crusader Strike" or "Divine Storm") && target.Distance > 8 ? "range"
                : spell == "Divine Storm" && !AreaSafe ? "area-safety" : null;
            Trace.Add(spell + ":" + (reason ?? "selected"));
            if (reason != null) return RunStatus.Failure;
            var callback = BeforeDispatch; BeforeDispatch = null; callback?.Invoke();
            target = select(context); // Real Spell.Cast resolves the selector again after setup.
            if (target == null) { Trace.Add(spell + ":late-no-target"); return RunStatus.Failure; }
            Selected = spell;
            if (ApplyEffects && spell.StartsWith("Seal of "))
            {
                foreach (var key in Styx.StyxWoW.Me.Auras.Keys.Where(k => k.StartsWith("Seal of ")).ToArray()) Styx.StyxWoW.Me.Auras.Remove(key);
                Styx.StyxWoW.Me.Auras[spell] = new Styx.Aura { Name = spell, CreatorGuid = Styx.StyxWoW.Me.Guid };
            }
            return RunStatus.Success;
        });
}
namespace Styx
{
    public class Aura { public string Name { get; set; } = ""; public ulong CreatorGuid { get; set; } public bool IsActive { get; set; } = true; public TimeSpan TimeLeft { get; set; } = TimeSpan.FromSeconds(20); }
    public partial class UnitState
    {
        public uint Entry { get; set; } = 1;
        public double HealthPercent { get; set; } = 100;
        public float Distance { get; set; } = 3;
        public ulong Guid { get; set; } = 1;
        public bool UndeadOrDemon { get; set; }
        public bool Boss { get; set; }
        public bool IsValid { get; set; } = true;
        public bool IsAlive { get; set; } = true;
        public bool IsPlayer { get; set; }
        public bool Elite { get; set; }
        public bool IsMoving { get; set; }
        public bool Fleeing { get; set; }
        public bool Combat { get; set; }
        public IEnumerable<Aura> GetAllAuras() => Auras.Values;
        public bool HasMyAura(string name) => Auras.TryGetValue(name, out var a) && a.CreatorGuid == StyxWoW.Me.Guid;
        public bool IsWithinMeleeRange => Distance <= 5;
        public readonly Dictionary<string, Aura> Auras = new();
        public bool HasAura(string name) => Auras.ContainsKey(name);
        public bool IsUndeadOrDemon() => UndeadOrDemon;
        public bool IsBoss() => Boss;
    }
    public sealed partial class Player : UnitState
    {
        public UnitState? CurrentTarget { get; set; }
        public int Level { get; set; }
        public double ManaPercent { get; set; } = 100;
        public bool IsAutoAttacking { get; set; } = true;
        public bool IsInParty { get; set; }
        public bool IsInRaid { get; set; }
        public bool Mounted { get; set; }
        public bool IsOnTransport { get; set; }
        public bool IsCasting { get; set; }
        public bool IsChanneling { get; set; }
        public Dictionary<string, Aura> ActiveAuras => Auras;
        public bool HasAuraWithMechanic(params Logic.Combat.WoWSpellMechanic[] _) => false;
    }
    public static partial class StyxWoW { public static Player Me { get; set; } = new(); }
}
namespace Styx.Helpers
{
    public static partial class Logging { public static void WriteException(Exception error) => Fixture.Exceptions.Add(error); }
}
namespace Styx.Combat.CombatRoutine { public enum WoWClass { Paladin } }
namespace Styx.Logic.Combat
{
    public enum WoWSpellMechanic { Dazed, Disoriented, Frozen, Incapacitated, Rooted, Slowed, Snared }
    public static partial class SpellManager { public static bool HasSpell(string name) => Fixture.Known.Contains(name);
        public static bool CanCast(string name, Styx.UnitState? target, bool range = true, bool movement = false) => target != null && HasSpell(name) && Fixture.Ready.Contains(name); }
}
namespace Singular.Managers
{
    public enum TalentSpec { RetributionPaladin, ProtectionPaladin, HolyPaladin }
    public static partial class TalentManager { public static TalentSpec CurrentSpec { get; set; } = TalentSpec.RetributionPaladin; }
    public static class HealerManager { public static bool NeedHealTargeting { get; set; } }
}
namespace Singular.Dynamics
{
    public enum BehaviorType { Heal, Rest, Pull, Combat }
    public enum WoWContext { All, Normal, Battlegrounds, Instances }
}
namespace Singular
{
    public static class SingularRoutine
    {
        public static Singular.Dynamics.WoWContext CurrentWoWContext { get; set; } = Singular.Dynamics.WoWContext.Normal;
    }
}
namespace Singular.Settings
{
    public sealed class PaladinSettings
    {
        public int LayOnHandsHealth => 15;
        public int HolyLightHealth { get; set; } = 30;
        public int FlashOfLightHealth => 50;
        public int DivineProtectionHealthRet => 20;
        public int ConsecrationCount => 3;
        public int DivinePleaMana => 30;
        public int RetributionHealHealth { get; set; } = 30;
        public Singular.ClassSpecific.Paladin.PaladinSeal Seal { get; set; }
        public void Reset() { Seal = 0; HolyLightHealth = 30; RetributionHealHealth = 30; }
    }
    public sealed class SingularSettings
    {
        public static SingularSettings Instance { get; } = new();
        public PaladinSettings Paladin { get; } = new();
    }
}
namespace Singular.Helpers
{
    public static class Unit { public static List<Styx.UnitState> NearbyUnfriendlyUnits { get; } = new();
        public static bool IsAreaEffectSafe(string name, Styx.UnitState target) => Fixture.AreaSafe; }
    public static class Safers { public static Composite EnsureTarget() => Fixture.Nothing(); }
    // The real shared Helpers.Common is linked by the project, not mocked.
    public static class Movement
    {
        public static Composite CreateMoveToLosBehavior() => Fixture.Nothing();
        public static Composite CreateFaceTargetBehavior() => Fixture.Nothing();
        public static Composite CreateMoveToMeleeBehavior(bool _) => new TreeSharp.Action(context =>
        { Fixture.Selected = "movement"; return RunStatus.Success; });
    }
    public static class Rest { public static Composite CreateDefaultRestBehaviour() => Fixture.Nothing(); }
    public static partial class Spell
    {
        public const float MeleeRange = 5;
        public static Composite WaitForCast(bool _ = true, bool __ = true) => Fixture.Nothing();
        public static Composite Resurrect(string _) => Fixture.Nothing();
        public static Composite Cast(string name, Func<object, bool>? requires = null) =>
            Fixture.Attempt(name, _ => Styx.StyxWoW.Me.CurrentTarget, requires);
        public static Composite Cast(string name, Func<object, Styx.UnitState?> select, Func<object, bool> requires) =>
            Fixture.Attempt(name, select, requires);
        public static Composite BuffSelf(string name, Func<object, bool>? requires = null) =>
            Fixture.Attempt(name, _ => Styx.StyxWoW.Me, c => !Styx.StyxWoW.Me.HasAura(name) && (requires == null || requires(c)));
        public static Composite Heal(string name, Func<object, Styx.UnitState?> select, Func<object, bool> requires) =>
            Fixture.Attempt(name, select, requires);
    }
}

// The support suite links the actual Common owner; rotation-only tests isolate it.
namespace Singular.ClassSpecific.Paladin
{
    public enum PaladinSeal { Auto, Command, Corruption, Justice, Light, Righteousness, Vengeance, Wisdom }
    public static class Common
    {
        public static Composite CreatePaladinDispelBehavior() => Fixture.Nothing();
    }
}

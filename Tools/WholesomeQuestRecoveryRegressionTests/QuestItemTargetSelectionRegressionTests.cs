using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Compile the complete tracked UseItemOn script with real TreeSharp and the
// host's CustomForcedBehavior. Only external world/item observations are
// controlled. These tests do not invent quest recipes or execute a native use.
internal static class QuestItemTargetSelectionRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");
        string temp = Path.Combine(Path.GetTempPath(), "cb-quest-item-selection-" + Guid.NewGuid().ToString("N"));
        bool oldLogging = Styx.Helpers.Logging.FileLogging;
        Directory.CreateDirectory(temp);
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            File.Copy(Path.Combine(root, "runtime-snapshot", "Quest Behaviors", "UseItemOn.cs"), Path.Combine(temp, "UseItemOn.cs"));
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), Boundary);
            Type type = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(type, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference", flags)!.Invoke(compiler, new object[] { path });
            var results = (CompilerResults)type.GetMethod("Compile", flags)!.Invoke(compiler, null)!;
            var errors = results.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Actual UseItemOn compile failed: " + string.Join(";", errors.Select(e => e.ToString())));
            var assembly = (Assembly)type.GetProperty("CompiledAssembly", flags)!.GetValue(compiler)!;
            try { assembly.GetType("QuestItemSelectionCases", true)!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = oldLogging; Directory.Delete(temp, true); }
    }

    private const string Boundary = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Styx;
using Styx.Logic.Pathing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Script = Styx.Bot.Quest_Behaviors.UseItemOn.UseItemOn;

public static class QuestItemSelectionCases
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Failure(string message) : Exception(message) { }
    private static Script owner = null!;
    private static WoWUnit candidate = null!;
    public static void Run()
    {
        var cases = new List<(string, System.Action)>();
        for (int configured = 0; configured < 4; configured++)
            for (int observed = 0; observed < 4; observed++)
            {
                int c = configured, o = observed;
                cases.Add(($"aura conjunction configured={c}, observed={o}", () => {
                    Reset();
                    if ((c & 1) != 0) Set("MobAuraName", "Required");
                    if ((c & 2) != 0) Set("MobAuraMissingName", "Forbidden");
                    if ((o & 1) != 0) candidate.Auras.Add("Required");
                    if ((o & 2) != 0) candidate.Auras.Add("Forbidden");
                    bool allowed = ((c & 1) == 0 || (o & 1) != 0) && ((c & 2) == 0 || (o & 2) == 0);
                    Expect(allowed ? candidate : null);
                }));
            }
        void Add(string name, System.Action body) => cases.Add((name, () => { Reset(); body(); }));
        Add("qualified farther target is not masked by a forbidden nearer target", () => {
            Set("MobAuraName", "Required"); Set("MobAuraMissingName", "Forbidden");
            candidate.Auras.UnionWith(new[] { "Required", "Forbidden" });
            var next = new WoWUnit { Entry = candidate.Entry, Guid = 3, Location = new WoWPoint(15, 10, 10) };
            next.Auras.Add("Required"); ObjectManager.Objects!.Add(next); Expect(next);
        });
        Add("invalid nearer NPC cannot hide a valid NPC", () => {
            candidate.IsValid = false; var next = new WoWUnit { Entry = candidate.Entry, Guid = 3, Location = new WoWPoint(15,10,10) };
            ObjectManager.Objects!.Add(next); Expect(next);
        });
        Add("invalid game object is not an item target", () => {
            Set("MobType", Script.ObjectType.GameObject); ObjectManager.Objects!.Clear();
            ObjectManager.Objects.Add(new WoWGameObject { Entry=70001, Guid=2, IsValid=false, Location=new WoWPoint(12,10,10) }); Expect(null);
        });
        Add("valid game object retains independent target namespace", () => {
            Set("MobType", Script.ObjectType.GameObject);
            var obj=new WoWGameObject { Entry=70001, Guid=3, Location=new WoWPoint(13,10,10) };ObjectManager.Objects!.Add(obj);Expect(obj);
        });
        Add("NPC mode never borrows a same-entry game object", () => {
            ObjectManager.Objects!.Clear();ObjectManager.Objects.Add(new WoWGameObject { Entry=70001,Guid=3,Location=new WoWPoint(12,10,10) });Expect(null);
        });
        Add("wrong entry is not selected", () => { candidate.Entry=70002;Expect(null); });
        Add("unknown target GUID is not actionable", () => { candidate.Guid=0;Expect(null); });
        Add("same-distance ordinary candidate remains usable", () => { Expect(candidate); });
        Add("dead mode retains valid corpse", () => { Set("NpcState", Script.NpcStateType.Dead);candidate.IsAlive=false;Expect(candidate); });
        Add("dead mode excludes a living unit", () => { Set("NpcState", Script.NpcStateType.Dead);Expect(null); });
        Add("alive mode excludes a corpse", () => { Set("NpcState", Script.NpcStateType.Alive);candidate.IsAlive=false;Expect(null); });
        Add("DontCare remains valid for a corpse", () => { candidate.IsAlive=false;Expect(candidate); });
        Add("BelowHp preserves strict threshold", () => { Set("NpcState",Script.NpcStateType.BelowHp);Set("MobHpPercentLeft",30d);candidate.HealthPercent=30;Expect(null); });
        Add("BelowHp admits a living weakened target", () => { Set("NpcState",Script.NpcStateType.BelowHp);Set("MobHpPercentLeft",30d);candidate.HealthPercent=29;Expect(candidate); });
        Add("BelowHp excludes a corpse", () => { Set("NpcState",Script.NpcStateType.BelowHp);Set("MobHpPercentLeft",30d);candidate.HealthPercent=0;candidate.IsAlive=false;Expect(null); });
        Add("collection radius remains strict", () => { candidate.Location=new WoWPoint(110,10,10);Expect(null); });
        Add("vertical distance is not discarded", () => { candidate.Location=new WoWPoint(10,10,111);Expect(null); });
        Add("behaviour-local blacklist retains its exclusion", () => { ((List<ulong>)typeof(Script).GetField("_npcBlacklist",Hidden)!.GetValue(owner)!).Add(candidate.Guid);Expect(null); });
        Add("missing player does not produce target authority", () => { ObjectManager.Me=null;Expect(null); });
        Add("dead player does not produce target authority", () => { ObjectManager.Me!.IsAlive=false;Expect(null); });
        Add("invalid player does not produce target authority", () => { ObjectManager.Me!.IsValid=false;Expect(null); });
        Add("null observed object collection is unknown", () => { ObjectManager.Objects=null;Expect(null); });
        Add("empty observed object collection remains empty", () => { ObjectManager.Objects!.Clear();Expect(null); });
        Add("missing item actor is safe to inspect", () => { ObjectManager.Me=null;ExpectItem(null); });
        Add("missing carried-items observation is safe to inspect", () => { ObjectManager.Me!.CarriedItems=null;ExpectItem(null); });
        Add("null inventory member does not hide matching item", () => {
            var item = new WoWItem { Entry=12345,Guid=17 };ObjectManager.Me!.CarriedItems=new List<WoWItem>{null!,item};ExpectItem(item);
        });
        Add("invalid matching item is not usable inventory", () => {
            ObjectManager.Me!.CarriedItems!.Add(new WoWItem { Entry=12345,Guid=17,IsValid=false });ExpectItem(null);
        });
        Add("valid matching carried item is retained", () => {
            var item=new WoWItem {Entry=12345,Guid=17};ObjectManager.Me!.CarriedItems!.Add(item);ExpectItem(item);
        });
        int passed=0,assertions=0,unexpected=0;
        foreach(var item in cases)
        {
            try { item.Item2();passed++;Console.WriteLine("PASS quest item selection: "+item.Item1); }
            catch(Failure e) { assertions++;Console.Error.WriteLine("FAIL quest item selection assertion: "+item.Item1+": "+e.Message); }
            catch(Exception e) { unexpected++;Console.Error.WriteLine("ERROR quest item selection fixture: "+item.Item1+": "+e); }
        }
        Console.WriteLine($"Quest item selection scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete tracked UseItemOn; controlled observations; no native use or automatic recipe mapping.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Quest item selection regressions");
    }
    private static void Reset()
    {
        ObjectManager.Me=new LocalPlayer { Guid=1,Location=new WoWPoint(10,10,10) };ObjectManager.Objects=new();
        candidate=new WoWUnit {Guid=2,Entry=70001,Location=new WoWPoint(12,10,10)};ObjectManager.Objects.Add(candidate);
        owner=(Script)RuntimeHelpers.GetUninitializedObject(typeof(Script));
        typeof(Script).GetField("_isDisposed",Hidden)!.SetValue(owner,true);
        typeof(Script).GetField("_npcBlacklist",Hidden)!.SetValue(owner,new List<ulong>());
        typeof(Script).GetField("_npcAuraWait",Hidden)!.SetValue(owner,new List<ulong>());
        Set("MobIds",new[]{70001});Set("ItemId",12345);Set("CollectionDistance",100d);Set("Range",4d);
        Set("MobType",Script.ObjectType.Npc);Set("NpcState",Script.NpcStateType.DontCare);Set("MobHpPercentLeft",100d);
        Set("Location",new WoWPoint(10,10,10));Set("NumOfTimes",1);Set("WaitTime",100);
    }
    private static void Set(string name,object value)=>typeof(Script).GetProperty(name,Hidden)!.SetValue(owner,value);
    private static object? Read(string property)
    {
        try { return typeof(Script).GetProperty(property,Hidden)!.GetValue(owner); }
        catch(TargetInvocationException e)when(e.InnerException is NullReferenceException or ArgumentNullException)
        { throw new Failure(property+" threw instead of safely rejecting the explicit missing observation: "+e.InnerException.GetType().Name); }
        catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }
    private static void Expect(WoWObject? expected)
    { if(!ReferenceEquals(Read("CurrentObject"),expected))throw new Failure("selected target differs from the fully qualified candidate"); }
    private static void ExpectItem(WoWItem? expected)
    { if(!ReferenceEquals(Read("Item"),expected))throw new Failure("item lookup differs from the valid carried item"); }
}
/* Embedded controlled observation namespace, not the outer initializer. */ namespace Styx
{
    public static class StyxWoW { public static WoWInternals.WoWObjects.LocalPlayer? Me=>WoWInternals.ObjectManager.Me;public static void SleepForLagDuration(){} }
}
/* Embedded controlled observation namespace, not the outer initializer. */ namespace Styx.WoWInternals.WoWObjects
{
    public class WoWObject
    {
        public ulong Guid{get;set;} public uint Entry{get;set;} public bool IsValid{get;set;}=true;
        public WoWPoint Location{get;set;}=new WoWPoint(10,10,10);public string Name=>"Controlled";
        public float Distance=>ObjectManager.Me==null?0:Location.Distance(ObjectManager.Me.Location);
        public float DistanceSqr=>Distance*Distance;
        public bool InLineOfSight{get;set;}=true;
    }
    public class WoWUnit:WoWObject
    {
        public bool IsAlive{get;set;}=true;public bool Dead=>!IsAlive;public bool IsPlayer{get;set;}
        public double HealthPercent{get;set;}=100;public HashSet<string> Auras{get;}=new();
        public bool HasAura(string name)=>Auras.Contains(name);public void Target(){ObjectManager.Me!.CurrentTarget=this;}
    }
    public class WoWPlayer:WoWUnit{}
    public class WoWGameObject:WoWObject{}
    public class WoWItem:WoWObject { public float Cooldown{get;set;} public bool TryUseContainerItem()=>true; public void UseContainerItem(){TryUseContainerItem();} }
    public class LocalPlayer:WoWPlayer
    {
        public bool IsMoving{get;set;}public WoWUnit? CurrentTarget{get;set;}
        public List<WoWItem>? CarriedItems{get;set;}=new();public List<WoWPlayer> PartyMembers{get;}=new();
        public FakeQuestLog QuestLog{get;}=new();public void ClearTarget(){CurrentTarget=null;}
    }
    public class FakeQuestLog { public Styx.Logic.Questing.PlayerQuest? GetQuestById(uint id)=>null; }
}
/* Embedded controlled observation namespace, not the outer initializer. */ namespace Styx.WoWInternals
{
    public static class ObjectManager
    {
        public static LocalPlayer? Me{get;set;}
        public static List<WoWObject>? Objects{get;set;}=new();
        public static IEnumerable<T>? GetObjectsOfType<T>(bool a=false,bool b=false)where T:WoWObject=>Objects?.OfType<T>();
    }
    public static class WoWMovement { public static void MoveStop(){} public static void ClickToMove(WoWPoint p){}public static void Face(ulong id){} }
}
/* Embedded controlled observation namespace, not the outer initializer. */ namespace Styx.Logic.Combat
{
    public class WoWSpell { public string Name=>"ControlledAura";public static WoWSpell FromId(int id)=>new(); }
}
/* Embedded controlled observation namespace, not the outer initializer. */ namespace Styx.Logic
{
    public static class Targeting { public static bool IsTooNearBlackspot(object ignored,WoWPoint point)=>false; }
}
/* Embedded controlled observation namespace, not the outer initializer. */ namespace Styx.Logic.Pathing
{
    public static class Navigator { public static bool CanNavigateFully(WoWPoint a,WoWPoint b)=>true;public static MoveResult MoveTo(WoWPoint p)=>MoveResult.Moved; }
}
/* Embedded controlled observation namespace, not the outer initializer. */ namespace Styx.Logic.Profiles
{
    public static class ProfileManager { public static Profile CurrentProfile{get;}=new(); }
    public class Profile { public object Blackspots{get;}=new(); }
}
/* Embedded controlled observation namespace, not the outer initializer. */ namespace Styx.Logic.BehaviorTree
{
    public static class TreeRoot { public static string GoalText{get;set;}="";public static string StatusText{get;set;}="";public static ControlledBot? Current{get;set;} }
    public class ControlledBot { public Composite Root{get;set;}=new PrioritySelector(); }
}
""";
}

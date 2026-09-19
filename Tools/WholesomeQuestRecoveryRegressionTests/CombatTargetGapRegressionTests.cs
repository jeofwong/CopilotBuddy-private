using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Execute the exact contiguous LevelBot combat region with real TreeSharp and
// tracked POI decorator. Routine leaves/world observations are controlled. This
// isolates the subtree GatherBuddy calls; it does not simulate a native stun.
internal static class CombatTargetGapRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        string? root=null;
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj"))){root=d.FullName;break;}
        if(root==null)throw new InvalidOperationException("Tracked checkout required");
        string source=File.ReadAllText(Path.Combine(root,"Bots","Grind","LevelBot.cs"));
        const string begin="        public static Composite CreateCombatBehavior()";
        int start=source.IndexOf(begin,StringComparison.Ordinal);
        int end=start<0?-1:source.IndexOf("        #endregion",start,StringComparison.Ordinal);
        if(start<0||end<0||source.IndexOf(begin,start+begin.Length,StringComparison.Ordinal)>=0)
            throw new InvalidOperationException("Combat extraction boundary changed");
        string region=source.Substring(start,end-start);
        if(!region.Contains("private static bool CanPull()"))throw new InvalidOperationException("Missing original pull control");
        string temp=Path.Combine(Path.GetTempPath(),"cb-combat-gap-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);bool logging=Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging=false;
            File.WriteAllText(Path.Combine(temp,"Combat.cs"),"using System;using TreeSharp;using Styx;using Styx.Logic;using Styx.Logic.POI;using Styx.Logic.Pathing;using Styx.WoWInternals;using Styx.WoWInternals.WoWObjects;using CommonBehaviors.Actions;using CommonBehaviors.Decorators;using Levelbot.Actions.Combat;using Mount=Styx.Logic.Pathing.Mount;namespace Bots.Grind{public static class LevelBot{private static RoutineSet Routine=>GapCases.Routine;\n"+region+"\n}}");
            File.WriteAllText(Path.Combine(temp,"IsolationBoundary.cs"),"using TreeSharp;namespace Levelbot.Actions.Combat{public static class PullIsolationCoordinator{public static Composite CreatePreCombatBehavior()=>new TreeSharp.Action(_=>RunStatus.Failure);public static Composite CreateRetreatBehavior()=>new TreeSharp.Action(_=>RunStatus.Failure);}}");
            File.Copy(Path.Combine(root,"CommonBehaviors","Decorators","DecoratorIsPoiType.cs"),Path.Combine(temp,"DecoratorIsPoiType.cs"));
            File.WriteAllText(Path.Combine(temp,"Boundary.cs"),Boundary);
            Type type=typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler",true)!;
            object compiler=Activator.CreateInstance(type,new object[]{temp})!;
            foreach(string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference",flags)!.Invoke(compiler,new object[]{path});
            var result=(CompilerResults)type.GetMethod("Compile",flags)!.Invoke(compiler,null)!;
            var errors=result.Errors.Cast<CompilerError>().Where(e=>!e.IsWarning).ToArray();
            if(errors.Length!=0)throw new InvalidOperationException("Actual combat region failed compilation: "+string.Join(";",errors.Select(e=>e.ToString())));
            var assembly=(Assembly)type.GetProperty("CompiledAssembly",flags)!.GetValue(compiler)!;
            try{assembly.GetType("GapCases",true)!.GetMethod("Run")!.Invoke(null,null);}
            catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        finally{Styx.Helpers.Logging.FileLogging=logging;Directory.Delete(temp,true);}
    }
    private const string Boundary="""
using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Logic;
using Styx.Logic.POI;
using Styx.Logic.Pathing;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Mount=Styx.Logic.Pathing.Mount;
public sealed class RoutineSet
{
    public Composite RestBehavior=GapCases.Leaf("rest");
    public Composite PreCombatBuffBehavior=GapCases.Leaf("prebuff");
    public Composite PullBehavior=GapCases.Leaf("pull");
    public Composite HealBehavior=GapCases.Leaf("heal");
    public Composite CombatBuffBehavior=GapCases.Leaf("combatbuff");
    public Composite CombatBehavior=GapCases.Leaf("combat");
}
public static class GapCases
{
    private sealed class Failure(string text):Exception(text){}
    internal static RoutineSet Routine=null!;
    internal static readonly List<string> Trace=new();
    internal static readonly Dictionary<string,RunStatus> Results=new();
    internal static int Fallback,Targets,Dismounts;
    private static LocalPlayer Me=>StyxWoW.Me;
    internal static Composite Leaf(string name)=>new TreeSharp.Action(_=>{Trace.Add(name);return Results.TryGetValue(name,out var r)?r:RunStatus.Failure;});
    public static void Run()
    {
        var cases=new List<(string,System.Action)>();
        void Add(string name,System.Action body)=>cases.Add((name,()=>{Reset();body();}));
        foreach(bool petOnly in new[]{false,true})
        {
            bool pet=petOnly;string label=pet?"pet-only combat":"player combat";
            void Fighting(){Me.Combat=!pet;Me.Pet=new WoWUnit{Combat=pet,IsAlive=true};}
            foreach(bool restSucceeds in new[]{false,true})
            {
                bool rest=restSucceeds;
                Add(label+": target gap cannot enter "+(rest?"successful":"failed")+" rest or patrol",()=>{
                    Fighting();Results["rest"]=rest?RunStatus.Success:RunStatus.Failure;Tick(Build());
                    Check(Fallback==0&&!Trace.Contains("rest")&&!Trace.Contains("prebuff"),"combat released to rest/prebuff/patrol");
                    Check(Trace.Contains("heal")&&!Trace.Contains("combat")&&!Trace.Contains("combatbuff"),"targetless branch lost healing or invoked target-dependent leaves");
                });
            }
            Add(label+": a self-heal remains available without a selected enemy",()=>{
                Fighting();Results["heal"]=RunStatus.Success;Tick(Build());
                Check(Trace.SequenceEqual(new[]{"heal"})&&Fallback==0,"target gap suppressed a valid self-heal");
            });
            Add(label+": restored selected enemy resumes normal routine",()=>{
                Fighting();Targeting.Instance.TargetList.Add(new WoWUnit());Results["combat"]=RunStatus.Success;Tick(Build());
                Check(Trace.SequenceEqual(new[]{"heal","combatbuff","combat"})&&Fallback==0,"valid target rotation changed");
            });
            Add(label+": failed offensive leaves still retain combat",()=>{
                Fighting();Targeting.Instance.TargetList.Add(new WoWUnit());Tick(Build());
                Check(Fallback==0&&Trace.SequenceEqual(new[]{"heal","combatbuff","combat"}),"routine failure authorized gathering");
            });
            Add(label+": friendly buff priority remains ahead of offensive routine",()=>{
                Fighting();Targeting.Instance.TargetList.Add(new WoWUnit());Results["combatbuff"]=RunStatus.Success;Tick(Build());
                Check(Trace.SequenceEqual(new[]{"heal","combatbuff"})&&Fallback==0,"combat-buff priority changed");
            });
            Add(label+": target recovery after one empty observation does not leave combat",()=>{
                Fighting();var tree=Build();Tick(tree);Check(Fallback==0,"first empty observation reached patrol");
                Trace.Clear();Targeting.Instance.TargetList.Add(new WoWUnit());Tick(tree);
                Check(Trace.Contains("combat")&&Fallback==0,"restored target failed to resume");
            });
            Add(label+": ending combat releases normal rest and gathering",()=>{
                Fighting();var tree=Build();Tick(tree);Check(Fallback==0,"combat gap reached patrol");
                Me.Combat=false;Me.Pet!.Combat=false;Trace.Clear();Tick(tree);
                Check(Fallback==1&&Trace.SequenceEqual(new[]{"rest","prebuff"}),"ended combat remained permanently owned");
            });
            Add(label+": displayed target cleared on current stunned enemy does not authorize patrol",()=>{
                Fighting();Me.CurrentTarget=new WoWUnit{Combat=true,CurrentTarget=null};Tick(Build());
                Check(Fallback==0&&Targets==0,"displayed target loss triggered patrol or forced replacement");
            });
        }
        Add("idle player and idle pet retain normal rest",()=>{Me.Pet=new WoWUnit();Results["rest"]=RunStatus.Success;Tick(Build());Check(Trace.SequenceEqual(new[]{"rest"})&&Fallback==0,"idle rest was suppressed");});
        Add("dead pet combat flag does not block routine rest",()=>{Me.Pet=new WoWUnit{Combat=true,IsAlive=false};Results["rest"]=RunStatus.Success;Tick(Build());Check(Trace.SequenceEqual(new[]{"rest"}),"dead pet retained combat");});
        Add("no combat and no work retains gathering fallback",()=>{Tick(Build());Check(Fallback==1&&Trace.SequenceEqual(new[]{"rest","prebuff"}),"ordinary idle fallback changed");});
        Add("existing mounted-travel suppression remains outside ground-combat ownership",()=>{Me.Mounted=true;Me.Combat=true;Tick(Build());Check(Fallback==1&&!Trace.Contains("heal")&&Dismounts==0,"ground policy forced combat while mounted travel is owned elsewhere");});
        Add("explicit dismount request keeps its priority",()=>{Me.Combat=true;Me.Mounted=true;Mount.DismountNeeded=true;Tick(Build());Check(Dismounts==1&&Fallback==0&&Trace.Count==0,"explicit dismount priority changed");});
        Add("empty Kill POI cleanup does not authorize same-tick gathering",()=>{Me.Combat=true;BotPoi.Current=new BotPoi(null,PoiType.Kill);Tick(Build());Check(BotPoi.Current.Type==PoiType.None&&Fallback==0,"POI cleanup fell into gathering");});
        Add("clear-sight ordinary pull still works",()=>{var enemy=new WoWUnit();Me.CurrentTarget=enemy;Targeting.Instance.TargetList.Add(enemy);BotPoi.Current=new BotPoi(enemy,PoiType.Kill);Results["pull"]=RunStatus.Success;Tick(Build());Check(Trace.Contains("pull")&&Fallback==0,"ordinary pull lost");});
        Add("blocked sight still rejects a pull",()=>{var enemy=new WoWUnit{InLineOfSpellSight=false};Me.CurrentTarget=enemy;Targeting.Instance.TargetList.Add(enemy);BotPoi.Current=new BotPoi(enemy,PoiType.Kill);Results["pull"]=RunStatus.Success;Tick(Build());Check(!Trace.Contains("pull"),"blocked target was pulled");});
        int passed=0,assertions=0,unexpected=0;
        foreach(var item in cases)
        {
            try{item.Item2();passed++;Console.WriteLine("PASS combat target gap: "+item.Item1);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL combat target gap assertion: "+item.Item1+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR combat target gap fixture: "+item.Item1+": "+e);}
        }
        Console.WriteLine($"Combat target-gap scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; exact LevelBot combat region and POI decorator; real TreeSharp; controlled routine/world; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Combat target-gap regressions");
    }
    private static Composite Build()=>new PrioritySelector(Bots.Grind.LevelBot.CreateCombatBehavior(),new TreeSharp.Action(_=>{Fallback++;return RunStatus.Success;}));
    private static void Reset(){Trace.Clear();Results.Clear();Fallback=Targets=Dismounts=0;StyxWoW.Me=new LocalPlayer();Targeting.Instance.TargetList.Clear();BotPoi.Current=new BotPoi(null,PoiType.None);Mount.DismountNeeded=false;Routine=new RoutineSet();}
    private static void Tick(Composite root)
    {
        var errors=new List<string>();
        void Record(Styx.Helpers.LogLevel level,string text){if(text.Contains("Exception")||text.Contains("Object reference not set"))errors.Add(text);}
        Styx.Helpers.Logging.OnMessageLogged+=Record;
        try{root.Start(null!);try{Check(root.Tick(null!)!=RunStatus.Running,"controlled leaves unexpectedly running");}finally{root.Stop(null!);}Check(errors.Count==0,"swallowed callback error: "+string.Join(";",errors));}
        finally{Styx.Helpers.Logging.OnMessageLogged-=Record;}
    }
    private static void Check(bool ok,string why){if(!ok)throw new Failure(why);}
}
/* Embedded fixture namespace, not the initializer scope. */ namespace Styx {public static class StyxWoW{public static LocalPlayer Me=new();}}
/* Embedded fixture namespace, not the initializer scope. */ namespace Styx.WoWInternals.WoWObjects
{
    public class WoWObject{public WoWPoint Location=new(10,10,10);public WoWUnit ToUnit()=>(WoWUnit)this;}
    public class WoWUnit:WoWObject{public bool IsAlive=true;public bool Dead=>!IsAlive;public bool Combat;public bool InLineOfSpellSight=true;public float Distance=3;public WoWUnit? CurrentTarget;public void Target(){GapCases.Targets++;StyxWoW.Me.CurrentTarget=this;}}
    public class LocalPlayer:WoWUnit{public bool Mounted;public WoWUnit? Pet;public bool GotAlivePet=>Pet?.IsAlive==true;public bool HasPendingSpell(string name)=>false;}
}
/* Embedded fixture namespace, not the initializer scope. */ namespace Styx.Logic {public sealed class Targeting{public static Targeting Instance=new();public static float PullDistance=30;public List<WoWUnit> TargetList=new();public WoWUnit? FirstUnit=>TargetList.FirstOrDefault();}}
/* Embedded fixture namespace, not the initializer scope. */ namespace Styx.Logic.POI
{
    public enum PoiType{None,Kill,Skin}
    public sealed class BotPoi{public static BotPoi Current=new(null,PoiType.None);public WoWObject? AsObject;public PoiType Type;public WoWPoint Location=>AsObject?.Location??WoWPoint.Zero;public BotPoi(WoWObject? subject,PoiType type){AsObject=subject;Type=type;}public static void Clear(string reason){Current=new(null,PoiType.None);}}
}
/* Embedded fixture namespace, not the initializer scope. */ namespace Styx.Logic.Pathing {public static class Mount{public static bool DismountNeeded;public static bool ShouldDismount(WoWPoint p)=>DismountNeeded;public static void Dismount(string reason){GapCases.Dismounts++;StyxWoW.Me.Mounted=false;}}}
/* Embedded fixture namespace, not the initializer scope. */ namespace Styx.WoWInternals {public static class Lua{public static void DoString(string text){}}}
/* Embedded fixture namespace, not the initializer scope. */ namespace CommonBehaviors.Actions
{
    public sealed class ActionClearPoi:TreeSharp.Action{public ActionClearPoi(string reason):base(_=>{BotPoi.Clear(reason);return RunStatus.Success;}){}}
    public sealed class ActionDebugString:TreeSharp.Action{public ActionDebugString(string text):base(_=>RunStatus.Success){}}
    public sealed class ActionSetPoi:TreeSharp.Action{public ActionSetPoi(bool unused,Func<object,BotPoi> select):base(ctx=>{BotPoi.Current=select(ctx);return RunStatus.Success;}){}}
}
""";
}

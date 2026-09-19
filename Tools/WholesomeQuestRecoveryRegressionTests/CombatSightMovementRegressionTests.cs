using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Compile complete tracked Movement.cs; real TreeSharp and WoWPoint execute.
// World observations and navigation effects are controlled, not the owner methods.
internal static class CombatSightMovementRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        string? root=null;
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj"))){root=d.FullName;break;}
        if(root==null)throw new InvalidOperationException("Tracked checkout required");
        string temp=Path.Combine(Path.GetTempPath(),"cb-combat-sight-movement-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool logging=Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging=false;
            File.Copy(Path.Combine(root,"runtime-snapshot","Routines","Singular wotlk","Helpers","Movement.cs"),Path.Combine(temp,"Movement.cs"));
            File.WriteAllText(Path.Combine(temp,"Boundary.cs"),Boundary);
            Type type=typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler",true)!;
            object compiler=Activator.CreateInstance(type,new object[]{temp})!;
            foreach(string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference",flags)!.Invoke(compiler,new object[]{path});
            var result=(CompilerResults)type.GetMethod("Compile",flags)!.Invoke(compiler,null)!;
            var errors=result.Errors.Cast<CompilerError>().Where(e=>!e.IsWarning).ToArray();
            if(errors.Length!=0)throw new InvalidOperationException("Actual Movement owner did not compile: "+string.Join(";",errors.Select(e=>e.ToString())));
            var assembly=(Assembly)type.GetProperty("CompiledAssembly",flags)!.GetValue(compiler)!;
            try{assembly.GetType("MovementCases",true)!.GetMethod("Run")!.Invoke(null,null);}
            catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        finally{Styx.Helpers.Logging.FileLogging=logging;Directory.Delete(temp,true);}
    }
    private const string Boundary="""
using System;
using System.Collections.Generic;
using System.Linq;
using Singular.Helpers;
using Singular.Settings;
using Styx;
using Styx.Logic.Pathing;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
public static class MovementCases
{
    private sealed class Failure(string text):Exception(text){}
    internal static readonly List<WoWPoint> Moves=new();
    internal static int Stops, Selections;
    internal static MoveResult Result;
    private static WoWUnit Player=>StyxWoW.Me!;
    private static WoWUnit Target=>Player.CurrentTarget!;
    public static void Run()
    {
        var cases=new List<(string,System.Action)>();
        foreach(bool customSelector in new[]{false,true})
        {
            bool custom=customSelector;string label=custom?"explicit selector":"current target";
            Composite Build()=>custom?Movement.CreateMoveToLosBehavior(_=>{Selections++;return StyxWoW.Me?.CurrentTarget!;}):Movement.CreateMoveToLosBehavior();
            void Add(string name,System.Action test)=>cases.Add((label+": "+name,()=>{Reset();test();}));
            void Deny(string name,System.Action mutation)=>Add(name,()=>{mutation();Tick(Build());Check(Moves.Count==0,"ineligible observation authorized movement");});
            Add("blocked valid target requests navigation",()=>{var expected=Target.Location;Tick(Build());Check(Moves.SequenceEqual(new[]{expected}),"did not route to the observed target");});
            Deny("clear sight does not reposition",()=>Target.Sight=true);
            Deny("disabled movement stays disabled",()=>SingularSettings.Instance.DisableAllMovement=true);
            Deny("absent target does not navigate",()=>Player.CurrentTarget=null);
            Deny("absent player does not navigate",()=>StyxWoW.Me=null);
            Deny("self target does not navigate",()=>Player.CurrentTarget=Player);
            Deny("invalid target does not navigate",()=>Target.IsValid=false);
            Deny("dead target does not navigate",()=>Target.IsAlive=false);
            // Actual Druid Rebirth calls the delegate overload for a dead ally.
            Add("dead friendly resurrection target remains reachable",()=>{Target.IsAlive=false;Target.IsFriendly=true;var expected=Target.Location;Tick(Build());Check(Moves.SequenceEqual(new[]{expected}),"resurrection approach was disabled by a blanket dead-target veto");});
            Add("living friendly healing target remains reachable",()=>{Target.IsFriendly=true;var expected=Target.Location;Tick(Build());Check(Moves.SequenceEqual(new[]{expected}),"friendly healing approach was disabled");});
            Deny("invalid player does not navigate",()=>Player.IsValid=false);
            Deny("dead player does not navigate",()=>Player.IsAlive=false);
            Deny("in-progress cast is not interrupted for LOS",()=>Player.IsCasting=true);
            Deny("in-progress channel is not interrupted for LOS",()=>Player.ChanneledCastingSpellId=101);
            Deny("unknown destination sentinel is not routed",()=>Target.Location=WoWPoint.Empty);
            Deny("zero destination is not routed",()=>Target.Location=WoWPoint.Zero);
            Deny("NaN destination is not routed",()=>Target.Location=new WoWPoint(float.NaN,3,4));
            Deny("infinite destination is not routed",()=>Target.Location=new WoWPoint(1,float.PositiveInfinity,4));
            Add("negative finite world coordinate remains usable",()=>{Target.Location=new WoWPoint(-12,-5,7);var expected=Target.Location;Tick(Build());Check(Moves.SequenceEqual(new[]{expected}),"valid negative coordinate rejected");});
            foreach(MoveResult outcome in Enum.GetValues<MoveResult>())
            {
                var selected=outcome;
                Add("navigation result "+selected,()=>{
                    Result=selected;int fallback=0;
                    Tick(new PrioritySelector(Build(),new TreeSharp.Action(_=>{fallback++;return RunStatus.Success;})));
                    Check(Moves.Count==1,"one decision must make one navigation attempt");
                    Check(fallback==(selected is MoveResult.Failed or MoveResult.PathGenerationFailed?1:0),"navigation failure swallowed as handled LOS");
                });
            }
            Deny("movement disabled during sight observation revokes request",()=>Target.OnSight=()=>SingularSettings.Instance.DisableAllMovement=true);
            Deny("casting starts during sight observation revokes request",()=>Target.OnSight=()=>Player.IsCasting=true);
            Deny("player replacement during sight observation revokes request",()=>Target.OnSight=()=>StyxWoW.Me=new WoWUnit{Guid=99});
            Deny("target invalidation during sight observation revokes request",()=>{var target=Target;target.OnSight=()=>target.IsValid=false;});
            Deny("movement disabled during destination observation revokes request",()=>Target.OnLocation=()=>SingularSettings.Instance.DisableAllMovement=true);
            Add("restored visibility releases movement on next decision",()=>{Tick(Build());Check(Moves.Count==1,"initial movement missing");Moves.Clear();Target.Sight=true;Tick(Build());Check(Moves.Count==0,"clear sight retained stale movement intent");});
            if(custom)Add("one decision invokes selector once",()=>{Tick(Build());Check(Selections==1,"selector evaluated "+Selections+" times");});
        }
        void StopCase(string name,System.Action arrange,bool expected)=>cases.Add(("stop in range: "+name,()=>{
            Reset();Player.IsMoving=true;Target.Sight=true;arrange();Tick(Movement.CreateEnsureMovementStoppedWithinRange(30));
            Check(Stops==(expected?1:0),"stop admission ignored target sight/owner");
        }));
        StopCase("visible target allows a casting stop",()=>{},true);
        StopCase("obstruction cannot stop LOS approach",()=>Target.Sight=false,false);
        StopCase("range boundary remains inclusive",()=>Target.Distance=30,true);
        StopCase("distant target does not stop approach",()=>Target.Distance=31,false);
        StopCase("missing target does not stop approach",()=>Player.CurrentTarget=null,false);
        StopCase("missing player does not stop approach",()=>StyxWoW.Me=null,false);
        StopCase("disabled movement does not stop approach",()=>SingularSettings.Instance.DisableAllMovement=true,false);
        StopCase("invalid target is not casting evidence",()=>Target.IsValid=false,false);
        StopCase("dead target is not casting evidence",()=>Target.IsAlive=false,false);
        StopCase("visible friendly corpse can be a resurrection destination",()=>{Target.IsAlive=false;Target.IsFriendly=true;},true);
        StopCase("owner replaced during sight read cannot stop replacement",()=>Target.OnSight=()=>StyxWoW.Me=new WoWUnit{Guid=99,IsMoving=true},false);
        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases)
        {
            try{c.Item2();passed++;Console.WriteLine("PASS combat sight movement: "+c.Item1);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL combat sight movement assertion: "+c.Item1+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR combat sight movement fixture/owner: "+c.Item1+": "+e);}
        }
        Console.WriteLine($"Combat sight movement scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete tracked Movement and real TreeSharp; controlled world/navigation; no native path or game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Combat sight movement failures");
    }
    private static void Reset(){StyxWoW.Me=new WoWUnit{Guid=1,CurrentTarget=new WoWUnit{Guid=2,Location=new WoWPoint(20,30,5)}};Moves.Clear();Stops=Selections=0;Result=MoveResult.Moved;SingularSettings.Instance.DisableAllMovement=false;}
    private static void Tick(Composite tree)
    {
        // TreeSharp intentionally logs ordinary owner errors and returns Failure.
        // A no-navigation assertion must not accidentally accept a null dereference.
        var diagnostics=new List<string>();
        void Record(Styx.Helpers.LogLevel level,string message)=>diagnostics.Add(message);
        Styx.Helpers.Logging.OnMessageLogged+=Record;
        try
        {
            tree.Start(null!);
            try{int n=0;while(tree.Tick(null!)==RunStatus.Running)if(++n>10)throw new Failure("unbounded helper");}
            finally{tree.Stop(null!);}
            Check(diagnostics.Count==0,"owner produced a swallowed diagnostic: "+string.Join(";",diagnostics));
        }
        finally{Styx.Helpers.Logging.OnMessageLogged-=Record;}
    }
    private static void Check(bool yes,string why){if(!yes)throw new Failure(why);}
}
/* Controlled unit observations only. */ namespace Styx.WoWInternals.WoWObjects
{
    public class WoWUnit
    {
        public ulong Guid;public WoWUnit? CurrentTarget;public bool IsValid=true,IsAlive=true,IsMoving,IsCasting,IsFriendly,IsPlayer,MeIsBehind,Stunned;
        public uint ChanneledCastingSpellId;public float Distance=20,Rotation;
        public bool IsMe=>ReferenceEquals(this,StyxWoW.Me);public bool Sight;
        public System.Action? OnSight,OnLocation;
        private WoWPoint position=new WoWPoint(5,5,5);
        public WoWPoint Location{get{var a=OnLocation;OnLocation=null;a?.Invoke();return position;}set{position=value;}}
        public bool InLineOfSpellSight{get{var a=OnSight;OnSight=null;a?.Invoke();return Sight;}}
        public bool IsSafelyFacing(WoWUnit target,float angle)=>true;
        public void Face(){}public void Target(){StyxWoW.Me!.CurrentTarget=this;}public string SafeName()=>"controlled";
    }
}
/* Controlled owner and settings observations. */ namespace Styx{public static class StyxWoW{public static WoWUnit? Me;}}
/* Controlled movement effects only. */ namespace Styx.Logic.Pathing
{
    public static class Navigator
    {
        public static Mover PlayerMover=new();
        public static MoveResult MoveTo(WoWPoint point){MovementCases.Moves.Add(point);return MovementCases.Result;}
    }
    public class Mover{public void MoveStop(){MovementCases.Stops++;}}
}
/* Controlled settings only. */ namespace Singular.Settings
{
    public class SingularSettings{public static SingularSettings Instance=new();public bool DisableAllMovement;}
}
/* Unused class context dependencies; owner source is retained in full. */ namespace Singular
{
    public enum WoWContext{Normal,Battlegrounds,Instances}
    public static class SingularRoutine{public static WoWContext CurrentWoWContext;}
}
/* Unused helpers and delegate types only. */ namespace Singular.Helpers
{
    public delegate WoWUnit UnitSelectionDelegate(object context);
    public delegate bool SimpleBooleanDelegate(object context);
    public static class Group{public static bool MeIsTank=>false;}
    public static class Spell{public static float MeleeRange=>5;}
    public static class Logger{public static void WriteDebug(string text){}}
}
""";
}

using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

// Execute exact contiguous Cast/Buff/BuffSelf source regions with real TreeSharp.
// Only world observations, range/safety admission and terminal spell dispatch are
// controlled. Full Singular compilation is separately retained by the existing suite.
internal static class SpellSightDispatchRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        string? root=null;
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj"))){root=d.FullName;break;}
        if(root==null)throw new InvalidOperationException("Tracked checkout required");
        string text=File.ReadAllText(Path.Combine(root,"runtime-snapshot","Routines","Singular wotlk","Helpers","Spell.cs"));
        const string start="        #region Cast - by name", end="        #region Heal - by name";
        int first=text.IndexOf(start,StringComparison.Ordinal), last=text.IndexOf(end,StringComparison.Ordinal);
        if(first<0||last<=first||text.IndexOf(start,first+start.Length,StringComparison.Ordinal)>=0)
            throw new InvalidOperationException("Ambiguous actual Cast/Buff region boundaries");
        string region=text.Substring(first,last-first);
        Console.WriteLine("Spell sight exact-region SHA256: "+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(region))).ToLowerInvariant());
        string prefix="using System; using System.Collections.Generic; using System.Linq; using CommonBehaviors.Actions; using Styx; using Styx.Logic.Combat; using Styx.WoWInternals.WoWObjects; using TreeSharp; using Action=TreeSharp.Action; namespace Singular.Helpers { public delegate WoWUnit UnitSelectionDelegate(object c); public delegate bool SimpleBooleanDelegate(object c); internal static " + "class Spell { private const float MeleeRange=5; \n";
        string temp=Path.Combine(Path.GetTempPath(),"cb-spell-sight-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool logging=Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging=false;
            File.WriteAllText(Path.Combine(temp,"Owners.cs"),prefix+region+"}}",Encoding.UTF8);
            File.WriteAllText(Path.Combine(temp,"Boundary.cs"),Boundary,Encoding.UTF8);
            Type type=typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler",true)!;
            object compiler=Activator.CreateInstance(type,new object[]{temp})!;
            foreach(string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference",flags)!.Invoke(compiler,new object[]{path});
            var result=(CompilerResults)type.GetMethod("Compile",flags)!.Invoke(compiler,null)!;
            var errors=result.Errors.Cast<CompilerError>().Where(e=>!e.IsWarning).ToArray();
            if(errors.Length!=0)throw new InvalidOperationException("Exact spell sight owners did not compile: "+string.Join(";",errors.Select(e=>e.ToString())));
            var assembly=(Assembly)type.GetProperty("CompiledAssembly",flags)!.GetValue(compiler)!;
            try{assembly.GetType("SightCases",true)!.GetMethod("Run")!.Invoke(null,null);}
            catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        finally{Styx.Helpers.Logging.FileLogging=logging;Directory.Delete(temp,true);}
    }
    private const string Boundary="""
using System;
using System.Collections.Generic;
using System.Linq;
using Singular.Helpers;
using Styx;
using Styx.Logic.Combat;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
public static class SightCases
{
    private sealed class Failure(string text):Exception(text){}
    internal static readonly List<ulong> Targets=new();
    internal static System.Action? DuringSetup, DuringLog;
    internal static bool Setup, Ready, Safe, Required, Accepted;
    public static void Run()
    {
        var cases=new List<(string,System.Action)>();
        foreach(bool value in new[]{false,true})
        {
            bool id=value;string label=id?"ID":"name";
            void Add(string name,System.Action body)=>cases.Add((label+": "+name,()=>{Reset();body();}));
            Add("clear stationary target casts once",()=>{Tick(Build(id));Expect(2);});
            Add("blocked initial line of sight denies cast",()=>{Target.Sight=false;Tick(Build(id));Expect();});
            Add("initial target beyond maximum range denies cast",()=>{Target.Distance=101;Tick(Build(id));Expect();});
            Add("unknown spell remains denied",()=>{Ready=false;Tick(Build(id));Expect();});
            Add("unchanged target across setup retains cast",()=>{Setup=true;DuringSetup=()=>{};Tick(Build(id));Expect(2);});
            foreach(bool logPhase in new[]{false,true})
            {
                bool log=logPhase;string at=log?"logging":"yielded setup";
                void Change(string name,System.Action mutation)=>Add(at+": "+name,()=>{
                    if(log)DuringLog=mutation;else{Setup=true;DuringSetup=mutation;}
                    Tick(Build(id));Expect();
                });
                Change("new obstruction revokes cast",()=>Target.Sight=false);
                Change("target moving beyond range revokes cast",()=>Target.Distance=101);
                Change("target moving inside minimum range revokes cast",()=>{SpellManager.Spells["Test"].MinRange=8;Target.Distance=4;});
                Change("cooldown or availability loss revokes cast",()=>Ready=false);
                Change("changed caller requirement revokes cast",()=>Required=false);
                Change("changed combat safety revokes cast",()=>Safe=false);
            }
            Add("missing target after setup rejects without dispatch",()=>{Setup=true;DuringSetup=()=>StyxWoW.Me.CurrentTarget=null;Tick(Build(id));Expect();});
            Add("rejected dispatch yields to recovery leaf",()=>{
                Setup=true;DuringSetup=()=>Target.Sight=false;int recovery=0;
                var tree=new PrioritySelector(Build(id),new TreeSharp.Action(_=>{recovery++;return RunStatus.Success;}));
                Tick(tree);Expect();Check(recovery==1,"blocked cast monopolized priority before recovery");
            });
            Add("new clear-sight decision recovers without sticky denial",()=>{
                Target.Sight=false;for(int n=0;n<10;n++)Tick(Build(id));Expect();Target.Sight=true;Tick(Build(id));Expect(2);
            });
            Add("rejected cast cannot execute success bookkeeping",()=>{
                Setup=true;DuringSetup=()=>Target.Sight=false;int counted=0;
                Tick(new Sequence(Build(id),new TreeSharp.Action(_=>{counted++;return RunStatus.Success;})));
                Expect();Check(counted==0,"blocked dispatch ran success-only continuation");
            });
            Add("healthy self cast does not borrow enemy obstruction",()=>{
                Target.Sight=false;Tick(id?Spell.Cast(101,_=>StyxWoW.Me,_=>Required):Spell.Cast("Test",_=>StyxWoW.Me,_=>Required));Expect(1);
            });
            Add("backend false remains failure and not recorded success",()=>{Accepted=false;Tick(Build(id));Expect();});
            Add("late obstruction does not enter buff retry dictionary",()=>{
                Setup=true;DuringSetup=()=>Target.Sight=false;Tick(id?Spell.Buff(101):Spell.Buff("Test"));Expect();
                Check(!Spell.DoubleCastPreventionDict.ContainsKey("Test"),"denied buff acquired retry state");
            });
            Add("unchanged buff still records successful dispatch",()=>{Tick(id?Spell.Buff(101):Spell.Buff("Test"));Expect(2);});
        }
        cases.Add(("name: existing self-range exception remains unchanged",()=>{Reset();Target.Sight=false;SpellManager.Spells["Test"].SpellRangeId=1;Tick(Build(false));Expect(2);}));
        cases.Add(("name: existing melee-range admission is preserved",()=>{Reset();Target.Distance=3;Target.Sight=false;SpellManager.Spells["Test"].SpellRangeId=2;Tick(Build(false));Expect(2);}));
        cases.Add(("name: moving out of melee during setup revokes cast",()=>{Reset();Target.Distance=3;SpellManager.Spells["Test"].SpellRangeId=2;Setup=true;DuringSetup=()=>Target.Distance=8;Tick(Build(false));Expect();}));
        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases)
        {
            try{c.Item2();passed++;Console.WriteLine("PASS spell sight: "+c.Item1);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL spell sight assertion: "+c.Item1+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR spell sight fixture/owner: "+c.Item1+": "+e);}
        }
        Console.WriteLine($"Spell sight dispatch scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; exact tracked Cast/Buff region and real TreeSharp; controlled sight/range/backend; no native obstruction trace or game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Spell sight dispatch failures");
    }
    private static WoWUnit Target=>StyxWoW.Me.CurrentTarget!;
    private static Composite Build(bool id)=>id?Spell.Cast(101,_=>StyxWoW.Me.CurrentTarget,_=>Required):Spell.Cast("Test",_=>false,_=>StyxWoW.Me.CurrentTarget,_=>Required);
    private static void Reset(){StyxWoW.Me=new WoWUnit{Guid=1,CurrentTarget=new WoWUnit{Guid=2}};Targets.Clear();Spell.DoubleCastPreventionDict.Clear();SpellManager.Spells.Clear();SpellManager.Spells["Test"]=new();Setup=false;DuringSetup=null;DuringLog=null;Ready=Safe=Required=Accepted=true;}
    private static void Tick(Composite tree){tree.Start(null!);try{int count=0;while(tree.Tick(null!)==RunStatus.Running)if(++count>12)throw new Failure("unbounded decision");}finally{tree.Stop(null!);}}
    private static void Expect(params ulong[] ids)=>Check(Targets.SequenceEqual(ids),"unexpected submission targets: "+string.Join(',',Targets));
    private static void Check(bool yes,string why){if(!yes)throw new Failure(why);}
}
/* Controlled world observations only. */ namespace Styx.WoWInternals.WoWObjects
{
    public class Aura{public int SpellId;public string Name="";public ulong CreatorGuid;}
    public class WoWUnit
    {
        public ulong Guid;public WoWUnit? CurrentTarget;public bool Mounted,IsCasting;
        public bool IsMe=>ReferenceEquals(this,StyxWoW.Me);public float Distance=20;public bool Sight=true;
        public bool InLineOfSpellSight=>Sight;
        public Dictionary<string,Aura> Auras=new();
        public bool HasAura(string name)=>Auras.ContainsKey(name);
        public bool HasMyAura(string name)=>Auras.TryGetValue(name,out var aura)&&aura.CreatorGuid==StyxWoW.Me.Guid;
        public string SafeName()=>"controlled";
    }
}
/* Controlled backend; real host range logic is not claimed by this fixture. */ namespace Styx.Logic.Combat
{
    public class WoWSpell{public uint SpellRangeId=3;public float MinRange=0;public float MaxRange=100;public int CastTime=>0;public bool IsFunnel=>false;public bool IsChanneled=>false;}
    public static class SpellManager
    {
        public static Dictionary<string,WoWSpell> Spells=new();
        public static bool CanCast(string name,WoWUnit target,bool range,bool movement)=>SightCases.Ready&&target!=null&&Spells.TryGetValue(name,out var s)&&(!range||target.IsMe||(target.InLineOfSpellSight&&target.Distance>=s.MinRange&&target.Distance<=s.MaxRange));
        public static bool CanCast(int id,WoWUnit target,bool range)=>id==101&&CanCast("Test",target,range,false);
        public static bool Cast(string name,WoWUnit target)=>Submit(target);
        public static bool Cast(int id,WoWUnit target)=>Submit(target);
        private static bool Submit(WoWUnit target){if(!SightCases.Accepted)return false;SightCases.Targets.Add(target.Guid);return true;}
    }
}
/* Controlled owner observation. */ namespace Styx{public static class StyxWoW{public static WoWUnit Me=null!;}}
/* Only setup yields and terminal effects are controlled. */ namespace Singular.Helpers
{
    public static class Unit{public static bool IsCombatActionSafe(string name,WoWUnit target)=>target!=null&&SightCases.Safe;public static bool IsCombatActionSafe(int id,WoWUnit target)=>target!=null&&SightCases.Safe;}
    public static class Logger{public static void Write(string text){var action=SightCases.DuringLog;SightCases.DuringLog=null;action?.Invoke();}}
    public class SetupAction:Composite
    {
        protected override IEnumerable<RunStatus> Execute(object context){yield return RunStatus.Running;var action=SightCases.DuringSetup;SightCases.DuringSetup=null;action?.Invoke();yield return RunStatus.Success;}
    }
    public static class Common{public static Composite CreateDismount(string why)=>new SetupAction();}
    public static class Movement
    {
        public static bool NeedsOffTargetCastSetup(WoWUnit target)=>SightCases.Setup;
        public static Composite CreateEnsureTargetAndFaceBehavior(UnitSelectionDelegate select)=>new SetupAction();
    }
}
""";
}

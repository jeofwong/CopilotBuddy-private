using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Execute the complete tracked UseItemOn tree. Reuse the unchanged selection
// fixture, instrumenting only its external callbacks; never rewrite the owner.
internal static class QuestItemDispatchRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");
        string boundary = (string)typeof(QuestItemTargetSelectionRegressionTests).GetField("Boundary", flags)!.GetRawConstantValue()!;
        void Replace(string before, string after)
        {
            if (boundary.Split(new[] { before }, StringSplitOptions.None).Length != 2)
                throw new InvalidOperationException("External fixture shape changed: " + before);
            boundary = boundary.Replace(before, after);
        }
        Replace("public static void SleepForLagDuration(){}", "public static void SleepForLagDuration(){QuestItemDispatchCases.Boundary(\"lag\");}");
        Replace("public bool TryUseContainerItem()=>true; public void UseContainerItem(){TryUseContainerItem();}", "public bool TryUseContainerItem(){return QuestItemDispatchCases.Submit(this);}public void UseContainerItem(){TryUseContainerItem();}");
        Replace("public void Target(){ObjectManager.Me!.CurrentTarget=this;}", "public void Target(){ObjectManager.Me!.CurrentTarget=this;QuestItemDispatchCases.Boundary(\"target\");}");
        Replace("public void ClearTarget(){CurrentTarget=null;}", "public void ClearTarget(){QuestItemDispatchCases.Clears++;CurrentTarget=null;}");
        Replace("public static void MoveStop(){}", "public static void MoveStop(){if(ObjectManager.Me!=null)ObjectManager.Me.IsMoving=false;QuestItemDispatchCases.Boundary(\"stop\");}");
        Replace("public static void Face(ulong id){}", "public static void Face(ulong id){QuestItemDispatchCases.Boundary(\"face\");}");
        Replace("public static string StatusText{get;set;}=\"\";", "private static string status=\"\";public static string StatusText{get=>status;set{status=value;if(value.StartsWith(\"Using item\"))QuestItemDispatchCases.Boundary(\"status\");}}");
        Replace("public static List<WoWObject>? Objects{get;set;}=new();", "public static List<WoWObject>? Objects{get;set;}=new();public static T? GetObjectByGuid<T>(ulong id)where T:WoWObject=>Objects?.OfType<T>().FirstOrDefault(o=>o.Guid==id);");
        string temp = Path.Combine(Path.GetTempPath(), "cb-item-dispatch-" + Guid.NewGuid().ToString("N"));
        bool oldLogging = Styx.Helpers.Logging.FileLogging;
        Directory.CreateDirectory(temp);
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            File.Copy(Path.Combine(root, "runtime-snapshot", "Quest Behaviors", "UseItemOn.cs"), Path.Combine(temp, "UseItemOn.cs"));
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), boundary + Cases);
            Type type = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(type, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference", flags)!.Invoke(compiler, new object[] { path });
            var result = (CompilerResults)type.GetMethod("Compile", flags)!.Invoke(compiler, null)!;
            var errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Actual item-use compile failed: " + string.Join(";", errors.Select(e => e.ToString())));
            var assembly = (Assembly)type.GetProperty("CompiledAssembly", flags)!.GetValue(compiler)!;
            try { assembly.GetType("QuestItemDispatchCases", true)!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = oldLogging; Directory.Delete(temp, true); }
    }
    private const string Cases = """

public static class QuestItemDispatchCases
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Failure(string message):Exception(message){}
    private static Script owner=null!;
    private static LocalPlayer player=null!;
    private static WoWUnit target=null!;
    private static WoWItem item=null!;
    private static System.Action? mutation;
    private static string stage="";
    private static bool fired;
    private static readonly List<ulong> used=new();
    private static bool SubmissionAllowed;
    public static int Clears;
    public static void Boundary(string name)
    {
        if(!fired && name==stage){fired=true;var callback=mutation;mutation=null;callback?.Invoke();}
    }
    public static bool Submit(WoWItem selected)
    {
        if(!SubmissionAllowed){Boundary("use");return false;}
        used.Add(selected.Guid);Boundary("use");return true;
    }
    public static void Run()
    {
        var cases=new List<(string,System.Action)>();
        void Add(string name,System.Action test)=>cases.Add((name,()=>{Reset();test();}));
        foreach(string point in new[]{"stop","status","target","face"})
        {
            string boundary=point;
            void At(string label,System.Action change)=>Add(boundary+": "+label,()=>{
                stage=boundary;mutation=change;Tick();Check(fired,"requested callback not reached");
                Check(used.Count==0 && Counter==0,"invalidated attempt submitted an item or advanced its count");
            });
            At("missing actor revokes use",()=>ObjectManager.Me=null);
            At("replacement actor cannot inherit attempt",()=>ObjectManager.Me=new LocalPlayer{Guid=1,Location=player.Location,CarriedItems=player.CarriedItems});
            At("changed actor identity revokes use",()=>player.Guid=44);
            At("dead actor revokes use",()=>player.IsAlive=false);
            At("invalid item revokes use",()=>item.IsValid=false);
            At("removed item revokes use",()=>player.CarriedItems!.Clear());
            At("same-entry replacement item is not substituted",()=>player.CarriedItems=new(){new WoWItem{Guid=88,Entry=12345}});
            At("changed item identity revokes use",()=>item.Guid=88);
            At("new item cooldown revokes use",()=>item.Cooldown=5);
            At("invalid target revokes use",()=>target.IsValid=false);
            At("target leaving the object table revokes use",()=>ObjectManager.Objects!.Clear());
            At("changed target identity revokes use",()=>target.Guid=55);
            At("target moving out of range revokes use",()=>target.Location=new WoWPoint(50,10,10));
            At("new forbidden aura revokes use",()=>target.Auras.Add("Forbidden"));
            At("disposed behaviour cannot continue use",()=>typeof(Script).GetField("_isDisposed",Hidden)!.SetValue(owner,true));
            Add(boundary+": unchanged owner remains usable",()=>{stage=boundary;mutation=()=>{};Tick();Check(fired&&used.SequenceEqual(new[]{17ul})&&Counter==1,"ordinary use did not finish once");});
        }
        Add("another nearer NPC appearing does not redirect the captured attempt",()=>{
            stage="target";mutation=()=>ObjectManager.Objects!.Add(new WoWUnit{Guid=3,Entry=70001,Location=new WoWPoint(11,10,10)});
            Tick();Check(used.Count==1&&Counter==1&&Blacklist.Contains(2)&&!Blacklist.Contains(3),"old attempt switched recipient after targeting");
        });
        Add("new combat target during facing revokes the old item use",()=>{
            stage="face";mutation=()=>player.CurrentTarget=new WoWUnit{Guid=4,Entry=70002};Tick();Check(used.Count==0&&Counter==0,"item dispatched against a replacement current target");
        });
        Add("new target after submission is not cleared by old cleanup",()=>{
            var replacement=new WoWUnit{Guid=4,Entry=70002};stage="use";mutation=()=>player.CurrentTarget=replacement;
            Tick();Check(used.Count==1&&Counter==1&&ReferenceEquals(player.CurrentTarget,replacement)&&Clears==0,"old cleanup cleared a new target");
        });
        Add("a consumed item may disappear after a valid submission",()=>{
            stage="use";mutation=()=>player.CarriedItems!.Clear();Tick();Check(used.Count==1&&Counter==1,"valid consumption lost its local dispatch count");
        });
        Add("disposed owner after submission cannot write old bookkeeping",()=>{
            stage="use";mutation=()=>typeof(Script).GetField("_isDisposed",Hidden)!.SetValue(owner,true);
            Tick();Check(used.Count==1&&Counter==0&&Blacklist.Count==0&&Clears==0,"disposed attempt mutated local state or new target");
        });
        Add("safe slot refusal does not advance legacy invocation count",()=>{
            SubmissionAllowed=false;Tick();
            Check(used.Count==0&&Counter==0&&Blacklist.Count==0,
                "refused container submission consumed legacy invocation bookkeeping");
        });
        Add("safe slot refusal gates authoritative timestamp before bookkeeping",()=>{
            string source=System.IO.File.ReadAllText(System.IO.Path.Combine(Root(),"runtime-snapshot","Quest Behaviors","UseItemOn.cs"));
            int refusal=source.IndexOf("if (!item.TryUseContainerItem())",StringComparison.Ordinal);
            int accepted=refusal<0 ? -1 : source.IndexOf("_submissionRefusalUtc = -1;",refusal,StringComparison.Ordinal);
            int stamp=accepted<0 ? -1 : source.IndexOf("_lastSubmissionUtc = UtcNowMilliseconds()",accepted,StringComparison.Ordinal);
            int count=accepted<0 ? -1 : source.IndexOf("Counter++",accepted,StringComparison.Ordinal);
            Check(refusal>=0&&accepted>refusal&&stamp>accepted&&count>stamp,
                "authoritative/local bookkeeping is not structurally gated by safe submission result");
        });
        Add("persistent safe slot refusal has a bounded local deferral lifetime",()=>{
            string source=System.IO.File.ReadAllText(System.IO.Path.Combine(Root(),"runtime-snapshot","Quest Behaviors","UseItemOn.cs"));
            Check(source.Contains("SubmissionRefusalTimeout",StringComparison.Ordinal)
                && source.Contains("_submissionRefusalUtc",StringComparison.Ordinal)
                && source.Contains("DeferSubmissionRefusal",StringComparison.Ordinal),
                "safe container refusal can retry forever without consuming an attempt");
        });
        Add("no duplicate use after a completed local attempt",()=>{Tick();Tick();Check(used.Count==1&&Counter==1,"completed local repetition submitted twice");});
        Add("legacy caller without LOS requirement remains usable when observation is blocked",()=>{
            Set("RequireLos",false);target.InLineOfSight=false;Tick();
            Check(used.Count==1&&Counter==1,"legacy caller unexpectedly inherited the generated LOS requirement");
        });
        Add("explicit LOS requirement defers use while the recipient is blocked",()=>{
            Set("RequireLos",true);target.InLineOfSight=false;Tick();
            Check(used.Count==0&&Counter==0,"LOS-gated item use crossed an obstructed recipient");
        });
        Add("explicit LOS requirement admits the same recipient once sight is clear",()=>{
            Set("RequireLos",true);target.InLineOfSight=true;Tick();
            Check(used.Count==1&&Counter==1,"clear LOS did not admit the explicit generated recipe");
        });
        Add("recipe for a corpse is preserved",()=>{Set("NpcState",Script.NpcStateType.Dead);target.IsAlive=false;Tick();Check(used.Count==1&&Counter==1,"valid corpse recipe was removed");});
        Add("ordinary active-target cleanup remains available",()=>{Tick();Check(used.Count==1&&Counter==1&&Clears==1&&player.CurrentTarget==null,"normal targeted cleanup changed");});
        int passed=0,assertions=0,unexpected=0;
        foreach(var test in cases)
        {
            try{test.Item2();passed++;Console.WriteLine("PASS quest item dispatch: "+test.Item1);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL quest item dispatch assertion: "+test.Item1+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR quest item dispatch fixture: "+test.Item1+": "+e);}
        }
        Console.WriteLine($"Quest item dispatch scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete UseItemOn and real TreeSharp; controlled callbacks; no native use or server credit.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Quest item dispatch regressions");
    }
    private static void Reset()
    {
        typeof(QuestItemSelectionCases).GetMethod("Reset",Hidden)!.Invoke(null,null);
        owner=(Script)typeof(QuestItemSelectionCases).GetField("owner",Hidden)!.GetValue(null)!;
        target=(WoWUnit)typeof(QuestItemSelectionCases).GetField("candidate",Hidden)!.GetValue(null)!;
        player=ObjectManager.Me!;player.IsMoving=true;
        item=new WoWItem{Guid=17,Entry=12345};player.CarriedItems!.Add(item);
        typeof(Script).GetField("_isDisposed",Hidden)!.SetValue(owner,false);
        GC.SuppressFinalize(owner);Set("MobAuraMissingName","Forbidden");Set("WaitTime",101);
        mutation=null;stage="";fired=false;Clears=0;SubmissionAllowed=true;used.Clear();
    }
    private static int Counter=>(int)typeof(Script).GetProperty("Counter",Hidden)!.GetValue(owner)!;
    private static List<ulong> Blacklist=>(List<ulong>)typeof(Script).GetField("_npcBlacklist",Hidden)!.GetValue(owner)!;
    private static void Set(string name,object value)=>typeof(Script).GetProperty(name,Hidden)!.SetValue(owner,value);
    private static void Tick()
    {
        var errors=new List<string>();
        void Record(Styx.Helpers.LogLevel level,string message){if(message.Contains("Exception")||message.Contains("Object reference not set"))errors.Add(message);}
        Styx.Helpers.Logging.OnMessageLogged+=Record;
        try
        {
            Composite tree=(Composite)typeof(Script).GetMethod("CreateBehavior",Hidden)!.Invoke(owner,null)!;
            tree.Start(null!);
            try{int n=0;while(tree.Tick(null!)==RunStatus.Running)if(++n>20)throw new Failure("unbounded item tree");}
            finally{tree.Stop(null!);}
            Check(errors.Count==0,"swallowed owner exception: "+string.Join(";",errors));
        }
        finally{Styx.Helpers.Logging.OnMessageLogged-=Record;}
    }
    private static string Root()
    {
        for(var d=new System.IO.DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(System.IO.File.Exists(System.IO.Path.Combine(d.FullName,"CopilotBuddy.csproj")))return d.FullName;
        throw new Failure("tracked checkout required");
    }
    private static void Check(bool value,string message){if(!value)throw new Failure(message);}
}
""";
}

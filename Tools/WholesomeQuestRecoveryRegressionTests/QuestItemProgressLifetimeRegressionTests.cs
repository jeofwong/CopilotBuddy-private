using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;

// Complete tracked UseItemOn + real CustomForcedBehavior quest admission and
// actual QuestLog/Memory readers. World/item dispatch is controlled separately;
// the inherited progress predicate is not mocked. No native use or server credit.
internal static class QuestItemProgressLifetimeRegressionTests
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
        Replace("public static void SleepForLagDuration(){}", "public static void SleepForLagDuration(){QuestItemProgressLifetimeCases.Boundary(\"lag\");}");
        Replace("public bool TryUseContainerItem()=>true; public void UseContainerItem(){TryUseContainerItem();}", "public bool TryUseContainerItem(){QuestItemProgressLifetimeCases.Submit(this);return true;} public void UseContainerItem(){TryUseContainerItem();}");
        Replace("public void Target(){ObjectManager.Me!.CurrentTarget=this;}", "public void Target(){ObjectManager.Me!.CurrentTarget=this;QuestItemProgressLifetimeCases.Boundary(\"target\");}");
        Replace("public void ClearTarget(){CurrentTarget=null;}", "public void ClearTarget(){QuestItemProgressLifetimeCases.Clears++;CurrentTarget=null;}");
        Replace("public static void MoveStop(){}", "public static void MoveStop(){if(ObjectManager.Me!=null)ObjectManager.Me.IsMoving=false;QuestItemProgressLifetimeCases.Boundary(\"stop\");}");
        Replace("public static void Face(ulong id){}", "public static void Face(ulong id){QuestItemProgressLifetimeCases.Boundary(\"face\");}");
        Replace("public static string StatusText{get;set;}=\"\";", "private static string status=\"\";public static string StatusText{get=>status;set{status=value;if(value.StartsWith(\"Using item\"))QuestItemProgressLifetimeCases.Boundary(\"status\");}}");
        Replace("public static List<WoWObject>? Objects{get;set;}=new();", "public static List<WoWObject>? Objects{get;set;}=new();public static T? GetObjectByGuid<T>(ulong id)where T:WoWObject=>Objects?.OfType<T>().FirstOrDefault(o=>o.Guid==id);");
        string temp = Path.Combine(Path.GetTempPath(), "cb-item-progress-" + Guid.NewGuid().ToString("N"));
        bool oldLogging = Styx.Helpers.Logging.FileLogging;
        Directory.CreateDirectory(temp);
        IDisposable? questWorld = null;
        void Observe(string state)
        {
            if (state == "reset")
            {
                questWorld?.Dispose(); questWorld = null;
                questWorld = (IDisposable)Activator.CreateInstance(typeof(QuestPublicationRegressionTests).GetNestedType("Fixture", BindingFlags.NonPublic)!, true)!;
            }
            if (questWorld == null) throw new InvalidOperationException("Missing actual quest observation fixture");
            var kind = questWorld.GetType();
            uint descriptor = (uint)kind.GetField("descriptor", flags)!.GetValue(questWorld)!;
            var cache = (ThreadLocal<Dictionary<IntPtr, byte[]>>)kind.GetField("cache", flags)!.GetValue(questWorld)!;
            uint offset = state == "unrelated" ? 652u : 632u;
            uint id = state == "absent" ? 0u : state == "unrelated" ? 868u : 867u;
            uint completed = state is "complete" or "unrelated" ? Convert.ToUInt32(Enum.Parse(typeof(Styx.StyxWoW).Assembly.GetTypes().Single(t => t.IsEnum && t.Name == "WoWDescriptorQuestFlags"), "Completed")) : 0u;
            Marshal.WriteInt32(new IntPtr(unchecked((int)(descriptor + offset))), unchecked((int)id));
            Marshal.WriteInt32(new IntPtr(unchecked((int)(descriptor + offset + 4))), unchecked((int)completed));
            foreach (var key in cache.Value!.Keys.Where(k => unchecked((uint)k.ToInt32()) >= descriptor + 632 && unchecked((uint)k.ToInt32()) < descriptor + 1132).ToArray())
                cache.Value.Remove(key);
        }
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
            try { assembly.GetType("QuestItemProgressLifetimeCases", true)!.GetMethod("Run")!.Invoke(null, new object[] { (System.Action<string>)Observe }); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { questWorld?.Dispose(); Styx.Helpers.Logging.FileLogging = oldLogging; Directory.Delete(temp, true); }
    }
    private const string Cases = """

public static class QuestItemProgressLifetimeCases
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Failure(string message):Exception(message){}
    private static Script owner=null!;
    private static LocalPlayer player=null!;
    private static WoWUnit target=null!;
    private static WoWItem item=null!;
    private static System.Action<string> observe=null!;
    private static System.Action? mutation;
    private static string stage="";
    private static bool fired;
    private static readonly List<ulong> used=new();
    public static int Clears;
    public static void Boundary(string name)
    {
        if(!fired && name==stage){fired=true;var callback=mutation;mutation=null;callback?.Invoke();}
    }
    public static void Submit(WoWItem selected){used.Add(selected.Guid);Boundary("use");}
    public static void Run(System.Action<string> observation)
    {
        observe=observation;
        var cases=new List<(string,System.Action)>();
        void Add(string name,System.Action test)=>cases.Add((name,()=>{Reset();test();}));
        void Change(string state)
        {
            if(state=="attribute")typeof(Styx.Logic.Questing.CustomForcedBehavior).GetProperty("IsAttributeProblem",Hidden)!.SetValue(owner,true);
            else observe(state);
        }
        foreach(string point in new[]{"stop","status","target","face","lag"})
        {
            string boundary=point;
            foreach(string eventName in new[]{"complete","absent","attribute"})
            {
                string outcome=eventName;
                Add(boundary+": quest "+outcome+" revokes pending use",()=>{
                    stage=boundary;mutation=()=>Change(outcome);Tick();
                    Check(fired && owner.IsDone,"intended quest invalidation not observed by actual inherited predicate");
                    Check(used.Count==0&&Counter==0&&Blacklist.Count==0&&Clears==0,"obsolete quest use or cleanup occurred");
                });
            }
            Add(boundary+": unchanged quest retains one item use",()=>{
                stage=boundary;mutation=()=>{};Tick();
                Check(fired&&used.SequenceEqual(new[]{17ul})&&Counter==1,"valid quest use was lost");
            });
        }
        foreach(string eventName in new[]{"complete","absent","attribute"})
        {
            string outcome=eventName;
            Add("initial "+outcome+" never submits an obsolete use",()=>{
                Change(outcome);Check(owner.IsDone,"actual owner did not observe initial invalidation");Tick();
                Check(used.Count==0&&Counter==0&&Blacklist.Count==0,"initially ineligible quest submitted");
            });
            Add("post-submission "+outcome+" cannot mutate old bookkeeping or clear target",()=>{
                stage="use";mutation=()=>Change(outcome);Tick();
                Check(fired&&owner.IsDone,"post-use quest invalidation not observed");
                Check(used.Count==1&&Counter==0&&Blacklist.Count==0&&Clears==0,"invalidated continuation mutated old state");
            });
        }
        Add("explicit questless profile retains legacy use",()=>{
            Set("QuestId",0);observe("absent");Check(!owner.IsDone,"questless admission unexpectedly denied");Tick();Check(used.Count==1&&Counter==1,"questless profile no longer works");
        });
        Add("explicit before-acceptance recipe retains its NotInLog contract",()=>{
            Set("QuestRequirementInLog",Styx.Logic.Questing.CustomForcedBehavior.QuestInLogRequirement.NotInLog);
            Set("QuestRequirementComplete",Styx.Logic.Questing.CustomForcedBehavior.QuestCompleteRequirement.DontCare);
            observe("absent");Check(!owner.IsDone,"valid before-acceptance contract denied");Tick();Check(used.Count==1&&Counter==1,"before-acceptance recipe lost");
        });
        Add("explicit complete-quest recipe retains its completion contract",()=>{
            Set("QuestRequirementComplete",Styx.Logic.Questing.CustomForcedBehavior.QuestCompleteRequirement.Complete);
            observe("complete");Check(!owner.IsDone,"valid complete-quest contract denied");Tick();Check(used.Count==1&&Counter==1,"complete-quest recipe lost");
        });
        Add("another quest completing does not revoke this accepted quest",()=>{
            stage="face";mutation=()=>observe("unrelated");Tick();Check(fired&&used.Count==1&&Counter==1,"unrelated quest stole this recipe lifetime");
        });
        Add("explicit corpse recipe remains valid",()=>{
            Set("NpcState",Script.NpcStateType.Dead);target.IsAlive=false;Tick();Check(used.Count==1&&Counter==1,"corpse recipe denied");
        });
        Add("valid consumed item retains local attempt count without claiming credit",()=>{
            stage="use";mutation=()=>player.CarriedItems!.Clear();Tick();Check(used.Count==1&&Counter==1,"consumed item invalidated accepted invocation");
        });
        Add("repeat after settled local count cannot submit twice",()=>{
            Tick();Tick();Check(used.Count==1&&Counter==1,"settled repetition duplicated use");
        });
        int passed=0,assertions=0,unexpected=0;
        foreach(var test in cases)
        {
            try{test.Item2();passed++;Console.WriteLine("PASS quest item progress: "+test.Item1);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL quest item progress assertion: "+test.Item1+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR quest item progress fixture: "+test.Item1+": "+e);}
        }
        Console.WriteLine($"Quest item progress scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete UseItemOn/TreeSharp and inherited host quest reader; controlled raw quest bytes/world callbacks; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Quest item progress regressions");
    }
    private static void Reset()
    {
        observe("reset");
        typeof(QuestItemSelectionCases).GetMethod("Reset",Hidden)!.Invoke(null,null);
        owner=(Script)typeof(QuestItemSelectionCases).GetField("owner",Hidden)!.GetValue(null)!;
        target=(WoWUnit)typeof(QuestItemSelectionCases).GetField("candidate",Hidden)!.GetValue(null)!;
        player=ObjectManager.Me!;player.IsMoving=true;
        item=new WoWItem{Guid=17,Entry=12345};player.CarriedItems!.Add(item);
        typeof(Script).GetField("_isDisposed",Hidden)!.SetValue(owner,false);
        GC.SuppressFinalize(owner);Set("MobAuraMissingName","Forbidden");Set("WaitTime",101);
        Set("QuestId",867);
        Set("QuestRequirementInLog",Styx.Logic.Questing.CustomForcedBehavior.QuestInLogRequirement.InLog);
        Set("QuestRequirementComplete",Styx.Logic.Questing.CustomForcedBehavior.QuestCompleteRequirement.NotComplete);
        mutation=null;stage="";fired=false;Clears=0;used.Clear();
        if(owner.IsDone)throw new InvalidOperationException("Real quest reader must observe accepted incomplete867 before each test");
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
    private static void Check(bool value,string message){if(!value)throw new Failure(message);}
}
""";
}

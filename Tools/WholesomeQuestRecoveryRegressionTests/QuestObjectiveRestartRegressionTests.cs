using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Styx.Logic.Questing;

// Complete tracked owners, real CustomForcedBehavior/TreeSharp/Quest.GetData.
// Only allocated raw quest bytes, cached requirements and external world/UI
// observations are controlled. No game, native dispatch or new recipe is used.
internal static class QuestObjectiveRestartRegressionTests
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    [ModuleInitializer]
    internal static void Run()
    {
        string root = Root();
        string boundary = (string)typeof(QuestItemTargetSelectionRegressionTests)
            .GetField("Boundary", Hidden)!.GetRawConstantValue()!;
        void Replace(string before, string after)
        {
            if (boundary.Split(new[] { before }, StringSplitOptions.None).Length != 2)
                throw new InvalidOperationException("Controlled boundary shape changed: " + before);
            boundary = boundary.Replace(before, after);
        }
        Replace("public static void SleepForLagDuration(){}", "public static bool IsInGame=>true;public static void SleepForLagDuration(){}");
        Replace("public bool TryUseContainerItem()=>true;", "public bool TryUseContainerItem(){ObjectiveRestartCases.Uses++;return true;}");
        Replace("public bool IsAlive{get;set;}=true;", "public bool CanSelect=>true;public void Interact(){ObjectiveRestartCases.Interactions++;}public bool IsAlive{get;set;}=true;");
        Replace("public Styx.Logic.Questing.PlayerQuest? GetQuestById(uint id)=>null;", "public Styx.Logic.Questing.PlayerQuest? GetQuestById(uint id)=>ObjectiveRestartCases.FindQuest(id);");
        Replace("public static void Face(ulong id){}", "public static void Face(ulong id){var f=ObjectiveRestartCases.OnFace;ObjectiveRestartCases.OnFace=null;f?.Invoke();}");

        string temp = Path.Combine(Path.GetTempPath(), "cb-objective-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool priorLogging = Styx.Helpers.Logging.FileLogging;
        IDisposable? fixture = null;
        PlayerQuest? observedQuest = null;
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            File.Copy(Path.Combine(root, "runtime-snapshot", "Quest Behaviors", "UseItemOn.cs"), Path.Combine(temp, "UseItemOn.cs"));
            File.Copy(Path.Combine(root, "runtime-snapshot", "Quest Behaviors", "GossipEvent.cs"), Path.Combine(temp, "GossipEvent.cs"));
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), boundary + Cases);
            Type compilerType = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(compilerType, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                compilerType.GetMethod("AddReference", Hidden)!.Invoke(compiler, new object[] { path });
            var result = (CompilerResults)compilerType.GetMethod("Compile", Hidden)!.Invoke(compiler, null)!;
            string[] errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).Select(e => e.ToString()).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Actual quest owners did not compile: " + string.Join(";", errors));
            Assembly assembly = (Assembly)compilerType.GetProperty("CompiledAssembly", Hidden)!.GetValue(compiler)!;

            void Configure(int index, int current, int required, string mode)
            {
                fixture?.Dispose();
                fixture = null;
                observedQuest = null;
                fixture = (IDisposable)Activator.CreateInstance(typeof(QuestPublicationRegressionTests)
                    .GetNestedType("Fixture", BindingFlags.NonPublic)!, true)!;
                uint descriptor = (uint)fixture.GetType().GetField("descriptor", Hidden)!.GetValue(fixture)!;
                var cache = (ThreadLocal<Dictionary<IntPtr, byte[]>>)fixture.GetType().GetField("cache", Hidden)!.GetValue(fixture)!;
                observedQuest = Styx.StyxWoW.Me.QuestLog.GetQuestById(867U)
                    ?? throw new InvalidOperationException("Actual fixture quest867 is unavailable");
                object metadata = typeof(Quest).GetProperty("InternalInfo")!.GetValue(observedQuest)!;
                int[] ids = new int[4], amounts = new int[4];
                ids[index] = mode == "zero-id" ? 0 : 70001;
                amounts[index] = required;
                metadata.GetType().GetField("ObjectiveId")!.SetValue(metadata, mode == "short-meta" ? Array.Empty<int>() : ids);
                metadata.GetType().GetField("ObjectiveRequiredCount")!.SetValue(metadata, mode == "short-meta" ? Array.Empty<int>() : amounts);
                typeof(Quest).GetProperty("InternalInfo")!.SetValue(observedQuest, metadata);
                ushort[] counters = new ushort[4];
                counters[index] = checked((ushort)current);
                uint flags = mode == "failed" ? Convert.ToUInt32(Enum.Parse(typeof(Styx.StyxWoW).Assembly.GetTypes()
                    .Single(t => t.IsEnum && t.Name == "WoWDescriptorQuestFlags"), "Failed")) : 0U;
                void Write(uint offset, uint value) => Marshal.WriteInt32(new IntPtr(unchecked((int)(descriptor + offset))), unchecked((int)value));
                Write(632, mode == "missing-log" ? 0U : 867U);
                Write(636, flags);
                Write(640, (uint)counters[0] | ((uint)counters[1] << 16));
                Write(644, (uint)counters[2] | ((uint)counters[3] << 16));
                foreach (IntPtr key in cache.Value!.Keys.Where(k => unchecked((uint)k.ToInt32()) >= descriptor + 632U
                    && unchecked((uint)k.ToInt32()) < descriptor + 1132U).ToArray()) cache.Value.Remove(key);
                if (mode != "missing-log")
                {
                    if (!observedQuest.GetData(out QuestDescriptorData data) || data.Id != 867U || data.ObjectivesDone[index] != current)
                        throw new InvalidOperationException("Fixture did not reach actual packed quest counter reader");
                }
                if (mode == "missing-meta") observedQuest = null;
            }
            Func<uint, PlayerQuest?> find = id => id == 867U ? observedQuest : null;
            try
            {
                assembly.GetType("ObjectiveRestartCases", true)!.GetMethod("Run")!.Invoke(null,
                    new object[] { find, (Action<int, int, int, string>)Configure });
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally
        {
            fixture?.Dispose();
            Styx.Helpers.Logging.FileLogging = priorLogging;
            Directory.Delete(temp, true);
        }
    }

    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) return d.FullName;
        throw new InvalidOperationException("Tracked checkout required");
    }

    private const string Cases = """

public static class ObjectiveRestartCases
{
    private const BindingFlags Hidden=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private sealed class Failure(string message):Exception(message){}
    private static System.Action<int,int,int,string> configure=null!;
    public static System.Func<uint,Styx.Logic.Questing.PlayerQuest?> FindQuest=null!;
    public static System.Action? OnFace,OnMenu;
    public static int Uses,Interactions,Selections;
    private static object owner=null!;
    private static Type kind=null!;
    private static LocalPlayer player=null!;
    public static void Run(System.Func<uint,Styx.Logic.Questing.PlayerQuest?> find,System.Action<int,int,int,string> config)
    {
        FindQuest=find;configure=config;
        var tests=new List<(string Name,System.Action Test)>();
        foreach(string name in new[]{"UseItemOn","GossipEvent"})
        {
            string mode=name;
            for(int i=0;i<4;i++)
            {
                int slot=i;
                tests.Add((mode+": already-complete raw slot"+slot+" survives fresh OnStart",()=>{
                    Reset(mode,slot,4,4,"valid");Start();
                    Check((int?)kind.GetProperty("InitialObjectiveCount")!.GetValue(owner)==4,"OnStart did not capture the actual counter");
                    Check(Acknowledged(),"completed selected objective requires an impossible further increase after restart");
                }));
            }
            tests.Add((mode+": partial objective remains active",()=>{Reset(mode,0,2,4,"valid");Start();Check(!Acknowledged(),"partial objective became complete");}));
            tests.Add((mode+": genuine progress below total retains one-step acknowledgement",()=>{
                Reset(mode,0,2,4,"valid");Start();configure(0,3,4,"valid");Check(Acknowledged(),"existing progress acknowledgement was lost");
            }));
            tests.Add((mode+": restarted/reaccepted zero count is not permanently suppressed",()=>{
                Reset(mode,0,4,4,"valid");Start();Check(Acknowledged(),"completed objective was not observed");
                Reset(mode,0,0,4,"valid");Start();Check(!Acknowledged(),"new quest lifetime inherited completion");
            }));
            foreach(string observation in new[]{"zero-id","short-meta","missing-meta","missing-log","failed"})
            {
                string state=observation;
                tests.Add((mode+": "+state+" cannot establish already-complete credit",()=>{
                    Reset(mode,0,4,4,state);Start();Check(!Acknowledged(),"unknown or failed observation became completion");
                }));
            }
            tests.Add((mode+": zero required count is unknown rather than fulfilled",()=>{Reset(mode,0,4,0,"valid");Start();Check(!Acknowledged(),"zero requirement became completion");}));
            tests.Add((mode+": negative required count is not completion",()=>{Reset(mode,0,4,-1,"valid");Start();Check(!Acknowledged(),"invalid requirement became completion");}));
            tests.Add((mode+": QuestComplete mode does not borrow one finished counter",()=>{
                Reset(mode,0,4,4,"valid");Set("SuccessEvidence",Enum.Parse(kind.GetNestedType("SuccessEvidenceType")!,"QuestComplete"));Start();
                Check(!Acknowledged(),"one objective replaced the explicit whole-quest contract");
            }));
            tests.Add((mode+": completed objective tick makes no item or NPC request",()=>{
                Reset(mode,0,4,4,"valid");Start();Tick();Check(Uses==0&&Interactions==0&&Selections==0,"already-complete objective still dispatched");
            }));
        }
        tests.Add(("UseItemOn: legacy invocation mode is unchanged",()=>{
            Reset("UseItemOn",0,4,4,"valid");Set("SuccessEvidence",Script.SuccessEvidenceType.InvocationCount);Start();
            Check(!Acknowledged(),"legacy explicit invocation was converted to normal-objective completion");
        }));
        tests.Add(("UseItemOn: completion while facing prevents item submission",()=>{
            Reset("UseItemOn",0,0,4,"valid");Start();OnFace=()=>configure(0,4,4,"valid");Tick();
            Check(OnFace==null,"controlled facing boundary was not reached");Check(Uses==0,"item request crossed observed objective completion");
        }));
        tests.Add(("UseItemOn: unchanged facing boundary retains valid item use",()=>{
            Reset("UseItemOn",0,0,4,"valid");Start();OnFace=()=>{};Tick();Check(Uses==1,"valid item request was lost");
        }));
        tests.Add(("GossipEvent: completion during menu observation prevents selection",()=>{
            Reset("GossipEvent",0,0,4,"valid");Start();OpenGossip();OnMenu=()=>configure(0,4,4,"valid");Tick();
            Check(OnMenu==null,"controlled menu boundary was not reached");Check(Selections==0,"gossip selection crossed observed objective completion");
        }));
        tests.Add(("GossipEvent: unchanged menu remains usable",()=>{
            Reset("GossipEvent",0,0,4,"valid");Start();OpenGossip();OnMenu=()=>{};Tick();Check(Selections==1,"valid gossip option was lost");
        }));
        int pass=0,assertions=0,unexpected=0;
        foreach(var test in tests)
        {
            try{test.Test();pass++;Console.WriteLine("PASS quest objective restart: "+test.Name);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL quest objective restart: "+test.Name+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR quest objective restart: "+test.Name+": "+e);}
        }
        Console.WriteLine($"Quest objective restart scenarios: {pass}/{tests.Count}; assertions={assertions}; unexpected={unexpected}; complete tracked owners, real packed quest reader and OnStart/ticks; controlled metadata/world/UI; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Quest objective restart regression");
    }
    private static void Reset(string mode,int slot,int current,int required,string state)
    {
        configure(slot,current,required,state);
        typeof(QuestItemSelectionCases).GetMethod("Reset",Hidden)!.Invoke(null,null);
        player=ObjectManager.Me!;player.IsMoving=false;player.CurrentTarget=ObjectManager.Objects!.OfType<WoWUnit>().Single();
        player.CarriedItems!.Add(new WoWItem{Guid=17,Entry=12345});
        kind=mode=="UseItemOn"?typeof(Script):typeof(Styx.Bot.Quest_Behaviors.GossipEvent.GossipEvent);
        var args=new Dictionary<string,string>
        {
            ["QuestId"]="867",["ObjectiveIndex"]=slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SuccessEvidence"]="ObjectiveProgress",["MobId"]="70001",["CollectionDistance"]="100",
            ["Range"]="4",["MaxAttempts"]="3",["AcknowledgementTimeout"]="5000",
            ["X"]="10",["Y"]="10",["Z"]="10"
        };
        if(mode=="UseItemOn")args["ItemId"]="12345";
        else args["GossipOptionIndex"]="0";
        owner=Activator.CreateInstance(kind,new object[]{args})!;
        GC.SuppressFinalize(owner);
        if(((Styx.Logic.Questing.CustomForcedBehavior)owner).IsAttributeProblem)
            throw new InvalidOperationException("Controlled restart constructor rejected its explicit arguments");
        Set("QuestId",867);Set("ObjectiveIndex",slot);Set("MaxAttempts",3);Set("AcknowledgementTimeout",5000);
        Set("SuccessEvidence",Enum.Parse(kind.GetNestedType("SuccessEvidenceType")!,"ObjectiveProgress"));
        Set("QuestRequirementInLog",Styx.Logic.Questing.CustomForcedBehavior.QuestInLogRequirement.InLog);
        Set("QuestRequirementComplete",Styx.Logic.Questing.CustomForcedBehavior.QuestCompleteRequirement.NotComplete);
        if(mode=="UseItemOn"){Set("WaitTime",100);Set("SubmissionRefusalTimeout",5000);}
        else
        {
            Set("Location",player.Location);Set("MobIds",new[]{70001});Set("CollectionDistance",100d);Set("Range",4d);
            Set("GossipOptionIndex",0);Set("GossipOpenTimeout",3000);Set("TargetWaitTimeout",30000);Set("NavigationTimeout",120000);
        }
        Uses=Interactions=Selections=0;OnFace=OnMenu=null;
        Styx.Logic.Inventory.Frames.Gossip.GossipFrame.Instance.IsVisible=false;
    }
    private static void Set(string name,object value)=>kind.GetProperty(name,Hidden)!.SetValue(owner,value);
    private static object? Invoke(string name)
    {
        try{return kind.GetMethod(name,Hidden)!.Invoke(owner,null);}
        catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }
    private static void Start()=>Invoke("OnStart");
    private static bool Acknowledged()=>(bool)Invoke("HasAuthoritativeSuccess")!;
    private static void OpenGossip()
    {
        Styx.Logic.Inventory.Frames.Gossip.GossipFrame.Instance.IsVisible=true;
        kind.GetField("_gossipOpenStartedUtc",Hidden)!.SetValue(owner,DateTime.UtcNow.Ticks/TimeSpan.TicksPerMillisecond);
        kind.GetField("_interactionGuid",Hidden)!.SetValue(owner,2UL);
    }
    private static void Tick()
    {
        var errors=new List<string>();
        void Log(Styx.Helpers.LogLevel level,string message){if(message.Contains("Exception")||message.Contains("Object reference not set"))errors.Add(message);}
        Styx.Helpers.Logging.OnMessageLogged+=Log;
        try
        {
            Composite tree=(Composite)Invoke("CreateBehavior")!;
            tree.Start(null!);try{tree.Tick(null!);}finally{tree.Stop(null!);}
            Check(errors.Count==0,"swallowed owner exception: "+string.Join(";",errors));
        }
        finally{Styx.Helpers.Logging.OnMessageLogged-=Log;}
    }
    private static void Check(bool value,string message){if(!value)throw new Failure(message);}
}
/* Controlled external UI, not an owner implementation. */ namespace Styx.Logic.Inventory.Frames.Gossip
{
    public sealed class GossipFrame
    {
        public static GossipFrame Instance{get;}=new();public bool IsVisible{get;set;}
        public List<int> GossipOptionEntries{get{var f=ObjectiveRestartCases.OnMenu;ObjectiveRestartCases.OnMenu=null;f?.Invoke();return new(){1};}}
        public void SelectGossipOption(int index){ObjectiveRestartCases.Selections++;}
        public void Close(){IsVisible=false;}
    }
}
/* Only the existing exact-NPC query is controlled. */ namespace Styx.WoWInternals
{
    public static class Lua
    {
        public static T GetReturnVal<T>(string script,uint index)
        {
            if(script=="return UnitGUID('npc') == '0x0000000000000002'"&&index==0&&typeof(T)==typeof(bool))return (T)(object)true;
            throw new InvalidOperationException("Unexpected native Lua request in offline fixture: "+script);
        }
    }
}
""";
}

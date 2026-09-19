using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Styx.Logic.Pathing;
using WholesomeAQ;

// Source-bound GossipEvent contract. The complete tracked behavior is compiled
// against the host but never instantiated, so no game/NPC/gossip dispatch occurs.
internal static class GossipEventStrategyRegressionTests
{
    private sealed class Failure(string message) : Exception(message) { }
    private const BindingFlags Hidden =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Assembly? behaviorAssembly;
    private static Type? behaviorType;

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>
        {
            ("dedicated GossipEvent behavior source exists and compiles", () =>
            {
                Type type = Behavior();
                Check(type.FullName == "Styx.Bot.Quest_Behaviors.GossipEvent.GossipEvent",
                    "compiled owner identity changed");
            }),
            ("objective progress requires an observed increase", () =>
            {
                Type type=Behavior();
                Check(Ack(type,"ObjectiveProgress",2,3,false), "count increase was not acknowledged");
                Check(!Ack(type,"ObjectiveProgress",2,2,false), "unchanged count became acknowledgement");
                Check(!Ack(type,"ObjectiveProgress",2,null,false), "unknown count became acknowledgement");
            }),
            ("quest completion is the only QuestComplete acknowledgement", () =>
            {
                Type type=Behavior();
                Check(Ack(type,"QuestComplete",0,null,true), "explicit completion was ignored");
                Check(!Ack(type,"QuestComplete",0,99,false), "objective count became quest completion");
            }),
            ("acknowledgement wait is finite and monotonic", () =>
            {
                Type type=Behavior();
                MethodInfo m=Require(type,"IsAcknowledgementPending");
                Check((bool)m.Invoke(null,new object[]{1500L,1000L,1000})!, "active window was not pending");
                Check(!(bool)m.Invoke(null,new object[]{2000L,1000L,1000})!, "deadline remained pending");
                Check(!(bool)m.Invoke(null,new object[]{900L,1000L,1000})!, "clock reversal became pending");
                Check(!(bool)m.Invoke(null,new object[]{1500L,-1L,1000})!, "missing submission became pending");
            }),
            ("gossip option index is zero based and bounded by observed menu", () =>
            {
                Type type=Behavior();
                MethodInfo m=Require(type,"CanSelectGossipOption");
                Check((bool)m.Invoke(null,new object[]{0,1})!, "first zero-based option was rejected");
                Check((bool)m.Invoke(null,new object[]{1,2})!, "second zero-based option was rejected");
                Check(!(bool)m.Invoke(null,new object[]{2,2})!, "index equal to count was admitted");
                Check(!(bool)m.Invoke(null,new object[]{-1,2})!, "negative option was admitted");
            }),
            ("target discovery remains bounded to the source hotspot anchor", () =>
            {
                Type type=Behavior();
                MethodInfo m=Require(type,"IsWithinSourceAnchor");
                var anchor=new WoWPoint(10,20,30);
                Check((bool)m.Invoke(null,new object[]{new WoWPoint(12,20,30),anchor,5d})!,
                    "near source-bound target was rejected");
                Check(!(bool)m.Invoke(null,new object[]{new WoWPoint(30,20,30),anchor,5d})!,
                    "same-entry NPC outside source anchor was admitted");
                Check(!(bool)m.Invoke(null,new object[]{anchor,anchor,double.NaN})!,
                    "invalid collection radius was admitted");
            }),
            ("submission timestamp follows exact gossip selection", () =>
            {
                string source=BehaviorSource();
                int select=source.IndexOf("SelectGossipOption(GossipOptionIndex)",StringComparison.Ordinal);
                int stamp=source.IndexOf("_lastSubmissionUtc = UtcNowMilliseconds()",select,StringComparison.Ordinal);
                Check(select>=0 && stamp>select,
                    "authoritative acknowledgement window starts before exact gossip submission");
                Check(!source.Contains("SelectGossipOption(0)",StringComparison.Ordinal),
                    "behavior contains a first-option fallback");
            }),
            ("open gossip menu is fenced to the exact interacted NPC", () =>
            {
                string source=BehaviorSource();
                int identity=source.IndexOf("IsCurrentGossipNpc(_interactionGuid)",StringComparison.Ordinal);
                int select=source.IndexOf("SelectGossipOption(GossipOptionIndex)",StringComparison.Ordinal);
                Check(identity>=0 && select>identity
                    && source.Contains("UnitGUID('npc')",StringComparison.Ordinal),
                    "gossip option can be submitted without exact NPC-frame identity");
            }),
            ("approach navigation is bounded and fail-closed", () =>
            {
                string source=BehaviorSource();
                Check(source.Contains("NavigationTimeout",StringComparison.Ordinal)
                    && source.Contains("MoveResult.PathGenerationFailed",StringComparison.Ordinal)
                    && source.Contains("bounded navigation window",StringComparison.Ordinal),
                    "visible/unreachable gossip target can own navigation indefinitely");
            }),
            ("bounded attempt exhaustion defers rather than becoming quest success", () =>
            {
                string source=BehaviorSource();
                Check(source.Contains("Counter >= MaxAttempts",StringComparison.Ordinal)
                    && source.Contains("DeferAuthoritativeAttempt",StringComparison.Ordinal),
                    "bounded retry/defer contract is missing");
                Check(!source.Contains("Counter >= MaxAttempts,\n                    new Action(ret => _isBehaviorDone = true)",StringComparison.Ordinal),
                    "local attempt count is still a success condition");
            }),
            ("bound GossipEvent recipe owns the objective and emits exact facts", () =>
            {
                var s=Scenario(QuestStrategyKind.GossipEvent);
                string xml=Build(s.Builder,s.Plan,s.Database,s.Pack);
                Check(xml.Contains("File=\"GossipEvent\"",StringComparison.Ordinal)
                    && xml.Contains("QuestId=\"2118\"",StringComparison.Ordinal)
                    && xml.Contains("ObjectiveIndex=\"0\"",StringComparison.Ordinal)
                    && xml.Contains("MobId=\"2164\"",StringComparison.Ordinal)
                    && xml.Contains("GossipOptionIndex=\"1\"",StringComparison.Ordinal)
                    && xml.Contains("Range=\"5\"",StringComparison.Ordinal)
                    && xml.Contains("RequireLos=\"true\"",StringComparison.Ordinal)
                    && xml.Contains("MaxAttempts=\"3\"",StringComparison.Ordinal)
                    && xml.Contains("SuccessEvidence=\"ObjectiveProgress\"",StringComparison.Ordinal),
                    "generated GossipEvent lost source-bound action facts");
                Check(!xml.Contains("Type=\"KillMob\"",StringComparison.Ordinal),
                    "generic objective remained executable beside GossipEvent owner");
            }),
            ("generated GossipEvent carries bounded wait policies", () =>
            {
                var s=Scenario(QuestStrategyKind.GossipEvent);
                string xml=Build(s.Builder,s.Plan,s.Database,s.Pack);
                Check(xml.Contains("AcknowledgementTimeout=\"5000\"",StringComparison.Ordinal)
                    && xml.Contains("GossipOpenTimeout=\"3000\"",StringComparison.Ordinal)
                    && xml.Contains("TargetWaitTimeout=\"30000\"",StringComparison.Ordinal)
                    && xml.Contains("NavigationTimeout=\"120000\"",StringComparison.Ordinal)
                    && xml.Contains("WaitForNpcs=\"true\"",StringComparison.Ordinal),
                    "generated GossipEvent omitted bounded liveness policy");
            }),
            ("GossipEvent refuses a hotspot derived from a different objective target", () =>
            {
                var s=Scenario(QuestStrategyKind.GossipEvent, objectiveMobId:9999, targetId:2164);
                Throws<InvalidDataException>(()=>Build(s.Builder,s.Plan,s.Database,s.Pack),
                    "unrelated objective hotspot became authority for the gossip target");
            }),
            ("Escort remains non-executable until its recipe carries start and completion semantics", () =>
            {
                var s=Scenario(QuestStrategyKind.Escort);
                string legacy=s.Builder.BuildProfileXml(s.Plan,s.Database,"zone","player",20,null);
                string wired=Build(s.Builder,s.Plan,s.Database,s.Pack);
                Check(wired==legacy && !wired.Contains("File=\"Escort\"",StringComparison.Ordinal),
                    "Escort became executable from the still-incomplete recipe schema");
            })
        };

        int pass=0, assertions=0, unexpected=0;
        foreach(var c in cases)
        {
            try{c.Test();pass++;Console.WriteLine("PASS gossip strategy: "+c.Name);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL gossip strategy assertion: "+c.Name+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR gossip strategy fixture: "+c.Name+": "+e);}
        }
        Console.WriteLine($"Gossip strategy scenarios: {pass}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete tracked behavior compile plus controlled profile XML; no NPC/gossip/game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Gossip strategy regressions");
    }

    private static bool Ack(Type type,string evidence,int baseline,int? current,bool complete)
    {
        MethodInfo m=Require(type,"IsAuthoritativeAcknowledged");
        Type e=m.GetParameters()[0].ParameterType;
        object value=Enum.Parse(e,evidence);
        return (bool)m.Invoke(null,new object?[]{value,baseline,current,complete})!;
    }

    private static MethodInfo Require(Type type,string name) =>
        type.GetMethod(name,Hidden) ?? throw new Failure(type.Name+"."+name+" is missing");

    private static Type Behavior()
    {
        if(behaviorType!=null)return behaviorType;
        string source=BehaviorSource();
        string root=Root();
        string temp=Path.Combine(Path.GetTempPath(),"cb-gossip-strategy-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Combine(temp,"GossipEvent.cs"),source);
            Type compilerType=typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler",true)!;
            object compiler=Activator.CreateInstance(compilerType,new object[]{temp})!;
            foreach(string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                compilerType.GetMethod("AddReference",Hidden)!.Invoke(compiler,new object[]{path});
            var result=(CompilerResults)compilerType.GetMethod("Compile",Hidden)!.Invoke(compiler,null)!;
            var errors=result.Errors.Cast<CompilerError>().Where(e=>!e.IsWarning).ToArray();
            if(errors.Length!=0)throw new Failure("GossipEvent source failed compilation: "+string.Join(";",errors.Select(e=>e.ToString())));
            behaviorAssembly=(Assembly)compilerType.GetProperty("CompiledAssembly",Hidden)!.GetValue(compiler)!;
            behaviorType=behaviorAssembly.GetType("Styx.Bot.Quest_Behaviors.GossipEvent.GossipEvent",false)
                ?? throw new Failure("compiled GossipEvent owner is missing");
            return behaviorType;
        }
        finally { try{Directory.Delete(temp,true);}catch{} }
    }

    private static string BehaviorSource()
    {
        string path=Path.Combine(Root(),"runtime-snapshot","Quest Behaviors","GossipEvent.cs");
        if(!File.Exists(path))throw new Failure("dedicated GossipEvent.cs is missing");
        return File.ReadAllText(path);
    }

    private sealed record StrategyScenario(
        ProfileBuilder Builder,List<QuestPlanEntry> Plan,QuestDatabase Database,QuestStrategyPack Pack);

    private static StrategyScenario Scenario(
        QuestStrategyKind kind,
        int objectiveMobId=2164,
        int targetId=2164)
    {
        var quest=new QuestEntry{
            Id=2118,Name="Controlled",
            Objectives=new List<QuestObjective>{new QuestObjective{Index=0,Type=ObjectiveType.KillMob,MobId=objectiveMobId,KillCount=1}}
        };
        var plan=new List<QuestPlanEntry>{new QuestPlanEntry{
            Quest=quest,Stage=QuestWorkStage.Objective,ObjectiveIndex=0,
            Hotspots=new[]{new SpawnPoint{Map=1,X=10,Y=20,Z=30}}
        }};
        var db=new QuestDatabase{Quests=new List<QuestEntry>{quest}};
        var recipe=new QuestStrategyRecipe{
            QuestId=2118,ObjectiveIndex=0,Kind=kind,SourceRef="controlled://gossip/2118/0",
            TargetType=QuestStrategyTargetType.Creature,TargetId=targetId,
            Range=5,RequireLos=true,MaxAttempts=3,GossipOptionIndex=1,
            SuccessEvidence=QuestStrategySuccessEvidence.ObjectiveProgress
        };
        var pack=new QuestStrategyPack{
            Status=QuestStrategyPackStatus.DeclaredAndBound,ClientBuild=12340,
            QuestDataSha256=new string('a',64),SourceKind="curated-profile",SourceRevision="controlled",
            Recipes=new List<QuestStrategyRecipe>{recipe}
        };
        return new StrategyScenario(new ProfileBuilder(),plan,db,pack);
    }

    private static string Build(ProfileBuilder builder,IReadOnlyList<QuestPlanEntry> plan,QuestDatabase db,QuestStrategyPack pack)
    {
        MethodInfo? method=typeof(ProfileBuilder).GetMethods(BindingFlags.Public|BindingFlags.Instance)
            .SingleOrDefault(m=>m.Name=="BuildProfileXml"&&m.GetParameters().Length==7);
        if(method==null)throw new Failure("strategy-aware BuildProfileXml overload is missing");
        try{return (string)(method.Invoke(builder,new object?[]{plan,db,"zone","player",20,null,pack})
            ?? throw new Failure("strategy-aware profile builder returned null"));}
        catch(TargetInvocationException e)when(e.InnerException!=null)
        {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }

    private static string Root()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj")))return d.FullName;
        throw new Failure("tracked checkout required");
    }

    private static void Throws<T>(Action action,string why) where T:Exception
    {
        try{action();}catch(T){return;}
        throw new Failure(why);
    }

    private static void Check(bool ok,string why){if(!ok)throw new Failure(why);}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using WholesomeAQ;

// Runtime wiring contract for source-bound UseItemOn strategies.
// Controlled quest-data/strategy files and XML only; no profile is loaded and no game/server is attached.
internal static class QuestStrategyExecutionRegressionTests
{
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>
        {
            ("missing strategy pack preserves legacy execution identity", () =>
            {
                using var fixture = new LoaderFixture(includePack:false);
                fixture.Loader.Load();
                Check(ReadString(fixture.Loader, "ExecutionFingerprint") == fixture.Loader.DatasetFingerprint,
                    "missing pack changed legacy execution identity");
                Check(ReadPackStatus(fixture.Loader) == "Missing", "missing pack was not retained explicitly");
            }),
            ("bound pack contributes exact bytes to execution identity without changing dataset identity", () =>
            {
                using var fixture = new LoaderFixture(includePack:true);
                fixture.Loader.Load();
                string dataset = fixture.Loader.DatasetFingerprint;
                string execution = ReadString(fixture.Loader, "ExecutionFingerprint");
                Check(dataset != "unknown" && execution != "unknown" && execution != dataset,
                    "bound strategy bytes did not enter runtime identity");
                string before = dataset;
                File.WriteAllText(fixture.StrategyPath, fixture.CreatePack("controlled-revision-2"), Encoding.UTF8);
                var second = new DataLoader(fixture.DataPath);
                second.Load();
                Check(second.DatasetFingerprint == before,
                    "strategy bytes changed the legacy dataset fingerprint");
                Check(ReadString(second, "ExecutionFingerprint") != execution,
                    "changed strategy bytes did not change execution identity");
            }),
            ("invalid bound pack fails closed before database publication and can be retried", () =>
            {
                using var fixture = new LoaderFixture(includePack:false);
                File.WriteAllText(fixture.StrategyPath,
                    fixture.CreatePack("bad", questSha:new string('0',64)), Encoding.UTF8);
                Throws<InvalidDataException>(() => fixture.Loader.Load(),
                    "mismatched strategy pack was accepted");
                File.WriteAllText(fixture.StrategyPath, fixture.CreatePack("repaired"), Encoding.UTF8);
                Check(fixture.Loader.Load() != null, "loader did not retry after repaired strategy pack");
                Check(ReadPackStatus(fixture.Loader) == "DeclaredAndBound",
                    "repaired pack did not become the published strategy owner");
            }),
            ("UseItemOn recipe replaces generic objective order with authoritative custom behavior", () =>
            {
                var scenario = Scenario(QuestStrategyKind.UseItemOn, QuestStrategyTargetType.Creature);
                string xml = BuildWithStrategies(scenario.Builder, scenario.Plan, scenario.Database, scenario.Pack);
                Check(xml.Contains("File=\"UseItemOn\"", StringComparison.Ordinal),
                    "bound UseItemOn recipe did not emit the custom behavior");
                Check(!xml.Contains("Type=\"KillMob\"", StringComparison.Ordinal),
                    "generic kill objective remained executable beside owned UseItemOn recipe");
                Check(xml.Contains("QuestId=\"2118\"", StringComparison.Ordinal)
                    && xml.Contains("ObjectiveIndex=\"0\"", StringComparison.Ordinal)
                    && xml.Contains("ItemId=\"7586\"", StringComparison.Ordinal)
                    && xml.Contains("MobId=\"2164\"", StringComparison.Ordinal)
                    && xml.Contains("SuccessEvidence=\"ObjectiveProgress\"", StringComparison.Ordinal)
                    && xml.Contains("MaxAttempts=\"3\"", StringComparison.Ordinal),
                    "generated behavior lost authoritative recipe identity");
            }),
            ("generated UseItemOn preserves explicit LOS range state and source target type", () =>
            {
                var scenario = Scenario(QuestStrategyKind.UseItemOn, QuestStrategyTargetType.GameObject);
                string xml = BuildWithStrategies(scenario.Builder, scenario.Plan, scenario.Database, scenario.Pack);
                Check(xml.Contains("MobType=\"GameObject\"", StringComparison.Ordinal)
                    && xml.Contains("MobState=\"Alive\"", StringComparison.Ordinal)
                    && xml.Contains("Range=\"5\"", StringComparison.Ordinal)
                    && xml.Contains("RequireLos=\"true\"", StringComparison.Ordinal)
                    && xml.Contains("WaitForNpcs=\"true\"", StringComparison.Ordinal),
                    "generated behavior discarded explicit target/range/LOS waiting contract");
                Check(xml.Contains("X=\"10\"", StringComparison.Ordinal)
                    && xml.Contains("Y=\"20\"", StringComparison.Ordinal)
                    && xml.Contains("Z=\"30\"", StringComparison.Ordinal),
                    "generated behavior lost the scheduler-owned search anchor");
            }),
            ("missing strategy pack keeps legacy profile output byte-for-byte", () =>
            {
                var scenario = Scenario(QuestStrategyKind.UseItemOn, QuestStrategyTargetType.Creature);
                string legacy = scenario.Builder.BuildProfileXml(
                    scenario.Plan, scenario.Database, "zone", "player", 20, null);
                var missing = new QuestStrategyPack();
                string wired = BuildWithStrategies(scenario.Builder, scenario.Plan, scenario.Database, missing);
                Check(wired == legacy, "missing strategy pack changed legacy profile XML");
            }),
            ("Escort remains non-executable until its separate start/completion lifetime exists", () =>
            {
                var scenario = Scenario(QuestStrategyKind.Escort, QuestStrategyTargetType.Creature);
                string legacy = scenario.Builder.BuildProfileXml(
                    scenario.Plan, scenario.Database, "zone", "player", 20, null);
                string wired = BuildWithStrategies(scenario.Builder, scenario.Plan, scenario.Database, scenario.Pack);
                Check(wired == legacy, "Escort became executable before its separate start/completion lifetime exists");
                Check(!wired.Contains("File=\"UseItemOn\"", StringComparison.Ordinal)
                    && !wired.Contains("File=\"GossipEvent\"", StringComparison.Ordinal),
                    "Escort was rewritten as another strategy kind");
            }),
            ("recipe ownership is exact quest and objective", () =>
            {
                var scenario = Scenario(QuestStrategyKind.UseItemOn, QuestStrategyTargetType.Creature);
                scenario.Pack.Recipes[0] = CopyRecipe(scenario.Pack.Recipes[0], questId:9999);
                string legacy = scenario.Builder.BuildProfileXml(
                    scenario.Plan, scenario.Database, "zone", "player", 20, null);
                string wired = BuildWithStrategies(scenario.Builder, scenario.Plan, scenario.Database, scenario.Pack);
                Check(wired == legacy, "recipe for another quest captured this objective");
            }),
            ("generated behavior carries a finite acknowledgement window", () =>
            {
                var scenario = Scenario(QuestStrategyKind.UseItemOn, QuestStrategyTargetType.Creature);
                string xml = BuildWithStrategies(scenario.Builder, scenario.Plan, scenario.Database, scenario.Pack);
                Check(xml.Contains("AcknowledgementTimeout=\"5000\"", StringComparison.Ordinal),
                    "generated authoritative behavior omitted bounded post-submission acknowledgement");
            })
        };

        int passed=0, assertions=0, unexpected=0;
        foreach(var c in cases)
        {
            try { c.Test(); passed++; Console.WriteLine("PASS quest strategy execution: "+c.Name); }
            catch(AssertionFailure e) { assertions++; Console.Error.WriteLine("FAIL quest strategy execution assertion: "+c.Name+": "+e.Message); }
            catch(Exception e) { unexpected++; Console.Error.WriteLine("ERROR quest strategy execution fixture/owner: "+c.Name+": "+e); }
        }
        Console.WriteLine($"Quest strategy execution scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; controlled files/XML; no profile/client/server execution.");
        if(assertions+unexpected!=0) throw new InvalidOperationException("Quest strategy execution regression");
    }

    private sealed class LoaderFixture : IDisposable
    {
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"cb-strategy-exec-"+Guid.NewGuid().ToString("N"));
        internal readonly string DataPath;
        internal readonly string StrategyPath;
        internal readonly string DataText;
        internal readonly DataLoader Loader;

        internal LoaderFixture(bool includePack)
        {
            Directory.CreateDirectory(Root);
            DataPath=Path.Combine(Root,"quest_data.json");
            StrategyPath=Path.Combine(Root,"quest_strategies.json");
            DataText="{\"Quests\":[],\"QuestGivers\":[],\"QuestEnders\":[],\"CreatureSpawns\":{},\"GameObjectSpawns\":{}}";
            File.WriteAllText(DataPath,DataText,Encoding.UTF8);
            if(includePack) File.WriteAllText(StrategyPath,CreatePack("controlled-revision"),Encoding.UTF8);
            Loader=new DataLoader(DataPath);
        }

        internal string CreatePack(string revision,string? questSha=null)
        {
            string sha=questSha ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(DataPath))).ToLowerInvariant();
            return "{"+
                "\"Schema\":\"quest-strategy-pack-335-v1\","+
                "\"ClientBuild\":12340,"+
                "\"QuestDataSha256\":\""+sha+"\","+
                "\"SourceKind\":\"curated-profile\","+
                "\"SourceRevision\":\""+revision+"\","+
                "\"Recipes\":[]}";
        }

        public void Dispose(){Directory.Delete(Root,true);}
    }

    private sealed record StrategyScenario(
        ProfileBuilder Builder,
        List<QuestPlanEntry> Plan,
        QuestDatabase Database,
        QuestStrategyPack Pack);

    private static StrategyScenario Scenario(QuestStrategyKind kind, QuestStrategyTargetType targetType)
    {
        var quest=new QuestEntry
        {
            Id=2118,
            Name="Controlled",
            Objectives=new List<QuestObjective>
            {
                new QuestObjective{Index=0,Type=ObjectiveType.KillMob,MobId=2164,KillCount=1}
            }
        };
        var plan=new List<QuestPlanEntry>
        {
            new QuestPlanEntry
            {
                Quest=quest,
                Stage=QuestWorkStage.Objective,
                ObjectiveIndex=0,
                Hotspots=new[]{new SpawnPoint{Map=1,X=10,Y=20,Z=30}}
            }
        };
        var db=new QuestDatabase{Quests=new List<QuestEntry>{quest}};
        var recipe=new QuestStrategyRecipe
        {
            QuestId=2118,
            ObjectiveIndex=0,
            Kind=kind,
            SourceRef="controlled://strategy/2118/0",
            ItemId=7586,
            TargetType=targetType,
            TargetId=2164,
            TargetState=QuestStrategyTargetState.Alive,
            Range=5,
            RequireLos=true,
            MaxAttempts=3,
            GossipOptionIndex=1,
            SuccessEvidence=QuestStrategySuccessEvidence.ObjectiveProgress
        };
        var pack=new QuestStrategyPack
        {
            Status=QuestStrategyPackStatus.DeclaredAndBound,
            ClientBuild=12340,
            QuestDataSha256=new string('a',64),
            SourceKind="curated-profile",
            SourceRevision="controlled",
            Recipes=new List<QuestStrategyRecipe>{recipe}
        };
        return new StrategyScenario(new ProfileBuilder(),plan,db,pack);
    }

    private static QuestStrategyRecipe CopyRecipe(QuestStrategyRecipe value,int? questId=null) =>
        new QuestStrategyRecipe
        {
            QuestId=questId ?? value.QuestId,
            ObjectiveIndex=value.ObjectiveIndex,
            Kind=value.Kind,
            SourceRef=value.SourceRef,
            ItemId=value.ItemId,
            TargetType=value.TargetType,
            TargetId=value.TargetId,
            TargetState=value.TargetState,
            Range=value.Range,
            RequireLos=value.RequireLos,
            MaxAttempts=value.MaxAttempts,
            GossipOptionIndex=value.GossipOptionIndex,
            SuccessEvidence=value.SuccessEvidence
        };

    private static string BuildWithStrategies(
        ProfileBuilder builder,
        IReadOnlyList<QuestPlanEntry> plan,
        QuestDatabase db,
        QuestStrategyPack pack)
    {
        MethodInfo? method=typeof(ProfileBuilder).GetMethods(BindingFlags.Public|BindingFlags.Instance)
            .SingleOrDefault(m=>m.Name=="BuildProfileXml" && m.GetParameters().Length==7);
        if(method==null) throw new AssertionFailure("strategy-aware BuildProfileXml overload is missing");
        try
        {
            return (string)(method.Invoke(builder,new object?[]{plan,db,"zone","player",20,null,pack})
                ?? throw new AssertionFailure("strategy-aware profile builder returned null"));
        }
        catch(TargetInvocationException e) when(e.InnerException!=null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static string ReadString(object owner,string property)
    {
        PropertyInfo? info=owner.GetType().GetProperty(property,BindingFlags.Public|BindingFlags.Instance);
        if(info==null) throw new AssertionFailure(property+" property is missing");
        return Convert.ToString(info.GetValue(owner),System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static string ReadPackStatus(DataLoader loader)
    {
        PropertyInfo? info=loader.GetType().GetProperty("StrategyPack",BindingFlags.Public|BindingFlags.Instance);
        if(info==null) throw new AssertionFailure("StrategyPack property is missing");
        object? pack=info.GetValue(loader);
        if(pack==null) throw new AssertionFailure("StrategyPack is null");
        return Convert.ToString(pack.GetType().GetProperty("Status")?.GetValue(pack)) ?? "";
    }

    private static void Throws<T>(Action action,string reason) where T:Exception
    {
        try { action(); } catch(T) { return; }
        throw new AssertionFailure(reason);
    }

    private static void Check(bool ok,string why)
    {
        if(!ok) throw new AssertionFailure(why);
    }
}

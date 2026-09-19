using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using WholesomeAQ;

// Execute actual generated profile conditions, not a substitute admission policy.
// HasQuest and GetItemCount are controlled observations. No profile is installed,
// no movement/item action is dispatched, and no client or server is attached.
internal static class QuestCollectionReplayRegressionTests
{
    private sealed class Failure(string message) : Exception(message) { }
    private const int QuestId = 900001;
    private const int CollectedItemId = 12345;
    private const int RequiredCount = 3;

    [ModuleInitializer]
    internal static void Run()
    {
        int passed = 0, assertions = 0, unexpected = 0, total = 0;
        void Case(string name, Action test)
        {
            total++;
            try { test(); passed++; Console.WriteLine("PASS quest collection replay: " + name); }
            catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL quest collection replay: " + name + ": " + e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR quest collection replay: " + name + ": " + e); }
        }

        foreach (string mode in new[] { "ordinary", "gameobject", "UseItemOn", "GossipEvent" })
        {
            XElement guard = BuildGuard(mode, RequiredCount);
            using var compiled = new CompiledCondition((string?)guard.Attribute("Condition") ?? "");
            foreach (var state in new[]
            {
                (Name: "no items still needs work", Accepted: true, Count: 0, Expected: true),
                (Name: "partial inventory still needs work", Accepted: true, Count: 2, Expected: true),
                (Name: "required inventory suppresses replay", Accepted: true, Count: 3, Expected: false),
                (Name: "excess inventory suppresses replay", Accepted: true, Count: 4, Expected: false),
                (Name: "abandoned quest cannot enter travel", Accepted: false, Count: 0, Expected: false)
            })
            {
                Case(mode + ": " + state.Name, () =>
                    Check(compiled.Evaluate(state.Accepted, state.Count) == state.Expected,
                        "generated admission disagreed with current acceptance/collected-item observation"));
            }
            Case(mode + ": restart rereads inventory instead of retaining a once-ever flag", () =>
            {
                Check(!compiled.Evaluate(true, RequiredCount), "initial fulfilled collection remained active");
                Check(compiled.Evaluate(true, 0), "subsequent item loss was hidden by stale completion memory");
            });
            Case(mode + ": abandoned then reaccepted quest remains usable", () =>
            {
                Check(!compiled.Evaluate(false, 0), "abandoned quest was admitted");
                Check(compiled.Evaluate(true, 0), "reaccepted quest was permanently suppressed");
            });
            Case(mode + ": collection guard encloses transport and objective together", () =>
            {
                XElement? transport = guard.Elements("If")
                    .SelectMany(e => e.Elements("CustomBehavior"))
                    .SingleOrDefault(e => (string?)e.Attribute("File") == "UseTransport");
                Check(transport != null, "controlled Freewind transport preamble missing");
                Check(guard.Elements().Last().Name == (mode == "ordinary" || mode == "gameobject"
                    ? "Objective" : "CustomBehavior"), "objective no longer follows the transport guard");
                Check(!compiled.Evaluate(true, RequiredCount),
                    "already-collected objective can still enter its transport preamble");
            });
        }

        Case("kill objectives preserve their existing guard contract", () =>
            Check((string?)BuildGuard("kill", 1).Attribute("Condition") == "HasQuest(900001)",
                "inventory-only repair changed kill-objective admission"));
        Case("unknown zero collection count is not invented as an already-complete objective", () =>
            Check((string?)BuildGuard("ordinary", 0).Attribute("Condition") == "HasQuest(900001)",
                "unspecified collection target was treated as authoritative completion"));

        Console.WriteLine($"Quest collection replay scenarios: {passed}/{total}; assertions={assertions}; unexpected={unexpected}; actual generated condition compilation/execution; controlled acceptance and inventory; no game attached.");
        if (assertions + unexpected != 0)
            throw new InvalidOperationException("Quest collection replay regression");
    }

    private static XElement BuildGuard(string mode, int count)
    {
        var objective = new QuestObjective
        {
            Index = 0,
            Type = mode == "kill" ? ObjectiveType.KillMob
                : mode == "gameobject" ? ObjectiveType.CollectFromGameObject : ObjectiveType.CollectItem,
            MobId = 2164,
            GameObjectId = 141931,
            ItemId = CollectedItemId,
            CollectCount = count,
            KillCount = 1
        };
        var quest = new QuestEntry { Id = QuestId, Name = "Controlled collection", Objectives = new List<QuestObjective> { objective } };
        var plan = new List<QuestPlanEntry>
        {
            new QuestPlanEntry
            {
                Quest = quest, Stage = QuestWorkStage.Objective, ObjectiveIndex = 0,
                Hotspots = new[] { new SpawnPoint { Map = 1, X = -5400, Y = -2450, Z = -40 } }
            }
        };
        var database = new QuestDatabase { Quests = new List<QuestEntry> { quest } };
        QuestStrategyPack? pack = null;
        if (mode == "UseItemOn" || mode == "GossipEvent")
        {
            pack = new QuestStrategyPack
            {
                Status = QuestStrategyPackStatus.DeclaredAndBound, ClientBuild = 12340,
                QuestDataSha256 = new string('a', 64), SourceKind = "curated-profile", SourceRevision = "controlled",
                Recipes = new List<QuestStrategyRecipe>
                {
                    new QuestStrategyRecipe
                    {
                        QuestId = QuestId, ObjectiveIndex = 0,
                        Kind = mode == "UseItemOn" ? QuestStrategyKind.UseItemOn : QuestStrategyKind.GossipEvent,
                        SourceRef = "controlled://collection/replay", ItemId = 7586,
                        TargetType = QuestStrategyTargetType.Creature, TargetId = 2164,
                        TargetState = QuestStrategyTargetState.Alive, Range = 5, RequireLos = true,
                        MaxAttempts = 3, GossipOptionIndex = 1,
                        SuccessEvidence = QuestStrategySuccessEvidence.ObjectiveProgress
                    }
                }
            };
        }
        string xml = new ProfileBuilder().BuildProfileXml(plan, database, "controlled", "player", 45, null, pack);
        XDocument document = XDocument.Parse(xml);
        return document.Root!.Element("QuestOrder")!.Elements("If").Single();
    }

    private sealed class CompiledCondition : IDisposable
    {
        private readonly string directory;
        private readonly MethodInfo execute;
        internal CompiledCondition(string condition)
        {
            directory = Path.Combine(Path.GetTempPath(), "cb-collection-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string source = "using System; public sealed class CollectionConditionProbe { " +
                    "private bool accepted; private int count; " +
                    "private bool HasQuest(int id) { if(id!=900001) throw new InvalidOperationException(\"Wrong quest\"); return accepted; } " +
                    "private int GetItemCount(int id) { if(id!=12345) throw new InvalidOperationException(\"Used tool instead of collected item\"); return count; } " +
                    "public bool Execute(bool hasQuest,int itemCount) { accepted=hasQuest; count=itemCount; return " + condition + "; } }";
                File.WriteAllText(Path.Combine(directory, "Probe.cs"), source);
                Type compilerType = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
                object compiler = Activator.CreateInstance(compilerType, new object[] { directory })!;
                var result = (CompilerResults?)compilerType.GetMethod("Compile")!.Invoke(compiler, null);
                string[] errors = result == null ? new[] { "null compilation result" }
                    : result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).Select(e => e.ToString()).ToArray();
                if (errors.Length != 0) throw new InvalidOperationException(string.Join(" | ", errors));
                var assembly = (Assembly?)compilerType.GetProperty("CompiledAssembly")!.GetValue(compiler);
                execute = assembly!.GetType("CollectionConditionProbe", true)!.GetMethod("Execute")!;
            }
            catch { Directory.Delete(directory, true); throw; }
        }
        internal bool Evaluate(bool accepted, int count)
        {
            object instance = Activator.CreateInstance(execute.DeclaringType!)!;
            return (bool)execute.Invoke(instance, new object[] { accepted, count })!;
        }
        public void Dispose() { Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Failure(reason);
    }
}

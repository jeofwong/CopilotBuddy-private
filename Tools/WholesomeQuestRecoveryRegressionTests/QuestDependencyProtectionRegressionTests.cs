using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Styx.Logic.Questing.Recovery;
using WholesomeAQ;

// Actual imported-data loader, dependency traversal and abandonment decision.
// Controlled JSON and guide IDs; no player, quest removal or server attached.
internal static class QuestDependencyProtectionRegressionTests
{
    private sealed class AssertionFailure : Exception
    {
        internal AssertionFailure(string message) : base(message) { }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>
        {
            ("loaded negative parent protects a current guide child", () =>
            {
                using var f = new Fixture(Database(-100));
                RequireProtected(100, 200);
                Check(f.Loaded.Quests.Single(q => q.Id == 200).PrevQuestID == -100,
                    "protection publication rewrote active-parent eligibility metadata");
            }),
            ("negative parent remains protected through a positive descendant", () =>
            {
                var db = Database(-100); db.Quests.Add(new QuestEntry { Id = 201, PrevQuestID = 200 });
                using var f = new Fixture(db); RequireProtected(100, 201);
            }),
            ("cached loader republishes the negative protection edge", () =>
            {
                using var f = new Fixture(Database(-100));
                QuestPrerequisiteAuthority.ClearPublishedDependencyAuthority();
                Check(ReferenceEquals(f.Loaded, f.Loader.Load()), "cached database identity changed");
                RequireProtected(100, 200);
            }),
            ("largest representable negative parent retains its identity", () =>
            {
                var db = Database(-int.MaxValue); db.Quests[0].Id = int.MaxValue;
                using var f = new Fixture(db); RequireProtected((uint)int.MaxValue, 200);
            }),
            ("positive rewarded-predecessor protection remains", () =>
            {
                using var f = new Fixture(Database(100)); RequireProtected(100, 200);
            }),
            ("explicit previous-quest list protection remains", () =>
            {
                var db = Database(0); db.Quests[1].PreviousQuestsIds.Add(100);
                using var f = new Fixture(db); RequireProtected(100, 200);
            }),
            ("positive forward-link protection remains", () =>
            {
                var db = Database(0); db.Quests[0].NextQuestID = 200;
                using var f = new Fixture(db); RequireProtected(100, 200);
            }),
            ("negative exclusive dependent predecessor protects every group member", () =>
            {
                var db = Database(0);
                db.Quests[0].ExclusiveGroup = -7;
                db.Quests.Insert(1, new QuestEntry { Id = 101, ExclusiveGroup = -7 });
                db.Quests.Single(q => q.Id == 200).PreviousQuestsIds.Add(100);
                using var f = new Fixture(db);
                RequireProtected(100, 200);
                RequireProtected(101, 200);
            }),
            ("positive exclusive dependent predecessor does not protect its sibling", () =>
            {
                var db = Database(0);
                db.Quests[0].ExclusiveGroup = 7;
                db.Quests.Insert(1, new QuestEntry { Id = 101, ExclusiveGroup = 7 });
                db.Quests.Single(q => q.Id == 200).PreviousQuestsIds.Add(100);
                using var f = new Fixture(db);
                RequireProtected(100, 200);
                var result = Status(101, 200);
                Check(result == QuestPrerequisiteStatus.NotActive && Decision(result).MayAbandon,
                    "positive alternative sibling became a required parent");
            }),
            ("direct positive predecessor does not expand a negative exclusive group", () =>
            {
                var db = Database(100);
                db.Quests[0].ExclusiveGroup = -7;
                db.Quests.Insert(1, new QuestEntry { Id = 101, ExclusiveGroup = -7 });
                using var f = new Fixture(db);
                RequireProtected(100, 200);
                var result = Status(101, 200);
                Check(result == QuestPrerequisiteStatus.NotActive && Decision(result).MayAbandon,
                    "direct PrevQuestID incorrectly expanded through negative group");
            }),
            ("active-parent and rewarded-parent edges are both retained", () =>
            {
                var db = Database(-100); db.Quests.Add(new QuestEntry { Id = 101 });
                db.Quests[1].PreviousQuestsIds.Add(101);
                using var f = new Fixture(db); RequireProtected(100, 200); RequireProtected(101, 200);
            }),
            ("removing dependent work releases only that protection", () =>
            {
                using var f = new Fixture(Database(-100));
                Check(Status(100, 400) == QuestPrerequisiteStatus.NotActive,
                    "unrelated current work kept a dormant dependency active");
                Check(Decision(Status(100, 400)).MayAbandon, "known unrelated control lost its policy outcome");
            }),
            ("int minimum does not overflow or publish a wrapped parent", () =>
            {
                using var f = new Fixture(Database(int.MinValue));
                Check(f.Loaded.Quests[1].PrevQuestID == int.MinValue, "invalid raw signed value was rewritten");
                Check(!Fixture.Edges().Any(e => e.QuestId == 200), "int minimum invented a parent edge");
            }),
            ("zero predecessor does not invent a dependency", () =>
            {
                using var f = new Fixture(Database(0));
                Check(Status(100, 200) == QuestPrerequisiteStatus.NotActive, "zero predecessor became parent100");
            }),
            ("unknown queried quest stays unknown beside known guide work", () =>
            {
                using var f = new Fixture(Database(100)); RequireUnknown(999, 200);
            }),
            ("unknown queried quest stays unknown when it is the sole active ID", () =>
            {
                using var f = new Fixture(Database(100)); RequireUnknown(999, 999);
            }),
            ("zero queried quest cannot establish safe abandonment", () =>
            {
                using var f = new Fixture(Database(100)); RequireUnknown(0);
            }),
            ("direct dependency API retains its explicit complete-edge-list contract", () =>
            {
                var result = QuestPrerequisiteAuthority.DetermineFromDependencies(999,
                    new[] { new QuestDependencyEvidence(200, 100, true, true) });
                Check(result == QuestPrerequisiteStatus.NotActive && Decision(result).MayAbandon,
                    "direct traversal changed its established complete-edge-list contract");
            }),
            ("known queried quest is not its own dependent", () =>
            {
                using var f = new Fixture(Database(100));
                Check(Status(100, 100) == QuestPrerequisiteStatus.NotActive, "self acceptance became a dependency");
            }),
            ("known unrelated candidate retains a known-negative result", () =>
            {
                using var f = new Fixture(Database(100));
                var result = Status(300, 200);
                Check(result == QuestPrerequisiteStatus.NotActive && Decision(result).MayAbandon,
                    "coverage repair blocked a fully known unrelated candidate");
            }),
            ("unknown other guide quest still prevents negative inference", () =>
            {
                using var f = new Fixture(Database(100)); RequireUnknown(100, 999);
            }),
            ("empty published dependency authority remains unknown", () =>
            {
                using var f = new Fixture(new QuestDatabase { Quests = new() { new QuestEntry { Id = 100 } } });
                RequireUnknown(100);
            }),
            ("unknown other work is not hidden by a known dependent", () =>
            {
                using var f = new Fixture(Database(100)); RequireUnknown(100, 200, 999);
            })
        };
        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS quest dependency protection: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL quest dependency protection: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR quest dependency protection: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"Quest dependency-protection scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual loader, authority and abandonment policy; controlled guide data; no game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Quest dependency-protection regression");
    }

    private static QuestDatabase Database(int previous) => new()
    {
        Quests = new()
        {
            new QuestEntry { Id = 100 },
            new QuestEntry { Id = 200, PrevQuestID = previous },
            // Independent real edge makes the graph authoritative even before
            // the missing negative-parent edge is repaired.
            new QuestEntry { Id = 300 },
            new QuestEntry { Id = 400, PrevQuestID = 300 }
        }
    };

    private static QuestPrerequisiteStatus Status(uint quest, params uint[] active) =>
        QuestPrerequisiteAuthority.DetermineFromPublishedDependencies(quest, active);

    private static QuestAbandonmentDecision Decision(QuestPrerequisiteStatus status) =>
        QuestAbandonmentPolicy.Evaluate(new QuestAbandonmentContext
        {
            IsAccepted = true, IsCompleted = false, StateIsCertain = true,
            HasObjectiveProgress = false, PrerequisiteStatus = status, FreeQuestLogSlots = 0,
            RecoveryState = QuestRecoveryState.Quarantined, Reason = QuestFailureReason.NoObjectiveProgress
        });

    private static void RequireProtected(uint quest, params uint[] active)
    {
        var result = Status(quest, active);
        Check(result == QuestPrerequisiteStatus.Active, "a required parent was not recognized as active");
        Check(!Decision(result).MayAbandon, "required parent became an abandonment candidate");
    }

    private static void RequireUnknown(uint quest, params uint[] active)
    {
        var result = Status(quest, active);
        Check(result == QuestPrerequisiteStatus.Unknown, "missing coverage became known-negative evidence");
        Check(!Decision(result).MayAbandon, "unknown prerequisite state permitted abandonment");
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly Type Authority = typeof(QuestPrerequisiteAuthority);
        private static readonly object Sync = Field("DependencySync").GetValue(null)!;
        private readonly (FieldInfo Field, object? Value)[] previous;
        private readonly string directory;
        internal readonly DataLoader Loader;
        internal readonly QuestDatabase Loaded;

        internal Fixture(QuestDatabase database)
        {
            lock (Sync)
                previous = new[] { "_publishedDependencies", "_publishedQuestIds", "_publishedDependencyAuthority" }
                    .Select(name => { FieldInfo field = Field(name); return (field, field.GetValue(null)); }).ToArray();
            directory = Path.Combine(Path.GetTempPath(), "cb-dependency-protection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string file = Path.Combine(directory, "quest_data.json");
                File.WriteAllText(file, JsonSerializer.Serialize(database));
                Loader = new DataLoader(file);
                Loaded = Loader.Load() ?? throw new InvalidOperationException("Controlled database did not load");
            }
            catch { Dispose(); throw; }
        }

        internal static QuestDependencyEvidence[] Edges()
        {
            lock (Sync) return ((QuestDependencyEvidence[])Field("_publishedDependencies").GetValue(null)!).ToArray();
        }

        public void Dispose()
        {
            lock (Sync) foreach (var item in previous) item.Field.SetValue(null, item.Value);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static FieldInfo Field(string name) => Authority.GetField(name, Flags)
            ?? throw new InvalidOperationException("Dependency authority boundary changed: " + name);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new AssertionFailure(message);
    }
}

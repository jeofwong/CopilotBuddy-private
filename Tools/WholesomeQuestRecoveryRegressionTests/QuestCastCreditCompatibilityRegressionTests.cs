using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Styx.Logic.Questing.Recovery;
using WholesomeAQ;

// TrinityCore 3.3.5 8fda442f and AzerothCore 8337a378 both define
// SpecialFlags 0x20 as cast credit, explicitly NOT a creature kill.
// Execute the actual scheduler/profile writer with complete controlled data.
// These tests do not infer an item, target recipe, spell or server completion.
internal static class QuestCastCreditCompatibilityRegressionTests
{
    private sealed class AssertionFailure : Exception { internal AssertionFailure(string text) : base(text) { } }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>();
        foreach (int value in new[] { 0x20, 0x21, 0x22, 0x30 })
        {
            int flags = value;
            cases.Add(($"accepted cast-credit flags {flags:X} never become a kill instruction", () =>
            {
                var db = Database(flags); var failures = new List<QuestAttemptOutcome>();
                int probes = 0;
                var result = Plan(db, accepted: true, report: failures.Add, probe: () => probes++);
                Check(!result.Plan.Any(p => p.Stage == QuestWorkStage.Objective), "cast credit was scheduled as ordinary killing");
                Check(failures.Count == 1 && failures[0].Reason == QuestFailureReason.UnsupportedObjective
                    && failures[0].Key.QuestId == 90001, "missing exact unsupported-strategy evidence");
                Check(probes == 0, "unsupported cast work was sent for kill navigation");
                string xml = new ProfileBuilder().BuildProfileXml(result.Plan, db, "Test zone", "Tester", 30);
                Check(!XDocument.Parse(xml).Descendants("Objective").Any(n => (string?)n.Attribute("Type") == "KillMob"), "profile emitted a false kill node");
                Check(db.Quests[0].Objectives[0].Type == ObjectiveType.KillMob && db.Quests[0].SpecialFlags == flags,
                    "source evidence was silently rewritten instead of rejected");
            }));
            cases.Add(($"cast-credit flags {flags:X} do not authorize automatic pickup", () =>
            {
                var result = Plan(Database(flags), accepted: false);
                Check(!result.Plan.Any(p => p.Stage == QuestWorkStage.Pickup), "unsupported cast quest was picked up as a known kill plan");
            }));
            cases.Add(($"completed cast-credit flags {flags:X} retain valid turn-in", () =>
            {
                var result = Plan(Database(flags), accepted: true, completed: true);
                Check(result.Plan.Any(p => p.Stage == QuestWorkStage.TurnIn), "known completed quest lost its turn-in");
                Check(!result.Plan.Any(p => p.Stage == QuestWorkStage.Objective), "completed quest generated another objective");
            }));
        }
        foreach (int value in new[] { 0, 1, 0x10 })
        {
            int flags = value;
            cases.Add(($"ordinary kill with non-cast special flags {flags:X} remains supported", () =>
            {
                var result = Plan(Database(flags), accepted: true);
                Check(result.Plan.Any(p => p.Stage == QuestWorkStage.Objective && p.ObjectiveIndex == 0), "legitimate kill work rejected");
            }));
        }
        cases.Add(("ordinary client Flags 0x20 is not server SpecialFlags CAST", () =>
        {
            var db = Database(0); db.Quests[0].Flags = 0x20;
            Check(Plan(db, true).Plan.Any(p => p.Stage == QuestWorkStage.Objective), "unrelated flag namespace was mistaken for CAST");
        }));
        cases.Add(("a start item alone does not invent a cast objective", () =>
        {
            var db = Database(0); db.Quests[0].StartItem = 777;
            Check(Plan(db, true).Plan.Any(p => p.Stage == QuestWorkStage.Objective), "an item ID was guessed to be a recipe");
        }));
        cases.Add(("satisfied cast counter does not fabricate whole-quest completion", () =>
        {
            var failures = new List<QuestAttemptOutcome>();
            var result = Plan(Database(0x20), true, count: 2, report: failures.Add);
            Check(result.Plan.Count == 0 && failures.Count == 0, "satisfied counter became a new action, failure or turn-in");
        }));
        cases.Add(("mixed quest retains a real collection objective but not a cast-derived kill", () =>
        {
            var db = Database(0x20);
            db.Quests[0].Objectives.Add(new QuestObjective { Index = 1, Type = ObjectiveType.CollectItem,
                MobId = 2001, ItemId = 501, CollectCount = 2 });
            var result = Plan(db, true);
            Check(result.Plan.Any(p => p.ObjectiveIndex == 1) && !result.Plan.Any(p => p.ObjectiveIndex == 0),
                "mixed collection was erased or ambiguous kill work survived");
        }));
        cases.Add(("unrelated ordinary accepted quest remains schedulable", () =>
        {
            var db = Database(0x20);
            var other = Database(0).Quests[0]; other.Id = 90002; db.Quests.Add(other);
            var result = Plan(db, true, includeOther: true);
            Check(result.Plan.Any(p => p.Quest.Id == 90002 && p.Stage == QuestWorkStage.Objective)
                && !result.Plan.Any(p => p.Quest.Id == 90001), "unsupported work starved or contaminated an independent quest");
        }));

        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS quest cast credit: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL quest cast credit assertion: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR quest cast credit fixture: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"Quest cast-credit compatibility scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual scheduler/profile writer; controlled imported flags; no server/game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException($"Quest cast-credit regressions: assertions={assertions}; unexpected={unexpected}");
    }

    private static QuestDatabase Database(int specialFlags) => new()
    {
        Quests = new() { new QuestEntry { Id = 90001, Name = "Controlled cast-credit quest", QuestLevel = 20, MinLevel = 1,
            SpecialFlags = specialFlags, Objectives = new() { new QuestObjective { Index = 0, Type = ObjectiveType.KillMob, MobId = 2001, KillCount = 2 } } } },
        QuestGivers = new() { new QuestGiverEntry { QuestId = 90001, GiverId = 1001, GiverName = "Giver" } },
        QuestEnders = new() { new QuestEnderEntry { QuestId = 90001, EnderId = 1001, EnderName = "Ender" } },
        CreatureSpawns = new() { ["1001"] = new() { new SpawnPoint { Map = 1, X = 10, Y = 10, Z = 1 } },
            ["2001"] = new() { new SpawnPoint { Map = 1, X = 20, Y = 20, Z = 1 } } }
    };

    private static QuestScheduleResult Plan(QuestDatabase db, bool accepted, bool completed = false, int count = 0,
        Action<QuestAttemptOutcome>? report = null, Action? probe = null, bool includeOther = false)
    {
        var active = new List<QuestSchedulerAcceptedQuest>();
        if (accepted) active.Add(new QuestSchedulerAcceptedQuest { QuestId = 90001, IsCompleted = completed, ObjectiveCounts = new[] { count, 0 } });
        if (includeOther) active.Add(new QuestSchedulerAcceptedQuest { QuestId = 90002, ObjectiveCounts = new[] { 0 } });
        var snapshot = new QuestSchedulerSnapshot { UtcNow = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
            PlayerLevel = 30, PlayerRaceId = 1, MapId = 1, HasAuthoritativeCompletions = !accepted,
            AcceptedQuests = active, CarriedItemCounts = new Dictionary<int, long>() };
        return QuestScheduler.MaterializeSchedule(db, snapshot,
            _ => new QuestRecoveryDecision { State = QuestRecoveryState.Eligible, MayAttempt = true, Status = "eligible" },
            10, 500, 20, navigationAssessment: _ => { probe?.Invoke(); return new SpawnNavigationAssessment { IsKnownReachable = true, IsKnownSafe = true }; },
            reportDataFailure: report);
    }
    private static void Check(bool value, string message) { if (!value) throw new AssertionFailure(message); }
}

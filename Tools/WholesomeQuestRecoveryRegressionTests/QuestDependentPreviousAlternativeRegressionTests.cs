using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Styx.Logic.Questing.Recovery;
using WholesomeAQ;

// Pinned TrinityCore 3.3.5 8fda442f: DependentPreviousQuests is an
// alternative gate. Any rewarded ordinary predecessor satisfies it; a rewarded
// predecessor in a negative ExclusiveGroup instead requires that whole group.
// Direct signed PrevQuestID remains a separate prerequisite contract.
// Controlled scheduler data only; no realm/database provenance or game attached.
internal static class QuestDependentPreviousAlternativeRegressionTests
{
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>
        {
            ("first rewarded ordinary alternative unlocks child", () =>
            {
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100, 101 });
                Check(Pickup(r, 200), "second unrewarded alternative was treated as independently required");
            }),
            ("later rewarded ordinary alternative unlocks child", () =>
            {
                var r = Plan(new uint[] { 101 }, dependent: new[] { 100, 101 });
                Check(Pickup(r, 200), "earlier unrewarded alternative blocked a later rewarded predecessor");
            }),
            ("no rewarded alternative keeps child unavailable", () =>
            {
                var r = Plan(Array.Empty<uint>(), dependent: new[] { 100, 101 });
                Check(!Pickup(r, 200), "unrewarded dependent alternatives admitted the child");
            }),
            ("accepted but unrewarded alternative is not rewarded history", () =>
            {
                var r = Plan(Array.Empty<uint>(), accepted: new uint[] { 100 }, dependent: new[] { 100, 101 });
                Check(!Pickup(r, 200), "active alternative was mistaken for rewarded history");
            }),
            ("accepted unused alternative cannot shadow rewarded alternative", () =>
            {
                var r = Plan(new uint[] { 100 }, accepted: new uint[] { 101 }, dependent: new[] { 100, 101 });
                Check(Pickup(r, 200), "ancestor correction for an unused alternative shadowed a rewarded predecessor");
            }),
            ("complete negative group satisfies dependent gate without later alternative", () =>
            {
                var groups = new Dictionary<int, int> { [100] = -7, [101] = -7 };
                var r = Plan(new uint[] { 100, 101 }, dependent: new[] { 100, 103 }, groups: groups);
                Check(Pickup(r, 200), "complete negative predecessor group did not satisfy the alternative gate");
            }),
            ("partial negative group fails immediately even when later ordinary alternative is rewarded", () =>
            {
                var groups = new Dictionary<int, int> { [100] = -7, [101] = -7 };
                var r = Plan(new uint[] { 100, 103 }, dependent: new[] { 100, 103 }, groups: groups);
                Check(!Pickup(r, 200), "partial negative group incorrectly fell through to a later alternative");
            }),
            ("earlier rewarded ordinary alternative succeeds before later partial negative group", () =>
            {
                var groups = new Dictionary<int, int> { [100] = -7, [101] = -7 };
                var r = Plan(new uint[] { 100, 103 }, dependent: new[] { 103, 100 }, groups: groups);
                Check(Pickup(r, 200), "stored dependent order was ignored after an ordinary predecessor had already satisfied the gate");
            }),
            ("direct positive predecessor remains an independent requirement", () =>
            {
                // TrinityCore also inserts a positive direct predecessor into its
                // derived DependentPreviousQuests list. Keep the fixture core-shaped.
                var r = Plan(new uint[] { 100 }, dependent: new[] { 90, 100, 101 }, direct: 90);
                Check(!Pickup(r, 200), "dependent alternative bypassed the direct positive predecessor");
            }),
            ("direct positive plus its derived dependency unlocks child", () =>
            {
                var r = Plan(new uint[] { 90, 101 }, dependent: new[] { 90, 100, 101 }, direct: 90);
                Check(Pickup(r, 200), "rewarded direct predecessor did not satisfy its derived dependent gate");
            }),
            ("core-shaped direct positive negative group still requires every group member", () =>
            {
                var groups = new Dictionary<int, int> { [100] = -7, [101] = -7 };
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100 }, direct: 100, groups: groups);
                Check(!Pickup(r, 200), "derived dependent edge lost negative-group all-required semantics");
            }),
            ("active negative direct parent remains separate from dependent alternatives", () =>
            {
                var r = Plan(new uint[] { 101 }, accepted: new uint[] { 90 }, dependent: new[] { 100, 101 }, direct: -90);
                Check(Pickup(r, 200), "active signed parent plus rewarded dependent alternative did not unlock child");
            }),
            ("rewarded alternative without predecessor metadata cannot authorize", () =>
            {
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100, 101 }, omitMetadata: new[] { 100 });
                Check(!Pickup(r, 200), "unknown predecessor group metadata was treated as permission");
            }),
            ("later known rewarded alternative can satisfy after unknown rewarded candidate", () =>
            {
                var r = Plan(new uint[] { 100, 101 }, dependent: new[] { 100, 101 }, omitMetadata: new[] { 100 });
                Check(Pickup(r, 200), "unknown candidate poisoned a later known rewarded ordinary alternative");
            })
        };

        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try
            {
                item.Test();
                passed++;
                Console.WriteLine("PASS dependent previous alternatives: " + item.Name);
            }
            catch (AssertionFailure error)
            {
                assertions++;
                Console.Error.WriteLine("FAIL dependent previous alternatives: " + item.Name + ": " + error.Message);
            }
            catch (Exception error)
            {
                unexpected++;
                Console.Error.WriteLine("ERROR dependent previous alternatives: " + item.Name + ": " + error);
            }
        }

        Console.WriteLine(
            $"Dependent previous alternative scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; " +
            "actual MaterializeSchedule; pinned TC3.3.5 semantics; controlled data; no game/server attached.");
        if (assertions + unexpected != 0)
            throw new InvalidOperationException("Dependent previous alternative regression");
    }

    private static bool Pickup(QuestScheduleResult result, int id) =>
        result.Plan.Any(entry => entry.Stage == QuestWorkStage.Pickup && entry.Quest.Id == id);

    private static QuestScheduleResult Plan(
        uint[] rewarded,
        uint[]? accepted = null,
        int[]? dependent = null,
        int direct = 0,
        IReadOnlyDictionary<int, int>? groups = null,
        IReadOnlyCollection<int>? omitMetadata = null)
    {
        int Group(int id) => groups != null && groups.TryGetValue(id, out int group) ? group : 0;

        QuestEntry Q(int id) => new()
        {
            Id = id,
            Name = "Controlled " + id,
            MinLevel = 1,
            QuestLevel = 20,
            ExclusiveGroup = Group(id),
            Objectives = new()
            {
                new QuestObjective
                {
                    Type = ObjectiveType.KillMob,
                    Index = 0,
                    MobId = 10000 + id,
                    KillCount = 1
                }
            }
        };

        var ids = new HashSet<int> { 90, 100, 101, 103 };
        foreach (int id in dependent ?? Array.Empty<int>())
            if (id > 0) ids.Add(id);
        if (direct > 0) ids.Add(direct);
        if (direct < 0 && direct != int.MinValue) ids.Add(-direct);

        var quests = ids
            .Where(id => omitMetadata == null || !omitMetadata.Contains(id))
            .OrderBy(id => id)
            .Select(Q)
            .ToList();
        var child = Q(200);
        child.PrevQuestID = direct;
        child.PreviousQuestsIds = (dependent ?? Array.Empty<int>()).ToList();
        quests.Add(child);

        var spawns = new Dictionary<string, List<SpawnPoint>>
        {
            ["1200"] = new() { new SpawnPoint { Map = 1, X = 10, Y = 10, Z = 5 } }
        };
        foreach (int id in ids)
            spawns[(10000 + id).ToString()] = new()
            {
                new SpawnPoint { Map = 1, X = 12 + id % 3, Y = 10, Z = 5 }
            };

        var db = new QuestDatabase
        {
            Quests = quests,
            QuestGivers = new() { new QuestGiverEntry { QuestId = 200, GiverId = 1200 } },
            CreatureSpawns = spawns
        };
        var snapshot = new QuestSchedulerSnapshot
        {
            UtcNow = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc),
            PlayerLevel = 20,
            PlayerRaceId = 1,
            MapId = 1,
            HasCompleteQuestLog = true,
            HasAuthoritativeCompletions = true,
            CompletedQuestIds = rewarded,
            AcceptedQuests = (accepted ?? Array.Empty<uint>())
                .Select(id => new QuestSchedulerAcceptedQuest { QuestId = id })
                .ToArray()
        };
        var failures = new List<QuestAttemptOutcome>();
        QuestScheduleResult result = QuestScheduler.MaterializeSchedule(
            db,
            snapshot,
            _ => new QuestRecoveryDecision
            {
                State = QuestRecoveryState.Eligible,
                MayAttempt = true,
                Status = "eligible"
            },
            10,
            1000,
            7,
            navigationAssessment: _ => new SpawnNavigationAssessment
            {
                IsKnownSafe = true,
                IsKnownReachable = true
            },
            reportDataFailure: failures.Add);

        Check(!failures.Any(failure => failure.Key.QuestId == 200),
            "unmet alternative prerequisite became a persistent child data failure");
        return result;
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid)
            throw new AssertionFailure(reason);
    }
}

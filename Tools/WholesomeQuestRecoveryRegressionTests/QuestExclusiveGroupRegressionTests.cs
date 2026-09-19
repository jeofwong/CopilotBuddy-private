using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Styx.Logic.Questing.Recovery;
using WholesomeAQ;

// Original-3.3.5 positive ExclusiveGroup admission. Pinned TC/AC behavior:
// positive groups are alternatives; negative groups are not mutual-exclusion.
// Controlled scheduler data only; no server/NPC/client attached.
internal static class QuestExclusiveGroupRegressionTests
{
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>
        {
            ("two viable positive-group siblings publish one alternative", () =>
            {
                var r = Plan(new[] { Q(100, 7), Q(101, 7) });
                Check(Pickups(r).Count == 1 && Pickups(r)[0] == 100,
                    "positive exclusive group published multiple pickup alternatives");
            }),
            ("accepted positive-group sibling blocks another pickup", () =>
            {
                var r = Plan(new[] { Q(100, 7), Q(101, 7) }, accepted: new uint[] { 100 });
                Check(!Pickups(r).Contains(101), "accepted exclusive sibling did not block pickup");
            }),
            ("rewarded positive-group sibling blocks another pickup", () =>
            {
                var r = Plan(new[] { Q(100, 7), Q(101, 7) }, rewarded: new uint[] { 100 });
                Check(!Pickups(r).Contains(101), "rewarded exclusive sibling did not block pickup");
            }),
            ("zero group keeps independent pickups", () =>
            {
                var r = Plan(new[] { Q(100, 0), Q(101, 0) });
                Check(Pickups(r).OrderBy(x => x).SequenceEqual(new[] { 100, 101 }),
                    "ordinary quests were treated as exclusive");
            }),
            ("different positive groups remain independent", () =>
            {
                var r = Plan(new[] { Q(100, 7), Q(101, 8) });
                Check(Pickups(r).OrderBy(x => x).SequenceEqual(new[] { 100, 101 }),
                    "different exclusive groups collided");
            }),
            ("negative group members are not mutually exclusive pickups", () =>
            {
                var r = Plan(new[] { Q(100, -7), Q(101, -7) });
                Check(Pickups(r).OrderBy(x => x).SequenceEqual(new[] { 100, 101 }),
                    "negative dependency group was mistaken for positive exclusivity");
            }),
            ("unavailable first sibling does not reserve the positive group", () =>
            {
                var first = Q(100, 7); first.Name = "No giver";
                var second = Q(101, 7);
                var r = Plan(new[] { first, second }, omitGiver: new[] { 100 });
                Check(Pickups(r).SequenceEqual(new[] { 101 }),
                    "unplannable sibling shadowed a viable alternative");
            }),
            ("unmet-prerequisite sibling does not reserve the positive group", () =>
            {
                var first = Q(100, 7); first.PrevQuestID = 999;
                var second = Q(101, 7);
                var r = Plan(new[] { first, second });
                Check(Pickups(r).SequenceEqual(new[] { 101 }),
                    "ineligible sibling shadowed a viable alternative");
            }),
            ("single positive-group quest remains eligible", () =>
            {
                var r = Plan(new[] { Q(100, 7) });
                Check(Pickups(r).SequenceEqual(new[] { 100 }), "single grouped quest was suppressed");
            }),
            ("exclusive exclusion is contextual and does not record data failure", () =>
            {
                var failures = new List<QuestAttemptOutcome>();
                var r = Plan(new[] { Q(100, 7), Q(101, 7) }, report: failures.Add);
                Check(Pickups(r).Count == 1, "exclusive control changed");
                Check(!failures.Any(f => f.Key.QuestId == 101),
                    "alternative exclusion became a persistent quest-data failure");
            })
        };

        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS quest exclusive group: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL quest exclusive group: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR quest exclusive group: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"Quest exclusive-group scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual MaterializeSchedule; controlled core-shaped data; no game/server attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Quest exclusive-group regression");
    }

    private static QuestEntry Q(int id, int group) => new()
    {
        Id = id, Name = "Controlled " + id, MinLevel = 1, QuestLevel = 20,
        ExclusiveGroup = group,
        Objectives = new() { new QuestObjective { Type = ObjectiveType.TurnInOnly, Index = 0 } }
    };

    private static List<int> Pickups(QuestScheduleResult r) =>
        r.Plan.Where(p => p.Stage == QuestWorkStage.Pickup).Select(p => p.Quest.Id).Distinct().OrderBy(id => id).ToList();

    private static QuestScheduleResult Plan(
        IReadOnlyList<QuestEntry> quests,
        uint[]? accepted = null,
        uint[]? rewarded = null,
        int[]? omitGiver = null,
        Action<QuestAttemptOutcome>? report = null)
    {
        var point = new SpawnPoint { Map = 1, X = 10, Y = 10, Z = 5 };
        var omitted = new HashSet<int>(omitGiver ?? Array.Empty<int>());
        var givers = quests.Where(q => !omitted.Contains(q.Id))
            .Select(q => new QuestGiverEntry { QuestId = q.Id, GiverId = 1000 + q.Id }).ToList();
        var spawns = givers.ToDictionary(g => g.GiverId.ToString(), _ => new List<SpawnPoint> { point });
        var db = new QuestDatabase
        {
            Quests = quests.ToList(),
            QuestGivers = givers,
            CreatureSpawns = spawns
        };
        var snapshot = new QuestSchedulerSnapshot
        {
            UtcNow = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
            PlayerLevel = 20, PlayerRaceId = 1, MapId = 1,
            HasCompleteQuestLog = true, HasAuthoritativeCompletions = true,
            CompletedQuestIds = rewarded ?? Array.Empty<uint>(),
            AcceptedQuests = (accepted ?? Array.Empty<uint>())
                .Select(id => new QuestSchedulerAcceptedQuest { QuestId = id }).ToArray()
        };
        return QuestScheduler.MaterializeSchedule(
            db, snapshot,
            _ => new QuestRecoveryDecision { State = QuestRecoveryState.Eligible, MayAttempt = true, Status = "eligible" },
            10, 1000, 7,
            navigationAssessment: _ => new SpawnNavigationAssessment { IsKnownSafe = true, IsKnownReachable = true },
            reportDataFailure: report ?? (_ => { }));
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid) throw new AssertionFailure(reason);
    }
}

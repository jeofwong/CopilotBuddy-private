using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Styx.Logic.Questing.Recovery;
using WholesomeAQ;

// Pinned TC 3.3.5 / AC WotLK distinction:
// PreviousQuestsIds-style dependent predecessors in a negative ExclusiveGroup
// require every member of that group rewarded. Direct signed PrevQuestID remains
// a separate contract and must not be expanded through the group.
internal static class QuestNegativeExclusiveDependencyRegressionTests
{
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>
        {
            ("one rewarded member of negative dependency group is insufficient", () =>
            {
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100 }, group100: -7, group101: -7);
                Check(!Pickup(r, 200), "child admitted before every negative-group predecessor was rewarded");
            }),
            ("all rewarded members of negative dependency group unlock child", () =>
            {
                var r = Plan(new uint[] { 100, 101 }, dependent: new[] { 100 }, group100: -7, group101: -7);
                Check(Pickup(r, 200), "complete negative dependency group did not unlock child");
            }),
            ("accepted but unrewarded sibling does not satisfy negative dependency group", () =>
            {
                var r = Plan(new uint[] { 100 }, accepted: new uint[] { 101 }, dependent: new[] { 100 }, group100: -7, group101: -7);
                Check(!Pickup(r, 200), "active sibling was mistaken for rewarded group completion");
            }),
            ("singleton negative dependency group behaves as its one rewarded predecessor", () =>
            {
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100 }, group100: -7, include101: false);
                Check(Pickup(r, 200), "singleton negative group became impossible");
            }),
            ("positive dependency group is not expanded to all siblings", () =>
            {
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100 }, group100: 7, group101: 7);
                Check(Pickup(r, 200), "positive alternative group was treated as all-required");
            }),
            ("zero group predecessor remains ordinary", () =>
            {
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100 }, group100: 0, group101: 0);
                Check(Pickup(r, 200), "ordinary predecessor changed semantics");
            }),
            ("unrelated negative group is irrelevant", () =>
            {
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100 }, group100: 0, group101: -9);
                Check(Pickup(r, 200), "unreferenced negative group blocked child");
            }),
            ("direct positive PrevQuestID does not expand negative group", () =>
            {
                var r = Plan(new uint[] { 100 }, direct: 100, group100: -7, group101: -7);
                Check(Pickup(r, 200), "direct predecessor was incorrectly converted to grouped dependency");
            }),
            ("direct negative PrevQuestID still uses accepted parent only", () =>
            {
                var r = Plan(Array.Empty<uint>(), accepted: new uint[] { 100 }, direct: -100, group100: -7, group101: -7);
                Check(Pickup(r, 200), "signed active-parent contract was incorrectly expanded through group");
            }),
            ("dependent group omission is contextual rather than data failure", () =>
            {
                var failures = new List<QuestAttemptOutcome>();
                var r = Plan(new uint[] { 100 }, dependent: new[] { 100 }, group100: -7, group101: -7, report: failures.Add);
                Check(!Pickup(r, 200), "negative group control changed");
                Check(!failures.Any(f => f.Key.QuestId == 200), "unmet grouped prerequisite became persistent data failure");
            })
        };

        int passed=0, assertions=0, unexpected=0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS negative dependency group: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL negative dependency group: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR negative dependency group: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"Negative exclusive dependency scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual MaterializeSchedule; controlled core-shaped data; no game/server attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Negative exclusive dependency regression");
    }

    private static bool Pickup(QuestScheduleResult result, int id) =>
        result.Plan.Any(p => p.Stage == QuestWorkStage.Pickup && p.Quest.Id == id);

    private static QuestScheduleResult Plan(
        uint[] rewarded,
        uint[]? accepted = null,
        int[]? dependent = null,
        int direct = 0,
        int group100 = 0,
        int group101 = 0,
        bool include101 = true,
        Action<QuestAttemptOutcome>? report = null)
    {
        QuestEntry Q(int id, int group) => new()
        {
            Id=id, Name="Controlled " + id, MinLevel=1, QuestLevel=20, ExclusiveGroup=group,
            Objectives=new() { new QuestObjective { Type=ObjectiveType.TurnInOnly, Index=0 } }
        };
        var p100=Q(100, group100);
        var child=Q(200, 0);
        child.PrevQuestID=direct;
        child.PreviousQuestsIds=(dependent ?? Array.Empty<int>()).ToList();
        var quests=new List<QuestEntry>{p100,child};
        if(include101) quests.Insert(1,Q(101,group101));

        var point=new SpawnPoint{Map=1,X=10,Y=10,Z=5};
        var db=new QuestDatabase
        {
            Quests=quests,
            QuestGivers=new(){new QuestGiverEntry{QuestId=200,GiverId=1200}},
            CreatureSpawns=new(){["1200"]=new(){point}}
        };
        var snapshot=new QuestSchedulerSnapshot
        {
            UtcNow=new DateTime(2026,9,18,0,0,0,DateTimeKind.Utc),
            PlayerLevel=20,PlayerRaceId=1,MapId=1,
            HasCompleteQuestLog=true,HasAuthoritativeCompletions=true,
            CompletedQuestIds=rewarded,
            AcceptedQuests=(accepted ?? Array.Empty<uint>())
                .Select(id=>new QuestSchedulerAcceptedQuest{QuestId=id}).ToArray()
        };
        return QuestScheduler.MaterializeSchedule(
            db,snapshot,
            _=>new QuestRecoveryDecision{State=QuestRecoveryState.Eligible,MayAttempt=true,Status="eligible"},
            10,1000,7,
            navigationAssessment:_=>new SpawnNavigationAssessment{IsKnownSafe=true,IsKnownReachable=true},
            reportDataFailure:report ?? (_=>{}));
    }

    private static void Check(bool valid, string reason)
    {
        if(!valid) throw new AssertionFailure(reason);
    }
}

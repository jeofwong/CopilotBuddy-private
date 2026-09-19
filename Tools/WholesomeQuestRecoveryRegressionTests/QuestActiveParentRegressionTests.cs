using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Styx.Logic.Questing.Recovery;
using WholesomeAQ;

// Actual public scheduler. Snapshot observations are explicit controls, not a
// replacement prerequisite policy or evidence that the NPC offered this quest.
// Pinned TrinityCore 3.3.5 8fda442f requires negative direct PrevQuestID to
// reference QUEST_STATUS_INCOMPLETE. Pinned AzerothCore WotLK 8337a378 is
// broader (non-NONE); this fixture deliberately tests the TC-primary contract.
internal static class QuestActiveParentRegressionTests
{
    private sealed class AssertionFailure(string text) : Exception(text) { }
    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Body)>();
        void Add(string name, Action body) => cases.Add((name, body));
        foreach (bool metadata in new[] { false, true })
        {
            bool include = metadata;
            string prefix = include ? "parent metadata present: " : "parent metadata absent: ";
            Add(prefix + "negative parent absent defers child", () => Expect(-100, Array.Empty<uint>(), Array.Empty<uint>(), false, include));
            Add(prefix + "negative parent historically rewarded but absent still defers", () => Expect(-100, Array.Empty<uint>(), new uint[] {100}, false, include));
            Add(prefix + "wrong active quest cannot unlock child", () => Expect(-100, new uint[] {101}, Array.Empty<uint>(), false, include));
            Add(prefix + "negative parent accepted unlocks child", () => Expect(-100, new uint[] {100}, Array.Empty<uint>(), true, include));
            Add(prefix + "TC-primary ready but unturned-in negative parent does not unlock child", () => Expect(-100, new uint[] {100}, Array.Empty<uint>(), false, include, ready: true));
            Add(prefix + "TC-primary ready negative parent is not rescued by old rewarded history", () => Expect(-100, new uint[] {100}, new uint[] {100}, false, include, ready: true));
            Add(prefix + "TC-primary failed negative parent does not unlock child", () => Expect(-100, new uint[] {100}, Array.Empty<uint>(), false, include, failed: true));
            Add(prefix + "TC-primary failed negative parent is not rescued by old rewarded history", () => Expect(-100, new uint[] {100}, new uint[] {100}, false, include, failed: true));
            Add(prefix + "accepted incomplete repeat parent outranks old rewarded history", () => Expect(-100, new uint[] {100}, new uint[] {100}, true, include));
            Add(prefix + "positive predecessor absent defers child", () => Expect(100, Array.Empty<uint>(), Array.Empty<uint>(), false, include));
            Add(prefix + "positive predecessor accepted only cannot unlock", () => Expect(100, new uint[] {100}, Array.Empty<uint>(), false, include));
            Add(prefix + "positive predecessor ready is not rewarded", () => Expect(100, new uint[] {100}, Array.Empty<uint>(), false, include, ready: true));
            Add(prefix + "positive predecessor rewarded unlocks child", () => Expect(100, Array.Empty<uint>(), new uint[] {100}, true, include));
            Add(prefix + "no predecessor retains normal pickup", () => Expect(0, Array.Empty<uint>(), Array.Empty<uint>(), true, include));
        }
        Add("parent removal revokes child on next materialization", () => { Expect(-100,new uint[]{100},Array.Empty<uint>(),true,true); Expect(-100,Array.Empty<uint>(),Array.Empty<uint>(),false,true); });
        Add("parent acceptance restores deferred child without permanent blacklist", () => { Expect(-100,Array.Empty<uint>(),Array.Empty<uint>(),false,true); Expect(-100,new uint[]{100},Array.Empty<uint>(),true,true); });
        Add("minimum signed integer cannot wrap into a valid parent", () => Expect(int.MinValue,Array.Empty<uint>(),Array.Empty<uint>(),false,true));
        Add("self prerequisite cannot unlock an unaccepted child", () => Expect(-200,Array.Empty<uint>(),Array.Empty<uint>(),false,true));
        Add("active parent does not bypass explicit rewarded prerequisites", () => Expect(-100,new uint[]{100},Array.Empty<uint>(),false,true, required:new[]{102}));
        Add("active parent and rewarded extra prerequisite allow pickup", () => Expect(-100,new uint[]{100},new uint[]{102},true,true, required:new[]{102}));
        Add("unknown completion snapshot still defers all new pickup", () => Expect(-100,new uint[]{100},Array.Empty<uint>(),false,true, authority:false));
        Add("incomplete accepted-log snapshot does not authorize child", () => Expect(-100,new uint[]{100},Array.Empty<uint>(),false,true, completeLog:false));
        Add("unmet parent leaves unrelated available pickup usable", () => {
            var r=Plan(-100,Array.Empty<uint>(),Array.Empty<uint>(),true,unrelated:true);
            Check(!Pickup(r,200)&&Pickup(r,300),"blocked child suppressed unrelated pickup or was admitted");
        });
        Add("ready parent retains turn-in rather than being classified as failed pickup", () => {
            var r=Plan(100,new uint[]{100},Array.Empty<uint>(),true,ready:true);
            Check(!Pickup(r,200)&&r.Plan.Any(p=>p.Quest.Id==100&&p.Stage==QuestWorkStage.TurnIn),"ready parent turn-in was lost");
        });
        Add("live scheduler carries raw failed state into accepted prerequisite status", () => {
            string source=File.ReadAllText(Path.Combine(Root(),"runtime-snapshot","Bots","WholesomeAutoQuest-master","QuestScheduler.cs"));
            int start=source.IndexOf("new QuestSchedulerAcceptedQuest",StringComparison.Ordinal);
            Check(start>=0,"live accepted-quest projection is missing");
            string region=source.Substring(start,Math.Min(1400,source.Length-start));
            Check(region.Contains("IsFailed",StringComparison.Ordinal)
                && region.Contains("FailedQuestIds",StringComparison.Ordinal),
                "live scheduler does not carry the raw failed-quest snapshot into prerequisite state");
        });
        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases) { try {c.Body();passed++;Console.WriteLine("PASS active parent: "+c.Name);} catch(AssertionFailure e) {assertions++;Console.Error.WriteLine("FAIL active parent assertion: "+c.Name+": "+e.Message);} catch(Exception e) {unexpected++;Console.Error.WriteLine("ERROR active parent fixture: "+c.Name+": "+e);} }
        Console.WriteLine($"Active parent prerequisite scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual MaterializeSchedule; explicit snapshots; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Active parent regressions");
    }
    private static bool Pickup(QuestScheduleResult r,int id)=>r.Plan.Any(p=>p.Quest.Id==id&&p.Stage==QuestWorkStage.Pickup);
    private static void Expect(int parent,uint[] accepted,uint[] rewarded,bool expected,bool metadata,bool ready=false,bool failed=false,int[]? required=null,bool authority=true,bool completeLog=true)
    {
        var r=Plan(parent,accepted,rewarded,metadata,ready,failed,required,authority,completeLog);
        Check(Pickup(r,200)==expected,$"expected pickup={expected}, observed {Pickup(r,200)}; parent={parent}");
    }
    private static QuestScheduleResult Plan(int parent,uint[] accepted,uint[] rewarded,bool metadata,bool ready=false,bool failed=false,int[]? required=null,bool authority=true,bool completeLog=true,bool unrelated=false)
    {
        QuestEntry Q(int id)=>new(){Id=id,Name="Controlled quest "+id,MinLevel=1,QuestLevel=20,Objectives=new(){new QuestObjective{Type=ObjectiveType.TurnInOnly}}};
        var child=Q(200);child.PrevQuestID=parent;child.PreviousQuestsIds=required?.ToList()??new();
        var quests=new List<QuestEntry>{child};if(metadata)quests.Add(Q(100));if(unrelated)quests.Add(Q(300));
        // Explicit rewarded prerequisites model core-loaded predecessor metadata.
        // The metadata flag above intentionally controls only the signed parent
        // presence/absence cases; do not turn a rewarded required ID into
        // unknown-provenance evidence for this active-parent contract.
        foreach(int id in required??Array.Empty<int>())
            if(id>0&&id!=200&&!quests.Any(q=>q.Id==id))quests.Add(Q(id));
        // Explicitly observed positions, not invented live terrain coordinates.
        var point=new SpawnPoint{Map=1,X=10,Y=10,Z=5};
        var db=new QuestDatabase{Quests=quests,QuestGivers=quests.Select(q=>new QuestGiverEntry{QuestId=q.Id,GiverId=1001}).ToList(),QuestEnders=quests.Select(q=>new QuestEnderEntry{QuestId=q.Id,EnderId=1001}).ToList(),CreatureSpawns=new(){["1001"]=new(){point}}};
        QuestSchedulerAcceptedQuest Accepted(uint id)
        {
            var value=new QuestSchedulerAcceptedQuest{QuestId=id,IsCompleted=ready&&id==100};
            if(failed&&id==100)
            {
                var property=typeof(QuestSchedulerAcceptedQuest).GetProperty("IsFailed");
                if(property!=null)property.SetValue(value,true);
            }
            return value;
        }
        var snapshot=new QuestSchedulerSnapshot{UtcNow=new DateTime(2026,9,18,0,0,0,DateTimeKind.Utc),PlayerLevel=20,PlayerRaceId=1,MapId=1,HasCompleteQuestLog=completeLog,HasAuthoritativeCompletions=authority,CompletedQuestIds=rewarded,AcceptedQuests=accepted.Select(Accepted).ToArray()};
        var failures=new List<QuestAttemptOutcome>();
        var r=QuestScheduler.MaterializeSchedule(db,snapshot,_=>new QuestRecoveryDecision{State=QuestRecoveryState.Eligible,MayAttempt=true,Status="eligible"},10,1000,7,navigationAssessment:_=>new SpawnNavigationAssessment{IsKnownSafe=true,IsKnownReachable=true},reportDataFailure:failures.Add);
        Check(!failures.Any(f=>f.Key.QuestId==200),"unmet prerequisite recorded as a child data failure");
        return r;
    }
    private static string Root()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj")))return d.FullName;
        throw new AssertionFailure("tracked checkout required");
    }
    private static void Check(bool ok,string reason){if(!ok)throw new AssertionFailure(reason);}
}

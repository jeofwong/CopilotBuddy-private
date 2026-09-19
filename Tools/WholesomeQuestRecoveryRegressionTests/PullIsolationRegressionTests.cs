using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Styx.Logic.Pathing;

// Opt-in dense-pack ranged-pull contract. This test uses pure geometry/context
// methods plus tracked-source ordering; it does not attach to a game or move/cast.
internal static class PullIsolationRegressionTests
{
    private sealed class AssertionFailure(string message):Exception(message){}
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Run()
    {
        var cases=new List<(string Name,System.Action Test)>
        {
            ("optional routine provider exists and Singular source declares it",()=>{
                var asm=typeof(Bots.Grind.LevelBot).Assembly;
                Type provider=RequireType(asm,"Styx.Combat.CombatRoutine.IIsolationPullProvider");
                Check(provider.GetMethod("CreateIsolationPullBehavior")!=null
                    && provider.GetProperty("IsolationPullDistance")!=null,
                    "provider contract does not expose behavior and practical range");
                string root=Checkout();
                string source=File.ReadAllText(Path.Combine(root,"runtime-snapshot","Routines","Singular wotlk","SingularRoutine.cs"));
                Check(source.Contains("public partial class SingularRoutine : CombatRoutine, IIsolationPullProvider",StringComparison.Ordinal),
                    "runtime-compiled Singular source did not opt into the provider");
            }),
            ("context policy is restricted to ordinary open-world NPC pulls",()=>{
                Type t=Coordinator();
                bool Eligible(bool dungeon=false,bool bg=false,bool arena=false,bool player=false,bool elite=false,bool transport=false)=>
                    (bool)Invoke(t,"IsContextEligible",dungeon,bg,arena,player,elite,transport)!;
                Check(Eligible(),"ordinary open-world target was rejected");
                Check(!Eligible(dungeon:true),"dungeon pull isolation was enabled");
                Check(!Eligible(bg:true),"battleground pull isolation was enabled");
                Check(!Eligible(arena:true),"arena pull isolation was enabled");
                Check(!Eligible(player:true),"player target entered PvE isolation policy");
                Check(!Eligible(elite:true),"elite target entered first-slice isolation policy");
                Check(!Eligible(transport:true),"transport movement entered isolation policy");
            }),
            ("retreat anchor extends the already-safe approach line away from target",()=>{
                Type t=Coordinator();
                var player=new WoWPoint(0,0,5);
                var target=new WoWPoint(20,0,5);
                var anchor=(WoWPoint)Invoke(t,"ComputeRetreatAnchor",player,target,10f)!;
                Check(Math.Abs(anchor.X+10)<0.01 && Math.Abs(anchor.Y)<0.01 && Math.Abs(anchor.Z-5)<0.01,
                    "retreat anchor did not extend away from target");
            }),
            ("coincident geometry fails closed instead of inventing a direction",()=>{
                Type t=Coordinator();
                var point=new WoWPoint(3,4,5);
                var anchor=(WoWPoint)Invoke(t,"ComputeRetreatAnchor",point,point,10f)!;
                Check(anchor==point,"coincident target/player invented a retreat vector");
            }),
            ("nearby social neighbor makes the target a dense-pack pull",()=>{
                Type t=Coordinator();
                Array obs=Observations(t,
                    O(1,new WoWPoint(20,0,0),20,false,true),
                    O(2,new WoWPoint(23,4,0),20,false,true));
                bool dense=(bool)Invoke(t,"HasDensePack",
                    new WoWPoint(20,0,0),new WoWPoint(0,0,0),obs,1ul,10f,2f)!;
                Check(dense,"socially adjacent hostile was ignored");
            }),
            ("isolated far neighbor does not manufacture pack risk",()=>{
                Type t=Coordinator();
                Array obs=Observations(t,
                    O(1,new WoWPoint(20,0,0),20,false,true),
                    O(2,new WoWPoint(60,40,0),20,false,true));
                bool dense=(bool)Invoke(t,"HasDensePack",
                    new WoWPoint(20,0,0),new WoWPoint(0,0,0),obs,1ul,10f,2f)!;
                Check(!dense,"distant hostile created a false dense pack");
            }),
            ("approach point inside another mob aggro envelope is risky even without social adjacency",()=>{
                Type t=Coordinator();
                Array obs=Observations(t,
                    O(1,new WoWPoint(20,0,0),20,false,true),
                    O(2,new WoWPoint(0,14,0),15,false,true));
                bool dense=(bool)Invoke(t,"HasDensePack",
                    new WoWPoint(20,0,0),new WoWPoint(0,0,0),obs,1ul,10f,2f)!;
                Check(dense,"pull point crossed another hostile aggro envelope");
            }),
            ("nonattackable and already-engaged observations do not create a fresh isolation plan",()=>{
                Type t=Coordinator();
                Array obs=Observations(t,
                    O(1,new WoWPoint(20,0,0),20,false,true),
                    O(2,new WoWPoint(22,1,0),20,false,false),
                    O(3,new WoWPoint(23,1,0),20,true,true));
                bool dense=(bool)Invoke(t,"HasDensePack",
                    new WoWPoint(20,0,0),new WoWPoint(0,0,0),obs,1ul,10f,2f)!;
                Check(!dense,"ineligible/previously engaged unit was treated as a fresh pack risk");
            }),
            ("retreat continues only while one owned target is still being peeled",()=>{
                Type t=Coordinator();
                bool Keep(bool same=true,bool alive=true,int engaged=1,bool safe=true,double fromPack=4,double toPlayer=15)=>
                    (bool)Invoke(t,"ShouldContinueRetreat",same,alive,engaged,safe,fromPack,toPlayer,10d,7d)!;
                Check(Keep(),"valid single-target peel was not continued");
                Check(!Keep(engaged:2),"second aggro did not abort retreat");
                Check(!Keep(same:false),"target replacement did not revoke retreat");
                Check(!Keep(alive:false),"dead target did not revoke retreat");
                Check(!Keep(safe:false),"unsafe anchor did not revoke retreat");
                Check(!Keep(fromPack:11),"already-separated target was kited farther");
                Check(!Keep(toPlayer:6),"target reaching close combat did not stop retreat");
            }),
            ("LevelBot orders isolation before ordinary pull and before ordinary combat damage",()=>{
                string root=Checkout();
                string source=File.ReadAllText(Path.Combine(root,"Bots","Grind","LevelBot.cs"));
                int pre=source.IndexOf("PullIsolationCoordinator.CreatePreCombatBehavior",StringComparison.Ordinal);
                int pull=source.IndexOf("Routine.PullBehavior",StringComparison.Ordinal);
                int retreat=source.IndexOf("PullIsolationCoordinator.CreateRetreatBehavior",StringComparison.Ordinal);
                int combat=source.IndexOf("Routine.CombatBehavior",StringComparison.Ordinal);
                Check(pre>=0&&pull>pre,"ordinary pull can bypass the isolation attempt");
                Check(retreat>=0&&combat>retreat,"ordinary combat can bypass an active retreat");
            }),
            ("Retribution isolation opener uses ranged damage and never taunt or melee closing",()=>{
                string root=Checkout();
                string source=File.ReadAllText(Path.Combine(root,"runtime-snapshot","Routines","Singular wotlk","ClassSpecific","Paladin","Retribution.cs"));
                const string marker="CreateRetributionPaladinIsolationPull";
                int start=source.IndexOf(marker,StringComparison.Ordinal);
                Check(start>=0,"Ret isolation opener is missing");
                int end=source.IndexOf("\n        private static",start+marker.Length,StringComparison.Ordinal);
                if(end<0) end=Math.Min(source.Length,start+5000);
                string body=source.Substring(start,end-start);
                Check(body.Contains("Exorcism",StringComparison.Ordinal),"Ret isolation opener has no ranged damage cast");
                Check(!body.Contains("Hand of Reckoning",StringComparison.Ordinal),"Ret isolation opener uses the taunt");
                Check(!body.Contains("CreateMoveToMeleeBehavior",StringComparison.Ordinal),"Ret isolation opener closes into the pack");
            }),
            ("Singular exposes isolation only as an explicit normal Ret capability",()=>{
                string root=Checkout();
                string source=File.ReadAllText(Path.Combine(root,"runtime-snapshot","Routines","Singular wotlk","SingularRoutine.cs"));
                Check(source.Contains("IIsolationPullProvider",StringComparison.Ordinal)
                    && source.Contains("CreateIsolationPullBehavior",StringComparison.Ordinal)
                    && source.Contains("RetributionPaladin",StringComparison.Ordinal)
                    && source.Contains("WoWContext.Normal",StringComparison.Ordinal),
                    "Singular isolation provider is not explicitly scoped to normal Ret");
            })
        };

        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases)
        {
            try{c.Test();passed++;Console.WriteLine("PASS pull isolation: "+c.Name);}
            catch(AssertionFailure e){assertions++;Console.Error.WriteLine("FAIL pull isolation assertion: "+c.Name+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR pull isolation fixture: "+c.Name+": "+e);}
        }
        Console.WriteLine($"Pull isolation scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; pure policy/source ordering; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Pull isolation regressions");
    }

    private sealed record Obs(ulong Guid,WoWPoint Location,float AggroRange,bool Engaged,bool Attackable);
    private static Obs O(ulong guid,WoWPoint p,float range,bool engaged,bool attackable)=>new(guid,p,range,engaged,attackable);

    private static Type Coordinator()=>RequireType(typeof(Bots.Grind.LevelBot).Assembly,"Levelbot.Actions.Combat.PullIsolationCoordinator");

    private static Type RequireType(Assembly asm,string name)=>
        asm.GetType(name,false) ?? throw new AssertionFailure(name+" is missing");

    private static object? Invoke(Type type,string method,params object?[] args)
    {
        MethodInfo? info=type.GetMethod(method,All);
        if(info==null)throw new AssertionFailure(type.FullName+"."+method+" is missing");
        try{return info.Invoke(null,args);}
        catch(TargetInvocationException e)when(e.InnerException!=null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;
        }
    }

    private static Array Observations(Type coordinator,params Obs[] values)
    {
        Type? observation=coordinator.Assembly.GetType("Levelbot.Actions.Combat.PullIsolationObservation",false);
        if(observation==null)throw new AssertionFailure("PullIsolationObservation is missing");
        Array array=Array.CreateInstance(observation,values.Length);
        for(int i=0;i<values.Length;i++)
        {
            object item=Activator.CreateInstance(observation) ?? throw new AssertionFailure("observation cannot be created");
            Set(item,"Guid",values[i].Guid);Set(item,"Location",values[i].Location);
            Set(item,"AggroRange",values[i].AggroRange);Set(item,"IsEngaged",values[i].Engaged);
            Set(item,"IsAttackable",values[i].Attackable);array.SetValue(item,i);
        }
        return array;
    }

    private static void Set(object owner,string property,object value)
    {
        PropertyInfo? p=owner.GetType().GetProperty(property,All);
        if(p==null)throw new AssertionFailure(owner.GetType().Name+"."+property+" is missing");
        p.SetValue(owner,value);
    }

    private static string Checkout()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj")))return d.FullName;
        throw new AssertionFailure("tracked checkout required");
    }

    private static void Check(bool ok,string why){if(!ok)throw new AssertionFailure(why);}
}

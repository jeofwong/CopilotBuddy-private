using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Singular.ClassSpecific.Paladin;
using Singular.Dynamics;
using Singular.Managers;
using Singular.Settings;
using Styx;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;

// Complete tracked Protection tree plus real TreeSharp. Spell/world/navigation
// and the default rest leaf are controlled boundaries, not a GatherBuddy simulation.
internal static class ProtectionRecoveryRegressionTests
{
    private sealed class AssertionFailure : Exception { internal AssertionFailure(string why) : base(why) { } }
    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, System.Action Test)>();
        foreach (bool grouped in new[] { false, true })
        foreach (bool multiple in new[] { false, true })
        {
            bool group=grouped, multi=multiple;
            cases.Add(($"no damage-filler HoJ group={group} multi={multi}",()=>{
                Setup(); StyxWoW.Me.IsInParty=group; Target();
                if(multi) Singular.Helpers.Unit.NearbyUnfriendlyUnits.Add(StyxWoW.Me.CurrentTarget!);
                Know("Hammer of Justice"); Fixture.Tick(Protection.CreateProtectionPaladinCombat()); None();
            }));
            cases.Add(($"damage continues instead of filler stun group={group} multi={multi}",()=>{
                Setup(); StyxWoW.Me.IsInParty=group; Target();
                if(multi) Singular.Helpers.Unit.NearbyUnfriendlyUnits.Add(StyxWoW.Me.CurrentTarget!);
                Know("Hammer of Justice","Judgement of Wisdom"); Fixture.Tick(Protection.CreateProtectionPaladinCombat()); Expect("Judgement of Wisdom",99);
            }));
        }
        cases.Add(("Protection Heal is registered exactly once",()=>{ Setup(); _=Heal(); }));
        foreach(string via in new[]{"Heal","Rest"})
        {
            string entry=via;
            void Add(string name,System.Action body)=>cases.Add((entry+": "+name,()=>{Setup();body();}));
            void Tick()=>Fixture.Tick(entry=="Heal"?Heal():Protection.CreateProtectionPaladinRest());
            Add("missing food does not hide an available self heal",()=>{StyxWoW.Me.HealthPercent=20;Know("Holy Light");Tick();Expect("Holy Light");Check(Fixture.DefaultRestCalls==0,"default wait preceded available healing");});
            Add("Flash of Light remains learned fallback",()=>{StyxWoW.Me.HealthPercent=40;Know("Flash of Light");Tick();Expect("Flash of Light");});
            Add("self healing does not require a hostile target",()=>{StyxWoW.Me.CurrentTarget=null;StyxWoW.Me.HealthPercent=20;Know("Holy Light");Tick();Expect("Holy Light");});
            Add("player target is not substituted for the healing recipient",()=>{Target();StyxWoW.Me.HealthPercent=20;Know("Holy Light");Tick();Expect("Holy Light");});
            Add("healthy low mana can recover with self Plea",()=>{StyxWoW.Me.ManaPercent=10;Know("Divine Plea");Tick();Expect("Divine Plea");});
            Add("low health heals before applying Plea healing penalty",()=>{StyxWoW.Me.HealthPercent=20;StyxWoW.Me.ManaPercent=10;Know("Holy Light","Divine Plea");Tick();Expect("Holy Light");});
            Add("low health with no heal does not start Plea",()=>{StyxWoW.Me.HealthPercent=20;StyxWoW.Me.ManaPercent=10;Know("Divine Plea");Tick();None();});
            Add("healthy full resources do not spend a heal or Plea",()=>{Know("Holy Light","Flash of Light","Divine Plea");Tick();None();});
            Add("an existing Plea aura is not recast",()=>{StyxWoW.Me.ManaPercent=10;Know("Divine Plea");Fixture.Aura(StyxWoW.Me,"Divine Plea",1);Tick();None();});
            Add("unavailable healing leaves ordinary rest reachable",()=>{StyxWoW.Me.HealthPercent=20;Know("Holy Light");Fixture.Unavailable.Add("Holy Light");Tick();None();if(entry=="Rest")Check(Fixture.DefaultRestCalls==1,"default recovery was suppressed");});
            foreach(string veto in new[]{"combat","mounted","transport","casting","channeling","moving","dead","ghost","invalid","food","drink","missing"})
            {
                string state=veto;
                Add("new recovery respects "+state,()=>{
                    StyxWoW.Me.HealthPercent=20;StyxWoW.Me.ManaPercent=10;Know("Holy Light","Flash of Light","Divine Plea");
                    switch(state){case "combat":StyxWoW.Me.Combat=true;break;case "mounted":StyxWoW.Me.Mounted=true;break;case "transport":StyxWoW.Me.IsOnTransport=true;break;case "casting":StyxWoW.Me.IsCasting=true;break;case "channeling":StyxWoW.Me.IsChanneling=true;break;case "moving":StyxWoW.Me.IsMoving=true;break;case "dead":StyxWoW.Me.IsAlive=false;break;case "ghost":StyxWoW.Me.IsGhost=true;break;case "invalid":StyxWoW.Me.IsValid=false;break;case "food":Fixture.Aura(StyxWoW.Me,"Food",1);break;case "drink":Fixture.Aura(StyxWoW.Me,"Drink",1);break;case "missing":StyxWoW.Me=null!;break;}
                    Tick();None();
                });
            }
        }
        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases)
        {
            try{c.Test();passed++;Console.WriteLine("PASS Protection recovery: "+c.Name);}
            catch(AssertionFailure e){assertions++;Console.Error.WriteLine("FAIL Protection recovery assertion: "+c.Name+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR Protection recovery fixture: "+c.Name+": "+e);}
        }
        Fixture.Reset();
        Console.WriteLine($"Protection recovery scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; linked complete Protection; controlled dispatch/default-rest; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException($"Protection regressions: assertions={assertions}; unexpected={unexpected}");
    }
    private static void Setup(){Fixture.Reset();TalentManager.CurrentSpec=TalentSpec.ProtectionPaladin;Fixture.DefaultRestResult=RunStatus.Success;}
    private static void Target()=>StyxWoW.Me.CurrentTarget=new WoWUnit{Guid=99,IsFriendly=false};
    private static void Know(params string[] names)=>Fixture.Known.UnionWith(names);
    private static Composite Heal()
    {
        var methods=typeof(Protection).GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m=>m.GetCustomAttributes<BehaviorAttribute>().Any(a=>a.Type==BehaviorType.Heal)).ToArray();
        Check(methods.Length==1,"missing/ambiguous Protection Heal factory");
        try{return (Composite)methods[0].Invoke(null,null)!;}
        catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }
    private static void Expect(string name,ulong target=1)=>Check(Fixture.Attempts.Count==1&&Fixture.Attempts[0]==(name,target),"expected "+name+" on "+target+", got "+string.Join(',',Fixture.Attempts));
    private static void None()=>Check(Fixture.Attempts.Count==0,"unexpected dispatch "+string.Join(',',Fixture.Attempts));
    private static void Check(bool ok,string why){if(!ok)throw new AssertionFailure(why);}
}

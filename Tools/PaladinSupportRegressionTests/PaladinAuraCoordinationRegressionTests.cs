using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Singular.ClassSpecific.Paladin;
using Singular.Dynamics;
using Singular.Managers;
using Singular.Settings;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using PaladinCommon = Singular.ClassSpecific.Paladin.Common;

// Linked production Common/PaladinSupport/Retribution and TreeSharp.
// Observed auras and cast dispatch use the unchanged support boundary fixture.
// The multi-player rounds exercise the real policy, not a server/network simulation.
internal static class PaladinAuraCoordinationRegressionTests
{
    private sealed class AssertionFailure : Exception { internal AssertionFailure(string why) : base(why) { } }
    private const string Ret = "Retribution Aura", Dev = "Devotion Aura", Con = "Concentration Aura";
    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, System.Action Test)>();
        foreach (var entry in new[] {
            (Spec:TalentSpec.RetributionPaladin, First:Ret, Second:Dev, Third:Con),
            (Spec:TalentSpec.HolyPaladin, First:Con, Second:Dev, Third:Ret),
            (Spec:TalentSpec.ProtectionPaladin, First:Dev, Second:Ret, Third:Con) })
        {
            var f=entry;
            void Add(string name,System.Action test) => cases.Add((f.Spec+": "+name,()=>{
                Fixture.Reset(); StyxWoW.Me.Guid=20; StyxWoW.Me.IsInParty=true;
                TalentManager.CurrentSpec=f.Spec; Know(Ret,Dev,Con); test(); }));
            Add("empty coverage uses the existing specialization preference",()=>{ Pre(); Expect(f.First); });
            Add("external first aura gets a complementary second",()=>{ Aura(f.First,10); Pre(); Expect(f.Second); });
            Add("two externally supplied effects get the third",()=>{ Aura(f.First,10); Aura(f.Second,11); Pre(); Expect(f.Third); });
            Add("all three supplied effects do not cause pointless switching",()=>{ Aura(f.First,10); Aura(f.Second,11); Aura(f.Third,12); Pre(); None(); });
            Add("a unique owned second aura remains useful when first is absent",()=>{ Aura(f.Second,20); Pre(); None(); });
            Add("a unique owned third aura remains useful when defaults are absent",()=>{ Aura(f.Third,20); Pre(); None(); });
            Add("permanent owned aura with zero duration remains active",()=>{ Aura(f.Second,20); StyxWoW.Me.ObservedAuras[0].TimeLeft=TimeSpan.Zero; Pre(); None(); });
            Add("inactive prior aura does not prevent recovery",()=>{ Aura(f.Second,20); StyxWoW.Me.ObservedAuras[0].IsActive=false; Pre(); Expect(f.First); });
            Add("lower known caster retains duplicated current effect",()=>{ Aura(f.First,20); Aura(f.First,30); Pre(); None(); });
            Add("higher known caster yields duplicated effect to lower caster",()=>{ Aura(f.First,20); Aura(f.First,10); Pre(); Expect(f.Second); });
            Add("unknown caster coverage cannot authorize a duplicate",()=>{ Aura(f.First,0); Pre(); Expect(f.Second); });
            Add("unknown duplicate owner does not force an unprovable reassignment",()=>{ Aura(f.First,20); Aura(f.First,0); Pre(); None(); });
            Add("no learned alternative means no invented spell",()=>{ Fixture.Known.Clear(); Know(f.First); Aura(f.First,10); Pre(); None(); });
            Add("unavailable preferred effect falls back to a castable one",()=>{ Fixture.Unavailable.Add(f.First); Pre(); Expect(f.Second); });
            Add("intermittent external coverage does not oscillate owned contribution",()=>{
                Aura(f.Second,20);
                for(int i=0;i<40;i++) { StyxWoW.Me.ObservedAuras.RemoveAll(a=>a.CreatorGuid==10); if(i%2==0) Aura(f.First,10); Pre(); }
                None();
            });
            Add("combat aura owner is registered and uses the same choice",()=>{ StyxWoW.Me.Combat=true; Aura(f.First,10); Combat(f.Spec); Expect(f.Second); });
            Add("combat aura owner respects active casting",()=>{ StyxWoW.Me.IsCasting=true; Combat(f.Spec); None(); });
            Add("combat aura owner respects an active channel",()=>{ StyxWoW.Me.IsChanneling=true; Combat(f.Spec); None(); });
        }
        foreach(var setting in Enum.GetValues<PaladinAura>().Where(x=>x!=PaladinAura.Auto))
        {
            var chosen=setting; string name=chosen==PaladinAura.Resistance ? "Shadow Resistance Aura" : chosen+" Aura";
            cases.Add(("manual "+chosen+" remains explicit",()=>{ Fixture.Reset(); Know(Ret,Dev,Con,name); SingularSettings.Instance.Paladin.Aura=chosen; Aura(chosen==PaladinAura.Devotion?Ret:Dev,1); Pre(); Expect(name); }));
            cases.Add(("manual "+chosen+" respects external coverage without choosing a different aura",()=>{ Fixture.Reset(); Know(Ret,Dev,Con,name); SingularSettings.Instance.Paladin.Aura=chosen; Aura(name,99); Pre(); None(); }));
        }
        cases.Add(("zero player identity cannot acquire an aura assignment",()=>{ Fixture.Reset(); StyxWoW.Me.Guid=0; Know(Ret,Dev,Con); Pre(); None(); }));
        cases.Add(("two identical automatic Paladins converge instead of swapping together",()=>Converge(2)));
        cases.Add(("three identical automatic Paladins converge on three distinct effects",()=>Converge(3)));
        cases.Add(("PvP combat does not overwrite manual Devotion with hard-coded Retribution",()=>{
            Fixture.Reset(); Know(Ret,Dev); SingularSettings.Instance.Paladin.Aura=PaladinAura.Devotion; Aura(Dev,1);
            Fixture.Tick(Retribution.CreateRetributionPaladinPvPPullAndCombat()); None();
        }));
        cases.Add(("PvP combat does not duplicate another Paladin's Retribution effect",()=>{
            Fixture.Reset(); Know(Ret,Dev); Aura(Ret,99); Aura(Dev,1);
            Fixture.Tick(Retribution.CreateRetributionPaladinPvPPullAndCombat()); None();
        }));
        cases.Add(("low-level combat owner is registered without inventing unlearned auras",()=>{
            Fixture.Reset(); TalentManager.CurrentSpec=TalentSpec.Lowbie; Know(Dev); Combat(TalentSpec.Lowbie); Expect(Dev);
        }));
        int passed=0,assertions=0,unexpected=0;
        foreach(var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS Paladin aura coordination: "+item.Name); }
            catch(AssertionFailure e) { assertions++; Console.Error.WriteLine("FAIL Paladin aura coordination assertion: "+item.Name+": "+e.Message); }
            catch(Exception e) { unexpected++; Console.Error.WriteLine("ERROR Paladin aura coordination fixture: "+item.Name+": "+e); }
        }
        Fixture.Reset();
        Console.WriteLine($"Paladin aura coordination scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; linked production selectors/trees; controlled observations; no game attached.");
        if(assertions+unexpected!=0) throw new InvalidOperationException($"Paladin aura regressions: assertions={assertions}; unexpected={unexpected}");
    }
    private static void Converge(int count)
    {
        var owned=Enumerable.Range(1,count).ToDictionary(i=>(ulong)i,_=>Ret);
        int changes=0;
        for(int round=0;round<8;round++)
        {
            var next=new Dictionary<ulong,string>(owned);
            foreach(var p in owned)
            {
                Fixture.Reset(); StyxWoW.Me.Guid=p.Key; StyxWoW.Me.IsInParty=true; Know(Ret,Dev,Con);
                foreach(var observed in owned) Aura(observed.Value,observed.Key);
                Pre(); Check(Fixture.Attempts.Count<=1,"one decision submitted multiple auras");
                if(Fixture.Attempts.Count==1) { next[p.Key]=Fixture.Attempts[0].Spell; changes++; }
            }
            owned=next;
        }
        Check(owned.Values.Distinct().Count()==count,"identical Paladins remain on duplicated effects: "+string.Join(',',owned.Values));
        Check(changes<=count*(count-1)/2,"assignment kept changing after convergence: "+changes);
    }
    private static void Combat(TalentSpec spec)
    {
        var methods=typeof(PaladinCommon).GetMethods(BindingFlags.Public|BindingFlags.Static)
            .Where(m=>m.GetCustomAttributes<BehaviorAttribute>().Any(a=>a.Type==BehaviorType.CombatBuffs)
                &&m.GetCustomAttributes<SpecAttribute>().Any(a=>a.SpecificSpec==spec)
                &&m.GetCustomAttributes<ContextAttribute>().Any(a=>a.SpecificContext==WoWContext.All)).ToArray();
        Check(methods.Length==1,"expected one shared combat aura owner for "+spec+", got "+methods.Length);
        try { Fixture.Tick((Composite)methods[0].Invoke(null,null)!); }
        catch(TargetInvocationException e) when(e.InnerException!=null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    private static void Know(params string[] spells)=>Fixture.Known.UnionWith(spells);
    private static void Aura(string name,ulong owner)=>Fixture.Aura(StyxWoW.Me,name,owner);
    private static void Pre()=>Fixture.Tick(PaladinCommon.CreatePaladinPreCombatBuffs());
    private static void Expect(string spell)=>Check(Fixture.Attempts.Count==1&&Fixture.Attempts[0]==(spell,StyxWoW.Me.Guid),"expected "+spell+", got "+string.Join(',',Fixture.Attempts));
    private static void None()=>Check(Fixture.Attempts.Count==0,"unexpected cast: "+string.Join(',',Fixture.Attempts));
    private static void Check(bool ok,string why) { if(!ok) throw new AssertionFailure(why); }
}

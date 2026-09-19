using System.Runtime.CompilerServices;
using Singular.ClassSpecific.Paladin;
using Singular.Managers;
using Singular.Settings;
using Styx;
using TreeSharp;

// Complete tracked Lowbie.cs is linked; only unrelated movement/dispatch is controlled.
internal static class LowbieAuraCoordinationRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        int total=0, passed=0, assertions=0, unexpected=0;
        foreach(var entry in new (string Name, Func<Composite> Build)[] {
            ("precombat",Lowbie.CreateLowbiePaladinPreCombatBuffs),
            ("combat",Lowbie.CreateLowbiePaladinCombatBuffs) })
        foreach(string scenario in new[]{"manual", "own-complement", "external-cover", "only-known", "seal-control"})
        {
            total++;
            try
            {
                Fixture.Reset(); TalentManager.CurrentSpec=TalentSpec.Lowbie;
                Fixture.Known.UnionWith(new[]{"Devotion Aura","Retribution Aura"});
                string? expected=null;
                if(scenario=="manual") { SingularSettings.Instance.Paladin.Aura=PaladinAura.Retribution; Fixture.Aura(StyxWoW.Me,"Retribution Aura",1); }
                if(scenario=="own-complement") { Fixture.Aura(StyxWoW.Me,"Retribution Aura",1); }
                if(scenario=="external-cover") { Fixture.Aura(StyxWoW.Me,"Devotion Aura",99); Fixture.Aura(StyxWoW.Me,"Retribution Aura",1); }
                if(scenario=="only-known") { Fixture.Known.Clear(); Fixture.Known.Add("Devotion Aura"); expected="Devotion Aura"; }
                if(scenario=="seal-control") { Fixture.Known.Add("Seal of Righteousness"); expected="Seal of Righteousness"; }
                Fixture.Tick(entry.Build());
                bool ok=expected==null ? Fixture.Attempts.Count==0 : Fixture.Attempts.Count==1&&Fixture.Attempts[0]==(expected,1UL);
                if(!ok) { assertions++; Console.Error.WriteLine("FAIL lowbie aura assertion: "+entry.Name+" "+scenario+": "+string.Join(',',Fixture.Attempts)); }
                else { passed++; Console.WriteLine("PASS lowbie aura: "+entry.Name+" "+scenario); }
            }
            catch(Exception e) { unexpected++; Console.Error.WriteLine("ERROR lowbie aura fixture: "+entry.Name+" "+scenario+": "+e); }
        }
        Fixture.Reset();
        Console.WriteLine($"Lowbie aura coordination scenarios: {passed}/{total}; assertions={assertions}; unexpected={unexpected}; linked full Lowbie; controlled observations; no game attached.");
        if(assertions+unexpected!=0) throw new InvalidOperationException("Lowbie aura regression");
    }
}

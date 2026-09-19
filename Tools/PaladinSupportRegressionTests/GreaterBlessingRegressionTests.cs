using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Singular.ClassSpecific.Paladin;
using Singular.Settings;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Combat;
using TreeSharp;
using PaladinCommon=Singular.ClassSpecific.Paladin.Common;

// Actual Common/PaladinSupport policy and trees, controlled spell metadata,
// inventory counts and roster observations. No server cast or reagent consumption.
internal static class GreaterBlessingRegressionTests
{
    private sealed class AssertionFailure:Exception { internal AssertionFailure(string why):base(why){} }
    [ModuleInitializer]
    internal static void Run()
    {
        var cases=new List<(string Name,System.Action Test)>();
        foreach(string family in new[]{"Kings","Might","Wisdom","Sanctuary"})
        {
            string f=family,n="Blessing of "+family,g="Greater Blessing of "+family;
            void Add(string name,System.Action test)=>cases.Add((f+": "+name,()=>{Setup(f);test();}));
            Add("normal remains default",()=>{Pre();Expect(n);});
            Add("opt-in selects learned reagent-backed Greater",()=>{Enable();Pre();Expect(g);});
            Add("unknown Greater retains normal",()=>{Enable();Fixture.Known.Remove(g);Pre();Expect(n);});
            Add("unavailable Greater retains normal",()=>{Enable();Fixture.Unavailable.Add(g);Pre();Expect(n);});
            Add("no reagent retains normal",()=>{Enable();StyxWoW.Me.ItemCounts.Clear();Pre();Expect(n);});
            Add("missing metadata retains normal",()=>{Enable();Fixture.Metadata.Clear();Pre();Expect(n);});
            Add("missing reagent array retains normal",()=>{Enable();Fixture.Metadata[g].InternalInfo.Reagent=null;Pre();Expect(n);});
            Add("mismatched reagent count array retains normal",()=>{Enable();Fixture.Metadata[g].InternalInfo.ReagentCount=new uint[1];Pre();Expect(n);});
            Add("zero-count positive reagent is unknown",()=>{Enable();Fixture.Metadata[g].InternalInfo.ReagentCount![0]=0;Pre();Expect(n);});
            Add("duplicate reagent slots require their total quantity",()=>{Enable();Fixture.Metadata[g].InternalInfo.Reagent![1]=777;Fixture.Metadata[g].InternalInfo.ReagentCount![1]=1;Pre();Expect(n);});
            Add("existing own normal is not upgraded every pulse",()=>{Enable();Fixture.Aura(StyxWoW.Me,n,1);for(int i=0;i<20;i++)Pre();None();});
            Add("external Greater covers the same family",()=>{Enable();Fixture.Aura(StyxWoW.Me,g,99);Pre();None();});
            Add("coherent uncovered same-class roster can use Greater",()=>{Enable();Fixture.Add(WoWClass.Paladin);Pre();Expect(g);});
            Add("other class coverage does not block a safe class buff",()=>{Enable();var p=Fixture.Add(WoWClass.Warrior);Fixture.Aura(p,n,99);Pre();Expect(g);});
            Add("same-class external normal coverage uses single-target fallback",()=>{Enable();var p=Fixture.Add(WoWClass.Paladin);Fixture.Aura(p,n,99);Pre();Expect(n);});
            Add("same-class distinct owned assignment is preserved",()=>{Enable();var p=Fixture.Add(WoWClass.Paladin);Fixture.Aura(p,f=="Kings"?"Blessing of Might":"Blessing of Kings",1);Pre();Expect(n);});
            Add("unobservable same-class member cannot authorize a mass buff",()=>{Enable();var p=Fixture.Add(WoWClass.Paladin);p.IsValid=false;Pre();Expect(n);});
            Add("same-class member outside observation range uses normal",()=>{Enable();Fixture.Add(WoWClass.Paladin).Distance=80;Pre();Expect(n);});
            Add("combat does not start a mass rebuff",()=>{Enable();StyxWoW.Me.Combat=true;Pre();Expect(n);});
            Add("reagent loss invalidates the captured Greater action",()=>{Enable();var a=Capture();Check(Spell(a)==g,"expected a Greater candidate");StyxWoW.Me.ItemCounts.Clear();Check(!Valid(a,g),"stale reagent permission survived");});
            Add("new same-class coverage invalidates a captured mass action",()=>{Enable();var p=Fixture.Add(WoWClass.Paladin);var a=Capture();Check(Spell(a)==g,"expected Greater candidate");Fixture.Aura(p,n,99);Check(!Valid(a,g),"stale class coverage survived");});
        }
        cases.Add(("Might preserves another same-class member's Battle Shout",()=>{Setup("Might");Enable();var p=Fixture.Add(WoWClass.Paladin);Fixture.Aura(p,"Battle Shout",99);Pre();Expect("Blessing of Might");}));
        cases.Add(("Sanctuary setting appends without renumbering existing values",()=>{Check((int)PaladinBlessings.Auto==0&&(int)PaladinBlessings.Kings==1&&(int)PaladinBlessings.Might==2&&(int)PaladinBlessings.Wisdom==3,"old enum values changed");Check(Enum.TryParse<PaladinBlessings>("Sanctuary",out var s)&&(int)s==4,"Sanctuary missing or shifted");}));
        cases.Add(("Sanctuary is not invented when unlearned",()=>{Setup("Sanctuary");Fixture.Known.Clear();Pre();None();}));
        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases)
        {
            try{c.Test();passed++;Console.WriteLine("PASS Greater blessing: "+c.Name);}
            catch(AssertionFailure e){assertions++;Console.Error.WriteLine("FAIL Greater blessing assertion: "+c.Name+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR Greater blessing fixture: "+c.Name+": "+e);}
        }
        Fixture.Reset();
        Console.WriteLine($"Greater blessing scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; linked policy; controlled metadata/roster/dispatch; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException($"Greater blessing regressions: assertions={assertions}; unexpected={unexpected}");
    }
    private static void Setup(string family)
    {
        Fixture.Reset();
        Check(Enum.TryParse<PaladinBlessings>(family,out var setting),"missing setting "+family);
        SingularSettings.Instance.Paladin.Blessings=setting;
        string n="Blessing of "+family,g="Greater "+n;
        Fixture.Known.UnionWith(new[]{n,g});
        var spell=new WoWSpell();spell.InternalInfo.Reagent![0]=777;spell.InternalInfo.ReagentCount![0]=1;
        Fixture.Metadata[g]=spell;StyxWoW.Me.ItemCounts[777]=1;
    }
    private static void Enable()=>SingularSettings.Instance.Paladin.UseGreaterBlessings=true;
    private static void Pre()=>Fixture.Tick(PaladinCommon.CreatePaladinPreCombatBuffs());
    private static object Capture()=>Invoke("FindBlessingAction")!;
    private static string? Spell(object? a)=>a?.GetType().GetField("Spell",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(a) as string;
    private static bool Valid(object a,string spell)=>(bool)Invoke("ValidSupportAction",a,spell)!;
    private static object? Invoke(string name,params object[] args)
    {
        try{return typeof(PaladinCommon).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,args);}
        catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }
    private static void Expect(string spell)=>Check(Fixture.Attempts.Count==1&&Fixture.Attempts[0]==(spell,StyxWoW.Me.Guid),"expected "+spell+", got "+string.Join(',',Fixture.Attempts));
    private static void None()=>Check(Fixture.Attempts.Count==0,"unexpected "+string.Join(',',Fixture.Attempts));
    private static void Check(bool ok,string why){if(!ok)throw new AssertionFailure(why);}
}

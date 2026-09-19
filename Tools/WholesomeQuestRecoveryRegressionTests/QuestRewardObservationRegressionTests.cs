using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Reward choices are an original-client observation. Missing/partial choice identity
// must defer turn-in rather than clicking a guessed index. No game/client attached.
internal static class QuestRewardObservationRegressionTests
{
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");

        string source = File.ReadAllText(Path.Combine(root, "Bots", "Quest", "Actions", "ActionSelectReward.cs"));
        Type owner = typeof(Bots.Quest.Actions.ActionSelectReward);
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        MethodInfo? read = owner.GetMethod("TryObserveLiveChoices", Flags);

        bool Observe(int count, Func<int,string?> link, Func<int,int?> stack, out IList choices)
        {
            if (read == null) throw new AssertionFailure("live reward-choice observation contract is missing");
            object?[] args = { (Func<int>)(() => count), link, stack, null };
            bool ok = (bool)(read.Invoke(null, args) ?? false);
            choices = args[3] as IList ?? Array.Empty<object>();
            return ok;
        }

        string Link(uint id) => $"|cffa335ee|Hitem:{id}:0:0:0:0:0:0:0|h[Test {id}]|h|r";

        var cases = new List<(string Name, Action Test)>
        {
            ("zero live choices is a complete empty observation", () =>
            {
                int links=0, stacks=0;
                bool ok=Observe(0,_=>{links++;return null;},_=>{stacks++;return null;},out IList choices);
                Check(ok && choices.Count==0 && links==0 && stacks==0, "zero-choice observation queried or fabricated items");
            }),
            ("two complete live choices preserve index id count and link", () =>
            {
                bool ok=Observe(2,i=>Link((uint)(1000+i)),i=>i+1,out IList choices);
                Check(ok && choices.Count==2, "complete choices were not observed");
                Check(Value(choices[0]!, "Index")=="0" && Value(choices[0]!, "ItemId")=="1000"
                    && Value(choices[0]!, "Count")=="1" && Value(choices[0]!, "ItemLink").Contains("Hitem:1000:"),
                    "first observed choice changed identity");
                Check(Value(choices[1]!, "Index")=="1" && Value(choices[1]!, "ItemId")=="1001"
                    && Value(choices[1]!, "Count")=="2", "second observed choice changed identity");
            }),
            ("one missing link invalidates the whole choice set", () =>
            {
                bool ok=Observe(2,i=>i==0?Link(1000):null,_=>1,out IList choices);
                Check(!ok && choices.Count==0, "partial reward identity was treated as complete");
            }),
            ("malformed link cannot borrow an unrelated number", () =>
            {
                bool ok=Observe(1,_=>"reward 999 |Hspell:123|h[x]|h",_=>1,out IList choices);
                Check(!ok && choices.Count==0, "non-item link became an item identity");
            }),
            ("zero item id is invalid", () =>
            {
                bool ok=Observe(1,_=>Link(0),_=>1,out IList choices);
                Check(!ok && choices.Count==0, "item zero was accepted");
            }),
            ("missing stack count invalidates whole choice set", () =>
            {
                bool ok=Observe(2,i=>Link((uint)(1000+i)),i=>i==0?1:null,out IList choices);
                Check(!ok && choices.Count==0, "unknown reward count was treated as known");
            }),
            ("zero or negative stack count is invalid", () =>
            {
                Check(!Observe(1,_=>Link(1000),_=>0,out IList a) && a.Count==0, "zero reward count was accepted");
                Check(!Observe(1,_=>Link(1000),_=>-1,out IList b) && b.Count==0, "negative reward count was accepted");
            }),
            ("unreasonable choice count fails before item queries", () =>
            {
                int calls=0;
                bool ok=Observe(65,_=>{calls++;return Link(1);},_=>{calls++;return 1;},out IList choices);
                Check(!ok && choices.Count==0 && calls==0, "unbounded live choice count was enumerated");
            }),
            ("observer exception is not converted to an empty authoritative set", () =>
            {
                var marker=new OperationCanceledException("stop");
                Exception? seen=null;
                try { Observe(1,_=>throw marker,_=>1,out _); }
                catch(TargetInvocationException e) when(e.InnerException!=null) { seen=e.InnerException; }
                catch(Exception e) { seen=e; }
                Check(ReferenceEquals(seen,marker), "cancellation/observer exception identity changed");
            }),
            ("production source no longer clicks arbitrary reward one", () =>
                Check(!source.Contains("QuestInfoItem1:Click()",StringComparison.Ordinal),
                    "unknown reward identity still clicks first reward")),
            ("production source consults original-client live count and links", () =>
                Check(source.Contains("GetNumQuestChoices()",StringComparison.Ordinal)
                    && source.Contains("GetQuestItemLink('choice'",StringComparison.Ordinal)
                    && source.Contains("TryObserveLiveChoices",StringComparison.Ordinal),
                    "reward owner does not use live original-client observation fallback")),
            ("failed live observation prevents success", () =>
            {
                int i=source.IndexOf("TryObserveLiveChoices",StringComparison.Ordinal);
                Check(i>=0, "live observation call missing");
                string region=source.Substring(i,Math.Min(2500,source.Length-i));
                Check(region.Contains("RunStatus.Failure",StringComparison.Ordinal),
                    "unresolved live reward identity can still continue turn-in");
            })
        };

        int passed=0, assertions=0, unexpected=0;
        foreach(var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS quest reward observation: "+item.Name); }
            catch(AssertionFailure e) { assertions++; Console.Error.WriteLine("FAIL quest reward observation: "+item.Name+": "+e.Message); }
            catch(Exception e) { unexpected++; Console.Error.WriteLine("ERROR quest reward observation: "+item.Name+": "+e); }
        }
        Console.WriteLine($"Quest reward-observation scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual ActionSelectReward contract/source; controlled observers; no game attached.");
        if(assertions+unexpected!=0) throw new InvalidOperationException("Quest reward-observation regression");
    }

    private static string Value(object target,string name)
    {
        object? value=target.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(target);
        return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static void Check(bool ok,string reason)
    {
        if(!ok) throw new AssertionFailure(reason);
    }
}

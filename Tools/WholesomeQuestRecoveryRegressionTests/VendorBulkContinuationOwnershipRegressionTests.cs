using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Execute the actual public bulk method and reset. Start admission and the
// external merchant callback are controlled; existing admission tests stay intact.
internal static class VendorBulkContinuationOwnershipRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");
        string source = File.ReadAllText(Path.Combine(root, "Styx", "Logic", "Vendors.cs"));
        string Slice(string first, string last)
        {
            int start = source.IndexOf(first, StringComparison.Ordinal);
            int end = start < 0 ? -1 : source.IndexOf(last, start + first.Length, StringComparison.Ordinal);
            if (start < 0 || end < start || source.IndexOf(first, start + first.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("Bulk owner extraction boundary changed");
            return source.Substring(start, end - start);
        }
        string methods = Slice("\t\tpublic static void SellAllItems()", "\t\tinternal static bool SellAllItemsStep()")
            + Slice("\t\tprivate static void ResetSellSession()", "\t\t/// <summary>");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        string boundary = (string)typeof(VendorSaleContinuationOwnershipRegressionTests).GetField("Boundary", flags)!.GetRawConstantValue()!;
        const string marker = "private sealed class MerchantStub\n    {";
        if (boundary.Split(marker).Length != 2) throw new InvalidOperationException("Shared external merchant fixture changed");
        boundary = boundary.Replace(marker, marker + "\n        public void SellItemQualities(ItemQuality q,IEnumerable<string> n,IEnumerable<uint> i){Queries++;SeenQualities=q;SeenNames=n;SeenIds=i;var callback=DuringQuery;DuringQuery=null;callback?.Invoke();}\n");
        string temp = Path.Combine(Path.GetTempPath(), "cb-bulk-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool logging = Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            // Include the actual stepped method because the shared fixture retains
            // its older controls. Only RunBulk below executes in this test group.
            string step = Slice("\t\tprivate static bool ContinueSellSession()", "\t\tprivate static void ResetSellSession()");
            File.WriteAllText(Path.Combine(temp, "Owner.cs"), "using System;using System.Linq;using System.Collections.Generic;using Styx.Logic.Inventory.Frames.Merchant;public static partial class SaleOwnerCases{\n" + methods + step + BulkCases + "\n}");
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), boundary);
            var type = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(type, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference", flags)!.Invoke(compiler, new object[] { path });
            var result = (CompilerResults)type.GetMethod("Compile", flags)!.Invoke(compiler, null)!;
            var errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Actual bulk owner compile: " + string.Join(";", errors.Select(e => e.ToString())));
            var assembly = (Assembly)type.GetProperty("CompiledAssembly", flags)!.GetValue(compiler)!;
            try { assembly.GetType("SaleOwnerCases", true)!.GetMethod("RunBulk")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = logging; Directory.Delete(temp, true); }
    }
    private const string BulkCases = """
    private static bool StartAllows;
    private static Action? DuringStart;
    private static ItemQuality SeenQualities;
    private static IEnumerable<string>? SeenNames;
    private static IEnumerable<uint>? SeenIds;
    private static bool StartSellSession(){var callback=DuringStart;DuringStart=null;callback?.Invoke();return StartAllows;}
    public static void RunBulk()
    {
        var cases=new List<(string,Action)>();
        void Add(string name,Action body)=>cases.Add((name,()=>{Reset();StartAllows=true;DuringStart=null;SeenNames=null;SeenIds=null;SeenQualities=ItemQuality.None;body();}));
        Add("denied admission cannot submit or clear force request",()=>{StartAllows=false;var token=_sellSessionCandidate;SellAllItems();Check(Queries==0&&ReferenceEquals(token,_sellSessionCandidate)&&ForceSell,"denied start mutated owner");});
        Add("stable bulk pass retains old completion contract",()=>{SellAllItems();Check(Queries==1&&!_sellSessionActive&&_sellSessionCandidate==null&&!ForceSell,"stable completion changed");});
        Add("replacement during bulk callback keeps new owner",()=>{DuringQuery=Replace;SellAllItems();CheckReplacement();Check(Queries==1,"bulk callback retried");});
        Add("reset during bulk callback preserves force request",()=>{DuringQuery=ResetSellSession;SellAllItems();CheckReset();});
        Add("replacement can complete on its own later call",()=>{DuringQuery=Replace;SellAllItems();CheckReplacement();SellAllItems();Check(Queries==2&&!_sellSessionActive&&!ForceSell,"new owner could not complete");});
        Add("inactive contradictory admission cannot submit",()=>{_sellSessionActive=false;SellAllItems();Check(Queries==0&&ForceSell,"inactive owner submitted");});
        Add("missing owner identity cannot submit",()=>{_sellSessionCandidate=null;SellAllItems();Check(Queries==0&&ForceSell,"missing owner submitted");});
        Add("admission reset cannot become a new request",()=>{DuringStart=ResetSellSession;SellAllItems();Check(Queries==0,"reset admission queried merchant");CheckReset();});
        Add("newly admitted owner supplies its own protection",()=>{DuringStart=Replace;SellAllItems();Check(Queries==1&&ReferenceEquals(SeenNames,ReplacementNames)&&ReferenceEquals(SeenIds,ReplacementIds)&&SeenQualities==ItemQuality.Rare,"wrong admission data");Check(!_sellSessionActive&&!ForceSell,"valid admission not completed");});
        Add("stable request passes the captured exclusions unchanged",()=>{var names=_sellSessionProtectedNames;var ids=_sellSessionProtectedIds;SellAllItems();Check(ReferenceEquals(names,SeenNames)&&ReferenceEquals(ids,SeenIds)&&SeenQualities==ItemQuality.Poor,"bulk exclusions changed");});
        foreach(int kind in new[]{0,1,2})foreach(bool replace in new[]{false,true})
        {
            int k=kind;bool replacement=replace;
            Add("bulk callback signal "+k+" replacement="+replacement,()=>{
                var original=_sellSessionCandidate;Exception signal=k==0?new InvalidOperationException("external failure"):k==1?new OperationCanceledException("stop"):new System.Threading.ThreadInterruptedException("stop");
                DuringQuery=()=>{if(replacement)Replace();throw signal;};
                try{SellAllItems();throw new Failure("signal swallowed");}catch(Exception seen){Check(ReferenceEquals(signal,seen),"signal replaced");}
                if(replacement)CheckReplacement();else Check(_sellSessionActive&&ForceSell&&ReferenceEquals(original,_sellSessionCandidate)&&_sellSessionStackCount==2,"failed callback completed old owner");
            });
        }
        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases){try{c.Item2();passed++;Console.WriteLine("PASS bulk continuation owner: "+c.Item1);}catch(Failure e){assertions++;Console.Error.WriteLine("FAIL bulk continuation owner assertion: "+c.Item1+": "+e.Message);}catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR bulk continuation owner fixture: "+c.Item1+": "+e);}}
        Console.WriteLine($"Bulk continuation ownership scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual public bulk/reset; controlled admission/merchant callbacks; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Bulk continuation ownership regressions");
    }
""";
}

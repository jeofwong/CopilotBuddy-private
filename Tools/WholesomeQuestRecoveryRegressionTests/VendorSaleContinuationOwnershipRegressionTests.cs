using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Exact tracked continuation and reset; merchant results and logging are the
// controlled callback boundaries. No native sale or full vendor root is run.
internal static class VendorSaleContinuationOwnershipRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");
        string source = File.ReadAllText(Path.Combine(root, "Styx", "Logic", "Vendors.cs"));
        const string begin = "\t\tprivate static bool ContinueSellSession()";
        int start = source.IndexOf(begin, StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf("\t\t/// <summary>", start + begin.Length, StringComparison.Ordinal);
        if (start < 0 || end < start || source.IndexOf(begin, start + begin.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Vendor continuation extraction changed");
        string region = source.Substring(start, end - start);
        if (!region.Contains("private static void ResetSellSession()")) throw new InvalidOperationException("Actual reset missing");
        string temp = Path.Combine(Path.GetTempPath(), "cb-sale-continuation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool logging = Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            File.WriteAllText(Path.Combine(temp, "Owner.cs"), "using System;using System.Linq;using System.Collections.Generic;using Styx.Logic.Inventory.Frames.Merchant;public static partial class SaleOwnerCases{\n" + region + "\n}");
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), Boundary);
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var type = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(type, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference", f)!.Invoke(compiler, new object[] { path });
            var result = (CompilerResults)type.GetMethod("Compile", f)!.Invoke(compiler, null)!;
            var errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Actual vendor continuation compile: " + string.Join(";", errors.Select(e => e.ToString())));
            var assembly = (Assembly)type.GetProperty("CompiledAssembly", f)!.GetValue(compiler)!;
            try { assembly.GetType("SaleOwnerCases", true)!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = logging; Directory.Delete(temp, true); }
    }
    private const string Boundary = """
using System;using System.Linq;using System.Collections.Generic;using Styx.Logic.Inventory.Frames.Merchant;
public static partial class SaleOwnerCases
{
    private sealed class Failure(string message):Exception(message){}
    private static bool _sellSessionActive,ForceSell;
    private static object? _sellSessionCandidate;
    private static ItemQuality _sellSessionQualities;
    private static List<string>? _sellSessionProtectedNames;
    private static List<uint>? _sellSessionProtectedIds;
    private static int _sellSessionStackCount;
    private static readonly MerchantStub _merchantFrame=new();
    private static int Result,Queries;
    private static Action? DuringQuery,DuringLog;
    private static readonly List<string> Messages=new();
    private static object? Replacement;
    private static List<string>? ReplacementNames;
    private static List<uint>? ReplacementIds;
    private sealed class MerchantStub
    {
        public int SellNextItemQualities(ItemQuality q,IEnumerable<string> n,IEnumerable<uint> i)
        {Queries++;var callback=DuringQuery;DuringQuery=null;callback?.Invoke();return Result;}
    }
    private static class Logging
    {
        public static void Write(string text,params object[] args){Messages.Add(string.Format(text,args));var callback=DuringLog;DuringLog=null;callback?.Invoke();}
        public static void WriteDebug(string text)=>Write(text);
    }
    private static void Reset()
    {
        _sellSessionActive=ForceSell=true;_sellSessionCandidate=new();_sellSessionQualities=ItemQuality.Poor;
        _sellSessionProtectedNames=new(){"old protected"};_sellSessionProtectedIds=new(){100};_sellSessionStackCount=2;
        Result=Queries=0;DuringQuery=DuringLog=null;Replacement=null;ReplacementNames=null;ReplacementIds=null;Messages.Clear();
    }
    private static void Replace()
    {
        Replacement=new();ReplacementNames=new(){"new protected"};ReplacementIds=new(){200};
        _sellSessionCandidate=Replacement;_sellSessionActive=ForceSell=true;_sellSessionStackCount=40;
        _sellSessionProtectedNames=ReplacementNames;_sellSessionProtectedIds=ReplacementIds;_sellSessionQualities=ItemQuality.Rare;
    }
    private static void CheckReplacement()
    {
        Check(ReferenceEquals(_sellSessionCandidate,Replacement)&&_sellSessionActive&&ForceSell&&_sellSessionStackCount==40
            &&ReferenceEquals(_sellSessionProtectedNames,ReplacementNames)&&ReferenceEquals(_sellSessionProtectedIds,ReplacementIds)
            &&_sellSessionQualities==ItemQuality.Rare,"obsolete continuation changed replacement session");
    }
    private static void CheckReset()
    {
        Check(_sellSessionCandidate==null&&!_sellSessionActive&&ForceSell&&_sellSessionStackCount==0
            &&_sellSessionProtectedNames==null&&_sellSessionProtectedIds==null,"obsolete continuation changed reset state/request");
    }
    public static void Run()
    {
        var cases=new List<(string,Action)>();void Add(string name,Action body)=>cases.Add((name,()=>{Reset();body();}));
        Add("inactive session cannot submit a sale",()=>{ResetSellSession();Check(!ContinueSellSession()&&Queries==0,"inactive session queried merchant");CheckReset();});
        Add("active flag without a session identity cannot submit",()=>{_sellSessionCandidate=null;Check(!ContinueSellSession()&&Queries==0,"missing owner queried merchant");});
        foreach(int code in new[]{-1,0,1,2,3,4,777})
        {
            int value=code;
            Add("replacement during merchant result "+value,()=>{Result=value;DuringQuery=Replace;Check(!ContinueSellSession(),"old result reported completion");CheckReplacement();Check(Messages.Count==0,"old result emitted completion diagnostics");});
            Add("reset during merchant result "+value,()=>{Result=value;DuringQuery=ResetSellSession;Check(!ContinueSellSession(),"reset result reported completion");CheckReset();Check(Messages.Count==0,"reset result emitted completion diagnostics");});
        }
        foreach(int code in new[]{0,3,4})
        {
            int value=code;
            Add("replacement during completion diagnostic "+value,()=>{Result=value;DuringLog=Replace;Check(!ContinueSellSession(),"old diagnostic completed replacement");CheckReplacement();});
            Add("reset during completion diagnostic "+value,()=>{Result=value;DuringLog=ResetSellSession;Check(!ContinueSellSession(),"old diagnostic completed reset");CheckReset();});
        }
        Add("stable submission increments its own request count",()=>{Result=1;Check(!ContinueSellSession()&&_sellSessionActive&&_sellSessionStackCount==3&&Queries==1,"stable submission changed");});
        Add("stable empty pass completes once",()=>{Result=0;Check(ContinueSellSession()&&!_sellSessionActive&&!ForceSell,"empty control changed");});
        Add("stable closed merchant retains force request",()=>{Result=3;Check(ContinueSellSession()&&!_sellSessionActive&&ForceSell,"closed control changed");});
        Add("stable retry deferral ends current pass",()=>{Result=4;Check(ContinueSellSession()&&!_sellSessionActive&&!ForceSell,"retry control changed");});
        Add("replacement can make progress on a later call",()=>{Result=0;DuringQuery=Replace;Check(!ContinueSellSession(),"old pass completed new owner");CheckReplacement();Result=1;Check(!ContinueSellSession()&&_sellSessionStackCount==41&&Queries==2,"new owner cannot continue");});
        foreach(bool diagnostic in new[]{false,true})foreach(bool interrupted in new[]{false,true})
        {
            bool log=diagnostic,interrupt=interrupted;
            Add((log?"diagnostic":"query")+" replacement retains "+(interrupt?"interruption":"cancellation"),()=>{
                Exception signal=interrupt?new System.Threading.ThreadInterruptedException("stop"):new OperationCanceledException("stop");
                Action callback=()=>{Replace();throw signal;};Result=0;if(log)DuringLog=callback;else DuringQuery=callback;
                try{ContinueSellSession();throw new Failure("stop signal swallowed");}catch(Exception seen){Check(ReferenceEquals(signal,seen),"stop signal replaced");}
                CheckReplacement();
            });
        }
        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases){try{c.Item2();passed++;Console.WriteLine("PASS vendor continuation owner: "+c.Item1);}catch(Failure e){assertions++;Console.Error.WriteLine("FAIL vendor continuation owner assertion: "+c.Item1+": "+e.Message);}catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR vendor continuation owner fixture: "+c.Item1+": "+e);}}
        Console.WriteLine($"Vendor continuation ownership scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; exact continuation/reset; controlled merchant/log callbacks; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Vendor continuation ownership regressions");
    }
    private static void Check(bool ok,string why){if(!ok)throw new Failure(why);}
}
""";
}

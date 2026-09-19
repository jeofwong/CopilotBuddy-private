using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

internal static class VendorSaleResultRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        string? root=null;for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj"))){root=d.FullName;break;}
        if(root==null)throw new InvalidOperationException("Tracked checkout required");
        var source=File.ReadAllText(Path.Combine(root,"Styx","Logic","Vendors.cs"));
        int start=source.IndexOf("\t\tprivate static bool ContinueSellSession()",StringComparison.Ordinal);
        int end=source.IndexOf("\t\t/// <summary>",start+1,StringComparison.Ordinal);
        if(start<0||end<start)throw new InvalidOperationException("Vendor result region changed");
        var region=source.Substring(start,end-start);
        if(!region.Contains("private static void ResetSellSession()"))throw new InvalidOperationException("Actual reset missing");
        string temp=Path.Combine(Path.GetTempPath(),"cb-sale-result-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        bool logging=Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging=false;
            File.WriteAllText(Path.Combine(temp,"Owner.cs"),"using System;using System.Linq;using System.Collections.Generic;using Styx.Logic.Inventory.Frames.Merchant;public static partial class SaleResultCases{\n"+region+"\n}");
            File.WriteAllText(Path.Combine(temp,"Boundary.cs"),Boundary);
            const BindingFlags f=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
            var type=typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler",true)!;object compiler=Activator.CreateInstance(type,new object[]{temp})!;
            foreach(string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))type.GetMethod("AddReference",f)!.Invoke(compiler,new object[]{path});
            var result=(CompilerResults)type.GetMethod("Compile",f)!.Invoke(compiler,null)!;
            var errors=result.Errors.Cast<CompilerError>().Where(e=>!e.IsWarning).ToArray();if(errors.Length!=0)throw new InvalidOperationException(string.Join(";",errors.Select(e=>e.ToString())));
            var assembly=(Assembly)type.GetProperty("CompiledAssembly",f)!.GetValue(compiler)!;
            try{assembly.GetType("SaleResultCases",true)!.GetMethod("Run")!.Invoke(null,null);}catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        finally{Styx.Helpers.Logging.FileLogging=logging;Directory.Delete(temp,true);}
    }
    private const string Boundary="""
using System;using System.Linq;using System.Collections.Generic;using Styx.Logic.Inventory.Frames.Merchant;
public static partial class SaleResultCases
{
    private sealed class Failure(string text):Exception(text){}
    private static bool _sellSessionActive,ForceSell;private static object? _sellSessionCandidate;private static ItemQuality _sellSessionQualities;private static List<string>? _sellSessionProtectedNames;private static List<uint>? _sellSessionProtectedIds;private static int _sellSessionStackCount;private static readonly MerchantStub _merchantFrame=new();
    private static int Result;private static Exception? Error;private static readonly List<string> Messages=new();
    private sealed class MerchantStub{public int SellNextItemQualities(ItemQuality q,IEnumerable<string> n,IEnumerable<uint> i){if(Error!=null)throw Error;return Result;}}
    private static class Logging{public static void Write(string text,params object[] args)=>Messages.Add(string.Format(text,args));public static void WriteDebug(string text)=>Messages.Add(text);}
    private static void Reset(){_sellSessionActive=ForceSell=true;_sellSessionCandidate=new();_sellSessionQualities=ItemQuality.Poor;_sellSessionProtectedNames=new();_sellSessionProtectedIds=new();_sellSessionStackCount=0;Messages.Clear();Error=null;}
    public static void Run()
    {
        var cases=new List<(string,Action)>();void Add(string n,Action a)=>cases.Add((n,()=>{Reset();a();}));
        Add("submission stays running rather than confirming empty",()=>{Result=1;Check(!ContinueSellSession()&&_sellSessionStackCount==1&&_sellSessionActive,"submission contract changed");});
        Add("finished pass reports requests not confirmed sales",()=>{Result=1;ContinueSellSession();Result=0;Check(ContinueSellSession(),"pass did not end");Check(Messages.Any(s=>s.Contains("submitted",StringComparison.OrdinalIgnoreCase))&&!Messages.Any(s=>s.Contains("sold",StringComparison.OrdinalIgnoreCase)),"submission count falsely reported sold");});
        Add("bounded deferral ends current pass without claiming sales",()=>{Result=4;Check(ContinueSellSession()&&!_sellSessionActive&&!ForceSell,"bounded deferral loops forever");Check(Messages.Count>0&&!Messages.Any(s=>s.Contains("sold",StringComparison.OrdinalIgnoreCase)),"deferral lacked accurate diagnostic");});
        Add("bounded deferral releases only local session fields",()=>{Result=4;ContinueSellSession();Check(_sellSessionCandidate==null&&_sellSessionProtectedIds==null&&_sellSessionProtectedNames==null&&_sellSessionStackCount==0,"session not drained");});
        Add("locked pending result retains session",()=>{Result=2;Check(!ContinueSellSession()&&_sellSessionActive&&ForceSell&&Messages.Count==0,"locked item reported complete");});
        Add("unknown observation is not empty queue",()=>{Result=-1;Check(!ContinueSellSession()&&_sellSessionActive&&ForceSell,"unknown became successful empty");});
        Add("unsupported result remains pending",()=>{Result=777;Check(!ContinueSellSession()&&_sellSessionActive,"unknown code became completion");});
        Add("closed merchant preserves outstanding force request",()=>{Result=3;Check(ContinueSellSession()&&!_sellSessionActive&&ForceSell,"close discarded outstanding request");});
        Add("empty queue clears completed force request",()=>{Result=0;Check(ContinueSellSession()&&!_sellSessionActive&&!ForceSell,"empty contract changed");});
        Add("zero submitted requests are reported accurately",()=>{Result=0;ContinueSellSession();Check(Messages.Any(s=>s.Contains("0")&&s.Contains("submitted",StringComparison.OrdinalIgnoreCase)),"empty pass false sold claim");});
        foreach(Exception signal in new Exception[]{new OperationCanceledException("stop"),new System.Threading.ThreadInterruptedException("stop")})
        {var saved=signal;Add(saved.GetType().Name+" preserves identity",()=>{Error=saved;try{ContinueSellSession();throw new Failure("signal swallowed");}catch(Exception e){Check(ReferenceEquals(e,saved),"signal replaced");}Check(_sellSessionActive&&ForceSell,"cancelled call destroyed session");});}
        int passed=0,assertions=0,unexpected=0;foreach(var c in cases){try{c.Item2();passed++;Console.WriteLine("PASS vendor sale result: "+c.Item1);}catch(Failure e){assertions++;Console.Error.WriteLine("FAIL vendor sale result assertion: "+c.Item1+": "+e.Message);}catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR vendor sale result fixture: "+c.Item1+": "+e);}}
        Console.WriteLine($"Vendor sale result scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual continuation/reset; controlled merchant result; no game attached.");if(assertions+unexpected!=0)throw new InvalidOperationException("Vendor sale result regressions");
    }
    private static void Check(bool ok,string reason){if(!ok)throw new Failure(reason);}
}
""";
}

using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Actual tracked seller region and retry gate; controlled external query/time.
// These cases establish shared managed accounting, not Lua/native sale execution.
internal static class MerchantBulkRetryRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");
        string source = File.ReadAllText(Path.Combine(root, "Styx", "Logic", "Inventory", "Frames", "Merchant", "MerchantFrame.cs"));
        int start = source.IndexOf("        // The original client marks", StringComparison.Ordinal);
        int end = source.IndexOf("        public void Close()", StringComparison.Ordinal);
        if (start < 0 || end <= start) throw new InvalidOperationException("Actual seller extraction changed");
        string temp = Path.Combine(Path.GetTempPath(), "cb-bulk-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool logging = Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            File.WriteAllText(Path.Combine(temp, "Seller.cs"), "using System;using System.Collections.Generic;using System.Text;using Styx.Helpers;namespace Styx.Logic.Inventory.Frames.Merchant{public class MerchantFrame{\n" + source.Substring(start, end - start) + "\n}}");
            File.Copy(Path.Combine(root, "Styx", "Logic", "Inventory", "Frames", "Merchant", "MerchantSaleAttemptGate.cs"), Path.Combine(temp, "MerchantSaleAttemptGate.cs"));
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), Boundary);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var type = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(type, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference", flags)!.Invoke(compiler, new object[] { path });
            var result = (CompilerResults)type.GetMethod("Compile", flags)!.Invoke(compiler, null)!;
            var errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Actual bulk seller compile: " + string.Join(";", errors.Select(e => e.ToString())));
            var assembly = (Assembly)type.GetProperty("CompiledAssembly", flags)!.GetValue(compiler)!;
            try { assembly.GetType("BulkRetryCases", true)!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = logging; Directory.Delete(temp, true); }
    }
    private const string Boundary = """
using System;using System.Collections.Generic;using System.Linq;using System.Text;using System.Text.Json;using System.Text.RegularExpressions;using Styx.Logic.Inventory.Frames.Merchant;
public static class BulkRetryCases
{
    private sealed class Failure(string message):Exception(message){}
    private const string First="p:m:0:1:1:item:100";
    public static long Now;public static MerchantFrame Seller=null!;public static List<string> Tokens=new();public static List<string>? Forced;public static Exception? Error;public static Action? Nested;
    public static int Queries,Requests;public static string Script="";public static string Status="0";
    private static string Literal(string token)=>"[\""+string.Concat(Encoding.UTF8.GetBytes(token).Select(b=>"\\"+b.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)))+"\"]=true";
    public static List<string> Query(string script,bool bulk)
    {
        Queries++;Script=script;
        if(Error!=null)throw Error;
        var nested=Nested;Nested=null;nested?.Invoke();
        if(Forced!=null)return Forced;
        int budget=bulk?256:1;
        var match=Regex.Match(script,@"local saleAttemptBudget=(\d+);");
        if(match.Success)budget=Math.Min(budget,int.Parse(match.Groups[1].Value));
        var eligible=Tokens.Distinct(StringComparer.Ordinal).Where(t=>!script.Contains(Literal(t),StringComparison.Ordinal)).Take(budget).ToList();
        Requests+=eligible.Count;
        if(!bulk)return eligible.Count==0?new(){"ok","0"}:new(){"ok","1",eligible[0]};
        var values=new List<string>{"ok",Status};values.AddRange(eligible);return values;
    }
    private static void Bulk()=>Seller.SellItemQualities(ItemQuality.Poor,null!,null!);
    private static int Step()=>Seller.SellNextItemQualities(ItemQuality.Poor,null!,null!);
    private static void Reset(){Now=1000;Seller=new();Tokens=new(){First};Forced=null;Error=null;Nested=null;Queries=Requests=0;Script="";Status="0";}
    public static void Run()
    {
        var cases=new List<(string,Action)>();void Add(string name,Action body)=>cases.Add((name,()=>{Reset();body();}));
        Add("ordinary bulk submits multiple eligible stacks",()=>{Tokens.Add("p:m:0:2:1:item:101");Bulk();Check(Requests==2&&Queries==1,"bulk multiplicity changed");});
        Add("repeated bulk excludes an unchanged rejected stack",()=>{Bulk();Bulk();Check(Requests==1,"bulk repeated unchanged stack");});
        Add("one hundred bulk passes do not spam unchanged stack",()=>{for(int i=0;i<100;i++)Bulk();Check(Requests==1,"bulk request spam");});
        Add("bulk receipt suppresses later stepped attempt",()=>{Bulk();Check(Step()==0&&Requests==1,"step ignored bulk receipt");});
        Add("stepped receipt suppresses later bulk attempt",()=>{Check(Step()==1,"step control failed");Bulk();Check(Requests==1,"bulk ignored stepped receipt");});
        Add("unrelated item does not release a prior rejected bulk stack",()=>{Bulk();Tokens.Add("p:m:0:2:1:item:101");Bulk();Bulk();Check(Requests==2,"unrelated request released old key");});
        Add("all receipts in a bulk batch remain suppressed",()=>{Tokens.Add("p:m:0:2:1:item:101");Tokens.Add("p:m:0:3:1:item:102");Bulk();Bulk();Check(Requests==3,"only one bulk receipt retained");});
        Add("pending observation retains earlier submission receipts",()=>{Status="2";Bulk();Status="0";Bulk();Check(Requests==1,"partial pending result discarded receipts");});
        Add("closed-after-submission status retains earlier receipts",()=>{Status="3";Bulk();Status="0";Bulk();Check(Requests==1,"partial close discarded receipts");});
        Add("bounded-pass status retains earlier receipts",()=>{Status="4";Bulk();Status="0";Bulk();Check(Requests==1,"batch limit discarded receipts");});
        Add("bulk guard retains exact cooldown boundary",()=>{Bulk();Now+=119999;Bulk();Check(Requests==1,"guard expired early");Now++;Bulk();Check(Requests==2,"guard never expired");Bulk();Check(Requests==2,"fresh receipt not protected");});
        foreach(string alternative in new[]{"p:m:0:1:2:item:100","p:m:0:1:1:item:101","other:m:0:1:1:item:100","p:other:0:1:1:item:100"})
        {string token=alternative;Add("changed observed identity remains eligible "+token,()=>{Bulk();Tokens=new(){token};Bulk();Check(Requests==2,"separate observation suppressed");});}
        Add("empty mask remains no-op",()=>{Seller.SellItemQualities(ItemQuality.None,null!,null!);Check(Queries==0&&Requests==0,"empty mask queried");});
        Add("unknown result does not clear known receipt",()=>{Bulk();Forced=new();Bulk();Forced=null;Bulk();Check(Requests==1,"unknown cleared known key");});
        Add("ordinary read failure preserves prior receipt",()=>{Bulk();var e=new InvalidOperationException("read");Error=e;try{Bulk();throw new Failure("read error swallowed");}catch(InvalidOperationException seen){Check(ReferenceEquals(e,seen),"read error replaced");}Error=null;Bulk();Check(Requests==1,"read failure cleared known key");});
        Add("cancellation retains identity and gate can recover",()=>{var e=new OperationCanceledException("stop");Error=e;try{Bulk();throw new Failure("cancel swallowed");}catch(OperationCanceledException seen){Check(ReferenceEquals(e,seen),"cancel replaced");}Error=null;Bulk();Bulk();Check(Requests==1,"gate failed to recover");});
        Add("interruption retains identity and gate can recover",()=>{var e=new System.Threading.ThreadInterruptedException("stop");Error=e;try{Bulk();throw new Failure("interrupt swallowed");}catch(System.Threading.ThreadInterruptedException seen){Check(ReferenceEquals(e,seen),"interrupt replaced");}Error=null;Bulk();Bulk();Check(Requests==1,"gate failed to recover");});
        Add("reentrant bulk cannot submit before outer receipt publication",()=>{Nested=Bulk;Bulk();Check(Queries==1&&Requests==1,"nested bulk bypassed in-flight owner");});
        Add("reentrant step cannot submit before outer receipt publication",()=>{Nested=()=>Check(Step()==2,"nested step not pending");Bulk();Check(Queries==1&&Requests==1,"nested step bypassed owner");});
        Add("full live key budget defers without evicting old keys",()=>{Tokens=Enumerable.Range(0,256).Select(i=>"p:m:0:"+i+":1:item:100").ToList();Bulk();Check(Requests==256,"bulk budget lost legitimate candidates");int q=Queries;Tokens=new(){"p:m:0:300:1:item:101"};Bulk();Check(Queries==q&&Requests==256,"capacity did not defer");Now+=120000;Bulk();Check(Requests==257,"expired capacity did not recover");});
        Add("batch receives remaining shared step capacity",()=>{for(int i=0;i<255;i++){Tokens=new(){"p:m:0:"+i+":1:item:100"};Check(Step()==1,"step preparation");}Tokens=new(){"p:m:0:400:1:item:101","p:m:0:401:1:item:101"};Bulk();Check(Requests==256,"bulk exceeded remaining capacity");});
        Add("a malformed batch cannot partially publish new receipts",()=>{Bulk();Forced=new(){"ok","0","valid-new",new string('x',2049)};Bulk();Forced=null;Tokens=new(){"valid-new"};Check(Step()==1,"malformed batch partially published");Tokens=new(){First};Check(Step()==0,"malformed batch cleared prior receipt");});
        Add("emitted bulk returns bounded receipts even after later Lua error",()=>{Bulk();Check(Script.Contains("submitted")&&Script.Contains("pcall(")&&Script.Contains("unpack(")&&Script.Contains("saleAttemptBudget")&&Script.Contains("blockedSaleStacks"),"bulk receipt/error/budget contract absent");});
        Add("export actual bulk producer payload",()=>{Bulk();Console.WriteLine("BULK_RETRY_LUA_EXPORT:"+JsonSerializer.Serialize(new{lua=Script}));});
        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases){try{c.Item2();passed++;Console.WriteLine("PASS bulk sale retry: "+c.Item1);}catch(Failure e){assertions++;Console.Error.WriteLine("FAIL bulk sale retry assertion: "+c.Item1+": "+e.Message);}catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR bulk sale retry fixture: "+c.Item1+": "+e);}}
        Console.WriteLine($"Bulk sale retry scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual seller/gate; controlled external query/time; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Bulk sale retry regressions");
    }
    private static void Check(bool ok,string why){if(!ok)throw new Failure(why);}
}
/* fixture */ namespace Styx.Logic.Inventory.Frames.Merchant {public static class Lua{public static List<string> GetReturnValues(string text)=>BulkRetryCases.Query(text,text.Contains("local submitted"));public static void DoString(string text){BulkRetryCases.Query(text,true);}public static string Escape(string text)=>text.Replace("\\","\\\\").Replace("\"","\\\"");}public static class Environment{public static long TickCount64=>BulkRetryCases.Now;}}
""";
}

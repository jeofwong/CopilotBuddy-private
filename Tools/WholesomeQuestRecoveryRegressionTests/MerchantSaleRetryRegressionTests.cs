using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Compile the exact tracked seller region; only Lua results and monotonic time
// are external controls. Separate emitted-script tests do not prove native sales.
internal static class MerchantSaleRetryRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        string? root=null;
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj"))){root=d.FullName;break;}
        if(root==null)throw new InvalidOperationException("Tracked checkout required");
        string source=File.ReadAllText(Path.Combine(root,"Styx","Logic","Inventory","Frames","Merchant","MerchantFrame.cs"));
        int start=source.IndexOf("        // The original client marks",StringComparison.Ordinal);
        int end=source.IndexOf("        public void Close()",StringComparison.Ordinal);
        if(start<0||end<=start)throw new InvalidOperationException("Actual seller region changed");
        string region=source.Substring(start,end-start);
        string temp=Path.Combine(Path.GetTempPath(),"cb-sale-retry-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);bool logging=Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging=false;
            File.WriteAllText(Path.Combine(temp,"Seller.cs"),"using System;using System.Collections.Generic;using System.Text;using Styx.Helpers;namespace Styx.Logic.Inventory.Frames.Merchant{public class MerchantFrame{\n"+region+"\n}}");
            string gate=Path.Combine(root,"Styx","Logic","Inventory","Frames","Merchant","MerchantSaleAttemptGate.cs");
            if(File.Exists(gate))File.Copy(gate,Path.Combine(temp,"MerchantSaleAttemptGate.cs"));
            File.WriteAllText(Path.Combine(temp,"Boundary.cs"),Boundary);
            var type=typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler",true)!;
            const BindingFlags f=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
            object compiler=Activator.CreateInstance(type,new object[]{temp})!;
            foreach(string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))type.GetMethod("AddReference",f)!.Invoke(compiler,new object[]{path});
            var result=(CompilerResults)type.GetMethod("Compile",f)!.Invoke(compiler,null)!;
            var errors=result.Errors.Cast<CompilerError>().Where(e=>!e.IsWarning).ToArray();
            if(errors.Length!=0)throw new InvalidOperationException("Actual seller region compile: "+string.Join(";",errors.Select(e=>e.ToString())));
            var assembly=(Assembly)type.GetProperty("CompiledAssembly",f)!.GetValue(compiler)!;
            try{assembly.GetType("SaleReplayCases",true)!.GetMethod("Run")!.Invoke(null,null);}
            catch(TargetInvocationException e)when(e.InnerException!=null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        finally{Styx.Helpers.Logging.FileLogging=logging;Directory.Delete(temp,true);}
    }
    private const string Boundary="""
using System;using System.Collections.Generic;using System.Linq;using System.Text;using System.Text.Json;using Styx.Logic.Inventory.Frames.Merchant;
public static class SaleReplayCases
{
    private sealed class Failure(string text):Exception(text){}
    public static long Now;public static string Token="p:m:0:1:1:item:100";public static int Queries,Requests;public static string Script="";public static List<string>? Result;public static Exception? Error;public static MerchantFrame Seller=null!;
    public static string Literal(string token)=>"[\""+string.Concat(Encoding.UTF8.GetBytes(token).Select(b=>"\\"+b.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)))+"\"]=true";
    public static List<string> Query(string script)
    {
        Queries++;Script=script;if(Error!=null)throw Error;if(Result!=null)return Result;
        // Scripted server/client boundary: the real producer must pass exclusions.
        // This is not a Lua interpreter or a native item transaction.
        if(script.Contains(Literal(Token),StringComparison.Ordinal))return new(){"ok","0"};
        Requests++;return new(){"ok","1",Token};
    }
    static int Step()=>Seller.SellNextItemQualities(ItemQuality.Poor,null!,null!);
    static void Reset(){Now=1000;Token="p:m:0:1:1:item:100";Queries=Requests=0;Script="";Result=null;Error=null;Seller=new();}
    public static void Run()
    {
        var cases=new List<(string,Action)>();void Add(string name,Action body)=>cases.Add((name,()=>{Reset();body();}));
        Add("first eligible observation submits once",()=>{Check(Step()==1&&Requests==1,"ordinary submission lost");});
        Add("unchanged rejected stack is excluded next call",()=>{Step();Check(Step()==0&&Requests==1,"same unchanged stack submitted again");});
        Add("one hundred repeated ticks do not resubmit unchanged stack",()=>{Step();for(int i=0;i<100;i++)Step();Check(Requests==1,"rejected item request spam");});
        foreach(string alternative in new[]{"p:m:0:2:1:item:100","p:m:1:1:1:item:100","p:m:0:1:2:item:100","p:m:0:1:1:item:101","other:m:0:1:1:item:100","p:other:0:1:1:item:100"})
        {string value=alternative;Add("separate observed key remains eligible "+value,()=>{Step();Token=value;Check(Step()==1&&Requests==2,"another observed stack/owner was suppressed");});}
        Add("unrelated successful request does not release rejected key",()=>{Step();Token="p:m:0:2:1:item:101";Step();Token="p:m:0:1:1:item:100";Check(Step()==0&&Requests==2,"unrelated item released old retry guard");});
        Add("cooldown remains effective immediately before deadline",()=>{Step();Now+=119999;Check(Step()==0&&Requests==1,"guard expired early");});
        Add("exact cooldown deadline permits one fresh attempt",()=>{Step();Now+=120000;Check(Step()==1&&Requests==2,"finite retry never recovered");Check(Step()==0&&Requests==2,"fresh attempt was not guarded");});
        Add("locked observation remains pending without an attempt key",()=>{Result=new(){"ok","2"};Check(Step()==2,"locked state changed");Now+=1000;Result=null;Check(Step()==1,"lock poisoned later eligibility");});
        Add("closed merchant retains closed code",()=>{Result=new(){"ok","3"};Check(Step()==3,"closed state changed");});
        Add("empty queue retains empty code",()=>{Result=new(){"ok","0"};Check(Step()==0,"empty queue changed");});
        Add("empty quality does not invoke external query",()=>{Check(Seller.SellNextItemQualities(ItemQuality.None,null!,null!)==0&&Queries==0,"empty mask queried");});
        Add("unknown result remains unknown",()=>{Result=new();Check(Step()==-1,"unknown interpreted as empty");});
        Add("malformed success receipt is not accepted",()=>{Result=new(){"ok","1"};Check(Step()==-1,"success without identity accepted");});
        Add("oversized receipt is not stored or accepted",()=>{Result=new(){"ok","1",new string('x',2049)};Check(Step()==-1,"unbounded token accepted");});
        Add("reserved numeric result is not an empty queue",()=>{Result=new(){"ok","777"};Check(Step()==-1,"unsupported result accepted");});
        Add("cancellation preserves exact exception identity",()=>{var e=new OperationCanceledException("stop");Error=e;try{Step();throw new Failure("cancellation swallowed");}catch(OperationCanceledException seen){Check(ReferenceEquals(e,seen),"signal replaced");}Error=null;Check(Step()==1,"cancellation stranded gate");});
        Add("interruption preserves exact exception identity",()=>{var e=new System.Threading.ThreadInterruptedException("stop");Error=e;try{Step();throw new Failure("interruption swallowed");}catch(System.Threading.ThreadInterruptedException seen){Check(ReferenceEquals(e,seen),"signal replaced");}Error=null;Check(Step()==1,"interruption stranded gate");});
        Add("receipt byte escaping cannot inject Lua",()=>{Token="p:m:0:1:1:item:\"\\\né";Step();Step();Check(Requests==1&&Script.Contains(Literal(Token)),"receipt not escaped as literal bytes");});
        Add("bounded live key capacity defers without eviction/resubmit",()=>{for(int i=0;i<256;i++){Token="p:m:0:"+i+":1:item:100";Check(Step()==1,"capacity too small");}Token="p:m:0:999:1:item:100";int q=Queries;Check(Step()==4&&Queries==q,"capacity did not defer safely");Now+=120000;Check(Step()==1,"capacity never recovered");});
        Add("generated step checks sell value and context before use",()=>{Step();Check(Script.Contains("sellPrice")&&Script.Contains("UnitGUID")&&Script.Contains("blockedSaleStacks"),"missing actual step guards");});
        Add("generated bulk checks sell value without changing bulk contract",()=>{Seller.SellItemQualities(ItemQuality.Poor,null!,null!);Check(Script.Contains("sellPrice")&&!Script.Contains("UseContainerItem(b,s) return 'ok',1"),"bulk filter missing or contract changed");});
        Add("both scripts expose verifiable actual payloads",()=>{Step();Console.WriteLine("SALE_RETRY_LUA_EXPORT:"+JsonSerializer.Serialize(new{name="step",lua=Script}));Seller.SellItemQualities(ItemQuality.Poor,null!,null!);Console.WriteLine("SALE_RETRY_LUA_EXPORT:"+JsonSerializer.Serialize(new{name="bulk",lua=Script}));});
        int passed=0,assertions=0,unexpected=0;foreach(var c in cases){try{c.Item2();passed++;Console.WriteLine("PASS merchant retry: "+c.Item1);}catch(Failure e){assertions++;Console.Error.WriteLine("FAIL merchant retry assertion: "+c.Item1+": "+e.Message);}catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR merchant retry fixture: "+c.Item1+": "+e);}}
        Console.WriteLine($"Merchant retry scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual seller region and emitted exclusions; scripted external query/time; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Merchant retry regressions");
    }
    private static void Check(bool ok,string reason){if(!ok)throw new Failure(reason);}
}
// Namespaces are deliberately inside a raw fixture string, not initializer owners.
/* fixture */ namespace Styx.Logic.Inventory.Frames.Merchant {public static class Lua{public static List<string> GetReturnValues(string s)=>SaleReplayCases.Query(s);public static void DoString(string s){SaleReplayCases.Script=s;}public static string Escape(string s)=>s.Replace("\\","\\\\").Replace("\"","\\\"");}public static class Environment{public static long TickCount64=>SaleReplayCases.Now;}}
""";
}

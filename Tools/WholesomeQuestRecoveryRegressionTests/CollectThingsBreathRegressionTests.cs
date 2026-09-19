using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Exact-source regression for CollectThings.SwimBreathBehavior breath budgeting.
// Extracts the production helper into a tiny controlled compiler boundary; no game,
// movement, Lua, client memory, or rewritten breath formula is used by the test.
internal static class CollectThingsBreathRegressionTests
{
    private sealed class Failure(string message):Exception(message){}
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;

    [ModuleInitializer]
    internal static void Run()
    {
        string root=Root();
        string source=File.ReadAllText(Path.Combine(root,"runtime-snapshot","Quest Behaviors","CollectThings.cs"));
        const string signature="internal static double RequiredBreathLeadMilliseconds(";
        int start=source.IndexOf(signature,StringComparison.Ordinal);
        string helper=start<0 ? "" : ExtractMethod(source,start);
        var cases=new (string Name,Action Test)[]
        {
            ("actual owner exposes a bounded breath-budget helper",()=>
                Check(start>=0 && helper.Contains("RequiredBreathLeadMilliseconds",StringComparison.Ordinal),
                    "CollectThings has no executable breath-budget owner")),
            ("actual IsBreathNeeded consumes the shared budget helper",()=>{
                int owner=source.IndexOf("private bool IsBreathNeeded()",StringComparison.Ordinal);
                int next=source.IndexOf("private void UnderwaterMoveTo",owner,StringComparison.Ordinal);
                Check(owner>=0&&next>owner&&source.Substring(owner,next-owner)
                    .Contains("RequiredBreathLeadMilliseconds",StringComparison.Ordinal),
                    "IsBreathNeeded still owns an unshared/ad-hoc travel budget");
            }),
            ("legacy thirty-second cap is removed from the owner",()=>
                Check(!source.Contains("Math.Min(travelTime, 30.0)",StringComparison.Ordinal),
                    "long recovery routes are still capped at thirty seconds")),
            ("short recovery keeps a thirty-second minimum",()=>{
                double actual=Run(helper,10d,5d,5d);
                Check(Math.Abs(actual-30000d)<0.001,$"expected 30000ms, observed {actual}");
            }),
            ("long recovery keeps full safety-adjusted travel time",()=>{
                double actual=Run(helper,100d,5d,5d);
                Check(Math.Abs(actual-70000d)<0.001,$"expected 70000ms, observed {actual}");
            }),
            ("zero swimming speed fails closed with infinite lead time",()=>{
                double actual=Run(helper,10d,0d,5d);
                Check(double.IsPositiveInfinity(actual),"zero swimming speed did not request immediate recovery");
            }),
            ("nonfinite distance fails closed with infinite lead time",()=>{
                double actual=Run(helper,double.NaN,5d,5d);
                Check(double.IsPositiveInfinity(actual),"nonfinite route did not fail closed");
            })
        };

        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases)
        {
            try{c.Test();passed++;Console.WriteLine("PASS CollectThings breath: "+c.Name);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL CollectThings breath assertion: "+c.Name+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR CollectThings breath fixture: "+c.Name+": "+e);}
        }
        Console.WriteLine($"CollectThings breath scenarios: {passed}/{cases.Length}; assertions={assertions}; unexpected={unexpected}; exact extracted production helper/source; no game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("CollectThings breath regressions");
    }

    private static double Run(string helper,double distance,double speed,double throttle)
    {
        if(string.IsNullOrWhiteSpace(helper))throw new Failure("production helper is missing");
        string temp=Path.Combine(Path.GetTempPath(),"cb-breath-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Combine(temp,"Owner.cs"),
                "using System; public static class Owner {\n"+helper+
                "\npublic static double Run(double d,double s,double t)=>RequiredBreathLeadMilliseconds(d,s,t);\n}");
            Type compilerType=typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler",true)!;
            object compiler=Activator.CreateInstance(compilerType,new object[]{temp})!;
            foreach(string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                compilerType.GetMethod("AddReference",Flags)!.Invoke(compiler,new object[]{path});
            var result=(CompilerResults)compilerType.GetMethod("Compile",Flags)!.Invoke(compiler,null)!;
            var errors=result.Errors.Cast<CompilerError>().Where(e=>!e.IsWarning).ToArray();
            if(errors.Length!=0)throw new Failure("extracted helper did not compile: "+string.Join(";",errors.Select(e=>e.ToString())));
            var asm=(Assembly)compilerType.GetProperty("CompiledAssembly",Flags)!.GetValue(compiler)!;
            try{return (double)asm.GetType("Owner",true)!.GetMethod("Run")!.Invoke(null,new object[]{distance,speed,throttle})!;}
            catch(TargetInvocationException e)when(e.InnerException!=null)
            {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        finally{try{Directory.Delete(temp,true);}catch{}}
    }

    private static string ExtractMethod(string source,int start)
    {
        int brace=source.IndexOf('{',start);
        if(brace<0)throw new Failure("breath helper has no body");
        int depth=0;
        for(int i=brace;i<source.Length;i++)
        {
            if(source[i]=='{')depth++;
            else if(source[i]=='}'&&--depth==0)return source.Substring(start,i-start+1);
        }
        throw new Failure("breath helper body is incomplete");
    }

    private static string Root()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj")))return d.FullName;
        throw new Failure("tracked checkout required");
    }
    private static void Check(bool ok,string why){if(!ok)throw new Failure(why);}
}

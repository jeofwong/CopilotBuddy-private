using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Styx.WoWInternals.WoWObjects;

// Log-backed allocation-safety contract for the 3.3.5 aura reader.
// Does not request a huge allocation; source ordering and the pure count guard
// are tested while existing owner tests retain real-memory valid-aura coverage.
internal static class AuraCountSafetyRegressionTests
{
    private sealed class Failure(string text):Exception(text){}
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;

    [ModuleInitializer]
    internal static void Run()
    {
        MethodInfo? guard=typeof(WoWUnit).GetMethod("IsPlausibleAuraCount",Flags);
        var cases=new List<(string Name,Action Test)>
        {
            ("aura allocation guard exists on the actual owner",()=>Check(guard!=null,"WoWUnit has no raw-count allocation guard")),
            ("zero auras remains valid",()=>Check(Call(guard,0),"zero count was rejected")),
            ("ordinary aura count remains valid",()=>Check(Call(guard,1),"ordinary count was rejected")),
            ("3.3.5 visible-aura upper boundary remains valid",()=>Check(Call(guard,255),"255 visible auras were rejected")),
            ("unresolved dynamic sentinel cannot reach allocation",()=>Check(!Call(guard,-1),"dynamic sentinel was accepted for allocation")),
            ("count above 3.3.5 visible-aura range fails closed",()=>Check(!Call(guard,256),"count 256 was accepted for allocation")),
            ("corrupt extreme count fails closed",()=>Check(!Call(guard,int.MaxValue),"extreme count was accepted for allocation")),
            ("guard executes after dynamic count resolution and before array allocation",()=>{
                string source=File.ReadAllText(Path.Combine(Root(),"Styx","WoWInternals","WoWObjects","WoWUnit.cs"));
                int method=source.IndexOf("public unsafe WoWAuraCollection GetAllAuras()",StringComparison.Ordinal);
                int dynamicRead=source.IndexOf("auraCount = wow.Read<int>(BaseAddress + 3156);",method,StringComparison.Ordinal);
                int guardCall=source.IndexOf("IsPlausibleAuraCount(auraCount)",method,StringComparison.Ordinal);
                int allocation=source.IndexOf("new WoWAura.AuraInfo[auraCount]",method,StringComparison.Ordinal);
                Check(method>=0&&dynamicRead>method&&guardCall>dynamicRead&&allocation>guardCall,
                    "raw aura count can reach allocation before bounded validation");
            })
        };

        int passed=0,assertions=0,unexpected=0;
        foreach(var c in cases)
        {
            try{c.Test();passed++;Console.WriteLine("PASS aura count safety: "+c.Name);}
            catch(Failure e){assertions++;Console.Error.WriteLine("FAIL aura count safety assertion: "+c.Name+": "+e.Message);}
            catch(Exception e){unexpected++;Console.Error.WriteLine("ERROR aura count safety fixture: "+c.Name+": "+e);}
        }
        Console.WriteLine($"Aura count safety scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual owner/source; no oversized allocation or game attached.");
        if(assertions+unexpected!=0)throw new InvalidOperationException("Aura count safety regressions");
    }

    private static bool Call(MethodInfo? method,int value)
    {
        if(method==null)throw new Failure("WoWUnit.IsPlausibleAuraCount is missing");
        try{return (bool)(method.Invoke(null,new object[]{value}) ?? false);}
        catch(TargetInvocationException e)when(e.InnerException!=null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;
        }
    }

    private static string Root()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"CopilotBuddy.csproj")))return d.FullName;
        throw new Failure("tracked checkout required");
    }

    private static void Check(bool ok,string why){if(!ok)throw new Failure(why);}
}

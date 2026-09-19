using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Verbatim tracked C# methods. Lua records requests only: no game, cursor,
// popup, mouse, native function or simulated Lua implementation is executed.
internal static class EquipPopupSubmissionRegressionTests
{
    private sealed class Failure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        string root = Root();
        int passed = 0, assertions = 0, unexpected = 0, total = 0;
        foreach (string path in new[]
        {
            "runtime-snapshot/Quest Behaviors/EquipItem.cs",
            "runtime-snapshot/Plugins/AutoEquip2/AutoEquip.cs"
        })
        {
            string source = File.ReadAllText(Path.Combine(root, path));
            string confirm = Method(source, "private void ConfirmOwnedEquipPopup()");
            string tick = Method(source, path.EndsWith("/EquipItem.cs", StringComparison.Ordinal)
                ? "private RunStatus TickPendingEquip()" : "private void TickPendingEquip()");
            string directory = Path.Combine(Path.GetTempPath(), "cb-equip-popup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "Probe.cs"), Prefix + confirm + tick + Suffix);
                Type compilerType = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
                object compiler = Activator.CreateInstance(compilerType, new object[] { directory })!;
                var result = (CompilerResults)compilerType.GetMethod("Compile")!.Invoke(compiler, null)!;
                string[] errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).Select(e => e.ToString()).ToArray();
                if (errors.Length != 0) throw new InvalidOperationException("Tracked popup/tick compilation: " + string.Join(";", errors));
                var assembly = (Assembly)compilerType.GetProperty("CompiledAssembly")!.GetValue(compiler)!;
                MethodInfo run = assembly.GetType("PopupProbe", true)!.GetMethod("Execute")!;
                foreach (var c in new[]
                {
                    (Name: "no pending transaction never confirms", Pending: false, Submitted: true, Slot: 1, Ticks: 0, Retry: false, Calls: 0, Attempts: 0),
                    (Name: "pending but unsubmitted transaction never confirms", Pending: true, Submitted: false, Slot: 1, Ticks: 0, Retry: false, Calls: 0, Attempts: 0),
                    (Name: "unknown slot stays unconfirmed after submission", Pending: true, Submitted: true, Slot: 0, Ticks: 0, Retry: false, Calls: 0, Attempts: 0),
                    (Name: "submitted explicit-slot transaction keeps confirmation", Pending: true, Submitted: true, Slot: 1, Ticks: 0, Retry: false, Calls: 1, Attempts: 0),
                    (Name: "submitted second equipment slot keeps its own dialog data", Pending: true, Submitted: true, Slot: 16, Ticks: 0, Retry: false, Calls: 1, Attempts: 0),
                    (Name: "failed retry cannot confirm first", Pending: true, Submitted: false, Slot: 1, Ticks: 1, Retry: false, Calls: 0, Attempts: 1),
                    (Name: "successful retry cannot confirm before that submission", Pending: true, Submitted: false, Slot: 1, Ticks: 1, Retry: true, Calls: 0, Attempts: 1),
                    (Name: "next tick can confirm a successfully retried submission", Pending: true, Submitted: false, Slot: 1, Ticks: 2, Retry: true, Calls: 1, Attempts: 1),
                    (Name: "continued refusals do not authorize confirmation", Pending: true, Submitted: false, Slot: 1, Ticks: 2, Retry: false, Calls: 0, Attempts: 2)
                })
                {
                    total++;
                    try
                    {
                        string[] actual = (string[])run.Invoke(null, new object[] { c.Pending, c.Submitted, c.Slot, c.Ticks, c.Retry })!;
                        if (int.Parse(actual[0]) != c.Calls || int.Parse(actual[1]) != c.Attempts)
                            throw new Failure("confirmation requests=" + actual[0] + ", attempts=" + actual[1]
                                + "; expected " + c.Calls + "," + c.Attempts);
                        if (c.Calls > 0 && (!actual[2].Contains("tonumber(p.data)==" + c.Slot, StringComparison.Ordinal)
                            || !actual[2].Contains("StaticPopup_FindVisible('EQUIP_BIND')", StringComparison.Ordinal)
                            || !actual[2].Contains("StaticPopup_FindVisible('AUTOEQUIP_BIND')", StringComparison.Ordinal)))
                            throw new Failure("existing type and slot ownership predicates were changed");
                        passed++;
                        Console.WriteLine("PASS equip popup submission: " + path + ": " + c.Name);
                    }
                    catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL equip popup submission: " + path + ": " + c.Name + ": " + e.Message); }
                    catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR equip popup submission: " + path + ": " + c.Name + ": " + e); }
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        Console.WriteLine($"Equip popup submission scenarios: {passed}/{total}; assertions={assertions}; unexpected={unexpected}; exact tracked C# confirmation/tick methods; recording Lua boundary only; no game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Equip popup submission regression");
    }

    private const string Prefix = """
using System;
using System.Collections.Generic;
using System.Linq;
public enum InventorySlot { None=0, HeadSlot=1, MainHandSlot=16 }
public enum RunStatus { Success, Failure, Running }
public sealed class WoWItem
{
    public uint Entry=>100; public ulong Guid=>200; public bool IsValid=>true;
    public bool TryPickUp(out int bag,out int slot){bag=0;slot=1;throw new InvalidOperationException("Unexpected item admission");}
}
public sealed class ProbePlayer { public List<WoWItem> CarriedItems=new List<WoWItem>(); }
public static class StyxWoW { public static ProbePlayer Me=new ProbePlayer(); }
public static class Lua
{
    public static readonly List<string> Requests=new List<string>();
    public static void DoString(string format,params object[] args){Requests.Add(args.Length==0?format:string.Format(format,args));}
}
public sealed class PopupProbe
{
    private bool _isBehaviorDone,_isDisposed,_pendingEquipSubmitted;
    private ulong _pendingEquipGuid;
    private uint _pendingEquipEntry;
    private InventorySlot _pendingEquipSlot;
    private int _pendingSourceBag,_pendingSourceSlot;
    private DateTime _pendingEquipSince;
    private static readonly TimeSpan EquipTimeout=TimeSpan.FromSeconds(10);
    private int ItemId=>100;
    private InventorySlot Slot=>InventorySlot.HeadSlot;
    private bool HasPendingEquip=>_pendingEquipGuid!=0&&_pendingEquipEntry!=0;
    private bool Retry;
    private int Attempts;
    private bool IsPendingEquipAcknowledged()=>false;
    private bool ReturnDisplacedCursorToSource()=>false;
    private void RestoreOwnedCursorToSource(){}
    private bool SubmitOwnedCursorEquip(){Attempts++;return Retry;}
    private void ResetPendingEquip(){_pendingEquipGuid=0;_pendingEquipEntry=0;_pendingEquipSubmitted=false;}
    private void LogMessage(string level,string format,params object[] args){}
    private void Log(string format,params object[] args){}
    private void LogDebug(string format,params object[] args){}
""";
    private const string Suffix = """
    public static string[] Execute(bool pending,bool submitted,int slot,int ticks,bool retry)
    {
        var owner=new PopupProbe();
        owner._pendingEquipGuid=pending?200UL:0UL;owner._pendingEquipEntry=pending?100U:0U;
        owner._pendingEquipSlot=(InventorySlot)slot;owner._pendingEquipSubmitted=submitted;
        owner._pendingEquipSince=DateTime.UtcNow;owner.Retry=retry;Lua.Requests.Clear();
        if(ticks==0)owner.ConfirmOwnedEquipPopup();
        else for(int i=0;i<ticks;i++)owner.TickPendingEquip();
        return new[]{Lua.Requests.Count.ToString(),owner.Attempts.ToString(),string.Join("\n",Lua.Requests)};
    }
}
""";
    private static string Method(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Missing tracked method " + marker);
        int brace = source.IndexOf('{', start), depth = 0;
        // In these selected methods any braces in format strings are balanced.
        // Preserve every source byte in the extracted method; no statement edits.
        for (int i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }
        throw new InvalidOperationException("Unclosed tracked method " + marker);
    }
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) return d.FullName;
        throw new InvalidOperationException("Tracked checkout required");
    }
}

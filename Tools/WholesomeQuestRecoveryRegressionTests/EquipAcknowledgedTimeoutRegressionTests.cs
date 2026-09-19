using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Exact tracked method execution. The client acknowledgement, cursor-return and
// item-admission boundaries are controlled; no game, Lua or native cursor is used.
internal static class EquipAcknowledgedTimeoutRegressionTests
{
    private sealed class Failure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        string root = Root();
        var paths = new[]
        {
            "runtime-snapshot/Quest Behaviors/EquipItem.cs",
            "runtime-snapshot/Plugins/AutoEquip2/AutoEquip.cs"
        };
        int passed = 0, assertions = 0, unexpected = 0, total = 0;
        foreach (string path in paths)
        {
            string marker = path.EndsWith("/EquipItem.cs", StringComparison.Ordinal)
                ? "private RunStatus TickPendingEquip()"
                : "private void TickPendingEquip()";
            string method = ExtractMethod(File.ReadAllText(Path.Combine(root, path)), marker);
            string directory = Path.Combine(Path.GetTempPath(), "cb-equip-timeout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string source = Prefix + method + Suffix;
                File.WriteAllText(Path.Combine(directory, "Probe.cs"), source);
                Type compilerType = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
                object compiler = Activator.CreateInstance(compilerType, new object[] { directory })!;
                var result = (CompilerResults?)compilerType.GetMethod("Compile")!.Invoke(compiler, null);
                var errors = result == null ? new[] { "null compiler result" }
                    : result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).Select(e => e.ToString()).ToArray();
                if (errors.Length != 0) throw new InvalidOperationException(string.Join(" | ", errors));
                var assembly = (Assembly?)compilerType.GetProperty("CompiledAssembly")!.GetValue(compiler);
                MethodInfo execute = assembly!.GetType("EquipTimeoutProbe", true)!.GetMethod("Execute")!;
                var cases = new[]
                {
                    (Name: "expired acknowledged item with blocked return releases pending ownership", Ack: true, Return: false, Expired: true, Pending: false),
                    (Name: "fresh acknowledged item with blocked return remains pending", Ack: true, Return: false, Expired: false, Pending: true),
                    (Name: "fresh acknowledged item with successful return completes", Ack: true, Return: true, Expired: false, Pending: false),
                    (Name: "expired unacknowledged submission remains bounded", Ack: false, Return: false, Expired: true, Pending: false),
                    (Name: "fresh unacknowledged submission stays pending", Ack: false, Return: false, Expired: false, Pending: true)
                };
                foreach (var c in cases)
                {
                    total++;
                    try
                    {
                        bool actual = (bool)execute.Invoke(null, new object[] { c.Ack, c.Return, c.Expired })!;
                        if (actual != c.Pending)
                            throw new Failure("pending=" + actual + ", expected=" + c.Pending);
                        passed++;
                        Console.WriteLine("PASS equip acknowledged timeout: " + path + ": " + c.Name);
                    }
                    catch (TargetInvocationException e)
                    {
                        unexpected++;
                        Console.Error.WriteLine("ERROR equip acknowledged timeout: " + path + ": " + c.Name + ": " + e.InnerException);
                    }
                    catch (Failure e)
                    {
                        assertions++;
                        Console.Error.WriteLine("FAIL equip acknowledged timeout: " + path + ": " + c.Name + ": " + e.Message);
                    }
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        Console.WriteLine($"Equip acknowledged timeout scenarios: {passed}/{total}; assertions={assertions}; unexpected={unexpected}; exact tracked method control flow; controlled external acknowledgements; no game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Equip acknowledged timeout regression");
    }

    private const string Prefix = @"
using System;
using System.Collections.Generic;
using System.Linq;
public enum InventorySlot { None = 0, HeadSlot = 1 }
public enum RunStatus { Success, Failure, Running }
public sealed class WoWItem
{
    public uint Entry { get { return 100; } }
    public ulong Guid { get { return 200; } }
    public bool IsValid { get { return true; } }
    public bool TryPickUp(out int bag, out int slot) { bag=0; slot=1; return false; }
}
public sealed class ProbePlayer { public List<WoWItem> CarriedItems = new List<WoWItem>(); }
public static class StyxWoW { public static ProbePlayer Me = new ProbePlayer(); }
public static class Lua { public static void DoString(string format, params object[] args) { throw new InvalidOperationException(""Unexpected item admission""); } }
public sealed class EquipTimeoutProbe
{
    private bool _isBehaviorDone, _isDisposed;
    private ulong _pendingEquipGuid;
    private uint _pendingEquipEntry;
    private InventorySlot _pendingEquipSlot = InventorySlot.HeadSlot;
    private int _pendingSourceBag, _pendingSourceSlot;
    private DateTime _pendingEquipSince;
    private bool _pendingEquipSubmitted;
    private static readonly TimeSpan EquipTimeout = TimeSpan.FromSeconds(10);
    private bool Ack, Return;
    private int ItemId { get { return 100; } }
    private InventorySlot Slot { get { return InventorySlot.HeadSlot; } }
    private bool HasPendingEquip { get { return _pendingEquipGuid != 0 && _pendingEquipEntry != 0; } }
    private void ConfirmOwnedEquipPopup() { }
    private bool IsPendingEquipAcknowledged() { return Ack; }
    private bool ReturnDisplacedCursorToSource() { return Return; }
    private void RestoreOwnedCursorToSource() { }
    private bool SubmitOwnedCursorEquip() { return true; }
    private void ResetPendingEquip() { _pendingEquipGuid=0; _pendingEquipEntry=0; }
    private void LogMessage(string level, string format, params object[] args) { }
    private void Log(string format, params object[] args) { }
";
    private const string Suffix = @"
    public static bool Execute(bool acknowledged, bool returned, bool expired)
    {
        var owner = new EquipTimeoutProbe();
        owner._pendingEquipGuid=200;
        owner._pendingEquipEntry=100;
        owner._pendingEquipSubmitted=true;
        owner._pendingEquipSince=DateTime.UtcNow-TimeSpan.FromSeconds(expired ? 20 : 0);
        owner.Ack=acknowledged;
        owner.Return=returned;
        owner.TickPendingEquip();
        return owner.HasPendingEquip;
    }
}
";

    private static string ExtractMethod(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Missing tracked method: " + marker);
        int brace = source.IndexOf('{', start), depth = 0;
        // These two methods contain no brace-bearing strings/comments; do not
        // normalize or replace any statement in the extracted production method.
        for (int i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i-start+1);
        }
        throw new InvalidOperationException("Unclosed tracked method: " + marker);
    }
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) return d.FullName;
        throw new InvalidOperationException("Tracked checkout required");
    }
}

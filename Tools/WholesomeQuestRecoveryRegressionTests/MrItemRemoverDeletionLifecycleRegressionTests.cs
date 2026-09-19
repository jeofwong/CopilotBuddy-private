using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Destructive-delete ownership contract for the real tracked MrItemRemover2 plugin.
// The full plugin source directory is compiled through the production SourceCompiler
// but the plugin is never enabled, pulsed, or attached to Lua events. Source assertions
// require explicit cursor/popup/item ownership before any production repair.
internal static class MrItemRemoverDeletionLifecycleRegressionTests
{
    private sealed class Failure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        string root = Root();
        string plugin = Path.Combine(root, "runtime-snapshot", "Plugins", "MrItemRemover2");
        string methodsPath = Path.Combine(plugin, "Methods.cs");
        string ownerPath = Path.Combine(plugin, "MrItemRemover2.cs");
        string methods = File.ReadAllText(methodsPath);
        string owner = File.ReadAllText(ownerPath);

        Assembly? assembly = null;
        Exception? compileFailure = null;
        var compilerMessages = new List<string>();
        try
        {
            Type compilerType = typeof(Styx.StyxWoW).Assembly.GetType(
                "Styx.Loaders.SourceCompiler", throwOnError: true)!;
            object compiler = Activator.CreateInstance(compilerType, new object[] { plugin })!;
            string? drawing = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                ?.Split(Path.PathSeparator)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path), "System.Drawing.Common.dll",
                    StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(drawing))
                compilerType.GetMethod("AddReference", BindingFlags.Instance | BindingFlags.Public)!
                    .Invoke(compiler, new object[] { drawing });
            var results = (CompilerResults?)compilerType
                .GetMethod("Compile", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(compiler, null);
            if (results == null)
            {
                compilerMessages.Add("SourceCompiler returned no results");
            }
            else
            {
                foreach (CompilerError error in results.Errors)
                    if (!error.IsWarning)
                        compilerMessages.Add(error.ToString());
                assembly = compilerType.GetProperty(
                    "CompiledAssembly", BindingFlags.Instance | BindingFlags.Public)!
                    .GetValue(compiler) as Assembly;
            }
        }
        catch (Exception error)
        {
            compileFailure = Unwrap(error);
        }

        var cases = new List<(string Name, Action Test)>
        {
            ("tracked plugin compiles through production source compiler", () =>
            {
                Check(compileFailure == null && assembly != null,
                    "tracked MrItemRemover2 did not compile: "
                    + (compileFailure?.Message ?? string.Join(" | ", compilerMessages)));
                Check(assembly!.GetType("MrItemRemover2.MrItemRemover2", false) != null,
                    "compiled assembly does not contain the plugin owner");
            }),
            ("destructive removal has one explicit pending owner", () =>
            {
                string all = methods + "\n" + owner;
                Check(all.Contains("_pendingDeleteGuid", StringComparison.Ordinal)
                    && all.Contains("_pendingDeleteEntry", StringComparison.Ordinal),
                    "plugin does not retain exact pending GUID and entry ownership");
            }),
            ("delete pickup observes TryPickUp success", () =>
            {
                Check(methods.Contains("TryPickUp()", StringComparison.Ordinal)
                    && !methods.Contains("item.PickUp();", StringComparison.Ordinal),
                    "delete paths still use compatibility PickUp without observing success");
            }),
            ("destructive plugin never clears an unowned cursor", () =>
            {
                Check(!methods.Contains("ClearCursor()", StringComparison.Ordinal),
                    "plugin still calls ClearCursor in destructive inventory paths");
            }),
            ("delete mutation is guarded by exact cursor entry ownership", () =>
            {
                Check(methods.Contains("GetCursorInfo", StringComparison.Ordinal)
                    && methods.Contains("CursorHasItem", StringComparison.Ordinal)
                    && methods.Contains("_pendingDeleteEntry", StringComparison.Ordinal),
                    "delete mutation does not revalidate exact owned cursor entry");
            }),
            ("delete confirmation binds exact original popup identities", () =>
            {
                string region = MethodRegion(methods,
                    "private static void DeleteItemConfirmPopup");
                Check(region.Contains("StaticPopup_FindVisible", StringComparison.Ordinal)
                    && region.Contains("DELETE_ITEM", StringComparison.Ordinal)
                    && region.Contains("DELETE_GOOD_ITEM", StringComparison.Ordinal),
                    "event handler does not prove exact delete popup ownership");
                Check(!region.Contains("StaticPopup1Button1", StringComparison.Ordinal),
                    "event handler still clicks a generic popup button");
            }),
            ("good-item popup uses original confirmation string", () =>
            {
                Check(methods.Contains("DELETE_ITEM_CONFIRM_STRING", StringComparison.Ordinal)
                    && methods.Contains("editBox", StringComparison.Ordinal),
                    "quality-3+ confirmation does not use exact edit-box contract");
            }),
            ("event confirmation requires a pending delete owner", () =>
            {
                string region = MethodRegion(methods,
                    "private static void DeleteItemConfirmPopup");
                Check(region.Contains("_pendingDeleteGuid", StringComparison.Ordinal)
                    || region.Contains("HasPendingDelete", StringComparison.Ordinal),
                    "global DELETE_ITEM_CONFIRM handler can act without an owned pending transaction");
                Check(!region.Contains("Me.CurrentTarget != null", StringComparison.Ordinal),
                    "current combat target is still being used as delete confirmation ownership");
            }),
            ("pending destructive transaction has a bounded lifetime", () =>
            {
                string all = methods + "\n" + owner;
                Check((all.Contains("DeleteTimeout", StringComparison.Ordinal)
                        || all.Contains("PendingDeleteTimeout", StringComparison.Ordinal))
                    && (all.Contains("_pendingDeleteSince", StringComparison.Ordinal)
                        || all.Contains("_pendingDeleteStart", StringComparison.Ordinal)),
                    "plugin delete transaction can remain pending indefinitely");
            }),
            ("plugin does not scan into multiple simultaneous delete submissions", () =>
            {
                Check(methods.Contains("HasPendingDelete", StringComparison.Ordinal)
                    || methods.Contains("_pendingDeleteGuid != 0", StringComparison.Ordinal)
                    || methods.Contains("_pendingDeleteGuid == 0", StringComparison.Ordinal),
                    "CheckForItems has no pending-owner gate between destructive candidates");
            }),
            ("deletion acknowledgement requires cursor release and exact item absence", () =>
            {
                string all = methods + "\n" + owner;
                Check(all.Contains("_pendingDeleteGuid", StringComparison.Ordinal)
                    && all.Contains("BagItems", StringComparison.Ordinal)
                    && (all.Contains("GetObjectByGuid<WoWItem>", StringComparison.Ordinal)
                        || all.Contains("item.Guid", StringComparison.Ordinal)
                        || all.Contains("candidate.Guid", StringComparison.Ordinal))
                    && (all.Contains("GetCursorInfo", StringComparison.Ordinal)
                        || all.Contains("CursorHasItem", StringComparison.Ordinal)),
                    "plugin can advance deletion without exact item absence plus cursor release");
            }),
            ("plugin disable revokes managed ownership without stealing cursor", () =>
            {
                string region = MethodRegion(owner, "public override void OnDisable()");
                Check(region.Contains("pending", StringComparison.OrdinalIgnoreCase)
                    || region.Contains("ResetDelete", StringComparison.Ordinal),
                    "OnDisable does not revoke plugin pending-delete state");
                Check(!region.Contains("ClearCursor", StringComparison.Ordinal),
                    "OnDisable clears a cursor it may not own");
            }),
            ("quest-item protection uses stable container identity", () =>
            {
                string region = MethodRegion(methods, "private bool IsQuestItem");
                Check(!region.Contains("item.BagIndex + 1", StringComparison.Ordinal)
                    && !region.Contains("item.BagSlot + 1", StringComparison.Ordinal),
                    "quest-item guard independently derives BagIndex and BagSlot");
                Check(region.Contains("GetContainerItemQuestInfo", StringComparison.Ordinal)
                    && (region.Contains("TryResolveContainerLocation", StringComparison.Ordinal)
                        || region.Contains("TryGetContainer", StringComparison.Ordinal)
                        || region.Contains("validated", StringComparison.OrdinalIgnoreCase)),
                    "quest-item guard has no stable slot-identity boundary");
            }),
            ("first repair stays scoped away from selling/opening/combining", () =>
            {
                Check(methods.Contains("SellVenderItems", StringComparison.Ordinal)
                    && methods.Contains("Combine3List", StringComparison.Ordinal)
                    && methods.Contains("OpnList", StringComparison.Ordinal),
                    "legacy non-delete plugin features unexpectedly disappeared from the source");
            })
        };

        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try
            {
                item.Test();
                passed++;
                Console.WriteLine("PASS MrItemRemover delete: " + item.Name);
            }
            catch (Failure error)
            {
                assertions++;
                Console.Error.WriteLine(
                    "FAIL MrItemRemover delete: " + item.Name + ": " + error.Message);
            }
            catch (Exception error)
            {
                unexpected++;
                Console.Error.WriteLine(
                    "ERROR MrItemRemover delete: " + item.Name + ": " + error);
            }
        }

        Console.WriteLine(
            $"MrItemRemover delete scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; " +
            "real tracked plugin compilation plus destructive-owner source contract; plugin not enabled and no Lua/game attached.");
        if (assertions + unexpected != 0)
            throw new InvalidOperationException("MrItemRemover deletion lifecycle regression");
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException tie && tie.InnerException != null)
            error = tie.InnerException;
        return error;
    }

    private static string MethodRegion(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new Failure(marker + " is missing");

        int brace = source.IndexOf('{', start);
        if (brace < 0)
            return source.Substring(start, Math.Min(5000, source.Length - start));

        int depth = 0;
        for (int i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(start, i - start + 1);
            }
        }

        return source.Substring(start);
    }

    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj")))
                return d.FullName;
        throw new Failure("tracked checkout required");
    }

    private static void Check(bool ok, string reason)
    {
        if (!ok)
            throw new Failure(reason);
    }
}

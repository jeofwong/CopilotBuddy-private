using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Styx.Helpers;
using Styx.Logic.Profiles.Quest;

// Standalone destructive-delete contract for the real runtime-snapshot DeleteItems
// quest behavior. The behavior is compiled through the production quest-behavior
// compiler; source assertions then require explicit cursor/popup/deletion ownership.
// No game process, Lua execution, inventory mutation or popup click is performed.
internal static class DeleteItemsLifecycleRegressionTests
{
    private sealed class Failure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        string root = Root();
        string tracked = Path.Combine(root, "runtime-snapshot", "Quest Behaviors", "DeleteItems.cs");
        string source = File.ReadAllText(tracked);
        Assembly? assembly = null;
        Exception? compileFailure = null;
        var compilerMessages = new List<string>();
        string temp = Path.Combine(Path.GetTempPath(), "cb-deleteitems-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string copy = Path.Combine(temp, "DeleteItems.cs");
            File.Copy(tracked, copy);
            try
            {
                Type compilerType = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", throwOnError: true)!;
                object compiler = Activator.CreateInstance(compilerType, new object[] { copy })!;
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
                    assembly = compilerType.GetProperty("CompiledAssembly", BindingFlags.Instance | BindingFlags.Public)!
                        .GetValue(compiler) as Assembly;
                }
            }
            catch (Exception error)
            {
                compileFailure = error;
            }

            var cases = new List<(string Name, Action Test)>
            {
                ("real DeleteItems behavior compiles through production compiler", () =>
                {
                    Check(compileFailure == null && assembly != null,
                        "tracked DeleteItems did not compile: "
                        + (compileFailure?.Message ?? string.Join(" | ", compilerMessages)));
                    Check(assembly!.GetType("DeleteItems.DeleteItems", throwOnError: false) != null,
                        "compiled assembly does not contain DeleteItems.DeleteItems");
                }),
                ("safe pickup is an explicit success gate", () =>
                {
                    Check(source.Contains("TryPickUp()", StringComparison.Ordinal)
                        && !source.Contains("item.PickUp();", StringComparison.Ordinal),
                        "destructive delete still invokes compatibility PickUp without observing success");
                }),
                ("delete request has an owned cursor identity helper", () =>
                {
                    string region = MethodRegion(source, "private static string BuildOwnedDeleteRequestLua");
                    Check(region.Contains("GetCursorInfo", StringComparison.Ordinal)
                        && region.Contains("DeleteCursorItem", StringComparison.Ordinal)
                        && (region.Contains("expectedEntry", StringComparison.Ordinal)
                            || region.Contains("_pendingEntry", StringComparison.Ordinal)),
                        "delete request does not revalidate exact item cursor ownership before mutation");
                    Check(!region.Contains("ClearCursor", StringComparison.Ordinal),
                        "delete request clears a cursor it does not own");
                }),
                ("confirmation handling proves exact original popup identity", () =>
                {
                    string region = MethodRegion(source, "private static string BuildOwnedDeleteConfirmationLua");
                    Check(region.Contains("StaticPopup_FindVisible", StringComparison.Ordinal)
                        && region.Contains("DELETE_ITEM", StringComparison.Ordinal)
                        && region.Contains("DELETE_GOOD_ITEM", StringComparison.Ordinal),
                        "confirmation path does not bind to exact original delete popup identities");
                    Check(!region.Contains("StaticPopup1Button1", StringComparison.Ordinal),
                        "confirmation path clicks a generic popup button");
                }),
                ("good-item confirmation uses the original confirmation string contract", () =>
                {
                    string region = MethodRegion(source, "private static string BuildOwnedDeleteConfirmationLua");
                    Check(region.Contains("DELETE_ITEM_CONFIRM_STRING", StringComparison.Ordinal)
                        && region.Contains("editBox", StringComparison.Ordinal),
                        "high-quality delete confirmation does not use the exact original edit-box contract");
                }),
                ("pending deletion keeps exact GUID and entry ownership", () =>
                {
                    Check(source.Contains("_pendingGuid", StringComparison.Ordinal)
                        && source.Contains("_pendingEntry", StringComparison.Ordinal),
                        "pending delete state does not retain exact physical item identity and entry");
                }),
                ("pending deletion has a bounded lifetime", () =>
                {
                    Check((source.Contains("PendingTimeout", StringComparison.Ordinal)
                            || source.Contains("DeleteTimeout", StringComparison.Ordinal))
                        && (source.Contains("_pendingSince", StringComparison.Ordinal)
                            || source.Contains("_pendingStart", StringComparison.Ordinal)),
                        "delete confirmation can remain pending forever");
                }),
                ("destructive mutation no longer runs to completion inside OnStart", () =>
                {
                    string onStart = MethodRegion(source, "public override void OnStart()");
                    Check(!onStart.Contains("DeleteCursorItem", StringComparison.Ordinal)
                        && !onStart.Contains("_isBehaviorDone = true", StringComparison.Ordinal),
                        "OnStart still mutates inventory and/or marks completion on invocation");
                    Check(source.Contains("public override void OnTick()", StringComparison.Ordinal),
                        "DeleteItems has no tick-owned lifecycle for asynchronous confirmation");
                }),
                ("actual item absence is required before advancing deletion", () =>
                {
                    Check(source.Contains("_pendingGuid", StringComparison.Ordinal)
                        && source.Contains("BagItems", StringComparison.Ordinal)
                        && (source.Contains("item.Guid", StringComparison.Ordinal)
                            || source.Contains("candidate.Guid", StringComparison.Ordinal)),
                        "pending lifecycle does not reobserve bag identity before acknowledging deletion");
                }),
                ("cursor release is part of delete acknowledgement", () =>
                {
                    Check(source.Contains("GetCursorInfo", StringComparison.Ordinal)
                        || source.Contains("CursorHasItem", StringComparison.Ordinal),
                        "delete lifecycle does not observe cursor release before advancing");
                }),
                ("behavior does not complete merely because DeleteCursorItem was invoked", () =>
                {
                    int delete = source.IndexOf("DeleteCursorItem", StringComparison.Ordinal);
                    Check(delete >= 0, "delete mutation is missing entirely");
                    string tail = source.Substring(delete);
                    int done = tail.IndexOf("_isBehaviorDone = true", StringComparison.Ordinal);
                    int absence = tail.IndexOf("_pendingGuid", StringComparison.Ordinal);
                    Check(done < 0 || absence >= 0,
                        "delete invocation directly becomes behavior completion without owned acknowledgement");
                }),
                ("MrItemRemover is not folded into this owner repair", () =>
                {
                    Check(!source.Contains("MrItemRemover", StringComparison.Ordinal),
                        "standalone DeleteItems repair was coupled to a different plugin owner");
                })
            };

            int passed = 0, assertions = 0, unexpected = 0;
            foreach (var item in cases)
            {
                try
                {
                    item.Test();
                    passed++;
                    Console.WriteLine("PASS DeleteItems lifecycle: " + item.Name);
                }
                catch (Failure error)
                {
                    assertions++;
                    Console.Error.WriteLine("FAIL DeleteItems lifecycle: " + item.Name + ": " + error.Message);
                }
                catch (Exception error)
                {
                    unexpected++;
                    Console.Error.WriteLine("ERROR DeleteItems lifecycle: " + item.Name + ": " + error);
                }
            }

            Console.WriteLine(
                $"DeleteItems lifecycle scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; " +
                "real quest-behavior compilation plus tracked source ownership; no Lua/game attached.");
            if (assertions + unexpected != 0)
                throw new InvalidOperationException("DeleteItems lifecycle regression");
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // Temporary compiler artifacts are non-game evidence cleanup only.
            }
        }
    }

    private static string MethodRegion(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new Failure(marker + " is missing");
        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        if (end < 0)
            end = Math.Min(source.Length, start + 5000);
        else
            end += "\n        }".Length;
        return source.Substring(start, end - start);
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

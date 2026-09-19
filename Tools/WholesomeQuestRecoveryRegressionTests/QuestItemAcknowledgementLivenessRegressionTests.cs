using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

internal static class QuestItemAcknowledgementLivenessRegressionTests
{
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");

        string boundary = (string)typeof(QuestItemTargetSelectionRegressionTests)
            .GetField("Boundary", flags)!.GetRawConstantValue()!;
        string source = File.ReadAllText(Path.Combine(root, "runtime-snapshot", "Quest Behaviors", "UseItemOn.cs"));

        string temp = Path.Combine(Path.GetTempPath(), "cb-item-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool oldLogging = Styx.Helpers.Logging.FileLogging;
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            File.WriteAllText(Path.Combine(temp, "UseItemOn.cs"), source, Encoding.UTF8);
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), boundary, Encoding.UTF8);
            Type compilerType = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(compilerType, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                compilerType.GetMethod("AddReference", flags)!.Invoke(compiler, new object[] { path });
            var result = (CompilerResults)compilerType.GetMethod("Compile", flags)!.Invoke(compiler, null)!;
            var errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToArray();
            if (errors.Length != 0)
                throw new InvalidOperationException("Actual UseItemOn did not compile: " + string.Join(";", errors.Select(e => e.ToString())));

            Assembly assembly = (Assembly)compilerType.GetProperty("CompiledAssembly", flags)!.GetValue(compiler)!;
            Type owner = assembly.GetType("Styx.Bot.Quest_Behaviors.UseItemOn.UseItemOn", true)!;
            MethodInfo? pending = owner.GetMethod("IsAcknowledgementPending", flags);

            bool Pending(long now, long submitted, int timeout)
            {
                if (pending == null) throw new AssertionFailure("acknowledgement-window helper is missing");
                return (bool)pending.Invoke(null, new object[] { now, submitted, timeout })!;
            }

            var cases = new List<(string Name, Action Test)>
            {
                ("no submission timestamp is not pending", () => Check(!Pending(1000, -1, 5000), "missing submission became a wait")),
                ("fresh submission waits for acknowledgement", () => Check(Pending(1001, 1000, 5000), "fresh submission can retry immediately")),
                ("window remains active before deadline", () => Check(Pending(5999, 1000, 5000), "ack window expired early")),
                ("deadline releases local execution", () => Check(!Pending(6000, 1000, 5000), "ack window never expires")),
                ("clock reversal fails open", () => Check(!Pending(999, 1000, 5000), "clock reversal can strand acknowledgement wait")),
                ("invalid timeout cannot create an infinite wait", () => Check(!Pending(1001, 1000, 0), "invalid timeout created pending state")),
                ("tracked source records submission time only after accepted safe item use", () =>
                {
                    int use = source.IndexOf("if (!item.TryUseContainerItem())", StringComparison.Ordinal);
                    int stamp = source.IndexOf("_lastSubmissionUtc = UtcNowMilliseconds()", use < 0 ? 0 : use, StringComparison.Ordinal);
                    Check(use >= 0 && stamp > use,
                        "submission timestamp is absent or published before accepted safe dispatch");
                }),
                ("safe local submission refusal has its own bounded lifetime", () =>
                    Check(source.Contains("SubmissionRefusalTimeout", StringComparison.Ordinal)
                        && source.Contains("_submissionRefusalUtc", StringComparison.Ordinal)
                        && source.Contains("DeferSubmissionRefusal", StringComparison.Ordinal),
                        "safe slot refusal can spin forever outside the authoritative acknowledgement window")),
                ("authoritative mode waits before considering attempt exhaustion", () =>
                {
                    int success = source.IndexOf("HasAuthoritativeSuccess()", StringComparison.Ordinal);
                    int pendingBranch = source.IndexOf("IsAcknowledgementPending", success < 0 ? 0 : success, StringComparison.Ordinal);
                    int exhausted = source.IndexOf("AuthoritativeAttemptsExhausted", pendingBranch < 0 ? 0 : pendingBranch, StringComparison.Ordinal);
                    Check(success >= 0 && pendingBranch > success && exhausted > pendingBranch,
                        "attempt exhaustion can outrun the acknowledgement window");
                }),
                ("consumed item has explicit bounded deferral after window", () =>
                    Check(source.Contains("Item == null", StringComparison.Ordinal)
                        && source.Contains("DeferAuthoritativeAttempt", StringComparison.Ordinal),
                        "consumed or missing item can wait forever after acknowledgement deadline")),
                ("legacy invocation mode does not consult acknowledgement window", () =>
                    Check(source.Contains("SuccessEvidence != SuccessEvidenceType.InvocationCount", StringComparison.Ordinal),
                        "acknowledgement liveness was applied to legacy invocation profiles"))
            };

            int passed=0, assertions=0, unexpected=0;
            foreach (var item in cases)
            {
                try { item.Test(); passed++; Console.WriteLine("PASS quest item acknowledgement liveness: " + item.Name); }
                catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL quest item acknowledgement liveness: " + item.Name + ": " + error.Message); }
                catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR quest item acknowledgement liveness: " + item.Name + ": " + error); }
            }
            Console.WriteLine($"Quest item acknowledgement-liveness scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete tracked UseItemOn compile; no game/server execution.");
            if(assertions+unexpected!=0) throw new InvalidOperationException("Quest item acknowledgement-liveness regression");
        }
        finally
        {
            Styx.Helpers.Logging.FileLogging = oldLogging;
            Directory.Delete(temp, true);
        }
    }

    private static void Check(bool value,string message)
    {
        if(!value) throw new AssertionFailure(message);
    }
}

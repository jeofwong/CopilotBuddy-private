using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

// Complete tracked UseItemOn compiled with the existing controlled world boundary.
// This slice defines authoritative acknowledgement semantics only; it does not map
// strategy packs, execute native Lua, or claim server acceptance.
internal static class QuestItemAuthoritativeProgressRegressionTests
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

        string temp = Path.Combine(Path.GetTempPath(), "cb-item-authority-" + Guid.NewGuid().ToString("N"));
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

            var cases = new List<(string Name, Action Test)>
            {
                ("authoritative success enum exists", () =>
                {
                    Type? evidence = owner.GetNestedType("SuccessEvidenceType", flags);
                    Check(evidence != null && Enum.GetNames(evidence).SequenceEqual(new[] { "InvocationCount", "ObjectiveProgress", "QuestComplete" }),
                        "missing or rewritten success-evidence contract");
                }),
                ("objective progress ignores unchanged count", () =>
                    Check(!Ack(owner, "ObjectiveProgress", 2, 2, false), "unchanged counter became server credit")),
                ("objective progress ignores lower count", () =>
                    Check(!Ack(owner, "ObjectiveProgress", 2, 1, false), "counter regression became success")),
                ("objective progress rejects unknown count", () =>
                    Check(!Ack(owner, "ObjectiveProgress", 2, null, false), "unknown objective observation became success")),
                ("objective progress accepts a real increase", () =>
                    Check(Ack(owner, "ObjectiveProgress", 2, 3, false), "observed objective increase was not acknowledged")),
                ("quest-complete evidence ignores objective count alone", () =>
                    Check(!Ack(owner, "QuestComplete", 0, 4, false), "objective count substituted for quest completion")),
                ("quest-complete evidence accepts explicit completion", () =>
                    Check(Ack(owner, "QuestComplete", 0, null, true), "explicit quest completion was not acknowledged")),
                ("invocation-count mode is not authoritative acknowledgement", () =>
                    Check(!Ack(owner, "InvocationCount", 0, 99, false), "legacy invocation count was promoted to server credit")),
                ("tracked source captures objective baseline from actual quest descriptor", () =>
                    Check(source.Contains("ObjectivesDone", StringComparison.Ordinal)
                        && source.Contains("GetData", StringComparison.Ordinal)
                        && source.Contains("InitialObjectiveCount", StringComparison.Ordinal),
                        "authoritative mode does not capture actual quest objective state")),
                ("bounded attempts are distinct from success evidence", () =>
                    Check(source.Contains("MaxAttempts", StringComparison.Ordinal)
                        && source.Contains("AuthoritativeAttemptsExhausted", StringComparison.Ordinal)
                        && source.Contains("HasAuthoritativeSuccess", StringComparison.Ordinal),
                        "attempt exhaustion is not separated from success")),
                ("authoritative validation occurs after quest identity parsing", () =>
                {
                    int quest = source.IndexOf("QuestId = GetAttributeAsNullable<int>", StringComparison.Ordinal);
                    int validation = source.IndexOf("SuccessEvidence == SuccessEvidenceType.ObjectiveProgress", quest < 0 ? 0 : quest, StringComparison.Ordinal);
                    Check(quest >= 0 && validation > quest,
                        "authoritative mode validates before the quest identity exists");
                })
            };

            int passed = 0, assertions = 0, unexpected = 0;
            foreach (var item in cases)
            {
                try { item.Test(); passed++; Console.WriteLine("PASS quest item authority: " + item.Name); }
                catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL quest item authority: " + item.Name + ": " + error.Message); }
                catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR quest item authority: " + item.Name + ": " + error); }
            }
            Console.WriteLine($"Quest item authoritative-progress scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete tracked UseItemOn compile; controlled observations; no native/server execution.");
            if (assertions + unexpected != 0)
                throw new InvalidOperationException("Quest item authoritative-progress regression");
        }
        finally
        {
            Styx.Helpers.Logging.FileLogging = oldLogging;
            Directory.Delete(temp, true);
        }
    }

    private static bool Ack(Type owner, string evidenceName, int baseline, int? current, bool complete)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        Type? evidence = owner.GetNestedType("SuccessEvidenceType", flags);
        MethodInfo? method = owner.GetMethod("IsAuthoritativeAcknowledged", flags);
        if (evidence == null || method == null)
            throw new AssertionFailure("authoritative acknowledgement helper is missing");
        object value = Enum.Parse(evidence, evidenceName);
        try
        {
            return (bool)method.Invoke(null, new object?[] { value, baseline, current, complete })!;
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new AssertionFailure(message);
    }
}

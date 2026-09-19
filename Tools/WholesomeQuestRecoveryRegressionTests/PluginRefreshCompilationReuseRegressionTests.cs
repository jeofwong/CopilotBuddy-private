using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Styx.Plugins;
using Styx.Plugins.PluginClass;

internal sealed class PluginRefreshFixturePlugin : HBPlugin
{
    public override string Name => "PluginRefreshFixture";
    public override string Author => "test";
    public override Version Version => new Version(1, 0);
    public override void Pulse() { }
}

// Log-backed contract for repeated source-plugin refresh. It never invokes
// Roslyn: the production cache boundary receives a controlled compile delegate,
// so the red cannot exhaust runner memory or manufacture a compiler result.
internal static class PluginRefreshCompilationReuseRegressionTests
{
    private sealed class Failure(string message) : Exception(message) { }
    private const BindingFlags Hidden =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    [ModuleInitializer]
    internal static void Run()
    {
        MethodInfo? fingerprint = typeof(PluginManager).GetMethod(
            "ComputePluginSourceFingerprint", Hidden);
        MethodInfo? load = typeof(PluginManager).GetMethod(
            "LoadPluginPathWithCache", Hidden);

        var cases = new List<(string Name, Action Test)>
        {
            ("production exposes source fingerprint and cached-load owner", () =>
                Check(fingerprint != null && load != null,
                    "PluginManager has no unchanged-source compilation reuse boundary")),

            ("RefreshPlugins routes source paths through the cache owner", () =>
            {
                string source = File.ReadAllText(Path.Combine(RepositoryRoot(),
                    "Styx", "Plugins", "PluginManager.cs"));
                int loop = source.IndexOf("for (int i = 0; i < files.Count; i++)", StringComparison.Ordinal);
                int cached = source.IndexOf("LoadPluginPathWithCache", loop, StringComparison.Ordinal);
                int direct = source.IndexOf("List<HBPlugin> loadedPlugins = CompileAndLoadFrom", loop, StringComparison.Ordinal);
                Check(loop >= 0 && cached > loop && direct < 0,
                    "RefreshPlugins can still bypass unchanged-source reuse");
            }),

            ("unchanged source compiles once but returns fresh plugin instances", () =>
            {
                using var f = new Fixture();
                int compiles = 0;
                var first = Load(load, f.Root, _ =>
                {
                    compiles++;
                    return new List<HBPlugin> { new PluginRefreshFixturePlugin() };
                });
                var second = Load(load, f.Root, _ =>
                {
                    compiles++;
                    return new List<HBPlugin> { new PluginRefreshFixturePlugin() };
                });
                Check(compiles == 1, "unchanged refresh recompiled source");
                Check(first.Count == 1 && second.Count == 1
                    && !ReferenceEquals(first[0], second[0])
                    && first[0].GetType() == second[0].GetType(),
                    "cache reuse did not create a fresh instance of the exact compiled type");
            }),

            ("CSharp source edit invalidates cached compilation", () =>
            {
                using var f = new Fixture();
                int compiles = 0;
                Func<string,List<HBPlugin>> compile = _ =>
                {
                    compiles++;
                    return new List<HBPlugin> { new PluginRefreshFixturePlugin() };
                };
                Load(load, f.Root, compile);
                File.AppendAllText(f.Source, "\n// changed");
                Load(load, f.Root, compile);
                Check(compiles == 2, "changed CSharp source reused stale compiled types");
            }),

            ("embedded resource edit invalidates cached compilation", () =>
            {
                using var f = new Fixture(includeResx:true);
                int compiles = 0;
                Func<string,List<HBPlugin>> compile = _ =>
                {
                    compiles++;
                    return new List<HBPlugin> { new PluginRefreshFixturePlugin() };
                };
                Load(load, f.Root, compile);
                File.AppendAllText(f.Resx!, "\n<!-- changed -->");
                Load(load, f.Root, compile);
                Check(compiles == 2, "changed resx reused stale compiled types");
            }),

            ("uncompiled unrelated file does not force another assembly", () =>
            {
                using var f = new Fixture();
                int compiles = 0;
                Func<string,List<HBPlugin>> compile = _ =>
                {
                    compiles++;
                    return new List<HBPlugin> { new PluginRefreshFixturePlugin() };
                };
                Load(load, f.Root, compile);
                File.WriteAllText(Path.Combine(f.Root, "notes.txt"), "not a compiler input");
                Load(load, f.Root, compile);
                Check(compiles == 1, "non-compiler input invalidated source cache");
            }),

            ("failed changed-source compile does not poison last valid cache", () =>
            {
                using var f = new Fixture();
                int compiles = 0;
                Func<string,List<HBPlugin>> good = _ =>
                {
                    compiles++;
                    return new List<HBPlugin> { new PluginRefreshFixturePlugin() };
                };
                Load(load, f.Root, good);
                string original = File.ReadAllText(f.Source);
                File.WriteAllText(f.Source, original + "\n// broken revision");
                Exception? seen = null;
                try
                {
                    Load(load, f.Root, _ =>
                    {
                        compiles++;
                        throw new InvalidOperationException("controlled compiler failure");
                    });
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    seen = e.InnerException;
                }
                catch (Exception e) { seen = e; }
                Check(seen is InvalidOperationException
                    && seen.Message == "controlled compiler failure",
                    "compile failure identity changed");
                File.WriteAllText(f.Source, original);
                Load(load, f.Root, good);
                Check(compiles == 2,
                    "failed changed-source compile discarded the still-valid restored cache");
            }),

            ("source mutation during compile is returned but not cached", () =>
            {
                using var f = new Fixture();
                int compiles = 0;
                Func<string,List<HBPlugin>> compile = _ =>
                {
                    compiles++;
                    if (compiles == 1)
                        File.AppendAllText(f.Source, "\n// changed during compile");
                    return new List<HBPlugin> { new PluginRefreshFixturePlugin() };
                };
                Load(load, f.Root, compile);
                Load(load, f.Root, compile);
                Check(compiles == 2,
                    "a compile whose inputs changed mid-flight became reusable");
            }),

            ("fingerprint is stable and path-sensitive across compiled inputs", () =>
            {
                using var f = new Fixture();
                string a = Fingerprint(fingerprint, f.Root);
                string b = Fingerprint(fingerprint, f.Root);
                Check(a == b && a.Length == 64, "unchanged source fingerprint is unstable");
                File.WriteAllText(Path.Combine(f.Root, "B.cs"),
                    File.ReadAllText(f.Source));
                string c = Fingerprint(fingerprint, f.Root);
                Check(c != a, "adding a compiled source path did not change fingerprint");
            })
        };

        int passed=0, assertions=0, unexpected=0;
        foreach (var c in cases)
        {
            try { c.Test(); passed++; Console.WriteLine("PASS plugin refresh reuse: " + c.Name); }
            catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL plugin refresh reuse assertion: " + c.Name + ": " + e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR plugin refresh reuse fixture: " + c.Name + ": " + e); }
        }
        Console.WriteLine($"Plugin refresh reuse scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; production PluginManager boundary with controlled compile delegate; no Roslyn or game attached.");
        if (assertions + unexpected != 0)
            throw new InvalidOperationException("Plugin refresh reuse regressions");
    }

    private static List<HBPlugin> Load(
        MethodInfo? method,
        string path,
        Func<string,List<HBPlugin>> compiler)
    {
        if (method == null) throw new Failure("PluginManager.LoadPluginPathWithCache is missing");
        try
        {
            return (List<HBPlugin>)(method.Invoke(null, new object[] { path, compiler })
                ?? throw new Failure("cached-load owner returned null"));
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static string Fingerprint(MethodInfo? method, string path)
    {
        if (method == null) throw new Failure("PluginManager.ComputePluginSourceFingerprint is missing");
        try
        {
            return Convert.ToString(method.Invoke(null, new object[] { path }),
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj")))
                return d.FullName;
        throw new Failure("tracked checkout required");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root =
            Path.Combine(Path.GetTempPath(), "cb-plugin-refresh-" + Guid.NewGuid().ToString("N"));
        internal readonly string Source;
        internal readonly string? Resx;

        internal Fixture(bool includeResx=false)
        {
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "Plugin.cs");
            File.WriteAllText(Source, "// controlled plugin source\n");
            if (includeResx)
            {
                Resx = Path.Combine(Root, "Plugin.resx");
                File.WriteAllText(Resx, "<root />");
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private static void Check(bool ok, string why)
    {
        if (!ok) throw new Failure(why);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using WholesomeAQ;
using Styx.Logic.Questing.Recovery;

// Dataset bytes and their declared source identity are separate evidence.
// This fixture uses controlled files only; no realm DB, network or addon data.
internal static class QuestDatasetProvenanceRegressionTests
{
    private sealed class AssertionFailure(string message) : Exception(message) { }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action<string> Test)>
        {
            ("missing sidecar remains explicitly unknown", root =>
            {
                string data = Data(root);
                var loader = new DataLoader(data);
                Check(loader.Load() != null, "ordinary dataset no longer loads");
                object identity = Identity(loader);
                Check(Value(identity, "Status") == "Unknown", "missing provenance was silently promoted");
                Check(Value(identity, "SourceCore") == "unknown", "unknown source gained a core label");
                Check(loader.DatasetFingerprint == DataLoader.CreateDatasetFingerprint(new[] { data }),
                    "legacy no-sidecar content identity changed");
            }),
            ("TrinityCore declaration is bound to exact dataset bytes", root =>
            {
                string data = Data(root);
                string sidecar = Manifest(root, data, "trinitycore-3.3.5", "3.3.5", "8fda442f6c30ca21a622638063ab8b28376f1b25");
                var loader = new DataLoader(data);
                Check(loader.Load() != null, "declared dataset did not load");
                object identity = Identity(loader);
                Check(Value(identity, "Status") == "DeclaredAndBound", "valid declaration was not bound");
                Check(Value(identity, "SourceCore") == "trinitycore-3.3.5", "core identity changed");
                Check(Value(identity, "CoreRevision") == "8fda442f6c30ca21a622638063ab8b28376f1b25", "revision identity changed");
                Check(loader.DatasetFingerprint == DataLoader.CreateDatasetFingerprint(new[] { data, sidecar }),
                    "provenance bytes were not bound into dataset identity");
            }),
            ("AzerothCore declaration remains a distinct explicit source", root =>
            {
                string data = Data(root);
                Manifest(root, data, "azerothcore-wotlk", "master", "8337a378ac325e62a6a91e00c6a5e944205e8536");
                var loader = new DataLoader(data); loader.Load();
                object identity = Identity(loader);
                Check(Value(identity, "Status") == "DeclaredAndBound"
                    && Value(identity, "SourceCore") == "azerothcore-wotlk",
                    "secondary source was relabelled or discarded");
            }),
            ("same quest bytes with different source revision have different identity", root =>
            {
                string data = Data(root);
                string sidecar = Manifest(root, data, "trinitycore-3.3.5", "3.3.5", "rev-a");
                var first = new DataLoader(data); first.Load(); string before = first.DatasetFingerprint;
                Manifest(root, data, "trinitycore-3.3.5", "3.3.5", "rev-b");
                var second = new DataLoader(data); second.Load();
                Check(before != second.DatasetFingerprint, "source revision was outside the content identity");
                Check(Value(Identity(second), "CoreRevision") == "rev-b", "updated declaration was not captured");
            }),
            ("wrong client build fails instead of silently loading", root =>
            {
                string data = Data(root);
                Manifest(root, data, "trinitycore-3.3.5", "3.3.5", "rev", clientBuild: 12341);
                var loader = new DataLoader(data);
                Throws<InvalidDataException>(() => loader.Load(), "wrong client build was accepted");
                Check(loader.Database == null && loader.DatasetFingerprint == "unknown",
                    "failed provenance validation cached dataset state");
            }),
            ("mismatched quest-data digest fails closed", root =>
            {
                string data = Data(root);
                Manifest(root, data, "trinitycore-3.3.5", "3.3.5", "rev", digest: new string('0', 64));
                var loader = new DataLoader(data);
                Throws<InvalidDataException>(() => loader.Load(), "sidecar was not bound to the parsed quest bytes");
            }),
            ("unsupported core label is not interpreted heuristically", root =>
            {
                string data = Data(root);
                Manifest(root, data, "warmane-custom", "3.3.5", "rev");
                Throws<InvalidDataException>(() => new DataLoader(data).Load(),
                    "realm/project label became an implicit supported core");
            }),
            ("missing required source field is rejected", root =>
            {
                string data = Data(root);
                string path = Path.Combine(root, "quest_data.provenance.json");
                File.WriteAllText(path, "{\"Schema\":\"quest-dataset-provenance-v1\",\"ClientBuild\":12340}");
                Throws<InvalidDataException>(() => new DataLoader(data).Load(),
                    "partial provenance declaration fell back to unknown");
            }),
            ("unknown sidecar fields are rejected instead of silently forwarded", root =>
            {
                string data = Data(root);
                string path = Manifest(root, data, "trinitycore-3.3.5", "3.3.5", "rev");
                string json = File.ReadAllText(path).TrimEnd('}', ' ', '\r', '\n') + ",\"ServerCredentials\":\"forbidden\"}";
                File.WriteAllText(path, json);
                Throws<InvalidDataException>(() => new DataLoader(data).Load(),
                    "unsupported provenance fields were silently ignored");
            }),
            ("cached loader retains the identity of the bytes it actually parsed", root =>
            {
                string data = Data(root);
                string path = Manifest(root, data, "trinitycore-3.3.5", "3.3.5", "rev-a");
                var loader = new DataLoader(data); object db = loader.Load()!;
                string fingerprint = loader.DatasetFingerprint;
                File.WriteAllText(path, "{}");
                Check(ReferenceEquals(db, loader.Load()) && loader.DatasetFingerprint == fingerprint
                    && Value(Identity(loader), "CoreRevision") == "rev-a",
                    "cached dataset adopted later unparsed provenance bytes");
            })
        };

        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            string root = Path.Combine(Path.GetTempPath(), "cb-provenance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { item.Test(root); passed++; Console.WriteLine("PASS quest provenance: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL quest provenance: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR quest provenance: " + item.Name + ": " + error); }
            finally { Directory.Delete(root, true); QuestPrerequisiteAuthority.ClearPublishedDependencyAuthority(); }
        }
        Console.WriteLine($"Quest dataset provenance scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual DataLoader; controlled files; no server/network/game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Quest dataset provenance regression");
    }

    private static string Data(string root)
    {
        string path = Path.Combine(root, "quest_data.json");
        File.WriteAllText(path, "{\"Quests\":[]}", Encoding.UTF8);
        return path;
    }

    private static string Manifest(string root, string data, string core, string branch, string revision,
        int clientBuild = 12340, string? digest = null)
    {
        string dataDigest = digest ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(data))).ToLowerInvariant();
        string path = Path.Combine(root, "quest_data.provenance.json");
        string json = "{" +
            "\"Schema\":\"quest-dataset-provenance-v1\"," +
            "\"ClientBuild\":" + clientBuild + "," +
            "\"SourceCore\":\"" + core + "\"," +
            "\"SourceBranch\":\"" + branch + "\"," +
            "\"CoreRevision\":\"" + revision + "\"," +
            "\"DatabaseRevision\":\"controlled-db\"," +
            "\"Exporter\":\"controlled-fixture\"," +
            "\"ExporterVersion\":\"1\"," +
            "\"QuestDataSha256\":\"" + dataDigest + "\"," +
            "\"RealmOverridesDeclared\":false}";
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    private static object Identity(DataLoader loader)
    {
        PropertyInfo? property = typeof(DataLoader).GetProperty("DatasetSourceIdentity",
            BindingFlags.Instance | BindingFlags.Public);
        if (property == null) throw new AssertionFailure("DataLoader exposes no dataset source identity");
        return property.GetValue(loader) ?? throw new AssertionFailure("dataset source identity is null");
    }

    private static string Value(object identity, string name)
    {
        PropertyInfo? property = identity.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property == null) throw new AssertionFailure("source identity field missing: " + name);
        object? value = property.GetValue(identity);
        return value?.ToString() ?? "";
    }

    private static void Throws<T>(Action action, string reason) where T : Exception
    {
        try { action(); } catch (T) { return; }
        catch (TargetInvocationException error) when (error.InnerException is T) { return; }
        throw new AssertionFailure(reason);
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid) throw new AssertionFailure(reason);
    }
}

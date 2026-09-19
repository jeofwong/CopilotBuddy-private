using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using WholesomeAQ;
using Styx.Logic.Questing.Recovery;

internal static class DatasetIdentityRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        var tests = new (string Name, Action<string> Run)[]
        {
            ("same-size same-mtime content edits invalidate identity", root =>
            {
                string p = Write(root, "quest_data.json", "alpha");
                DateTime time = File.GetLastWriteTimeUtc(p);
                string before = Fingerprint(p);
                File.WriteAllText(p, "bravo"); File.SetLastWriteTimeUtc(p, time);
                Check(before != Fingerprint(p), "different bytes must not reuse quest recovery context");
            }),
            ("timestamp-only changes preserve content identity", root =>
            {
                string p = Write(root, "quest_data.json", "same"); string before = Fingerprint(p);
                File.SetLastWriteTimeUtc(p, File.GetLastWriteTimeUtc(p).AddHours(2));
                Check(before == Fingerprint(p), "touching a file must not reset unchanged quest recovery context");
            }),
            ("relocation preserves logical-role and content identity", root =>
            {
                string a = Write(root, "a/quest_data.json", "same");
                string b = Write(root, "b/quest_data.json", "same");
                Check(Fingerprint(a) == Fingerprint(b), "absolute installation paths are not dataset content");
            }),
            ("legacy v2 manifest preserves pre-provenance digest identity", root =>
            {
                string p = Write(root, "quest_data.json", "{\"Quests\":[]}");
                Check(Fingerprint(p) == "e69a63f01976efd1a9c32d9488c2cda78a5d5512ac5a4fb0f88e83b2adebaee9",
                    "provenance support changed the existing no-sidecar recovery identity");
            }),
            ("manifest enumeration order is irrelevant", root =>
            {
                string a = Write(root, "quests.json", "quest"), b = Write(root, "spawns.json", "spawn");
                Check(Fingerprint(a,b) == Fingerprint(b,a), "input order must not change the manifest");
            }),
            ("swapping bytes across semantic roles changes identity", root =>
            {
                string a = Write(root, "quests.json", "aaaaa"), b = Write(root, "spawns.json", "bbbbb");
                DateTime ta = File.GetLastWriteTimeUtc(a), tb = File.GetLastWriteTimeUtc(b);
                string before = Fingerprint(a,b);
                File.WriteAllText(a,"bbbbb"); File.WriteAllText(b,"aaaaa");
                File.SetLastWriteTimeUtc(a,ta); File.SetLastWriteTimeUtc(b,tb);
                Check(before != Fingerprint(a,b), "a content multiset must not lose file role identity");
            }),
            ("duplicate logical filenames fail explicitly", root =>
            {
                string a = Write(root,"a/quest_data.json","one"), b = Write(root,"b/quest_data.json","two");
                Throws<InvalidDataException>(() => Fingerprint(a,b), "ambiguous roles must not silently alias");
            }),
            ("missing file never produces a success fingerprint", root =>
                Throws<IOException>(() => Fingerprint(Path.Combine(root,"missing.json")), "missing data must fail")),
            ("empty dataset manifest is invalid", root =>
                Throws<InvalidDataException>(() => Fingerprint(), "no data is not a valid dataset")),
            ("failed dataset load never caches partially published state", root =>
            {
                string p = Write(root,"quest_data.json","{\"Quests\":null}"); var loader = new DataLoader(p);
                try { loader.Load(); } catch (Exception) { }
                Check(loader.Database == null && loader.DatasetFingerprint == "unknown", "failed publication must leave no cached database or fingerprint");
                File.WriteAllText(p,"{\"Quests\":[]}");
                Check(loader.Load() != null, "the same loader must recover after the file is corrected");
            }),
            ("loaded snapshot identity matches its actual bytes", root =>
            {
                string p = Write(root,"quest_data.json","{\"Quests\":[]}"); var loader = new DataLoader(p);
                Check(loader.Load() != null && loader.DatasetFingerprint == Fingerprint(p), "load and manifest APIs must agree on the same snapshot");
                string before = loader.DatasetFingerprint;
                File.WriteAllText(p,"{\"Quests\":[],\"ZoneId\":123}");
                loader.Load();
                Check(loader.DatasetFingerprint == before, "a cached database must retain the identity of bytes it parsed, not a later file version");
            }),
            ("UTF16 BOM input remains loadable and content-identified", root =>
            {
                string p = Path.Combine(root,"quest_data.json");
                File.WriteAllText(p,"{\"Quests\":[]}",Encoding.Unicode);
                var loader = new DataLoader(p);
                Check(loader.Load() != null && loader.DatasetFingerprint == Fingerprint(p), "preserve the existing BOM-aware input contract");
            })
        };
        int failed = 0;
        foreach (var test in tests)
        {
            string root = Path.Combine(Path.GetTempPath(), "cb-dataset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test.Run(root); Console.WriteLine("PASS dataset: " + test.Name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL dataset: " + test.Name + ": " + error.Message); }
            finally { Directory.Delete(root,true); QuestPrerequisiteAuthority.ClearPublishedDependencyAuthority(); }
        }
        Console.WriteLine($"Dataset identity scenarios: {tests.Length-failed}/{tests.Length}; no game attached.");
        if (failed != 0) throw new InvalidOperationException($"{failed} dataset identity scenarios failed.");
    }
    private static string Fingerprint(params string[] paths) => DataLoader.CreateDatasetFingerprint(paths);
    private static string Write(string root,string relative,string data)
    {
        string path = Path.Combine(root,relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,data); return path;
    }
    private static void Check(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action,string message) where T:Exception
    {
        try { action(); } catch(T) { return; }
        throw new InvalidOperationException(message);
    }
}

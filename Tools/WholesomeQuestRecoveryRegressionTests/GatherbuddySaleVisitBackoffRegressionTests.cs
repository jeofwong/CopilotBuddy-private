using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Bots.Gatherbuddy;

// Whole-visit liveness contract for the already-bounded per-stack sale retry.
// This does not attach to WoW, submit a sale, or infer merchant acceptance.
internal static class GatherbuddySaleVisitBackoffRegressionTests
{
    private sealed class AssertionFailure : Exception
    {
        internal AssertionFailure(string message) : base(message) { }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");

        string source = File.ReadAllText(Path.Combine(root, "Bots", "Gatherbuddy", "GatherbuddyBot.cs"));
        const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        MethodInfo? policy = typeof(GatherbuddyBot).GetMethod("ShouldDeferSaleVisit", Hidden);

        bool Defer(DateTime now, DateTime last)
        {
            if (policy == null) throw new AssertionFailure("finite GatherBuddy sale-visit policy is missing");
            object? value = policy.Invoke(null, new object[] { now, last });
            return value is bool result ? result : throw new AssertionFailure("sale-visit policy did not return bool");
        }

        DateTime t = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        var cases = new List<(string Name, Action Test)>
        {
            ("no prior completed visit does not defer", () =>
                Check(!Defer(t, DateTime.MinValue), "fresh bot session was deferred")),
            ("immediate repeat visit is deferred", () =>
                Check(Defer(t, t), "same-tick full-bag vendor re-entry remained eligible")),
            ("retry interval remains active immediately before two minutes", () =>
                Check(Defer(t.AddMilliseconds(119999), t), "visit backoff expired before stack retry gate")),
            ("two-minute boundary permits a fresh visit", () =>
                Check(!Defer(t.AddMilliseconds(120000), t), "finite visit backoff never recovered")),
            ("future or invalid clock sample fails open rather than permanent lockout", () =>
                Check(!Defer(t.AddSeconds(-1), t), "clock reversal created an indefinite vendor lockout")),
            ("bag-full admission consults sale visit backoff", () =>
            {
                int start = source.IndexOf("private bool NeedsBagsEmptied", StringComparison.Ordinal);
                int end = start < 0 ? -1 : source.IndexOf("// HB 3.3.5a smethod_12", start, StringComparison.Ordinal);
                Check(start >= 0 && end > start, "bag-full admission region changed");
                string region = source.Substring(start, end - start);
                Check(region.Contains("ShouldDeferSaleVisit", StringComparison.Ordinal)
                    && region.Contains("_lastSaleVisitAt", StringComparison.Ordinal),
                    "bag-full admission ignores the completed sale-visit backoff");
            }),
            ("terminal still-open sale records a bounded visit timestamp", () =>
            {
                int start = source.IndexOf("private Composite CreateSellBehavior", StringComparison.Ordinal);
                int end = start < 0 ? -1 : source.IndexOf("private bool NeedsBagsEmptied", start, StringComparison.Ordinal);
                Check(start >= 0 && end > start, "sell behavior region changed");
                string region = source.Substring(start, end - start);
                int step = region.IndexOf("SellAllItemsStep()", StringComparison.Ordinal);
                int visible = region.IndexOf("MerchantFrame.Instance.IsVisible", step < 0 ? 0 : step, StringComparison.Ordinal);
                int stamp = region.IndexOf("_lastSaleVisitAt", step < 0 ? 0 : step, StringComparison.Ordinal);
                Check(step >= 0 && visible > step && stamp > visible,
                    "terminal sale does not distinguish a still-open visit before recording backoff");
            }),
            ("bot start clears stale visit backoff", () =>
            {
                int start = source.IndexOf("public override void Start()", StringComparison.Ordinal);
                int end = start < 0 ? -1 : source.IndexOf("#endregion", start, StringComparison.Ordinal);
                Check(start >= 0 && end > start, "start lifecycle region changed");
                string region = source.Substring(start, end - start);
                int field = region.IndexOf("_lastSaleVisitAt", StringComparison.Ordinal);
                int reset = field < 0 ? -1 : region.IndexOf("DateTime.MinValue", field, StringComparison.Ordinal);
                Check(field >= 0 && reset > field,
                    "new GatherBuddy session inherited a prior sale backoff");
            }),
            ("mail cooldown remains an independent owner", () =>
                Check(source.Contains("_lastMailedAt", StringComparison.Ordinal)
                    && source.Contains("MailCooldown", StringComparison.Ordinal),
                    "sale-visit repair rewrote the independent mail cooldown contract"))
        };

        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS GatherBuddy sale-visit backoff: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL GatherBuddy sale-visit backoff: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR GatherBuddy sale-visit backoff: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"GatherBuddy sale-visit backoff scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual tracked source and policy; no game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("GatherBuddy sale-visit backoff regression");
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid) throw new AssertionFailure(reason);
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Styx.Logic.Inventory.Frames.Merchant;

// Actual MerchantSaleAttemptGate through reflection. The query/time boundary is
// controlled; no Lua interpreter, native sale or game client is attached.
internal static class MerchantSalePendingLivenessRegressionTests
{
    private sealed class AssertionFailure : Exception
    {
        internal AssertionFailure(string message) : base(message) { }
    }

    private sealed class Fixture
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly object gate;
        private readonly MethodInfo execute;
        internal int Queries;
        internal List<string>? Result = new() { "ok", "2" };
        internal Exception? Error;

        internal Fixture()
        {
            Type type = typeof(MerchantFrame).Assembly.GetType(
                "Styx.Logic.Inventory.Frames.Merchant.MerchantSaleAttemptGate", true)!;
            gate = Activator.CreateInstance(type, true)!;
            execute = type.GetMethod("Execute", Hidden)!;
        }

        internal int Step(long now)
        {
            Func<string, List<string>> query = _ =>
            {
                Queries++;
                if (Error != null) throw Error;
                return Result!;
            };
            try
            {
                return (int)execute.Invoke(gate, new object[] { "return 'ok',0", query, now })!;
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action Test)>
        {
            ("first locked observation remains pending", () =>
            {
                var f = new Fixture();
                Check(f.Step(1000) == 2 && f.Queries == 1, "locked observation changed result");
            }),
            ("locked observation is not requeried every bot tick", () =>
            {
                var f = new Fixture();
                Check(f.Step(1000) == 2, "first pending failed");
                Check(f.Step(1001) == 2 && f.Queries == 1, "pending observation queried again immediately");
                Check(f.Step(1999) == 2 && f.Queries == 1, "pending observation ignored one-second poll interval");
            }),
            ("locked observation can be refreshed after one second", () =>
            {
                var f = new Fixture();
                f.Step(1000);
                Check(f.Step(2000) == 2 && f.Queries == 2, "bounded observation did not refresh");
            }),
            ("locked no-progress visit defers at ten seconds", () =>
            {
                var f = new Fixture();
                f.Step(1000);
                for (long now = 2000; now < 11000; now += 1000)
                    Check(f.Step(now) == 2, "pending visit terminated early");
                int queries = f.Queries;
                Check(f.Step(11000) == 4 && f.Queries == queries, "ten-second no-progress boundary did not defer before another query");
            }),
            ("deferral releases pending timer for a later visit", () =>
            {
                var f = new Fixture();
                f.Step(1000);
                f.Step(11000);
                f.Result = new() { "ok", "0" };
                Check(f.Step(11000) == 0 && f.Queries == 2, "later visit could not recover after bounded deferral");
            }),
            ("malformed result remains unknown on first observation", () =>
            {
                var f = new Fixture { Result = new List<string>() };
                Check(f.Step(1000) == -1 && f.Queries == 1, "first malformed result was converted to success");
            }),
            ("malformed result cannot trigger repeated immediate native queries", () =>
            {
                var f = new Fixture { Result = new List<string>() };
                Check(f.Step(1000) == -1, "first malformed result changed");
                Check(f.Step(1001) == 2 && f.Queries == 1, "malformed observation was requeried immediately");
            }),
            ("persistent malformed results defer at ten seconds", () =>
            {
                var f = new Fixture { Result = new List<string>() };
                f.Step(1000);
                for (long now = 2000; now < 11000; now += 1000)
                    f.Step(now);
                int queries = f.Queries;
                Check(f.Step(11000) == 4 && f.Queries == queries, "persistent malformed observation never bounded the visit");
            }),
            ("terminal empty result clears pending observation state", () =>
            {
                var f = new Fixture();
                f.Step(1000);
                f.Result = new() { "ok", "0" };
                Check(f.Step(2000) == 0, "empty result did not terminate");
                f.Result = new() { "ok", "2" };
                Check(f.Step(2001) == 2 && f.Queries == 3, "new pending episode inherited old no-progress age");
            }),
            ("submitted receipt clears pending observation state", () =>
            {
                var f = new Fixture();
                f.Step(1000);
                f.Result = new() { "ok", "1", "p:m:0:1:1:item:100" };
                Check(f.Step(2000) == 1, "submission was not retained");
                f.Result = new() { "ok", "2" };
                Check(f.Step(2001) == 2 && f.Queries == 3, "progress did not reset pending episode");
            }),
            ("cancellation preserves identity and does not strand the gate", () =>
            {
                var f = new Fixture();
                var stop = new OperationCanceledException("stop");
                f.Error = stop;
                Exception? seen = null;
                try { f.Step(1000); } catch (Exception error) { seen = error; }
                Check(ReferenceEquals(stop, seen), "cancellation identity changed");
                f.Error = null; f.Result = new() { "ok", "0" };
                Check(f.Step(1001) == 0, "cancellation stranded gate execution");
            }),
            ("independent merchant gate instances do not share pending age", () =>
            {
                var a = new Fixture(); var b = new Fixture();
                a.Step(1000);
                Check(b.Step(10999) == 2 && b.Queries == 1, "one merchant gate deferred another instance");
            })
        };

        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS merchant pending liveness: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL merchant pending liveness: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR merchant pending liveness: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"Merchant pending-liveness scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual MerchantSaleAttemptGate; controlled query/time; no game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Merchant pending-liveness regression");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new AssertionFailure(reason);
    }
}

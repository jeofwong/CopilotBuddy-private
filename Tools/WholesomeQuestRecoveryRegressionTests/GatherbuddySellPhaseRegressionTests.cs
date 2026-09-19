using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Bots.Gatherbuddy;
using TreeSharp;

// Invoke the actual host builder and tick its actual outer/inner composites.
// Native arrival and service leaves are controlled; no movement, UI, merchant,
// timing, acknowledgement or full GatherBuddy-root acceptance is claimed.
internal static class GatherbuddySellPhaseRegressionTests
{
    private sealed class AssertionFailure : Exception
    {
        internal AssertionFailure(string message) : base(message) { }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
            throw new PlatformNotSupportedException("GatherBuddy sale-phase tests require Windows x86.");
        var cases = new List<(string Name, System.Action Test)>
        {
            ("arrival success reaches service and cleanup once", () =>
            {
                using var f = new Fixture();
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 1 && f.Sales == 1 && f.Cleanups == 1,
                    "arrival success bypassed service");
                Check(f.Trace == "arrival,0,1,2,3,4,5,6,7,8,9", "service order changed");
            }),
            ("arrival failure cannot fall through to service", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Failure;
                Check(f.Tick() == RunStatus.Failure && f.ServiceTicks == 0, "failed arrival authorized service");
            }),
            ("running approach keeps service untouched", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running && f.Tick() == RunStatus.Running
                    && f.Arrivals == 2 && f.ServiceTicks == 0, "pending arrival reached service");
            }),
            ("delayed arrival success enters service", () =>
            {
                using var f = new Fixture(); f.Arrival = n => n == 1 ? RunStatus.Running : RunStatus.Success;
                Check(f.Tick() == RunStatus.Running && f.ServiceTicks == 0, "first approach escaped");
                Check(f.Tick() == RunStatus.Success && f.Sales == 1 && f.Cleanups == 1,
                    "completed approach did not advance to service");
            }),
            ("delayed arrival failure still prevents service", () =>
            {
                using var f = new Fixture(); f.Arrival = n => n == 1 ? RunStatus.Running : RunStatus.Failure;
                Check(f.Tick() == RunStatus.Running, "first approach escaped");
                Check(f.Tick() == RunStatus.Failure && f.ServiceTicks == 0, "late arrival failure authorized service");
            }),
            ("pending sale keeps the visit running", () =>
            {
                using var f = new Fixture(); f.Sale = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running && f.Sales == 1 && f.Cleanups == 0,
                    "pending sale became a completed visit");
            }),
            ("pending sale resumes without repeating arrival or interaction", () =>
            {
                using var f = new Fixture(); f.Sale = n => n < 3 ? RunStatus.Running : RunStatus.Success;
                Check(f.Tick() == RunStatus.Running && f.Tick() == RunStatus.Running,
                    "sale continuation was not retained");
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 1 && f.Hits[2] == 1
                    && f.Sales == 3 && f.Cleanups == 1, "continuation replayed setup or skipped completion");
            }),
            ("service failure propagates without later service effects", () =>
            {
                using var f = new Fixture(); f.Sale = _ => RunStatus.Failure;
                Check(f.Tick() == RunStatus.Failure && f.Sales == 1 && f.Hits[6] == 0 && f.Cleanups == 0,
                    "service failure became success or reached later effects");
            }),
            ("delayed service failure does not restart arrival", () =>
            {
                using var f = new Fixture(); f.Sale = n => n == 1 ? RunStatus.Running : RunStatus.Failure;
                Check(f.Tick() == RunStatus.Running && f.Tick() == RunStatus.Failure
                    && f.Arrivals == 1 && f.Sales == 2 && f.Cleanups == 0,
                    "failed continuation replayed arrival or completed service");
            }),
            ("stop during arrival cannot dispatch service", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running, "approach did not run"); f.Stop();
                Check(f.Tick() == RunStatus.Failure && f.ServiceTicks == 0, "stopped approach dispatched service");
            }),
            ("stop during pending sale cannot dispatch later leaves", () =>
            {
                using var f = new Fixture(); f.Sale = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running && f.Sales == 1, "sale did not remain pending"); f.Stop();
                Check(f.Tick() == RunStatus.Failure && f.Sales == 1 && f.Cleanups == 0,
                    "stopped sale continued into later leaves");
            }),
            ("restart after stopped approach gets a fresh arrival", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running, "approach did not run"); f.Stop();
                f.Arrival = _ => RunStatus.Success; f.Start(new object());
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 2 && f.Sales == 1,
                    "fresh visit did not repeat admission and then service");
            }),
            ("a second completed visit performs new arrival and service", () =>
            {
                using var f = new Fixture(); Check(f.Tick() == RunStatus.Success, "first visit failed");
                f.Start(new object());
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 2 && f.Sales == 2 && f.Cleanups == 2,
                    "completed visit was reused as a substitute for a new visit");
            }),
            ("both phases retain the supplied context", () =>
            {
                using var f = new Fixture(); object context = new object(); f.Start(context);
                Check(f.Tick() == RunStatus.Success && f.Contexts.Count == 11, "both phases did not run");
                foreach (object observed in f.Contexts) Check(ReferenceEquals(context, observed), "context changed");
            }),
            ("arrival cancellation propagates without service", () => Signal(false, false)),
            ("arrival interruption propagates without service", () => Signal(false, true)),
            ("sale cancellation propagates without later effects", () => Signal(true, false)),
            ("sale interruption propagates without later effects", () => Signal(true, true)),
            ("independent constructed visits retain separate phase lifetimes", () =>
            {
                using var first = new Fixture(); using var second = new Fixture();
                first.Arrival = _ => RunStatus.Running;
                Check(first.Tick() == RunStatus.Running && second.Tick() == RunStatus.Success
                    && first.ServiceTicks == 0 && second.Sales == 1, "one tree consumed another tree's phase");
            }),
            ("completed arrival failure can recover in a new visit", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Failure;
                Check(f.Tick() == RunStatus.Failure && f.ServiceTicks == 0, "failed visit performed service");
                f.Arrival = _ => RunStatus.Success; f.Start(new object());
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 2 && f.Sales == 1,
                    "fresh visit could not recover after failed arrival");
            })
        };
        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS GatherBuddy sell phase: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL GatherBuddy sell phase: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR GatherBuddy sell phase: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"GatherBuddy sell-phase scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual builder and TreeSharp composites; controlled native leaves; no game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("GatherBuddy sell-phase regression");
    }

    private static void Signal(bool sale, bool interruption)
    {
        using var f = new Fixture();
        Exception signal = interruption ? new ThreadInterruptedException("phase stop") : new OperationCanceledException("phase stop");
        if (sale) f.Sale = _ => throw signal; else f.Arrival = _ => throw signal;
        Exception? observed = null;
        try { f.Tick(); } catch (Exception error) { observed = error; }
        Check(ReferenceEquals(signal, observed), "exact stop signal was lost or its phase was skipped");
        Check(f.Cleanups == 0 && (sale ? f.Sales == 1 : f.ServiceTicks == 0), "stop signal authorized later effects");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GroupComposite root;
        private object context = new object();
        private readonly List<string> trace = new List<string>();
        internal readonly List<object> Contexts = new List<object>();
        internal readonly int[] Hits = new int[10];
        internal int Arrivals;
        internal int ServiceTicks;
        internal int Sales => Hits[5];
        internal int Cleanups => Hits[9];
        internal string Trace => string.Join(",", trace);
        internal Func<int, RunStatus> Arrival = _ => RunStatus.Success;
        internal Func<int, RunStatus> Sale = _ => RunStatus.Success;

        internal Fixture()
        {
            // Skip constructor file loading. Building delegates performs no native work.
            object bot = RuntimeHelpers.GetUninitializedObject(typeof(GatherbuddyBot));
            MethodInfo method = typeof(GatherbuddyBot).GetMethod("CreateSellBehavior", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Actual GatherBuddy sale builder missing");
            root = method.Invoke(bot, null) as GroupComposite
                ?? throw new InvalidOperationException("Actual sale builder did not return a phase group");
            if (root.Children.Count != 2 || root.Children[0] is not TreeSharp.Action
                || root.Children[1] is not Sequence service || service.Children.Count != Hits.Length)
                throw new InvalidOperationException("Unexpected actual sale phase layout; do not guess native boundaries");
            root.Children[0] = new TreeSharp.Action(ctx =>
            {
                Contexts.Add(ctx); trace.Add("arrival"); Arrivals++;
                return Arrival(Arrivals);
            }) { Parent = root };
            // Retain the actual service Sequence, replacing each native leaf only.
            service.Children.Clear();
            for (int i = 0; i < Hits.Length; i++)
            {
                int index = i;
                service.AddChild(new TreeSharp.Action(ctx =>
                {
                    Contexts.Add(ctx); trace.Add(index.ToString()); ServiceTicks++; Hits[index]++;
                    return index == 5 ? Sale(Hits[index]) : RunStatus.Success;
                }));
            }
            root.Start(context);
        }
        internal RunStatus Tick() => root.Tick(context);
        internal void Stop() => root.Stop(context);
        internal void Start(object next) { root.Stop(context); context = next; root.Start(context); }
        public void Dispose() => root.Stop(context);
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid) throw new AssertionFailure(reason);
    }
}

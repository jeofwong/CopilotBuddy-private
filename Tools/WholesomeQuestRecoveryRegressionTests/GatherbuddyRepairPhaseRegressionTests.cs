using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Bots.Gatherbuddy;
using TreeSharp;

// Exercise the actual compiled repair builder and both real TreeSharp groups.
// Arrival, movement, gossip, repair and cleanup leaves are controlled. This does
// not attach to WoW or establish native transaction or full-root preemption.
internal static class GatherbuddyRepairPhaseRegressionTests
{
    private sealed class AssertionFailure : Exception
    {
        internal AssertionFailure(string message) : base(message) { }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
            throw new PlatformNotSupportedException("Repair phase tests require Windows x86.");
        var cases = new List<(string Name, System.Action Test)>
        {
            ("successful arrival reaches repair and cleanup in order", () =>
            {
                using var f = new Fixture();
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 1 && f.Repairs == 1 && f.Cleanups == 1,
                    "successful arrival skipped repair");
                Check(f.Trace == "arrival,0,1,2,3,4,5,6,7", "repair service order changed");
            }),
            ("failed arrival never admits repair", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Failure;
                Check(f.Tick() == RunStatus.Failure && f.ServiceTicks == 0, "failed arrival reached service");
            }),
            ("pending arrival does not touch repair leaves", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running && f.Tick() == RunStatus.Running
                    && f.Arrivals == 2 && f.ServiceTicks == 0, "pending arrival reached service");
            }),
            ("delayed arrival success advances to repair", () =>
            {
                using var f = new Fixture(); f.Arrival = n => n == 1 ? RunStatus.Running : RunStatus.Success;
                Check(f.Tick() == RunStatus.Running && f.ServiceTicks == 0, "approach did not stay pending");
                Check(f.Tick() == RunStatus.Success && f.Repairs == 1 && f.Cleanups == 1,
                    "completed approach skipped service");
            }),
            ("delayed arrival failure still denies service", () =>
            {
                using var f = new Fixture(); f.Arrival = n => n == 1 ? RunStatus.Running : RunStatus.Failure;
                Check(f.Tick() == RunStatus.Running, "approach did not stay pending");
                Check(f.Tick() == RunStatus.Failure && f.ServiceTicks == 0, "late arrival failure admitted repair");
            }),
            ("pending service retains the visit", () =>
            {
                using var f = new Fixture(); f.Repair = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running && f.Repairs == 1 && f.Cleanups == 0,
                    "pending service became a completed visit");
            }),
            ("resumed service does not repeat arrival or interaction", () =>
            {
                using var f = new Fixture(); f.Repair = n => n < 3 ? RunStatus.Running : RunStatus.Success;
                Check(f.Tick() == RunStatus.Running && f.Tick() == RunStatus.Running, "continuation was lost");
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 1 && f.Hits[2] == 1
                    && f.Repairs == 3 && f.Cleanups == 1, "continuation replayed setup or skipped cleanup");
            }),
            ("service failure prevents later effects", () =>
            {
                using var f = new Fixture(); f.Repair = _ => RunStatus.Failure;
                Check(f.Tick() == RunStatus.Failure && f.Repairs == 1 && f.Hits[6] == 0 && f.Cleanups == 0,
                    "service failure authorized later effects");
            }),
            ("delayed service failure does not restart approach", () =>
            {
                using var f = new Fixture(); f.Repair = n => n == 1 ? RunStatus.Running : RunStatus.Failure;
                Check(f.Tick() == RunStatus.Running && f.Tick() == RunStatus.Failure
                    && f.Arrivals == 1 && f.Repairs == 2 && f.Cleanups == 0, "failed continuation replayed setup");
            }),
            ("explicit stop during arrival prevents service", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running, "arrival did not run"); f.Stop();
                Check(f.Tick() == RunStatus.Failure && f.ServiceTicks == 0, "stopped arrival dispatched service");
            }),
            ("explicit stop during service prevents later leaves", () =>
            {
                using var f = new Fixture(); f.Repair = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running && f.Repairs == 1, "service did not run"); f.Stop();
                Check(f.Tick() == RunStatus.Failure && f.Repairs == 1 && f.Cleanups == 0,
                    "stopped service dispatched later effects");
            }),
            ("restart after stopped arrival creates a fresh visit", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Running;
                Check(f.Tick() == RunStatus.Running, "arrival did not run"); f.Stop();
                f.Arrival = _ => RunStatus.Success; f.Start(new object());
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 2 && f.Repairs == 1,
                    "fresh visit did not re-admit service");
            }),
            ("second completed visit repeats both phases", () =>
            {
                using var f = new Fixture(); Check(f.Tick() == RunStatus.Success, "first visit failed");
                f.Start(new object());
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 2 && f.Repairs == 2 && f.Cleanups == 2,
                    "completed visit replaced fresh work");
            }),
            ("all phases preserve the supplied context", () =>
            {
                using var f = new Fixture(); object context = new object(); f.Start(context);
                Check(f.Tick() == RunStatus.Success && f.Contexts.Count == 9, "both phases did not execute");
                foreach (object observed in f.Contexts) Check(ReferenceEquals(context, observed), "context changed");
            }),
            ("arrival cancellation retains the exact signal", () => Signal(false, false)),
            ("arrival interruption retains the exact signal", () => Signal(false, true)),
            ("service cancellation prevents cleanup", () => Signal(true, false)),
            ("service interruption prevents cleanup", () => Signal(true, true)),
            ("separate constructed visits have independent phase lifetimes", () =>
            {
                using var first = new Fixture(); using var second = new Fixture();
                first.Arrival = _ => RunStatus.Running;
                Check(first.Tick() == RunStatus.Running && second.Tick() == RunStatus.Success
                    && first.ServiceTicks == 0 && second.Repairs == 1, "one tree consumed another visit");
            }),
            ("new visit recovers after an arrival failure", () =>
            {
                using var f = new Fixture(); f.Arrival = _ => RunStatus.Failure;
                Check(f.Tick() == RunStatus.Failure && f.ServiceTicks == 0, "failed visit performed service");
                f.Arrival = _ => RunStatus.Success; f.Start(new object());
                Check(f.Tick() == RunStatus.Success && f.Arrivals == 2 && f.Repairs == 1,
                    "new visit could not recover from failed arrival");
            })
        };
        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS GatherBuddy repair phase: " + item.Name); }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL GatherBuddy repair phase: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR GatherBuddy repair phase: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"GatherBuddy repair-phase scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual builder and composites; controlled native leaves; no game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("GatherBuddy repair-phase regression");
    }

    private static void Signal(bool service, bool interruption)
    {
        using var f = new Fixture();
        Exception signal = interruption ? new ThreadInterruptedException("repair phase stop") : new OperationCanceledException("repair phase stop");
        if (service) f.Repair = _ => throw signal; else f.Arrival = _ => throw signal;
        Exception? observed = null;
        try { f.Tick(); } catch (Exception error) { observed = error; }
        Check(ReferenceEquals(signal, observed), "exact stop signal was lost or its phase was skipped");
        Check(f.Cleanups == 0 && (service ? f.Repairs == 1 : f.ServiceTicks == 0), "stop signal admitted later effects");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GroupComposite root;
        private object context = new object();
        private readonly List<string> trace = new List<string>();
        internal readonly List<object> Contexts = new List<object>();
        internal readonly int[] Hits = new int[8];
        internal int Arrivals;
        internal int ServiceTicks;
        internal int Repairs => Hits[5];
        internal int Cleanups => Hits[7];
        internal string Trace => string.Join(",", trace);
        internal Func<int, RunStatus> Arrival = _ => RunStatus.Success;
        internal Func<int, RunStatus> Repair = _ => RunStatus.Success;

        internal Fixture()
        {
            object bot = RuntimeHelpers.GetUninitializedObject(typeof(GatherbuddyBot));
            MethodInfo method = typeof(GatherbuddyBot).GetMethod("CreateRepairBehavior", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Actual repair builder missing");
            root = method.Invoke(bot, null) as GroupComposite
                ?? throw new InvalidOperationException("Actual repair builder did not return a phase group");
            if (root.Children.Count != 2 || root.Children[0] is not TreeSharp.Action
                || root.Children[1] is not Sequence service || service.Children.Count != Hits.Length)
                throw new InvalidOperationException("Unexpected repair phase layout; native boundaries must be reviewed");
            root.Children[0] = new TreeSharp.Action(ctx =>
            {
                Contexts.Add(ctx); trace.Add("arrival"); Arrivals++;
                return Arrival(Arrivals);
            }) { Parent = root };
            service.Children.Clear();
            for (int i = 0; i < Hits.Length; i++)
            {
                int index = i;
                service.AddChild(new TreeSharp.Action(ctx =>
                {
                    Contexts.Add(ctx); trace.Add(index.ToString()); ServiceTicks++; Hits[index]++;
                    return index == 5 ? Repair(Hits[index]) : RunStatus.Success;
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

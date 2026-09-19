using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using TreeSharp;

// Complete tracked PublishedQuestRoot, actual publication/outer gate/executor.
// Only the addon query result and terminal/support effects are controlled.
// Does not load or execute third-party addon Lua or attach to a game.
internal static class QuestAddonConflictRegressionTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class Failure(string text) : Exception(text) { }
    private static IReadOnlyList<string>? records;
    private static int calls;
    private static System.Action? onRead;

    [ModuleInitializer]
    internal static void Run() => ExecuteCases();

    private static IReadOnlyList<string> ReadAddon()
    {
        calls++;
        var action = onRead; onRead = null; action?.Invoke();
        return records!;
    }

    private static void ExecuteCases()
    {
        var cases = new List<(string, System.Action<Case>)>();
        void Add(string label, System.Action<Case> run) => cases.Add((label, run));
        foreach (string value in new[] { "absent", "inactive" })
        {
            string state = value;
            Add(state + " preserves running quest and probes once per tick", c =>
            { c.Observe(state); c.Step(); c.Step(); Check(c.Effects == 2 && c.Cleanups == 0 && Calls == 2, "valid quest or bounded observation changed"); });
        }
        Add("known active addon prevents initial quest effects and roaming", c =>
        { c.Observe("active"); c.Step(); Check(c.Effects == 0 && c.Roam == 0 && Calls == 1, "active addon allowed quest/roam or was not observed"); c.Protected(); });
        Add("activation preempts an already running quest before next effect", c =>
        { c.Start(); c.Observe("active"); c.Step(); Check(c.Effects == 1 && c.Cleanups == 1 && c.Roam == 0, "running quest continued after conflict"); });
        Add("repeated conflict does not repeat cleanup or advance the order", c =>
        { c.Start(); c.Observe("active"); for (int i = 0; i < 5; i++) c.Step(); Check(c.Effects == 1 && c.Cleanups == 1 && c.SameBehavior && c.Roam == 0, "conflict changed order or repeated cleanup"); });
        Add("explicit inactive observation resumes a fresh quest lifetime", c =>
        { c.Start(); c.Observe("active"); c.Step(); Check(c.Effects == 1, "active failed to suspend"); c.Observe("inactive"); c.Step(); Check(c.Effects == 2 && c.Starts == 2 && c.SameBehavior, "clear observation did not resume same work"); });
        Add("explicit addon absence releases an earlier conflict", c =>
        { c.Start(); c.Observe("active"); c.Step(); c.Observe("absent"); c.Step(); Check(c.Effects == 2 && c.Starts == 2, "absence failed to release conflict"); });
        foreach (string[]? values in new string[]?[] { null, Array.Empty<string>(), new[] { "unknown" }, new[] { "unexpected" }, new[] { "inactive", "active" } })
        {
            var record = values;
            Add("uncertain record " + (record == null ? "null" : string.Join("/", record)) + " cannot clear observed conflict", c =>
            { c.Start(); c.Observe("active"); c.Step(); c.Records(record); c.Step(); Check(c.Effects == 1 && c.Cleanups == 1 && c.Roam == 0, "uncertainty cleared known conflict"); });
        }
        Add("initial uncertainty preserves legacy admission without claiming addon absence", c =>
        { c.Records(null); c.Step(); Check(c.Effects == 1 && Calls == 1, "initial unknown changed compatibility or bypassed observation"); });
        Add("combat protection does not wait for the optional addon query", c =>
        { c.Observe("active"); c.EnableCombat(); c.Step(); Check(c.Combat == 1 && c.Effects == 0 && Calls == 0, "addon query delayed protective combat"); });
        Add("combat still preempts a quest while a conflict is retained", c =>
        { c.Start(); c.Observe("active"); c.Step(); c.EnableCombat(); c.Step(); Check(c.Combat == 1 && c.Effects == 1 && c.Cleanups == 1 && c.Roam == 0, "retained conflict blocked combat"); });
        Add("conflicted exclusive quest cannot suppress a service request", c =>
        { c.Exclusive(); c.Start(); c.Observe("active"); c.EnableService(); c.Step(); Check(c.Service == 1 && c.Effects == 1 && c.Cleanups == 1, "conflicted quest retained service exclusivity"); });
        Add("inactive addon cannot revive stale quest publication", c =>
        { c.Start(); c.RawProgress(); c.Observe("inactive"); c.Step(); Check(c.Effects == 1 && c.Cleanups == 1 && c.Roam == 0, "addon clearance overrode stale publication"); });
        Add("stopped outer lifecycle cannot query addons or run effects", c =>
        { c.StopBot(); c.Observe("active"); c.Step(); Check(Calls == 0 && c.Effects == 0 && c.Combat == 0 && c.Roam == 0, "stopped root inspected addon or acted"); });
        Add("known conflict preserves scheduled quest-item protection", c =>
        { c.Observe("active"); c.Step(); c.Protected(); Check(c.Effects == 0 && c.SameBehavior, "conflict released or completed quest work"); });
        Add("ordinary query failure cannot clear a retained conflict", c =>
        { c.Start(); c.Observe("active"); c.Step(); c.OnRead(() => throw new InvalidOperationException("controlled query failure")); c.Step(); Check(c.Effects == 1 && c.Cleanups == 1, "failed query released conflict"); });
        foreach (Exception value in new Exception[] { new OperationCanceledException("probe cancelled"), new System.Threading.ThreadInterruptedException("probe interrupted") })
        {
            Exception signal = value;
            Add(signal.GetType().Name + " from observation retains its identity", c =>
            { c.Start(); c.OnRead(() => throw signal); Exception? seen = null; try { c.Step(); } catch (Exception e) { seen = e; } Check(ReferenceEquals(seen, signal) && c.Effects == 1 && c.Cleanups == 1 && c.Roam == 0, "stop signal swallowed or effects continued"); });
        }
        Add("outer stop during addon observation prevents same-tick work", c =>
        { c.Observe("inactive"); c.OnRead(c.StopBot); c.Step(); Check(Calls == 1 && c.Effects == 0 && c.Roam == 0, "query callback bypassed outer lifetime"); });
        Add("each root owns its conflict state independently", c =>
        { c.Observe("active"); c.Step(); c.ReplaceInner(); c.Observe("inactive"); c.Step(); Check(c.Effects == 1 && c.Roam == 0, "new root inherited another instance's conflict"); });
        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { using var c = new Case(); item.Item2(c); passed++; Console.WriteLine("PASS quest addon conflict: " + item.Item1); }
            catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL quest addon conflict assertion: " + item.Item1 + ": " + e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR quest addon conflict fixture/owner: " + item.Item1 + ": " + e); }
        }
        Console.WriteLine($"Quest addon conflict scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete PublishedQuestRoot and actual publisher/outer gate/executor; controlled addon-query results; no game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Quest addon conflict regressions");
    }

    private sealed class Case : IDisposable
    {
        private readonly object inner;
        private readonly object originalBehavior;
        private readonly GroupComposite outer;
        internal Case()
        {
            records = new[] { "inactive" }; calls = 0; onRead = null;
            inner = Activator.CreateInstance(typeof(QuestRootPreemptionRegressionTests).GetNestedType("Case", Flags)!, true)!;
            originalBehavior = Get(inner, "Behavior"); outer = (GroupComposite)Get(inner, "Root");
            ReplaceInner();
        }
        internal void ReplaceInner()
        {
            outer.Stop(Get(inner, "Context"));
            var old = All(outer).Single(x => x.GetType().FullName == "Bots.Quest.PublishedQuestRoot");
            old.Stop(Get(inner, "Context"));
            var parent = (GroupComposite)old.Parent;
            var permission = (Func<Func<bool>>)Get(old, "capturePermission");
            // The pre-feature root has no observation seam. Its ordinary path is
            // still executed, so unsafe continuation is an assertion failure,
            // not a fixture or missing-method error. Post-feature uses the same
            // actual host root with only the external addon read substituted.
            var type = typeof(Bots.Quest.PublishedQuestRoot);
            var constructor = type.GetConstructor(new[] { typeof(Func<Func<bool>>), typeof(Func<IReadOnlyList<string>>) });
            var replacement = constructor == null
                ? new Bots.Quest.PublishedQuestRoot(permission)
                : (Composite)constructor.Invoke(new object[] { permission, (Func<IReadOnlyList<string>>)ReadAddon });
            int index = parent.Children.IndexOf(old);
            parent.Children[index] = replacement; replacement.Parent = parent;
            Call(inner, "Configure", outer, false);
        }
        internal void Observe(string value) => Records(new[] { value });
        internal void Records(string[]? values) => records = values;
        internal void OnRead(System.Action action) => onRead = action;
        internal void Start() { Observe("inactive"); Call(inner, "StartQuest"); }
        internal void Step() => Call(inner, "Step");
        internal void EnableCombat() => Call(inner, "EnableCombat");
        internal void EnableService() => Call(inner, "EnableService");
        internal void Protected() => Call(inner, "Protected");
        internal void RawProgress() => Call(inner, "RawProgress", 1u);
        internal void Exclusive() => originalBehavior.GetType().GetField("Exclusive", Flags)!.SetValue(originalBehavior, true);
        internal void StopBot() => Get(inner, "Bot").GetType().GetField("_stopped", Flags)!.SetValue(Get(inner, "Bot"), true);
        internal int Effects => (int)Get(Get(originalBehavior, "Body"), "Effects");
        internal int Starts => (int)Get(Get(originalBehavior, "Body"), "Starts");
        internal int Cleanups => (int)Get(Get(originalBehavior, "Body"), "Cleanups");
        internal int Combat => (int)Get(Get(inner, "Combat"), "Effects");
        internal int Service => (int)Get(Get(inner, "Service"), "Effects");
        internal int Roam => (int)Get(Get(inner, "Roam"), "Effects");
        internal bool SameBehavior => ReferenceEquals(Bots.Quest.QuestState.Instance.Order.CurrentBehavior, originalBehavior);
        public void Dispose() { onRead = null; ((IDisposable)inner).Dispose(); }
    }
    private static int Calls => calls;
    private static IEnumerable<Composite> All(Composite root)
    { yield return root; if (root is GroupComposite group) foreach (var child in group.Children) if (child != null) foreach (var c in All(child)) yield return c; }
    private static object Get(object owner, string name) => owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static object? Call(object owner, string name, params object?[] args)
    {
        try { return owner.GetType().GetMethod(name, Flags)!.Invoke(owner, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Failure(message); }
}

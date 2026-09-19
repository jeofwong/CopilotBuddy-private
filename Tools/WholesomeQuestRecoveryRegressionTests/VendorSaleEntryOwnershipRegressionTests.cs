using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Styx.Logic;
using Styx.Logic.Profiles;
using Styx.WoWInternals;

// Exercise the actual SellAllItemsStep -> StartSellSession admission wrapper.
// The existing Windows fixture supplies empty bags and restores static owners.
// All profiles disable sale qualities: positive terminal controls reach the real
// None-quality no-op, never a merchant request, Lua dispatch or attached game.
internal static class VendorSaleEntryOwnershipRegressionTests
{
    private const uint ReplacementItem = 190010291;
    private sealed class AssertionFailure : Exception { internal AssertionFailure(string text) : base(text) { } }

    [ModuleInitializer]
    internal static void Run()
    {
        var cases = new List<(string Name, Action<Fixture> Test)>
        {
            ("ordinary empty pass completes without a merchant request", f =>
            {
                int calls = 0; Vendors.OnVendorItems = _ => calls++;
                Check(f.Step() && calls == 1 && !f.Active && !Vendors.ForceSell, "normal no-quality pass changed");
            }),
            ("missing profile at entry retains the existing terminal no-op", f =>
            {
                int calls = 0; f.Activate(null); Vendors.OnVendorItems = _ => calls++;
                Check(f.Step() && calls == 0 && !f.Active && Vendors.ForceSell, "missing-profile compatibility changed");
            }),
            ("already-active empty pass does not repeat admission callbacks", f =>
            {
                int calls = 0; Vendors.OnVendorItems = _ => calls++;
                bool started = f.Start();
                Check(started && f.Active, "active control was not established");
                Check(f.Step() && calls == 1 && !f.Active, "active session was admitted twice");
            }),
            ("profile replacement during admission is not service completion", f => ChangedProfile(f, Fixture.QuietProfile())),
            ("profile removal during admission is not service completion", f => ChangedProfile(f, null)),
            ("reset during admission is not service completion", f =>
            {
                int first = 0, later = 0;
                Vendors.OnVendorItems = _ => { first++; f.Reset(); };
                Vendors.OnVendorItems += _ => later++;
                bool done = f.Step();
                Check(!done && first == 1 && later == 0 && !f.Active && Vendors.ForceSell,
                    "revoked entry authorized the vendor caller to finish");
            }),
            ("earlier successful callback cannot authorize a replaced profile", f =>
            {
                int first = 0, changing = 0, later = 0;
                Vendors.OnVendorItems = args => { first++; args.IdExceptions.Add(ReplacementItem); };
                Vendors.OnVendorItems += _ => { changing++; f.Activate(Fixture.QuietProfile()); };
                Vendors.OnVendorItems += _ => later++;
                Check(!f.Step() && first == 1 && changing == 1 && later == 0 && !f.Active && Vendors.ForceSell,
                    "partial admission became a completed sale pass");
            }),
            ("ordinary failed callback before replacement does not fabricate completion", f =>
            {
                int failed = 0, changing = 0, later = 0;
                Vendors.OnVendorItems = _ => { failed++; throw new InvalidOperationException("ordinary admission failure"); };
                Vendors.OnVendorItems += _ => { changing++; f.Activate(Fixture.QuietProfile()); };
                Vendors.OnVendorItems += _ => later++;
                Check(!f.Step() && failed == 1 && changing == 1 && later == 0 && !f.Active && Vendors.ForceSell,
                    "failed obsolete entry became terminal success");
            }),
            ("twenty revoked entries stay pending and a later clean entry can finish", f =>
            {
                int calls = 0; Vendors.OnVendorItems = _ => { calls++; f.Activate(Fixture.QuietProfile()); };
                bool anyDone = false;
                for (int i = 0; i < 20; i++) anyDone |= f.Step();
                Check(!anyDone && calls == 20 && !f.Active && Vendors.ForceSell, "revoked entries reported completion");
                Vendors.OnVendorItems = null;
                Check(f.Step() && !f.Active && !Vendors.ForceSell, "revocation created a permanent admission block");
            }),
            ("profile disappearance is pending now but a later missing-profile entry remains terminal", f =>
            {
                Vendors.OnVendorItems = _ => f.Activate(null);
                Check(!f.Step() && Vendors.ForceSell, "in-flight removal reported completion");
                Check(f.Step() && !f.Active && Vendors.ForceSell, "later missing-profile behavior changed");
            }),
            ("reset entry can be followed by a fresh clean empty pass", f =>
            {
                Vendors.OnVendorItems = _ => f.Reset();
                Check(!f.Step() && !f.Active, "reset admission reported success");
                Vendors.OnVendorItems = null;
                Check(f.Step() && !f.Active && !Vendors.ForceSell, "reset stranded later work");
            }),
            ("same-profile nested session cannot be completed by its obsolete caller", f => Replacement(f, false, false, null, false)),
            ("new-profile nested session cannot be completed by its obsolete caller", f => Replacement(f, true, false, null, false)),
            ("reset followed by nested admission retains the replacement session", f => Replacement(f, false, true, null, false)),
            ("ordinary error after nested admission preserves its session and pending result", f => Replacement(f, false, false, new InvalidOperationException("old callback failed"), false)),
            ("entry cancellation preserves the exact signal and pending work", f => Signal(f, new OperationCanceledException("entry cancelled"))),
            ("entry interruption preserves the exact signal and pending work", f => Signal(f, new ThreadInterruptedException("entry interrupted"))),
            ("cancellation after nested admission cannot discard its replacement", f => Replacement(f, false, false, new OperationCanceledException("old callback cancelled"), false)),
            ("interruption after nested admission cannot discard its replacement", f => Replacement(f, false, false, new ThreadInterruptedException("old callback interrupted"), false)),
            ("replacement completes only on its next owned entry without another admission", f => Replacement(f, true, true, null, true)),
        };
        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try
            {
                using var fixture = new Fixture(); item.Test(fixture); fixture.CheckOffline();
                passed++; Console.WriteLine("PASS vendor sale entry: " + item.Name);
            }
            catch (AssertionFailure error) { assertions++; Console.Error.WriteLine("FAIL vendor sale entry assertion: " + item.Name + ": " + error.Message); }
            catch (Exception error) { unexpected++; Console.Error.WriteLine("ERROR vendor sale entry fixture/owner: " + item.Name + ": " + error); }
        }
        Console.WriteLine($"Vendor sale-entry ownership scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; actual host entry/admission/session owners; empty no-quality controls; no merchant request or game attached.");
        if (assertions + unexpected != 0) throw new InvalidOperationException($"Vendor sale-entry regressions: assertions={assertions}; unexpected={unexpected}");
    }

    private static void ChangedProfile(Fixture f, Profile? profile)
    {
        int first = 0, later = 0;
        Vendors.OnVendorItems = _ => { first++; f.Activate(profile); };
        Vendors.OnVendorItems += _ => later++;
        Check(!f.Step() && first == 1 && later == 0 && !f.Active && Vendors.ForceSell,
            "obsolete entry was reported complete after profile replacement/removal");
    }

    private static void Signal(Fixture f, Exception signal)
    {
        int first = 0, later = 0;
        Vendors.OnVendorItems = _ => { first++; throw signal; };
        Vendors.OnVendorItems += _ => later++;
        Exception? caught = null; try { f.Step(); } catch (Exception error) { caught = error; }
        Check(ReferenceEquals(signal, caught) && first == 1 && later == 0 && !f.Active && Vendors.ForceSell,
            "entry replaced the stop signal or claimed completed work");
    }

    private static void Replacement(Fixture f, bool changeProfile, bool reset, Exception? errorAfter, bool resume)
    {
        int old = 0, fresh = 0, later = 0;
        bool started = false; object? token = null; List<uint>? ids = null;
        Vendors.OnVendorItems = _ =>
        {
            old++;
            if (reset) f.Reset();
            if (changeProfile) f.Activate(Fixture.QuietProfile());
            Vendors.OnVendorItems = args => { fresh++; args.IdExceptions.Add(ReplacementItem); };
            started = f.Start(); token = f.Token; ids = f.Ids;
            f.Count = 29;
            if (errorAfter != null) throw errorAfter;
        };
        Vendors.OnVendorItems += _ => later++;
        Exception? caught = null; bool done = false;
        try { done = f.Step(); } catch (Exception error) { caught = error; }
        Check(started && old == 1 && fresh == 1 && later == 0 && token != null && ids != null,
            "nested admission control was not reached exactly once");
        Check(f.Active && ReferenceEquals(token, f.Token) && ReferenceEquals(ids, f.Ids)
            && f.Ids.Contains(ReplacementItem) && f.Count == 29 && Vendors.ForceSell,
            "obsolete entry consumed or changed replacement session state");
        bool stopping = errorAfter is OperationCanceledException || errorAfter is ThreadInterruptedException;
        Check(stopping ? ReferenceEquals(errorAfter, caught) : caught == null && !done,
            "obsolete entry reported completion or changed exception identity");
        if (resume)
            Check(f.Step() && fresh == 1 && !f.Active && !Vendors.ForceSell,
                "later owned entry could not complete the replacement independently");
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly object actual;
        internal Fixture()
        {
            actual = Activator.CreateInstance(typeof(VendorSessionBoundaryRegressionTests).GetNestedType("Fixture", BindingFlags.NonPublic)!, true)!;
            try { Activate(QuietProfile()); CheckOffline(); }
            catch { Dispose(); throw; }
        }
        internal static Profile QuietProfile()
        {
            var profile = new Profile(System.Xml.Linq.XElement.Parse(
                "<HBProfile><SellGrey>false</SellGrey><SellWhite>false</SellWhite>" +
                "<SellGreen>false</SellGreen><SellBlue>false</SellBlue><SellPurple>false</SellPurple></HBProfile>"), null);
            if (profile.SellGrey || profile.SellWhite || profile.SellGreen || profile.SellBlue || profile.SellPurple)
                throw new InvalidOperationException("Fixture requires a parsed profile with no sale qualities.");
            return profile;
        }
        internal bool Step() => (bool)Invoke(typeof(Vendors).GetMethod("SellAllItemsStep", Hidden)!, null, null)!;
        internal bool Start() => (bool)Invoke(actual.GetType().GetMethod("Start", All)!, actual, null)!;
        internal void Reset() => Invoke(typeof(Vendors).GetMethod("ResetSellSession", Hidden)!, null, null);
        internal void Activate(Profile? profile) => Invoke(actual.GetType().GetMethod("Activate", All)!, actual, new object?[] { profile });
        internal bool Active => (bool)Get("_sellSessionActive")!;
        internal object? Token => Get("_sellSessionCandidate");
        internal List<uint> Ids => (List<uint>)Get("_sellSessionProtectedIds")!;
        internal int Count
        {
            get => (int)Get("_sellSessionStackCount")!;
            set => typeof(Vendors).GetField("_sellSessionStackCount", Hidden)!.SetValue(null, value);
        }
        internal void CheckOffline()
        {
            if (ObjectManager.Executor != null) throw new InvalidOperationException("Fixture must not attach a native executor.");
        }
        private static object? Get(string name) => typeof(Vendors).GetField(name, Hidden)!.GetValue(null);
        public void Dispose() => ((IDisposable)actual).Dispose();
    }
    private static object? Invoke(MethodInfo method, object? target, object?[]? args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Check(bool condition, string text) { if (!condition) throw new AssertionFailure(text); }
}

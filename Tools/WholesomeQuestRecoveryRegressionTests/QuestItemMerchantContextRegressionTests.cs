using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// Execute the complete tracked UseItemOn tree and actual TreeSharp, instrumenting
// only observations/callbacks. No native item use, sale or server credit is simulated.
// A visible merchant must not turn a quest-use decision into generic container use.
internal static class QuestItemMerchantContextRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CopilotBuddy.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new InvalidOperationException("Tracked checkout required");
        string boundary = (string)typeof(QuestItemTargetSelectionRegressionTests).GetField("Boundary", flags)!.GetRawConstantValue()!;
        void Replace(string before, string after)
        {
            if (boundary.Split(new[] { before }, StringSplitOptions.None).Length != 2)
                throw new InvalidOperationException("External fixture shape changed: " + before);
            boundary = boundary.Replace(before, after);
        }
        Replace("public static void SleepForLagDuration(){}", "public static void SleepForLagDuration(){QuestItemMerchantCases.Boundary(\"lag\");}");
        Replace("public bool TryUseContainerItem()=>true; public void UseContainerItem(){TryUseContainerItem();}", "public bool TryUseContainerItem(){QuestItemMerchantCases.Submit(this);return true;}public void UseContainerItem(){TryUseContainerItem();}");
        Replace("public void Target(){ObjectManager.Me!.CurrentTarget=this;}", "public void Target(){ObjectManager.Me!.CurrentTarget=this;QuestItemMerchantCases.Boundary(\"target\");}");
        Replace("public void ClearTarget(){CurrentTarget=null;}", "public void ClearTarget(){QuestItemMerchantCases.Clears++;CurrentTarget=null;}");
        Replace("public static void MoveStop(){}", "public static void MoveStop(){if(ObjectManager.Me!=null)ObjectManager.Me.IsMoving=false;QuestItemMerchantCases.Boundary(\"stop\");}");
        Replace("public static void Face(ulong id){}", "public static void Face(ulong id){QuestItemMerchantCases.Boundary(\"face\");}");
        Replace("public static string StatusText{get;set;}=\"\";", "private static string status=\"\";public static string StatusText{get=>status;set{status=value;if(value.StartsWith(\"Using item\"))QuestItemMerchantCases.Boundary(\"status\");}}");
        Replace("public static List<WoWObject>? Objects{get;set;}=new();", "public static List<WoWObject>? Objects{get;set;}=new();public static T? GetObjectByGuid<T>(ulong id)where T:WoWObject=>Objects?.OfType<T>().FirstOrDefault(o=>o.Guid==id);");
        string temp = Path.Combine(Path.GetTempPath(), "cb-item-merchant-context-" + Guid.NewGuid().ToString("N"));
        bool oldLogging = Styx.Helpers.Logging.FileLogging;
        Directory.CreateDirectory(temp);
        try
        {
            Styx.Helpers.Logging.FileLogging = false;
            File.Copy(Path.Combine(root, "runtime-snapshot", "Quest Behaviors", "UseItemOn.cs"), Path.Combine(temp, "UseItemOn.cs"));
            File.WriteAllText(Path.Combine(temp, "Boundary.cs"), boundary + Cases);
            Type type = typeof(Styx.StyxWoW).Assembly.GetType("Styx.Loaders.SourceCompiler", true)!;
            object compiler = Activator.CreateInstance(type, new object[] { temp })!;
            foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
                type.GetMethod("AddReference", flags)!.Invoke(compiler, new object[] { path });
            var result = (CompilerResults)type.GetMethod("Compile", flags)!.Invoke(compiler, null)!;
            var errors = result.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Actual item-use compile failed: " + string.Join(";", errors.Select(e => e.ToString())));
            var assembly = (Assembly)type.GetProperty("CompiledAssembly", flags)!.GetValue(compiler)!;
            try { assembly.GetType("QuestItemMerchantCases", true)!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        finally { Styx.Helpers.Logging.FileLogging = oldLogging; Directory.Delete(temp, true); }
    }
    private const string Cases = """

public static class QuestItemMerchantCases
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Failure(string message) : Exception(message) { }
    private static Script owner = null!;
    private static LocalPlayer player = null!;
    private static WoWUnit target = null!;
    private static System.Action? mutation;
    private static string stage = "";
    private static bool fired;
    private static readonly List<ulong> submissions = new();
    public static int Clears;
    private static bool MerchantVisible
    {
        get => Styx.Logic.Inventory.Frames.Merchant.MerchantFrame.Instance.IsVisible;
        set => Styx.Logic.Inventory.Frames.Merchant.MerchantFrame.Instance.IsVisible = value;
    }
    public static void Boundary(string name)
    {
        if (!fired && name == stage)
        {
            fired = true;
            var callback = mutation;
            mutation = null;
            callback?.Invoke();
        }
    }
    public static void Submit(WoWItem item)
    {
        // Record the generic API request only. Do not call a fake sale a native result.
        submissions.Add(item.Guid);
        Boundary("use");
    }
    public static void Run()
    {
        var cases = new List<(string Name, System.Action Test)>();
        foreach (bool dead in new[] { false, true })
        {
            bool corpse = dead;
            string prefix = corpse ? "explicit corpse recipe: " : "living NPC recipe: ";
            void Add(string name, System.Action test) => cases.Add((prefix + name, () => { Reset(corpse); test(); }));
            Add("already-open merchant prevents a generic container request", () =>
            {
                MerchantVisible = true;
                Tick();
                ExpectNoRequest();
            });
            foreach (string point in new[] { "stop", "lag", "status", "target", "face" })
            {
                string boundary = point;
                Add("merchant opens during " + boundary, () =>
                {
                    stage = boundary;
                    mutation = () => MerchantVisible = true;
                    Tick();
                    Check(fired, "the intended controlled callback was not reached");
                    ExpectNoRequest();
                });
            }
            Add("closed merchant preserves the explicit item-use path", () => { Tick(); ExpectOneRequest(); });
            Add("a previously closed merchant is not a permanent blacklist", () =>
            {
                MerchantVisible = true;
                MerchantVisible = false;
                Tick();
                ExpectOneRequest();
            });
            Add("merchant opened after submission cannot invent a second request", () =>
            {
                stage = "use";
                mutation = () => MerchantVisible = true;
                Tick();
                Check(fired, "submission callback was not reached");
                Check(submissions.Count == 1, "a completed invocation was lost or duplicated");
                Tick();
                Check(submissions.Count == 1, "a second request followed the merchant opening");
            });
        }
        cases.Add(("deferred use can resume after the merchant closes", () =>
        {
            Reset(false);
            stage = "status";
            mutation = () => MerchantVisible = true;
            Tick();
            Check(fired, "the requested boundary was not reached");
            ExpectNoRequest();
            MerchantVisible = false;
            stage = "";
            mutation = null;
            fired = false;
            Tick();
            ExpectOneRequest();
        }));
        cases.Add(("completed explicit repetition does not repeat while closed", () =>
        {
            Reset(false);
            Tick();
            Tick();
            ExpectOneRequest();
        }));
        int passed = 0, assertions = 0, unexpected = 0;
        foreach (var item in cases)
        {
            try { item.Test(); passed++; Console.WriteLine("PASS quest item merchant: " + item.Name); }
            catch (Failure e) { assertions++; Console.Error.WriteLine("FAIL quest item merchant assertion: " + item.Name + ": " + e.Message); }
            catch (Exception e) { unexpected++; Console.Error.WriteLine("ERROR quest item merchant fixture: " + item.Name + ": " + e); }
        }
        Console.WriteLine($"Quest item merchant scenarios: {passed}/{cases.Count}; assertions={assertions}; unexpected={unexpected}; complete UseItemOn and real TreeSharp; controlled merchant/setup/API; no native sale or quest credit.");
        if (assertions + unexpected != 0) throw new InvalidOperationException("Quest item merchant regressions");
    }
    private static void Reset(bool corpse)
    {
        typeof(QuestItemSelectionCases).GetMethod("Reset", Hidden)!.Invoke(null, null);
        owner = (Script)typeof(QuestItemSelectionCases).GetField("owner", Hidden)!.GetValue(null)!;
        target = (WoWUnit)typeof(QuestItemSelectionCases).GetField("candidate", Hidden)!.GetValue(null)!;
        player = ObjectManager.Me!;
        player.IsMoving = true;
        player.CarriedItems!.Add(new WoWItem { Guid = 17, Entry = 12345 });
        typeof(Script).GetField("_isDisposed", Hidden)!.SetValue(owner, false);
        GC.SuppressFinalize(owner);
        if (corpse) { target.IsAlive = false; Set("NpcState", Script.NpcStateType.Dead); }
        Set("WaitTime", 101);
        stage = "";
        mutation = null;
        fired = false;
        MerchantVisible = false;
        Clears = 0;
        submissions.Clear();
    }
    private static int Counter => (int)typeof(Script).GetProperty("Counter", Hidden)!.GetValue(owner)!;
    private static List<ulong> Blacklist => (List<ulong>)typeof(Script).GetField("_npcBlacklist", Hidden)!.GetValue(owner)!;
    private static void Set(string property, object value) => typeof(Script).GetProperty(property, Hidden)!.SetValue(owner, value);
    private static void ExpectNoRequest() => Check(submissions.Count == 0 && Counter == 0 && Blacklist.Count == 0,
        $"merchant-visible attempt made {submissions.Count} generic request(s), count={Counter}, blacklist={Blacklist.Count}");
    private static void ExpectOneRequest() => Check(submissions.SequenceEqual(new[] { 17UL }) && Counter == 1,
        $"closed-merchant control made {submissions.Count} request(s), count={Counter}");
    private static void Tick()
    {
        var errors = new List<string>();
        void Observe(Styx.Helpers.LogLevel level, string message)
        {
            if (message.Contains("Exception") || message.Contains("Object reference not set")) errors.Add(message);
        }
        Styx.Helpers.Logging.OnMessageLogged += Observe;
        try
        {
            Composite root = (Composite)typeof(Script).GetMethod("CreateBehavior", Hidden)!.Invoke(owner, null)!;
            root.Start(null!);
            try
            {
                int ticks = 0;
                while (root.Tick(null!) == RunStatus.Running)
                    if (++ticks > 20) throw new Failure("unexpected nonterminal item-use decision");
            }
            finally { root.Stop(null!); }
            Check(errors.Count == 0, "swallowed owner error: " + string.Join(";", errors));
        }
        finally { Styx.Helpers.Logging.OnMessageLogged -= Observe; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Failure(message); }
}
// Only the merchant observation is controlled. This does not replace UseItemOn,
// TreeSharp, the host compiler or any confirmed effect/acknowledgement machinery.
/* Embedded fixture namespace, not the initializer scope. */ namespace Styx.Logic.Inventory.Frames.Merchant
{
    public sealed class MerchantFrame
    {
        public static MerchantFrame Instance { get; } = new();
        public bool IsVisible { get; set; }
    }
}
""";
}

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Bots.Quest.Actions;
using Bots.Quest.QuestOrder;
using CommonBehaviors.Actions;
using Styx.Logic.POI;
using Styx.Logic.Profiles.Quest;
using Styx.WoWInternals;
using TreeSharp;

#nullable disable
namespace Bots.Quest;

/// <summary>
/// An instance-owned QuestBot composition. Publication permission applies only to
/// quest execution; protective work keeps its normal priority. A continuing quest
/// is stopped before, not after, a newly eligible protective branch can act.
/// </summary>
public sealed class PublishedQuestRoot : PrioritySelector
{
    private readonly Bots.Quest.QuestOrder.QuestOrder order;
    private readonly Func<Func<bool>> capturePermission;
    private readonly Func<IReadOnlyList<string>> readAddon;
    private bool addonConflict;
    private Func<bool> permission;
    private Func<bool> runningPermission;
    private OrderNodeCollection nodes, runningNodes;
    private OrderNode node, runningNode;
    private ForcedBehavior behavior, runningBehavior;
    private bool exclusive, cycleChanged;
    private Composite lifecycleOwner;
    private object executionContext;

    public PublishedQuestRoot(Func<Func<bool>> capturePermission)
        : this(capturePermission, ReadQuestAutomation)
    {
    }

    // The optional observer is a read-only external boundary. Each root retains
    // its own confirmed conflict; no addon settings or shared bot state are set.
    public PublishedQuestRoot(Func<Func<bool>> capturePermission, Func<IReadOnlyList<string>> readAddon)
    {
        this.capturePermission = capturePermission ?? throw new ArgumentNullException(nameof(capturePermission));
        this.readAddon = readAddon ?? throw new ArgumentNullException(nameof(readAddon));
        order = QuestState.Instance.Order;
        // CreateRoot builds a fresh composition; do not borrow QuestBot.Root's
        // shared selector or mutate another bot's executor/admission policy.
        Children = ((PrioritySelector)QuestBot.CreateRoot()).Children;
        var service = (Decorator)Children[4];
        Children[4] = new Decorator(_ => !exclusive, service.DecoratedChild);
        Children[5] = new PrioritySelector(
            new ForcedBehaviorExecutor(order, CanExecuteQuest),
            new ActionAlwaysSucceed());
        foreach (var child in Children)
            if (child != null) child.Parent = this;
    }

    public override void Start(object context)
    {
        // Some callers restart explicitly; drain that lifetime as well as the
        // ordinary driver path which starts only after a terminal result.
        Stop(context);
        base.Start(context);
    }

    public override RunStatus Tick(object context)
    {
        if (LastStatus.HasValue && LastStatus != RunStatus.Running)
            return LastStatus.Value;
        try
        {
            lifecycleOwner = Parent;
            executionContext = context;
            if (!CanRunRoot())
                return StopDeniedRoot(context);
            permission = capturePermission();
            nodes = order.Nodes;
            node = order.CurrentNode;
            behavior = order.CurrentBehavior;
            cycleChanged = false;
            bool allowed = HasCurrentPublication();
            // Do not add an optional Lua round trip ahead of death/combat.
            // Query once per eligible root tick, never from each executor gate.
            if (allowed && ProtectivePriority() > 1)
                ObserveAddonConflict();
            allowed = CanExecuteQuest();
            exclusive = ObserveExclusiveOwner(allowed);
            allowed = CanExecuteQuest();

            if (ObjectManager.Me == null || !CanRunRoot())
                return StopDeniedRoot(context);

            int selected = Selection == null ? -1 : Children.IndexOf(Selection);
            bool questSelected = selected == 5;
            bool changedRunningOwner = questSelected &&
                (!ReferenceEquals(runningPermission, permission) ||
                 !ReferenceEquals(runningNodes, nodes) ||
                 !ReferenceEquals(runningNode, node) ||
                 !ReferenceEquals(runningBehavior, behavior));
            bool preempt = LastStatus == RunStatus.Running &&
                ((questSelected && (!allowed || changedRunningOwner)) ||
                 (selected >= 0 && ProtectivePriority() < selected));
            if (preempt)
            {
                // Cleanup is an effect boundary. Do not continue to a support
                // leaf if it fails, or adopt a publication created by cleanup.
                Stop(context);
                exclusive = exclusive && CanExecuteQuest();
                base.Start(context);
            }

            // Observation and preemption cleanup may end the outer lifecycle.
            // Quest-refresh cancellation alone is not this whole-root permission.
            if (!CanRunRoot())
                return StopDeniedRoot(context);
            var status = base.Tick(context);
            if (status == RunStatus.Running && ReferenceEquals(Selection, Children[5]))
            {
                runningPermission = permission;
                runningNodes = order.Nodes;
                runningNode = order.CurrentNode;
                runningBehavior = order.CurrentBehavior;
            }
            return status;
        }
        catch (Exception error)
        {
            // An ordinary observation failure must not mask a cleanup stop signal.
            // Reuse the first-stop-signal rule also used by Composite/GroupComposite.
            ExceptionDispatchInfo failure = ExceptionDispatchInfo.Capture(error);
            try { Stop(context); }
            catch (Exception cleanupError) { PreserveCleanupFailure(ref failure, cleanupError); }
            failure.Throw();
            throw;
        }
    }

    private bool CanRunRoot()
    {
        if (!ReferenceEquals(Parent, lifecycleOwner)) return false;
        // The outer decorator owns lifecycle/rest admission. Reuse that exact
        // instance's predicate instead of duplicating its policy or reading the
        // quest-publication lease as permission to run combat/services.
        var gate = lifecycleOwner as Decorator;
        return (gate == null || gate.IsExecutionAllowedFor(this, executionContext))
            && ReferenceEquals(Parent, lifecycleOwner);
    }

    private RunStatus StopDeniedRoot(object context)
    {
        Stop(context);
        LastStatus = RunStatus.Failure;
        return RunStatus.Failure;
    }

    private bool SameCycleOrder() => ReferenceEquals(order.Nodes, nodes)
        && ReferenceEquals(order.CurrentNode, node)
        && (behavior == null || ReferenceEquals(order.CurrentBehavior, behavior));

    private bool CanExecuteQuest() => !addonConflict && HasCurrentPublication();

    private bool HasCurrentPublication()
    {
        if (cycleChanged || !CanRunRoot() || !SameCycleOrder())
            return false;
        bool current = permission != null && permission();
        // Validation is itself a callback boundary. Any observed replacement
        // invalidates this cycle, even when the new owner is independently valid.
        if (!SameCycleOrder()) cycleChanged = true;
        return current && !cycleChanged && CanRunRoot();
    }

    // Contract of the reviewed TurnIn 2.1 upload: its runtime version string is
    // "2.0". Metadata/version alone is not a server-core or addon authenticity
    // check. Unknown layouts remain unknown; no addon code or callbacks run.
    private const string QuestAutomationQuery =
        "local v=rawget(_G,'TI_VersionString'); local f=rawget(_G,'TurnIn'); " +
        "local t=rawget(_G,'TI_status'); " +
        "if v==nil and f==nil and t==nil then return 'absent' end; " +
        "if v~='2.0' or f==nil or type(t)~='table' then return 'unknown' end; " +
        "local s=rawget(t,'state'); if s then return 'active' end; " +
        "if s==false then return 'inactive' end; return 'unknown'";

    private static IReadOnlyList<string> ReadQuestAutomation()
        => Lua.GetReturnValues(QuestAutomationQuery);

    private void ObserveAddonConflict()
    {
        IReadOnlyList<string> values;
        try { values = readAddon(); }
        catch (OperationCanceledException) { throw; }
        catch (System.Threading.ThreadInterruptedException) { throw; }
        catch (Exception) { return; } // Unavailable is not an explicit clearance.

        // External observation can invalidate its actor/publication. Do not
        // publish that observation into another lifetime or continue its effects.
        if (!HasCurrentPublication() || values == null || values.Count != 1)
            return;
        bool previous = addonConflict;
        switch (values[0])
        {
            case "active": addonConflict = true; break;
            case "inactive":
            case "absent": addonConflict = false; break;
            default: return;
        }
        if (addonConflict && !previous)
            Styx.Helpers.Logging.Write("[Wholesome] Quest execution paused: TurnIn automation is active. Use /ti off to let the bot own quest interaction. Combat and services remain available.");
    }

    private bool ObserveExclusiveOwner(bool allowed)
    {
        if (!allowed || behavior == null) return false;
        bool suppress = behavior.SuppressServiceBehavior;
        if (!CanExecuteQuest() || !suppress) return false;
        bool done = behavior.IsDone;
        if (!CanExecuteQuest() || done) return false;
        bool deferred = behavior.IsExecutionDeferred;
        return CanExecuteQuest() && !deferred;
    }

    private int ProtectivePriority()
    {
        var me = ObjectManager.Me;
        if (me == null) return int.MaxValue;
        if (me.Dead || me.IsGhost) return 0;
        var poi = BotPoi.Current?.Type ?? PoiType.None;
        if (me.Combat || poi == PoiType.Kill) return 1;
        var pet = me.Pet;
        if (pet != null && pet.IsAlive && pet.Combat) return 1;
        if (poi == PoiType.Loot || poi == PoiType.Skin || poi == PoiType.Harvest) return 2;
        if (!exclusive && (poi == PoiType.Sell || poi == PoiType.Repair ||
            poi == PoiType.Buy || poi == PoiType.Mail || poi == PoiType.Train || poi == PoiType.Fly)) return 4;
        return int.MaxValue;
    }
}

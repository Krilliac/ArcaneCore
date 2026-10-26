using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// The hot module lane on the EventAI handler registry: a module swap adds, replaces and removes its own handlers in one atomic table
/// write, never overrides a built-in one, and an engine built before the swap sees new actions at once and new event handlers from its
/// next rebuild.
/// </summary>
public sealed class EventAiRegistrySwapTests
{
    private const byte ModuleType = 200;

    // Each test swaps on its own fresh copy of the built-in handlers, never on the shared Default.
    private static EventAiRegistry Fresh() => EventAiRegistry.Discover(typeof(EventAiRegistry).Assembly);

    [Fact]
    public void AModuleAction_IsAdded_ThenReplacedAndRemovedInOneSwapEach()
    {
        EventAiRegistry registry = Fresh();
        long generation = registry.Generation;
        var first = new CountingAction(ModuleType);
        var second = new CountingAction(ModuleType);

        Assert.Null(registry.ValidateSwap([], [], [], [ModuleType]));
        registry.CommitSwap([], [], [], [first]);
        Assert.Same(first, registry.FindAction(ModuleType));
        Assert.Equal([ModuleType], registry.ModuleActionTypes);
        Assert.False(registry.IsBuiltInAction(ModuleType));
        Assert.Equal(generation + 1, registry.Generation);

        Assert.Contains("remove it in the same swap", registry.ValidateSwap([], [], [], [ModuleType]), StringComparison.Ordinal);
        registry.CommitSwap([], [ModuleType], [], [second]);
        Assert.Same(second, registry.FindAction(ModuleType));

        registry.CommitSwap([], [ModuleType], [], []);
        Assert.Null(registry.FindAction(ModuleType));
        Assert.Empty(registry.ModuleActionTypes);
        Assert.Equal(generation + 3, registry.Generation);
    }

    [Fact]
    public void AModuleCannotOverrideOrRemoveABuiltInHandler_AndARejectedCommitChangesNothing()
    {
        EventAiRegistry registry = Fresh();
        byte builtInEvent = (byte)EventAiEventType.Aggro;
        byte builtInAction = (byte)EventAiActionType.SetPhase;
        EventAiEventHandler original = registry.FindEvent(builtInEvent)!;
        long generation = registry.Generation;
        Assert.True(registry.IsBuiltInEvent(builtInEvent));
        Assert.True(registry.IsBuiltInAction(builtInAction));

        Assert.Contains("built in; a module cannot override it", registry.ValidateSwap([], [], [builtInEvent], []), StringComparison.Ordinal);
        Assert.Contains("built in; a module cannot remove it", registry.ValidateSwap([], [builtInAction], [], []), StringComparison.Ordinal);
        Assert.Contains("built in; a module cannot remove it", registry.ValidateSwap([builtInEvent], [], [builtInEvent], []), StringComparison.Ordinal);
        Assert.Contains("no handler to remove", registry.ValidateSwap([], [ModuleType], [], []), StringComparison.Ordinal);
        Assert.Contains("added twice", registry.ValidateSwap([], [], [], [ModuleType, ModuleType]), StringComparison.Ordinal);

        // A swap that adds a free module action AND overrides a built-in event fails as a whole.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => registry.CommitSwap([], [], [new ModuleEvent(builtInEvent)], [new CountingAction(ModuleType)]));
        Assert.Contains("cannot override", error.Message, StringComparison.Ordinal);
        Assert.Same(original, registry.FindEvent(builtInEvent));
        Assert.Null(registry.FindAction(ModuleType));
        Assert.Equal(generation, registry.Generation);
    }

    [Fact]
    public void DiscoverOverSeveralAssemblies_FailsClosedOnADuplicateAcrossThem()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => EventAiRegistry.Discover([typeof(EventAiRegistry).Assembly, typeof(EventAiRegistrySwapTests).Assembly]));
        Assert.Contains("is handled twice", error.Message, StringComparison.Ordinal);

        // The same assembly twice is one assembly, not a clash.
        EventAiRegistry twice = EventAiRegistry.Discover([typeof(EventAiRegistry).Assembly, typeof(EventAiRegistry).Assembly]);
        Assert.Equal(EventAiRegistry.Default.EventTypes.Order(), twice.EventTypes.Order());
        Assert.Equal(EventAiRegistry.Default.ActionTypes.Order(), twice.ActionTypes.Order());
    }

    [Fact]
    public void AnEngineBuiltBeforeTheSwap_RunsTheNewModuleActionAtOnce()
    {
        CreatureEventAI wolf = Wolf([Row(1, EventAiEventType.Aggro, a1: new CreatureAiAction(ModuleType, 0, 0, 0))], out WorldRuntime world, out Player player);
        using WorldRuntime w = world;
        EventAiRegistry registry = Fresh();
        var ai = new CreatureEventAI(wolf.Me, wolf.AiContent, registry);
        var action = new CountingAction(ModuleType);

        registry.CommitSwap([], [], [], [action]);
        ai.OnAggro(player);

        Assert.Equal(1, action.Runs);
    }

    [Fact]
    public void AnEventSwap_ReachesAnExistingEngineAtItsNextRebuild_AndTheOldHandlerIsReportedRetired()
    {
        CreatureEventAI wolf = Wolf([Row(1, (EventAiEventType)ModuleType)], out WorldRuntime world, out _);
        using WorldRuntime w = world;
        EventAiRegistry registry = Fresh();
        var first = new ModuleEvent(ModuleType);
        registry.CommitSwap([], [], [first], []);
        var ai = new CreatureEventAI(wolf.Me, wolf.AiContent, registry);
        Assert.Same(first, Assert.Single(ai.Engine.Holders).Handler);
        Assert.True(registry.IsCurrent(first));

        var second = new ModuleEvent(ModuleType);
        registry.CommitSwap([ModuleType], [], [second], []);

        Assert.Same(first, Assert.Single(ai.Engine.Holders).Handler); // bound with its row state until the creature is rebuilt
        Assert.False(registry.IsCurrent(first));
        ai.OnRespawn();
        Assert.Same(second, Assert.Single(ai.Engine.Holders).Handler);
        Assert.True(registry.IsCurrent(second));
    }

    [Fact]
    public void Restore_PutsTheSavedHandlersBack_SoAHandlerBoundBeforeTheUndoneSwapIsCurrentAgain()
    {
        EventAiRegistry registry = Fresh();
        var first = new ModuleEvent(ModuleType);
        var action = new CountingAction(ModuleType);
        registry.CommitSwap([], [], [first], [action]);
        EventAiRegistry.Checkpoint saved = registry.Save();
        long generation = registry.Generation;

        registry.CommitSwap([ModuleType], [ModuleType], [new ModuleEvent(ModuleType)], []);
        Assert.False(registry.IsCurrent(first));
        Assert.Null(registry.FindAction(ModuleType));

        registry.Restore(saved);

        Assert.True(registry.IsCurrent(first));
        Assert.Same(action, registry.FindAction(ModuleType));
        Assert.Equal([ModuleType], registry.ModuleEventTypes);
        Assert.Equal([ModuleType], registry.ModuleActionTypes);
        Assert.Equal(generation + 2, registry.Generation);
        Assert.Throws<ArgumentException>(() => Fresh().Restore(saved));
    }

    [Fact]
    public void FromHandlers_MakesExactlyTheGivenHandlersBuiltIn()
    {
        EventAiHandlerSet builtIns = EventAiRegistry.DiscoverHandlers([typeof(EventAiRegistry).Assembly]);
        var extra = new ModuleEvent(ModuleType);

        EventAiRegistry registry = EventAiRegistry.FromHandlers([.. builtIns.Events, extra], builtIns.Actions);

        Assert.Same(extra, registry.FindEvent(ModuleType));
        Assert.True(registry.IsBuiltInEvent(ModuleType));
        Assert.Contains("built in; a module cannot override it", registry.ValidateSwap([], [], [ModuleType], []), StringComparison.Ordinal);
        Assert.Equal(EventAiRegistry.Default.ActionTypes.Order(), registry.ActionTypes.Order());
    }

    [Fact]
    public void TheDefaultRegistry_IsTheHolderEveryEventAiCreatureUses()
    {
        CreatureEventAI wolf = Wolf([Row(1, EventAiEventType.Aggro)], out WorldRuntime world, out _);
        using WorldRuntime w = world;

        Assert.Same(EventAiRegistry.Default, Registry(wolf.Engine));
        Assert.Empty(EventAiRegistry.Default.ModuleEventTypes);
        Assert.Empty(EventAiRegistry.Default.ModuleActionTypes);
    }

    [Fact]
    public void AnActionThatThrows_DoesNotLeaveTheEventInProgress_SoItFiresAgainOnTheNextBatch()
    {
        // The caller of the AI swallows the exception; the engine must still unwind its depth and the InProgress flag.
        CreatureEventAI wolf = Wolf([Row(1, EventAiEventType.Aggro, a1: new CreatureAiAction(ModuleType, 0, 0, 0))], out WorldRuntime world, out Player player);
        using WorldRuntime w = world;
        EventAiRegistry registry = Fresh();
        var ai = new CreatureEventAI(wolf.Me, wolf.AiContent, registry);
        var action = new ThrowOnFirstRunAction(ModuleType);
        registry.CommitSwap([], [], [], [action]);
        EventAiHolder holder = Assert.Single(ai.Engine.Holders);

        Assert.Throws<InvalidOperationException>(() => ai.OnAggro(player));
        Assert.False(holder.InProgress);

        holder.Enabled = true;
        ai.OnAggro(player);

        Assert.Equal(2, action.Runs); // RED on main: the holder stays InProgress and the second batch never runs it
    }

    private static EventAiRegistry Registry(EventAiEngine engine)
        => (EventAiRegistry)typeof(EventAiEngine).GetField("_registry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(engine)!;

    private static CreatureAiEvent Row(uint id, EventAiEventType type, CreatureAiAction a1 = default)
        => new() { Id = id, CreatureId = WolfEntry, EventType = (byte)type, Chance = 100, Action1 = a1 };

    private static CreatureEventAI Wolf(IEnumerable<CreatureAiEvent> events, out WorldRuntime world, out Player player)
    {
        CreatureContent content = new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)], [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(events, []));
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = new FakeCaster() });
        world = runtime;
        (player, _) = AddPlayer(world, 1, 0, 0);
        return (CreatureEventAI)Assert.Single(system.Creatures).AI!;
    }

    // A constructor argument keeps these out of Discover over the test assembly.
    private sealed class CountingAction(byte type) : EventAiActionHandler
    {
        public int Runs { get; private set; }

        public override byte ActionType => type;

        public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
        {
            Runs++;
            return true;
        }
    }

    private sealed class ThrowOnFirstRunAction(byte type) : EventAiActionHandler
    {
        public int Runs { get; private set; }

        public override byte ActionType => type;

        public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
        {
            if (++Runs == 1)
            {
                throw new InvalidOperationException("action fault");
            }

            return true;
        }
    }

    private sealed class ModuleEvent(byte type) : EventAiEventHandler
    {
        public override byte EventType => type;

        public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => false;
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>The engine's registries and holder state (cmangos CreatureEventAIHolder, ResetEvent, UpdateRepeatTimer).</summary>
public sealed class EngineInternalsTests
{
    [Fact]
    public void TheDefaultRegistry_HandlesTheImplementedEventsAndActions()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 36],
            EventAiRegistry.Default.EventTypes.Select(t => (int)t).Order());
        Assert.Equal([1, 2, 3, 4, 5, 9, 10, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 22, 23, 24, 25, 28, 29, 30, 31, 32, 33, 36, 37, 38, 39, 40, 41, 42,
                43, 45, 47, 50, 51, 53, 54, 55, 56, 57, 58, 59, 61, 64],
            EventAiRegistry.Default.ActionTypes.Select(t => (int)t).Order());
    }

    [Fact]
    public void TwoHandlersForOneEventType_FailAtDiscovery()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => EventAiRegistry.Discover(typeof(EngineInternalsTests).Assembly));
        Assert.Contains("event type 250 is handled twice", error.Message, StringComparison.Ordinal);
    }

    // Two handlers claiming one type id: only EventAiRegistry.Discover over this test assembly sees them.
    public sealed class DuplicateEventOne : EventAiEventHandler
    {
        public override byte EventType => 250;

        public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;
    }

    public sealed class DuplicateEventTwo : EventAiEventHandler
    {
        public override byte EventType => 250;

        public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => true;
    }

    private static CreatureAiEvent Row(uint id, EventAiEventType type, uint flags, byte chance = 100, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0, CreatureAiAction a1 = default)
        => new() { Id = id, CreatureId = WolfEntry, EventType = (byte)type, Flags = flags, Chance = chance, Param1 = p1, Param2 = p2, Param3 = p3, Param4 = p4, Action1 = a1 };

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

    [Fact]
    public void AFailedChanceRoll_SetsTheRepeatTimer_AndDisablesANonRepeatingRow()
    {
        // cmangos ProcessEvent :608-614 runs ResetEvent on a failed roll: UpdateRepeatTimer, then disable without the flag.
        CreatureEventAI ai = Wolf([Row(1, EventAiEventType.TimerInCombat, 0, chance: 0, p3: 700, p4: 700)], out WorldRuntime world, out Player player);
        using WorldRuntime w = world;
        EventAiHolder holder = Assert.Single(ai.Engine.Holders);

        player.Map!.Combat.DealDamage(player, ai.Me, 1, direct: false); // enters combat: the initial timer (0) is armed ...
        Assert.True(holder.Enabled);
        Run(w, 600);                                                      // ... the first batch considers the row, the roll fails

        Assert.False(holder.Enabled);
        Assert.Equal(700u, holder.TimerMs);
    }

    [Fact]
    public void ARepeatTimerWithAMaximumBelowTheMinimum_DisablesTheEvent()
    {
        // cmangos CreatureEventAIHolder::UpdateRepeatTimer: RandomMax < RandomMin disables repeating.
        CreatureEventAI ai = Wolf([Row(1, EventAiEventType.TimerInCombat, 1, p3: 900, p4: 100)], out WorldRuntime world, out Player player);
        using WorldRuntime w = world;
        EventAiHolder holder = Assert.Single(ai.Engine.Holders);

        Assert.False(holder.UpdateRepeatTimer(ai.Engine.Context, 900, 100));
        Assert.False(holder.Enabled);
        Assert.True(holder.UpdateRepeatTimer(ai.Engine.Context, 300, 300));
        Assert.Equal(300u, holder.TimerMs);
        _ = player;
    }

    [Fact]
    public void ARespawnRebuildsFreshHolders_WhileThePhaseSurvivesIt()
    {
        CreatureEventAI ai = Wolf([Row(1, EventAiEventType.Aggro, 0, a1: new CreatureAiAction((byte)EventAiActionType.SetPhase, 4, 0, 0))], out WorldRuntime world, out Player player);
        using WorldRuntime w = world;
        EventAiHolder before = Assert.Single(ai.Engine.Holders);

        ai.OnAggro(player);
        Assert.Equal(4, ai.Phase);
        ai.OnRespawn();

        Assert.NotSame(before, Assert.Single(ai.Engine.Holders));
        Assert.Equal(4, ai.Phase); // only JustDied returns to phase 0 (CreatureEventAI.cpp:1510)
    }
}

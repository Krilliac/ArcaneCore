using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>npc_lazy_peon (kalimdor/durotar.cpp): the Foreman's Blackjack wakes him, after 80 s he walks home and sleeps; unwoken he gets up within 2 min.</summary>
public sealed class LazyPeonTests
{
    // The grid loads with the first player (as in the other script tests), so the player comes first.
    private static (WorldRuntime World, Player Player, Creature Peon) Start()
    {
        CreatureContent content = new([Template(LazyPeonAI.Entry)], [Spawn(1, LazyPeonAI.Entry, 0, 0)], [], [], [], new CreatureAiContent([], []));
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices());
        (Player player, _) = AddPlayer(world, 1, 5, 0);
        return (world, player, Assert.Single(system.Creatures));
    }

    [Fact]
    public void TheEntryRunsTheScript()
    {
        (WorldRuntime world, Player player, Creature peon) = Start();
        using (world)
        {
            LazyPeonAI ai = Assert.IsType<LazyPeonAI>(peon.AI);
            Assert.True(ai.IsAsleep);
        }
    }

    [Fact]
    public void Blackjack_WakesHim_ThenAfter80sHeWalksHomeAndSleeps()
    {
        (WorldRuntime world, Player player, Creature peon) = Start();
        using (world)
        {
            var ai = (LazyPeonAI)peon.AI!;

            ai.OnSpellHit(peon, new SpellInfo { Id = 1 }); // another spell does nothing
            Assert.True(ai.IsAsleep);
            ai.OnSpellHit(player, new SpellInfo { Id = LazyPeonAI.SpellAwakenPeon });
            Assert.False(ai.IsAsleep);

            Run(world, LazyPeonAI.SleepAfterMs - 1_000);
            Assert.False(ai.IsAsleep);
            Assert.False(ai.IsHeadingHome);
            Run(world, 2_000);
            Assert.True(ai.IsHeadingHome || ai.IsAsleep);
            Run(world, 10_000);
            Assert.True(ai.IsAsleep);
        }
    }

    [Fact]
    public void Unwoken_HeGetsUpOnHisOwnWithinTwoMinutes()
    {
        (WorldRuntime world, Player player, Creature peon) = Start();
        using (world)
        {
            var ai = (LazyPeonAI)peon.AI!;
            Run(world, 99_000);
            Assert.True(ai.IsAsleep);
            Run(world, 22_000);
            Assert.False(ai.IsAsleep);
        }
    }
}

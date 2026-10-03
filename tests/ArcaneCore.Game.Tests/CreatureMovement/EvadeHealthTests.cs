using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureMovement;

/// <summary>
/// vmangos CreatureAI::EnterEvadeMode (AI/CreatureAI.cpp:323-346) never sets health: a creature that leaves combat gets it back through
/// Creature::RegenerateAll, a third of its maximum every 5 s (Creature.cpp:1087-1100, 1127-1160; UnitDefines.h REGEN_TIME_CREATURE_FULL).
/// </summary>
public sealed class EvadeHealthTests
{
    /// <summary>A wolf hurt to a tenth of its health by a player (combat tracks it, so its regeneration runs as it does in play).</summary>
    private static (WorldRuntime World, CreatureMapSystem System, Creature Wolf) Start(CreatureOptions? options = null)
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, options: options);
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        map.Combat.DealDamage(player, wolf, wolf.MaxHealth - (wolf.MaxHealth / 10), direct: false);
        return (world, system, wolf);
    }

    [Fact]
    public void EvadeDoesNotHealAtOnce_TheCreatureRegeneratesThirdsOfItsMaximumAfterwards()
    {
        (WorldRuntime w, CreatureMapSystem system, Creature wolf) = Start();
        using WorldRuntime world = w;
        uint max = wolf.MaxHealth;
        uint hurt = wolf.Health;
        Assert.InRange(hurt, 1u, max / 2);

        system.EnterEvadeMode(wolf);

        Assert.Equal(hurt, wolf.Health); // retail: not restored by the evade itself
        uint previous = wolf.Health;
        bool sawPartial = false;
        for (int tick = 0; tick < 400; tick++) // 20 s of map time in 50 ms steps
        {
            world.RunTick(50);
            Assert.True(wolf.Health >= previous, "health only goes up while regenerating");
            if (wolf.Health > hurt && wolf.Health < max)
            {
                sawPartial = true;
                Assert.Equal(hurt + (max / 3), wolf.Health); // the first regeneration is exactly a third of the maximum
                break;
            }

            previous = wolf.Health;
        }

        Assert.True(sawPartial, "never regenerated a partial step");
        Run(world, 20_000, 250);
        Assert.Equal(max, wolf.Health); // capped at the maximum
    }

    [Fact]
    public void WithEvadeRestoresFullHealthOn_TheOldInstantResetIsBack()
    {
        var options = new CreatureOptions();
        options.Movement.EvadeRestoresFullHealth = true;
        (WorldRuntime w, CreatureMapSystem system, Creature wolf) = Start(options);
        using WorldRuntime world = w;

        system.EnterEvadeMode(wolf);

        Assert.Equal(wolf.MaxHealth, wolf.Health);
    }

    [Fact]
    public void TheOptionDefaultsToRetail()
        => Assert.False(new CreatureOptions().Movement.EvadeRestoresFullHealth);
}

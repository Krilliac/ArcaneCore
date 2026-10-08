using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// The respawn modes a battleground puts on its event creatures (vmangos BattleGround::SpawnBGCreature, BattleGround.cpp:1590-1630):
/// RESPAWN_FORCED brings a dead one back within a second and later deaths after two minutes; RESPAWN_STOP keeps a creature whose corpse is
/// gone dead and makes every later death final, while a corpse keeps the respawn time it had.
/// </summary>
public sealed class EventRespawnModeTests
{
    private static (WorldRuntime World, CreatureMapSystem System, Creature Wolf) Start(uint respawnSeconds = 600)
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 2 };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(Content([Template()], [Spawn(1, WolfEntry, 30, 0, respawnSeconds: respawnSeconds)]), options);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
        world.RunTick(50);
        return (world, system, Assert.Single(system.Creatures));
    }

    [Fact]
    public void Forced_BringsADeadOneBackInASecond_AndLaterDeathsAfterTwoMinutes()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature wolf) = Start();
        system.KillCreature(wolf);
        world.RunTick(3000); // the corpse is gone, the respawn is 600 s away

        Assert.True(system.SetEventRespawnMode(1, forced: true));
        world.RunTick(1100);
        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);

        system.KillCreature(wolf);
        Assert.Equal(system.ClockMs + (CreatureMapSystem.EventRespawnSeconds * 1000L), wolf.RespawnAtMs);
        Assert.False(system.SetEventRespawnMode(2, forced: true)); // no such spawn here
    }

    [Fact]
    public void Stop_KeepsTheDeadDead_AndMakesTheNextDeathFinal()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature wolf) = Start(respawnSeconds: 5);
        system.KillCreature(wolf);
        world.RunTick(3000);
        Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);

        system.SetEventRespawnMode(1, forced: false);
        world.RunTick(10_000);
        Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);

        // Alive when stopped: it lives on, and once killed it stays dead.
        (world, system, wolf) = Start(respawnSeconds: 5);
        system.SetEventRespawnMode(1, forced: false);
        world.RunTick(1000);
        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);
        system.KillCreature(wolf);
        world.RunTick(30_000);
        Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);

        // A corpse keeps the time it had.
        (world, system, wolf) = Start(respawnSeconds: 1);
        system.KillCreature(wolf);
        system.SetEventRespawnMode(1, forced: false);
        world.RunTick(1500);
        world.RunTick(1000);
        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);
    }
}

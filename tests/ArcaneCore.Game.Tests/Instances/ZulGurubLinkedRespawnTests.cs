using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// cmangos CreatureLinkingHolder::CanSpawn (CreatureLinkingMgr.cpp:678-751): a FLAG_CANT_SPAWN_IF_BOSS_DEAD slave is held back map-wide while
/// the instance script reports an encounter in progress. Zul'Gurub's script counts IN_PROGRESS and SPECIAL
/// (<see cref="ZulGurubInstance.IsEncounterInProgress"/>); the pause is in <c>CreatureMapSystem.LinkAllowsSpawn</c>.
/// </summary>
public sealed class ZulGurubLinkedRespawnTests
{
    private const uint Boss = 1, Trash = 2;
    private const uint CantSpawnIfBossDead = 0x400;

    private static (WorldRuntime World, ZulGurubInstance Zg, CreatureMapSystem System, Creature Trash) Start()
    {
        CreatureContent content = new(
            [Template(330) with { Civilian = true }, Template(390) with { Civilian = true }],
            [Spawn(Boss, 330, 0, 0), Spawn(Trash, 390, 8, 0, respawnSeconds: 10)], [], [], [],
            links: [new CreatureLink(Trash, Boss, CantSpawnIfBossDead)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        AddPlayer(world, 1, 0, 10);
        var zg = new ZulGurubInstance(map);
        map.AddUpdater(zg);
        return (world, zg, system, system.Creatures.Single(c => c.Spawn?.Guid == Trash));
    }

    [Theory]
    [InlineData(EncounterState.InProgress)]
    [InlineData(EncounterState.Special)]
    public void LinkedRespawn_IsPausedWhileAnEncounterIsInProgress(uint state)
    {
        (WorldRuntime world, ZulGurubInstance zg, CreatureMapSystem system, Creature trash) = Start();
        using (world)
        {
            zg.SetData(6, state);
            Assert.True(zg.IsEncounterInProgress);
            system.KillCreature(trash);
            Run(world, 20_000, 500);
            Assert.False(trash.IsAlive); // its own 10 s are up and the boss lives, but a fight is on

            zg.SetData(6, EncounterState.NotStarted);
            Assert.False(zg.IsEncounterInProgress);
            Run(world, 1_000);
            Assert.True(trash.IsAlive);
        }
    }

    [Fact]
    public void LinkedRespawn_IsNotPausedWithoutAnEncounter()
    {
        (WorldRuntime world, ZulGurubInstance zg, CreatureMapSystem system, Creature trash) = Start();
        using (world)
        {
            Assert.False(zg.IsEncounterInProgress);
            system.KillCreature(trash);
            Run(world, 20_000, 500);
            Assert.True(trash.IsAlive);
        }
    }
}

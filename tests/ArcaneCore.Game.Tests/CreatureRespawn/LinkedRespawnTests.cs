using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureRespawn;

/// <summary>
/// creature_linking respawn/despawn events (cmangos CreatureLinkingHolder::ProcessSlave and CanSpawn, Entities/CreatureLinkingMgr.cpp:555-751)
/// and TrinityCore's dynamic respawn scaling (Map::ApplyDynamicModeRespawnScaling, Maps/Map.cpp:3312-3354).
/// </summary>
public sealed class LinkedRespawnTests
{
    private const uint Boss = 1, Trash = 2;

    private static (WorldRuntime World, CreatureMapSystem System, Creature Boss, Creature Trash) Start(
        uint flags, CreatureOptions? options = null, bool templateLink = false, uint trashRespawn = 3600, uint bossRespawn = 3600)
    {
        CreatureContent content = new(
            [Template(330) with { Civilian = true }, Template(390) with { Civilian = true }],
            [Spawn(Boss, 330, 0, 0, respawnSeconds: bossRespawn), Spawn(Trash, 390, 8, 0, respawnSeconds: trashRespawn)], [], [], [],
            links: templateLink ? null : [new CreatureLink(Trash, Boss, flags)],
            templateLinks: templateLink ? [new CreatureTemplateLink(390, 0, 330, flags, 0)] : null);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, options: options);
        AddPlayer(world, 1, 0, 10);
        return (world, system, system.Creatures.Single(c => c.Spawn?.Guid == Boss), system.Creatures.Single(c => c.Spawn?.Guid == Trash));
    }

    [Fact]
    public void RespawnOnRespawn_BossBringsItsTrashBack()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x80);
        using (world)
        {
            system.KillCreature(trash);
            system.KillCreature(boss);
            system.ForceRespawn(boss);
            Assert.True(boss.IsAlive);
            Assert.True(trash.IsAlive);
        }
    }

    [Fact]
    public void RespawnOnRespawn_WorksForEntryLinks()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x80, templateLink: true);
        using (world)
        {
            system.KillCreature(trash);
            system.KillCreature(boss);
            system.ForceRespawn(boss);
            Assert.True(trash.IsAlive);
        }
    }

    [Fact]
    public void CantSpawnIfBossDead_TrashWaitsForTheBoss()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x400, trashRespawn: 10);
        using (world)
        {
            system.KillCreature(boss);
            system.KillCreature(trash);
            Run(world, 20_000, 500);
            Assert.False(trash.IsAlive); // its own 10 s are up, but the boss is still dead

            system.ForceRespawn(boss);
            Run(world, 1_000);
            Assert.True(trash.IsAlive);
        }
    }

    [Fact]
    public void CantSpawnIfBossAlive_WaitsWhileTheBossLives()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x800, trashRespawn: 10);
        using (world)
        {
            system.KillCreature(trash);
            Run(world, 20_000, 500);
            Assert.False(trash.IsAlive);

            system.KillCreature(boss);
            Run(world, 1_000);
            Assert.True(trash.IsAlive);
        }
    }

    [Fact]
    public void DespawnOnDeath_And_SelfkillOnDeath()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x10);
        using (world)
        {
            system.KillCreature(boss);
            Assert.Equal(CreatureDeathState.Dead, trash.DeathState); // despawned: no corpse
        }

        (world, system, boss, trash) = Start(0x20);
        using (world)
        {
            system.KillCreature(boss);
            Assert.Equal(CreatureDeathState.Corpse, trash.DeathState);
        }
    }

    [Fact]
    public void RespawnOnDeath_DeadSlaveComesBackWhenTheMasterDies()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x40);
        using (world)
        {
            system.KillCreature(trash);
            system.KillCreature(boss);
            Assert.True(trash.IsAlive);
        }
    }

    [Fact]
    public void RespawnOnEvade_And_ToRespawnOnEvade()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x4);
        using (world)
        {
            system.KillCreature(trash);
            system.EnterEvadeMode(boss);
            Assert.True(trash.IsAlive);
        }

        (world, system, boss, trash) = Start(0x8);
        using (world)
        {
            system.KillCreature(boss);
            system.EnterEvadeMode(trash);
            Assert.True(boss.IsAlive);
        }
    }

    [Fact]
    public void DespawnOnRespawn_LivingSlaveGoesWhenTheMasterRespawns()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x100);
        using (world)
        {
            system.KillCreature(boss);
            system.ForceRespawn(boss);
            Assert.False(trash.IsAlive);
        }
    }

    [Fact]
    public void LinkedOff_KeepsTheOldBehaviour()
    {
        var options = new CreatureOptions();
        options.Respawn.Linked = false;
        (WorldRuntime world, CreatureMapSystem system, Creature boss, Creature trash) = Start(0x80 | 0x10, options);
        using (world)
        {
            system.KillCreature(boss);
            Assert.True(trash.IsAlive);
            system.KillCreature(trash);
            system.ForceRespawn(boss);
            Assert.False(trash.IsAlive);
        }
    }

    private static (WorldRuntime, CreatureMapSystem, Creature) StartScaled(float rate, int players, uint rank = 0, uint respawn = 120)
    {
        var options = new CreatureOptions();
        options.Respawn.DynamicRate = rate;
        CreatureContent content = new([Template(330) with { Civilian = true, Rank = rank }], [Spawn(1, 330, 0, 0, respawnSeconds: respawn)], [], [], []);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { ZoneAndAreaOf = _ => (12, 0) }, options);
        for (uint i = 1; i <= players; i++)
        {
            (Player p, _) = AddPlayer(world, i, 0, 10);
            p.ZoneId = 12;
        }

        return (world, system, system.Creatures.Single());
    }

    private static long DelayAfterDeath(CreatureMapSystem system, Creature c)
    {
        system.KillCreature(c);
        return (c.RespawnAtMs - system.ClockMs) / 1000;
    }

    [Theory]
    [InlineData(0f, 4, 120)]   // off by default
    [InlineData(10f, 4, 120)]  // factor 2.5 >= 1: unchanged
    [InlineData(10f, 20, 60)]  // 120 * 0.5
    [InlineData(10f, 200, 10)] // floored at the 10 s minimum
    public void DynamicRespawnScaling(float rate, int players, long expected)
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = StartScaled(rate, players);
        using (world)
        {
            Assert.Equal(expected, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void DynamicRespawnScaling_SkipsRaresAndWorldBosses()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = StartScaled(10f, 20, rank: 4);
        using (world)
        {
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }
}

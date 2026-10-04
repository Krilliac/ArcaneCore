using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureRespawn;

/// <summary>
/// Durable respawn timers (vmangos Creature::SetDeathState saves the respawn time at death, Objects/Creature.cpp:2242-2263, SaveRespawnTime
/// :2785-2794; MapPersistentState saves/deletes the row, Maps/MapPersistentStateMgr.cpp:80-101; a pending time makes the creature DEAD at
/// load, Creature.cpp:1972-1989). The wall clock is a fake; the map clock is driven by explicit diffs.
/// </summary>
public sealed class RespawnPersistenceTests
{
    private const long StartUnix = 1_700_000_000;

    private sealed class FakeClock : IRespawnClock
    {
        public long UnixSeconds { get; set; } = StartUnix;
    }

    private sealed class FakePersistence : ICreatureRespawnPersistence
    {
        public Dictionary<(uint Map, uint Instance, uint Guid), long> Stored { get; } = [];

        public List<string> Calls { get; } = [];

        public IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId)
            => Stored.Where(e => e.Key.Map == mapId && e.Key.Instance == instanceId).ToDictionary(e => e.Key.Guid, e => e.Value);

        public void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds)
        {
            Calls.Add($"save:{mapId}:{instanceId}:{spawnGuid}:{respawnUnixSeconds}");
            Stored[(mapId, instanceId, spawnGuid)] = respawnUnixSeconds;
        }

        public void Delete(uint mapId, uint instanceId, uint spawnGuid)
        {
            Calls.Add($"delete:{mapId}:{instanceId}:{spawnGuid}");
            Stored.Remove((mapId, instanceId, spawnGuid));
        }
    }

    private static (WorldRuntime World, CreatureMapSystem System) Start(
        CreatureContent content, FakePersistence persistence, FakeClock clock, CreatureOptions? options = null)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new CreatureMapSystem(map, content, options, random: new Random(1), respawnPersistence: persistence, respawnClock: clock);
        map.AddUpdater(system);
        AddPlayer(world, 1, 0, 0);
        return (world, system);
    }

    private static CreatureContent One(uint respawnSeconds = 600, uint rank = 0)
        => Content([Template(configure: t => t.Rank = rank)], [Spawn(7, WolfEntry, 10, 0, respawnSeconds: respawnSeconds)]);

    [Fact]
    public void ADeathSavesTheRespawnTimeAtOnce_ByDefault()
    {
        var persistence = new FakePersistence();
        (WorldRuntime w, CreatureMapSystem system) = Start(One(600), persistence, new FakeClock());
        using WorldRuntime world = w;

        system.KillCreature(Assert.Single(system.Creatures));

        Assert.Equal([$"save:0:0:7:{StartUnix + 600}"], persistence.Calls);
    }

    [Fact]
    public void WithSaveImmediatelyOff_OnlyABossIsSavedAtDeath_TheOthersAtUnloadOrShutdown()
    {
        var options = new CreatureOptions();
        options.Respawn.SaveImmediately = false;
        var persistence = new FakePersistence();
        (WorldRuntime w, CreatureMapSystem system) = Start(One(600), persistence, new FakeClock(), options);
        using WorldRuntime world = w;
        system.KillCreature(Assert.Single(system.Creatures));
        Assert.Empty(persistence.Calls);

        // vmangos Creature::SaveRespawnTime (Creature.cpp:2790-2791): the respawn time is still in the future, so m_respawnTime is saved as it is;
        system.SaveRespawnTimes();
        Assert.Equal([$"save:0:0:7:{StartUnix + 600}"], persistence.Calls); // the corpse formula (+ corpse time left) is only for a passed respawn time

        var bossPersistence = new FakePersistence();
        (WorldRuntime w2, CreatureMapSystem bossSystem) = Start(One(600, rank: 3), bossPersistence, new FakeClock(), options);
        using WorldRuntime world2 = w2;
        bossSystem.KillCreature(Assert.Single(bossSystem.Creatures));
        Assert.Equal([$"save:0:0:7:{StartUnix + 600}"], bossPersistence.Calls); // "always save boss respawn time at death to prevent crash cheating"
    }

    [Fact]
    public void ADeadCreatureWithoutACorpse_IsSavedWithItsRespawnTimeAtShutdown()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 1 };
        options.Respawn.SaveImmediately = false;
        var persistence = new FakePersistence();
        var clock = new FakeClock();
        (WorldRuntime w, CreatureMapSystem system) = Start(One(600), persistence, clock, options);
        using WorldRuntime world = w;
        Creature wolf = Assert.Single(system.Creatures);
        system.KillCreature(wolf);

        Run(world, 5000, 500); // the corpse is gone after 1 s; the creature is Dead until its time
        clock.UnixSeconds += 5;
        Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);
        system.SaveRespawnTimes();

        Assert.Equal([$"save:0:0:7:{StartUnix + 600}"], persistence.Calls); // m_respawnTime itself (Creature.cpp:2790-2791)
    }

    [Fact]
    public void APendingTimeAtLoad_MakesTheCreatureDeadForTheRemainingTime_ThenItRespawnsAndTheRowGoes()
    {
        var persistence = new FakePersistence();
        persistence.Stored[(0, 0, 7)] = StartUnix + 300; // killed before the restart, 300 s left
        var clock = new FakeClock();
        (WorldRuntime w, CreatureMapSystem system) = Start(One(600), persistence, clock);
        using WorldRuntime world = w;

        Creature wolf = Assert.Single(system.Creatures);
        Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);
        Assert.Null(wolf.Map);
        Assert.Equal(system.ClockMs + 300_000, wolf.RespawnAtMs);

        Run(world, 290_000, 1000);
        Assert.Equal(CreatureDeathState.Dead, wolf.DeathState);
        Run(world, 12_000, 1000);

        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);
        Assert.Contains("delete:0:0:7", persistence.Calls);
        Assert.Empty(persistence.Stored);
    }

    [Fact]
    public void AnExpiredTimeAtLoad_SpawnsTheCreatureAliveAndDeletesTheRow()
    {
        var persistence = new FakePersistence();
        persistence.Stored[(0, 0, 7)] = StartUnix - 5;
        (WorldRuntime w, CreatureMapSystem system) = Start(One(600), persistence, new FakeClock());
        using WorldRuntime world = w;

        Assert.Equal(CreatureDeathState.Alive, Assert.Single(system.Creatures).DeathState);
        Assert.Equal(["delete:0:0:7"], persistence.Calls); // vmangos Creature.cpp:1984-1989
    }

    [Fact]
    public void ARestart_KeepsTheBossDead_WithTheTimeThatWasLeft()
    {
        var persistence = new FakePersistence();
        var clock = new FakeClock();
        (WorldRuntime first, CreatureMapSystem before) = Start(One(3600, rank: 3), persistence, clock);
        using (first)
        {
            before.KillCreature(Assert.Single(before.Creatures));
        }

        clock.UnixSeconds += 1000; // the world was down for 1000 s

        (WorldRuntime second, CreatureMapSystem after) = Start(One(3600, rank: 3), persistence, clock);
        using WorldRuntime world = second;
        Creature boss = Assert.Single(after.Creatures);

        Assert.Equal(CreatureDeathState.Dead, boss.DeathState);
        Assert.Equal(after.ClockMs + 2_600_000, boss.RespawnAtMs);
    }

    [Fact]
    public void AForcedRespawn_DeletesTheRow()
    {
        var persistence = new FakePersistence();
        (WorldRuntime w, CreatureMapSystem system) = Start(One(600), persistence, new FakeClock());
        using WorldRuntime world = w;
        Creature wolf = Assert.Single(system.Creatures);
        system.KillCreature(wolf);
        persistence.Calls.Clear();

        system.ForceRespawn(wolf);

        Assert.Equal(["delete:0:0:7"], persistence.Calls);
        Assert.Empty(persistence.Stored);
    }

    [Fact]
    public void ANaturalRespawn_DeletesTheRow()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 1 };
        var persistence = new FakePersistence();
        (WorldRuntime w, CreatureMapSystem system) = Start(One(5), persistence, new FakeClock(), options);
        using WorldRuntime world = w;
        Creature wolf = Assert.Single(system.Creatures);
        system.KillCreature(wolf);
        persistence.Calls.Clear();

        Run(world, 7000, 500);

        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState);
        Assert.Equal(["delete:0:0:7"], persistence.Calls);
    }

    [Fact]
    public void WithPersistOff_NothingIsSavedOrRead()
    {
        var options = new CreatureOptions();
        options.Respawn.Persist = false;
        var persistence = new FakePersistence();
        persistence.Stored[(0, 0, 7)] = StartUnix + 300;
        (WorldRuntime w, CreatureMapSystem system) = Start(One(600), persistence, new FakeClock(), options);
        using WorldRuntime world = w;

        Creature wolf = Assert.Single(system.Creatures);
        Assert.Equal(CreatureDeathState.Alive, wolf.DeathState); // the stored time is not consulted
        system.KillCreature(wolf);
        system.SaveRespawnTimes();
        Assert.Empty(persistence.Calls);
    }

    [Fact]
    public void WithoutAPersistenceSeam_NothingChanges()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var plain = new CreatureMapSystem(map, One(), random: new Random(1));
        map.AddUpdater(plain);
        AddPlayer(world, 1, 0, 0);

        plain.KillCreature(Assert.Single(plain.Creatures));
        plain.SaveRespawnTimes(); // a no-op without the seam

        Assert.Equal(CreatureDeathState.Corpse, Assert.Single(plain.Creatures).DeathState);
    }

    [Fact]
    public void WithTheDefaultOption_ShutdownSavesNothingMore_BecauseEveryDeathWasSavedAtDeath()
    {
        var persistence = new FakePersistence();
        (WorldRuntime w, CreatureMapSystem system) = Start(One(600), persistence, new FakeClock());
        using WorldRuntime world = w;
        system.KillCreature(Assert.Single(system.Creatures));
        persistence.Calls.Clear();

        system.SaveRespawnTimes(); // the corpse form (now + delay + corpse left) would only push the saved time later

        Assert.Empty(persistence.Calls);
    }

    [Fact]
    public void WithSaveImmediatelyOff_AGridUnloadSavesTheDeadCreature()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 1 };
        options.Respawn.SaveImmediately = false;
        var persistence = new FakePersistence();
        CreatureContent content = Content([Template()], [Spawn(7, WolfEntry, 30, 0, respawnSeconds: 600)]);
        WorldRuntime world = TestWorld.CreateRuntime();
        using (world)
        {
            Map map = world.GetMap(0);
            var system = new CreatureMapSystem(map, content, options, random: new Random(1), respawnPersistence: persistence, respawnClock: new FakeClock());
            map.AddUpdater(system);
            map.Grids.Options.GridCleanUpDelayMs = 60_000;
            (ArcaneCore.Game.Entities.Player player, _) = AddPlayer(world, 1, 0, 0);
            system.KillCreature(Assert.Single(system.Creatures));
            Assert.Empty(persistence.Calls);

            player.Relocate(5000, 5000, 83.5f, 0, 0);
            world.RunTick(6000);
            world.RunTick(1);
            world.RunTick(60_000);

            Assert.False(system.IsGridLoaded(30, 0));
            string call = Assert.Single(persistence.Calls);
            Assert.StartsWith("save:0:0:7:", call, StringComparison.Ordinal);
            long saved = long.Parse(call[(call.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(saved, StartUnix + 500, StartUnix + 560); // 600 s minus the ~66 s of map time that passed (m_respawnTime, no corpse any more)
        }
    }

    [Fact]
    public void TheOptionsDefaultToRetail()
    {
        var options = new CreatureOptions();
        Assert.True(options.Respawn.Persist);
        Assert.True(options.Respawn.SaveImmediately); // vmangos SaveRespawnTimeImmediately = 1 (mangosd.conf.dist.in:397, World.cpp:729)
    }
}

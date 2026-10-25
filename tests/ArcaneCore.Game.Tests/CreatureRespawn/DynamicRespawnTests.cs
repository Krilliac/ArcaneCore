using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Pools;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureRespawn;

/// <summary>Population-scaled respawn (World:Respawn:Dynamic): delay = base / (1 + ScalePerPlayer * max(0, players - PlayerThreshold)), floored.</summary>
public sealed class DynamicRespawnTests
{
    private const long StartUnix = 1_700_000_000;
    private const uint DungeonMap = 36;

    private sealed class FakeClock : IRespawnClock
    {
        public long UnixSeconds => StartUnix;
    }

    private sealed class FakePersistence : ICreatureRespawnPersistence
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId) => new Dictionary<uint, long>();

        public bool TryGetPending(uint mapId, uint instanceId, uint spawnGuid, out long respawnUnixSeconds)
        {
            respawnUnixSeconds = 0;
            return false;
        }

        public void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds) => Calls.Add($"save:{mapId}:{instanceId}:{spawnGuid}:{respawnUnixSeconds}");

        public void Delete(uint mapId, uint instanceId, uint spawnGuid)
        {
        }
    }

    private static void AddCrowd(WorldRuntime world, int humans, int bots, uint mapId = 0, uint instanceId = 0)
    {
        for (int i = 0; i < humans + bots; i++)
        {
            var session = new FakeSession(i + 1) { IsPlayerbot = i >= humans };
            Player p = TestWorld.CreatePlayer((uint)(i + 1), i % 10, i / 10, session, mapId);
            if (mapId == 0 && instanceId == 0)
            {
                world.AddPlayer(p);
            }
            else
            {
                world.GetMap(mapId, instanceId).AddPlayer(p);
            }
        }

        world.RunTick(0);
    }

    private static (WorldRuntime World, CreatureMapSystem System, Creature Creature) Start(
        int humans, int bots = 0, Action<DynamicRespawnOptions>? tune = null, uint rank = 0, uint respawn = 120, bool enabled = true,
        FakePersistence? persistence = null, uint mapId = 0, uint[]? links = null, uint instanceId = 0)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Respawn.Dynamic.Enabled = enabled;
        tune?.Invoke(world.Options.Respawn.Dynamic);
        if (mapId == DungeonMap)
        {
            WorldMaps.Of(world).Load(new MapContent([new MapTemplate(DungeonMap, 0, MapType.Instance, 0, 5, 0, -1, 0, 0, "Deadmines", "")], [], [], [], []));
        }

        Map map = world.GetMap(mapId, instanceId);
        CreatureTemplate template = Template(WolfEntry, t => t.Rank = rank) with { Civilian = true };
        CreatureContent content = new([template], [Spawn(7, WolfEntry, 0, 0, mapId: mapId, respawnSeconds: respawn)], [], [], [],
            links: links is null ? null : [new CreatureLink(7, 8, links[0])]);
        var system = new CreatureMapSystem(map, content, random: new Random(1), aiServices: new CreatureAiServices(),
            respawnPersistence: persistence, respawnClock: persistence is null ? null : new FakeClock());
        map.AddUpdater(system);
        AddCrowd(world, humans, bots, mapId, instanceId);
        return (world, system, system.Creatures.Single());
    }

    private static long DelayAfterDeath(CreatureMapSystem system, Creature c)
    {
        system.KillCreature(c);
        return (c.RespawnAtMs - system.ClockMs) / 1000;
    }

    [Theory]
    [InlineData(4, 120)]   // under the threshold of 5: unchanged
    [InlineData(5, 120)]   // at it: unchanged
    [InlineData(20, 48)]   // 120 / (1 + 0.1 * 15)
    [InlineData(35, 30)]   // 120 / (1 + 0.1 * 30)
    public void PlayersNearTheSpawn_ShortenItsRespawnPerTheFormula(int players, long expected)
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(players);
        using (world)
        {
            Assert.Equal(expected, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void TheMinimumSeconds_IsAFloor()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20, tune: o => o.MinimumSeconds = 100);
        using (world)
        {
            Assert.Equal(100, DelayAfterDeath(system, c)); // 48 s scaled, floored at 100
        }
    }

    [Fact]
    public void TheMaxReduction_IsAFloor()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(60, tune: o => o.MaxReduction = 0.5f);
        using (world)
        {
            Assert.Equal(60, DelayAfterDeath(system, c)); // 120 / 6.5 = 19 s scaled, at most half off
        }
    }

    [Fact]
    public void Disabled_LeavesTheDelayAlone()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(40, enabled: false);
        using (world)
        {
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }

    [Theory]
    [InlineData(1u)]  // elite
    [InlineData(2u)]  // rare elite
    [InlineData(4u)]  // rare
    [InlineData(3u)]  // world boss
    public void ElitesRaresAndBosses_AreNotScaledByDefault(uint rank)
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20, rank: rank);
        using (world)
        {
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void IncludeElitesAndRares_ScalesAnEliteButNeverAWorldBoss()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature elite) = Start(20, tune: o => o.IncludeElitesAndRares = true, rank: 1);
        using (world)
        {
            Assert.Equal(48, DelayAfterDeath(system, elite));
        }

        (WorldRuntime world2, CreatureMapSystem system2, Creature boss) = Start(20, tune: o => o.IncludeElitesAndRares = true, rank: 3);
        using (world2)
        {
            Assert.Equal(120, DelayAfterDeath(system2, boss));
        }
    }

    [Fact]
    public void ADungeonMap_IsNeverScaled()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20, mapId: DungeonMap);
        using (world)
        {
            Assert.Equal(20, world.GetMap(DungeonMap).Players.Count);
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void AScriptedDelay_IsNotScaled()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20);
        using (world)
        {
            c.RespawnDelayOnceSeconds = 300;
            Assert.Equal(300, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void ALinkedSpawn_IsNotScaled()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20, links: [0x40]); // FLAG_RESPAWN_ON_DEATH
        using (world)
        {
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void CountBotsFalse_IgnoresBots_AndTrueCountsThem()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(3, bots: 17, tune: o => o.CountBots = false);
        using (world)
        {
            Assert.Equal(120, DelayAfterDeath(system, c)); // 3 humans: under the threshold
        }

        (WorldRuntime world2, CreatureMapSystem system2, Creature c2) = Start(3, bots: 17);
        using (world2)
        {
            Assert.Equal(48, DelayAfterDeath(system2, c2));
        }
    }

    [Fact]
    public void PlayersOutsideTheRadius_AreNotCounted()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20, tune: o => o.Radius = 5f);
        using (world)
        {
            // players stand 0..9 yards east and 0..1 north of the spawn: with a 5 yard radius far fewer than 20 count, and 120 -> more than 48
            Assert.InRange(DelayAfterDeath(system, c), 49, 120);
        }
    }

    [Fact]
    public void ThePersistedRespawnTime_IsTheScaledOne()
    {
        var persistence = new FakePersistence();
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20, persistence: persistence);
        using (world)
        {
            system.KillCreature(c);
            Assert.Equal([$"save:0:0:7:{StartUnix + 48}"], persistence.Calls);
        }
    }

    [Fact]
    public void AQuestObject_ScalesWhenEnabled_AndAPlainObjectDoesNot()
    {
        foreach ((GameObjectType type, uint quest, long expected) in new[] { (GameObjectType.Goober, 1u, 19L), (GameObjectType.Generic, 0u, 60L) })
        {
            WorldRuntime world = TestWorld.CreateRuntime();
            using (world)
            {
                world.Options.Respawn.Dynamic.Enabled = true;
                world.Options.Respawn.Dynamic.ScalePerPlayer = 0.15f; // 60 / (1 + 0.15 * 15) = 18.5, rounded up to 19
                world.Options.Respawn.Dynamic.MinimumSeconds = 10;
                Map map = world.GetMap(0);
                GameObjectContent content = new(
                    [GameObjects.GameObjectTestKit.GoTemplate(500, type, (1, quest), (11, 1u))], [GameObjects.GameObjectTestKit.GoSpawn(1, 500, 0, 0)], [], [], []);
                var system = new GameObjectMapSystem(map, content);
                map.AddUpdater(system);
                AddCrowd(world, 20, 0);
                Run(world, 200);
                GameObject go = system.GameObjects.Single();
                system.Despawn(go);
                long seconds = (go.RespawnAtMs - system.ClockMs) / 1000;
                Assert.Equal(expected, seconds);
            }
        }
    }

    [Fact]
    public void IncludeGameObjectsFalse_LeavesAQuestObjectAlone()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        using (world)
        {
            world.Options.Respawn.Dynamic.Enabled = true;
            world.Options.Respawn.Dynamic.IncludeGameObjects = false;
            Map map = world.GetMap(0);
            GameObjectContent content = new(
                [GameObjects.GameObjectTestKit.GoTemplate(500, GameObjectType.Goober, (1, 1u), (11, 1u))], [GameObjects.GameObjectTestKit.GoSpawn(1, 500, 0, 0)], [], [], []);
            var system = new GameObjectMapSystem(map, content);
            map.AddUpdater(system);
            AddCrowd(world, 20, 0);
            Run(world, 200);
            GameObject go = system.GameObjects.Single();
            system.Despawn(go);
            Assert.Equal(60, (go.RespawnAtMs - system.ClockMs) / 1000);
        }
    }

    [Fact]
    public void CountPlayersWithin_AllocatesNothingInSteadyState()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(40);
        using (world)
        {
            Map map = world.GetMap(0);
            map.CountPlayersWithin(0, 0, 100, true); // grows the scratch list once
            long before = GC.GetAllocatedBytesForCurrentThread();
            int n = 0;
            for (int i = 0; i < 100; i++)
            {
                n += map.CountPlayersWithin(0, 0, 100, true);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(4000, n);
        }
    }

    private static (WorldRuntime World, CreatureMapSystem System, Creature Creature) StartContent(CreatureContent content, int humans = 20)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Respawn.Dynamic.Enabled = true;
        Map map = world.GetMap(0);
        var system = new CreatureMapSystem(map, content, random: new Random(1), aiServices: new CreatureAiServices());
        map.AddUpdater(system);
        AddCrowd(world, humans, 0);
        return (world, system, system.Creatures.First(c => c.IsAlive));
    }

    private static CreatureContent TwoWolves(SpawnGroupCatalog? groups = null, PoolCatalog? pools = null)
        => new([Template(WolfEntry) with { Civilian = true }], [Spawn(7, WolfEntry, 0, 0), Spawn(8, WolfEntry, 1, 0)], [], [], []) { SpawnGroups = groups ?? SpawnGroupCatalog.Empty, Pools = pools ?? PoolCatalog.Empty };

    [Fact]
    public void ASpawnGroupMember_IsNotScaled()
    {
        CreatureContent content = TwoWolves(new SpawnGroupCatalog([new SpawnGroupDefinition { Id = 1, Type = SpawnGroupType.Creature, Members = [new SpawnGroupMember(7, 0, 0), new SpawnGroupMember(8, 1, 0)] }]));
        (WorldRuntime world, CreatureMapSystem system, Creature c) = StartContent(content);
        using (world)
        {
            Assert.Equal(20, world.GetMap(0).Players.Count);
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void ACreaturePoolMember_IsNotScaled()
    {
        CreatureContent content = TwoWolves(pools: PoolCatalog.Build([new PoolTemplateData(1, 1, "pool")], [new PoolSpawnLink(7, 1, 0f), new PoolSpawnLink(8, 1, 0f)], [], [],
            new Dictionary<uint, (uint, uint)> { [7] = (WolfEntry, 0), [8] = (WolfEntry, 0) }));
        (WorldRuntime world, CreatureMapSystem system, Creature c) = StartContent(content);
        using (world)
        {
            Assert.Single(system.Creatures); // max_limit 1: one member of the pool exists
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void ABattlegroundsRespawnDelayOverride_IsNotScaled()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20, respawn: 300);
        using (world)
        {
            c.RespawnDelayOverrideSeconds = 120; // vmangos SpawnBGCreature: two minutes
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void AnInstanceCopyOfAnOpenWorldMapId_IsNeverScaled()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20, instanceId: 1);
        using (world)
        {
            Assert.Equal(20, world.GetMap(0, 1).Players.Count);
            Assert.Equal(120, DelayAfterDeath(system, c));
        }
    }

    [Fact]
    public void AGameMaster_IsNotCounted()
    {
        (WorldRuntime world, CreatureMapSystem system, Creature c) = Start(20);
        using (world)
        {
            world.GetMap(0).Players.First().Flags |= PlayerFlags.Gm;
            Assert.Equal(50, DelayAfterDeath(system, c)); // 19 counted: 120 / (1 + 0.1 * 14) = 50
        }
    }

    private static readonly LockEntry HerbLock = new(5, [(uint)LockKeyType.Skill], [(uint)LockType.Herbalism], [1]);

    private static (WorldRuntime World, GameObjectMapSystem System) StartHerbs(int seed, bool pooled, uint lockId)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Respawn.Dynamic.Enabled = true;
        Map map = world.GetMap(0);
        GameObjectSpawn[] spawns = [GameObjects.GameObjectTestKit.GoSpawn(1, 600, 0, 0, spawnTimeSeconds: 120), GameObjects.GameObjectTestKit.GoSpawn(2, 600, 2, 0, spawnTimeSeconds: 120)];
        var content = new GameObjectContent([GameObjects.GameObjectTestKit.GoTemplate(600, GameObjectType.Chest, (0, lockId))], spawns, [HerbLock], [], [])
        {
            Pools = pooled
                ? PoolCatalog.Build([new PoolTemplateData(1, 1, "herbs")], [new PoolSpawnLink(1, 1, 0f), new PoolSpawnLink(2, 1, 0f)], [], [],
                    new Dictionary<uint, (uint, uint)> { [1] = (600, 0), [2] = (600, 0) })
                : PoolCatalog.Empty,
        };
        var system = new GameObjectMapSystem(map, content, random: new Random(seed));
        map.AddUpdater(system);
        AddCrowd(world, 20, 0);
        Run(world, 200);
        return (world, system);
    }

    [Fact]
    public void AHerbNode_ScalesBecauseItsLockNeedsHerbalism_AndAPlainChestDoesNot()
    {
        foreach ((uint lockId, long expected) in new[] { (5u, 48L), (0u, 120L) })
        {
            (WorldRuntime world, GameObjectMapSystem system) = StartHerbs(1, pooled: false, lockId);
            using (world)
            {
                GameObject go = system.GameObjects.First(g => g.IsSpawned);
                system.Despawn(go);
                Assert.Equal(expected, (go.RespawnAtMs - system.ClockMs) / 1000);
            }
        }
    }

    [Fact]
    public void APooledHerb_IsScaledWhicheverMemberThePoolPicks()
    {
        int switched = 0;
        for (int seed = 0; seed < 16; seed++)
        {
            (WorldRuntime world, GameObjectMapSystem system) = StartHerbs(seed, pooled: true, lockId: 5);
            using (world)
            {
                GameObject gathered = Assert.Single(system.GameObjects, g => g.IsSpawned);
                system.Despawn(gathered);
                GameObject waiting = Assert.Single(system.GameObjects, g => !g.IsSpawned && g.RespawnAtMs > 0);
                Assert.Equal(48, (waiting.RespawnAtMs - system.ClockMs) / 1000);
                if (waiting.Spawn!.Guid != gathered.Spawn!.Guid)
                {
                    switched++;
                }
            }
        }

        Assert.True(switched > 0, "the pool never picked the other member, so SpawnMember was not exercised");
    }

    private static List<long> RandomisedDelays(Action<DynamicRespawnOptions>? tune)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        using (world)
        {
            tune?.Invoke(world.Options.Respawn.Dynamic);
            Map map = world.GetMap(0);
            GameObjectSpawn spawn = GameObjects.GameObjectTestKit.GoSpawn(1, 700, 0, 0, spawnTimeSeconds: 120) with { SpawnFlags = 0x04 }; // RANDOM_RESPAWN_TIME: x urand(90,110)/100
            var content = new GameObjectContent([GameObjects.GameObjectTestKit.GoTemplate(700, GameObjectType.Generic)], [spawn], [], [], []);
            var system = new GameObjectMapSystem(map, content, random: new Random(1234));
            map.AddUpdater(system);
            AddCrowd(world, 20, 0);
            Run(world, 200);
            GameObject go = system.GameObjects.Single();
            var delays = new List<long>();
            for (int i = 0; i < 12; i++)
            {
                system.Despawn(go);
                delays.Add(go.RespawnAtMs - system.ClockMs);
                system.ForceRespawn(go);
            }

            return delays;
        }
    }

    [Fact]
    public void Disabled_TheRandomisedDelaySequence_IsIdenticalToHavingNoDynamicSection()
    {
        List<long> untouched = RandomisedDelays(null);
        List<long> disabledWithValues = RandomisedDelays(o => { o.Enabled = false; o.PlayerThreshold = 0; o.ScalePerPlayer = 5f; o.MinimumSeconds = 1; });

        Assert.Equal(untouched, disabledWithValues);
        Assert.True(untouched.Distinct().Count() > 3, "the 0x04 flag should vary the delay");
        Assert.All(untouched, ms => Assert.InRange(ms, 108_000, 132_000)); // 120 s x 90..110 %
    }
}

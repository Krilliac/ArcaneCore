using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Pools;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Pools;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.Pools.ClassicDbPoolRows;

namespace ArcaneCore.Game.Tests.Pools;

/// <summary>
/// cmangos pools (Pools/PoolManager.cpp) in the game object and creature map systems, over classic-db z2815 rows
/// (<see cref="ClassicDbPoolRows"/>): only <c>max_limit</c> members of a pool exist at once, a gathered node lets its pool choose what comes
/// back, nested pools switch whole spots, and pooled event spawns follow their event.
/// </summary>
public sealed class PoolMapTests
{
    private static GameObjectSpawn Node(uint guid, uint entry, int index)
        => GoSpawn(guid, entry, 5 + (index * 4), 0, spawnTimeSeconds: 300) with { SpawnTimeMaxSeconds = 900 };

    private static (uint Guid, uint Entry)[] BadlandsSpawnList => [.. BadlandsSpots.SelectMany(s => s.Nodes.Select(n => (n.Guid, n.Entry)))];

    private static GameObjectContent BadlandsContent()
    {
        int i = 0;
        GameObjectSpawn[] spawns = [.. BadlandsSpots.SelectMany(s => { int spot = i++; return s.Nodes.Select(n => Node(n.Guid, n.Entry, spot)); })];
        return new GameObjectContent(
            [GoTemplate(TruesilverDeposit, GameObjectType.Chest), GoTemplate(GoldVein, GameObjectType.Chest), GoTemplate(MithrilDeposit, GameObjectType.Chest)],
            spawns, [], [], [])
        {
            Pools = Catalog(BadlandsNodes, BadlandsSpawnList, BadlandsLinks),
        };
    }

    private static (WorldRuntime World, GameObjectMapSystem System) StartObjects(GameObjectContent content, int seed)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, content) { Random = new Random(seed) };
        map.AddUpdater(system);
        world.AddPlayer(Player(1).Player);
        world.RunTick(50);
        return (world, system);
    }

    private static GameObject[] Live(GameObjectMapSystem system, IEnumerable<uint> guids)
        => [.. system.GameObjects.Where(g => g.Spawn is { } s && guids.Contains(s.Guid) && g.IsSpawned)];

    private static uint SpotOf(uint guid) => BadlandsSpots.First(s => s.Nodes.Any(n => n.Guid == guid)).Pool;

    [Fact]
    public void BadlandsMultinode_OneNodeOfFifteenExists_InOneSpot()
    {
        uint[] guids = [.. BadlandsSpawnList.Select(s => s.Guid)];
        var spots = new HashSet<uint>();
        for (int seed = 0; seed < 25; seed++)
        {
            (WorldRuntime world, GameObjectMapSystem system) = StartObjects(BadlandsContent(), seed);
            using (world)
            {
                GameObject node = Assert.Single(Live(system, guids)); // before pools: all fifteen stood there at once
                Assert.Equal(1u, system.PoolSpawnedCount(BadlandsMother));
                Assert.Equal(1u, system.PoolSpawnedCount(SpotOf(node.Spawn!.Guid)));
                spots.Add(SpotOf(node.Spawn.Guid));
            }
        }

        Assert.True(spots.Count >= 3, $"the mother pool rolled only spots {string.Join(", ", spots)}");
    }

    [Fact]
    public void BadlandsMultinode_AMinedNodeComesBackOneAtATime_AndTheSpotMoves()
    {
        uint[] guids = [.. BadlandsSpawnList.Select(s => s.Guid)];
        int moved = 0;
        for (int seed = 0; seed < 12; seed++)
        {
            (WorldRuntime world, GameObjectMapSystem system) = StartObjects(BadlandsContent(), seed);
            using (world)
            {
                GameObject mined = Assert.Single(Live(system, guids));
                uint spot = SpotOf(mined.Spawn!.Guid);
                system.Despawn(mined); // gathered: GO_JUST_DEACTIVATED
                Assert.Empty(Live(system, guids));
                for (int second = 0; second < 1000; second++)
                {
                    world.RunTick(1000);
                    Assert.True(Live(system, guids).Length <= 1, $"seed {seed}: two nodes at {second} s");
                }

                GameObject back = Assert.Single(Live(system, guids)); // within the 300-900 s respawn time
                Assert.Equal(1u, system.PoolSpawnedCount(BadlandsMother));
                if (SpotOf(back.Spawn!.Guid) != spot)
                {
                    moved++;
                    Assert.Null(system.FindBySpawn(mined.Spawn.Guid)); // the old spot's node left the world
                }
            }
        }

        Assert.InRange(moved, 4, 12); // four of five times another spot (the mother re-rolls its children)
    }

    [Fact]
    public void ExplicitChances_AreOneRollForTheShuffledList_AsCmangosRollOne()
    {
        // Spot 3027 alone (a top-level pool here): Truesilver 20, Gold 20, Mithril 0 (equal). cmangos rolls once and walks the shuffled
        // explicit list comparing the same roll with each chance, so each 20 wins only 10% of the time and Mithril 80% (vmangos'
        // subtracting walk would give 20/20/60).
        (uint Pool, (uint Guid, uint Entry, float Chance)[] Nodes) spot = BadlandsSpots[1];
        PoolCatalog catalog = Catalog(spot.Nodes.Select(n => new PoolSpawnLink(n.Guid, spot.Pool, n.Chance)), spot.Nodes.Select(n => (n.Guid, n.Entry)));
        var host = new RecordingHost();
        var counts = new Dictionary<uint, int>();
        var random = new Random(7);
        const int Trials = 6000;
        for (int i = 0; i < Trials; i++)
        {
            host.Spawned.Clear();
            var state = new PoolSpawnState(catalog, 0, host, random);
            state.Initialize();
            uint entry = spot.Nodes.Single(n => n.Guid == Assert.Single(host.Spawned)).Entry;
            counts[entry] = counts.GetValueOrDefault(entry) + 1;
        }

        Assert.InRange(counts[TruesilverDeposit] / (double)Trials, 0.08, 0.12);
        Assert.InRange(counts[GoldVein] / (double)Trials, 0.08, 0.12);
        Assert.InRange(counts[MithrilDeposit] / (double)Trials, 0.77, 0.83);
    }

    [Fact]
    public void MerchantCoastChests_OneOfTwelve_AndALootedChestIsReplacedAfterItsRespawnTime()
    {
        uint[] guids = [.. MerchantCoastChestSpawns.Select(c => c.Guid)];
        var content = new GameObjectContent(
            [GoTemplate(BatteredChestA, GameObjectType.Chest), GoTemplate(BatteredChestB, GameObjectType.Chest)],
            MerchantCoastChestSpawns.Select((c, i) => Node(c.Guid, c.Entry, i)), [], [], [])
        {
            Pools = Catalog(MerchantCoastChestSpawns.Select(c => new PoolSpawnLink(c.Guid, MerchantCoastChests, 0f)), MerchantCoastChestSpawns),
        };
        (WorldRuntime world, GameObjectMapSystem system) = StartObjects(content, 3);
        using (world)
        {
            GameObject chest = Assert.Single(Live(system, guids));
            system.Despawn(chest);
            for (int second = 0; second < 901; second++)
            {
                world.RunTick(1000);
                Assert.True(Live(system, guids).Length <= 1);
            }

            Assert.Single(Live(system, guids));
            Assert.Equal(1u, system.PoolSpawnedCount(MerchantCoastChests));
        }
    }

    [Fact]
    public void Plaguebloom_APoolWithoutATemplate_NeverSpawnsItsMembers()
    {
        // cmangos: the pool_gameobject join keeps them out of the grid, and the pool (MaxLimit 0, not auto-spawned) never brings them.
        var content = new GameObjectContent(
            [GoTemplate(Plaguebloom, GameObjectType.Chest)], PlaguebloomSpawns.Select((g, i) => Node(g, Plaguebloom, i)), [], [], [])
        {
            Pools = Catalog(PlaguebloomSpawns.Select(g => new PoolSpawnLink(g, PlaguebloomPool, 0f)), PlaguebloomSpawns.Select(g => (g, Plaguebloom))),
        };
        (WorldRuntime world, GameObjectMapSystem system) = StartObjects(content, 1);
        using (world)
        {
            Assert.Empty(Live(system, PlaguebloomSpawns));
            Assert.True(content.Pools.IsPooled(PlaguebloomSpawns[0]));
            // cmangos keeps the rows (8648 is below the largest pool id): a pool with no template has max_limit 0 and is never auto-spawned.
            Assert.Equal(PlaguebloomPool, content.Pools.PoolOf(PlaguebloomSpawns[0]));
            Assert.Equal(new { HasTemplate = false, MaxLimit = 0u, AutoSpawn = false },
                content.Pools.Find(PlaguebloomPool) is { } pool ? new { pool.HasTemplate, pool.MaxLimit, pool.AutoSpawn } : null);
        }
    }

    [Fact]
    public void NoblegardenEggs_PooledEventSpawns_FollowTheirEvent_TenOfTwelve()
    {
        var content = new GameObjectContent(
            [GoTemplate(BrightlyColoredEgg, GameObjectType.Chest)], EggSpawns.Select((g, i) => Node(g, BrightlyColoredEgg, i) with { SpawnTimeSeconds = 30, SpawnTimeMaxSeconds = null }), [], [], [])
        {
            Pools = Catalog(EggSpawns.Select(g => new PoolSpawnLink(g, GoldshireEggs, 0f)), EggSpawns.Select(g => (g, BrightlyColoredEgg))),
        };
        (WorldRuntime world, GameObjectMapSystem system) = StartObjects(content, 5);
        using (world)
        {
            var gate = new EventGate(EggSpawns);
            system.SpawnGate = gate; // Noblegarden not running
            Assert.Empty(Live(system, EggSpawns));
            Assert.Equal(0u, system.PoolSpawnedCount(GoldshireEggs));

            gate.Running = true; // event 9 starts
            system.RefreshSpawns(EggSpawns);
            Assert.Equal(10, Live(system, EggSpawns).Length);

            gate.Running = false; // and stops
            system.RefreshSpawns(EggSpawns);
            Assert.Empty(Live(system, EggSpawns));
            Assert.Equal(0u, system.PoolSpawnedCount(GoldshireEggs));
        }
    }

    [Fact]
    public void APooledSpawnInASpawnGroup_IsLeftToItsPool()
    {
        // cmangos ObjectMgr::LoadSpawnGroups skips a pooled spawn ("part of pool or game event (incompatible)").
        var group = new SpawnGroupDefinition
        {
            Id = 77,
            Type = SpawnGroupType.GameObject,
            MaxCount = 12,
            Members = [.. MerchantCoastChestSpawns.Select(c => new SpawnGroupMember(c.Guid, -1, 0))],
        };
        var content = new GameObjectContent(
            [GoTemplate(BatteredChestA, GameObjectType.Chest), GoTemplate(BatteredChestB, GameObjectType.Chest)],
            MerchantCoastChestSpawns.Select((c, i) => Node(c.Guid, c.Entry, i)), [], [], [])
        {
            Pools = Catalog(MerchantCoastChestSpawns.Select(c => new PoolSpawnLink(c.Guid, MerchantCoastChests, 0f)), MerchantCoastChestSpawns),
            SpawnGroups = new SpawnGroupCatalog([group]),
        };
        (WorldRuntime world, GameObjectMapSystem system) = StartObjects(content, 2);
        using (world)
        {
            world.RunTick(1000);
            Assert.Single(Live(system, MerchantCoastChestSpawns.Select(c => c.Guid))); // the group would have spawned all twelve
            Assert.Empty(system.SpawnGroupObjects(77));
        }
    }

    // --- creatures --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThurosLightfingers_ARarePooledByEntry_OneOfEightSpots_AndAfterHisDeathOneAgain()
    {
        int moved = 0;
        for (int seed = 0; seed < 4; seed++)
        {
            var content = new CreatureContent(
                [Template(Thuros)], ThurosSpawns.Select((g, i) => Spawn(g, Thuros, 5 + (i * 4), 0, respawnSeconds: 5400) with { SpawnTimeMaxSeconds = 9000 }), [], [], [])
            {
                Pools = PoolCatalog.Build(Templates, [], [new PoolSpawnLink(Thuros, ThurosPool, 0f)], [], ThurosSpawns.ToDictionary(g => g, _ => (Thuros, 0u))),
            };
            WorldRuntime world = TestWorld.CreateRuntime();
            using (world)
            {
                Map map = world.GetMap(0);
                var system = new CreatureMapSystem(map, content, new CreatureOptions { CorpseDecayNormalSeconds = 60 }, random: new Random(seed));
                map.AddUpdater(system);
                world.AddPlayer(Player(1).Player);
                world.RunTick(50);
                Creature[] Alive() => [.. system.Creatures.Where(c => c.Spawn is not null && c.IsAlive)];

                Assert.Equal(1, ThurosSpawns.Count(system.IsPoolSpawned));
                Creature thuros = Assert.Single(Alive());
                uint first = thuros.Spawn!.Guid;
                system.KillCreature(thuros);
                for (int second = 0; second < 9_100; second += 10)
                {
                    world.RunTick(10_000);
                    Assert.True(Alive().Length <= 1, $"seed {seed}: two at {second} s");
                }

                // His respawn time (5400-9000 s) lets the pool roll: he comes back in place, or another spot is chosen and, its grid being
                // loaded, appears at once (cmangos Spawn1Object creates it alive) while he leaves the world.
                Creature back = Assert.Single(Alive());
                if (back.Spawn!.Guid != first)
                {
                    moved++;
                    Assert.DoesNotContain(system.Creatures, c => c.Spawn?.Guid == first);
                }
            }
        }

        Assert.True(moved >= 1, "the pool never moved Thuros to another spot in four respawns");
    }

    private sealed class EventGate(IReadOnlyCollection<uint> listed) : ISpawnGate
    {
        public bool Running { get; set; }

        public bool AllowsCreature(uint spawnGuid) => true;

        public bool AllowsGameObject(uint spawnGuid) => Running || !listed.Contains(spawnGuid);

        public IEnumerable<uint> GatedCreatures => [];

        public IEnumerable<uint> GatedGameObjects => listed;
    }

    private sealed class RecordingHost : IPoolHost
    {
        public List<uint> Spawned { get; } = [];

        public bool CanSpawn(uint spawnGuid) => true;

        public void SpawnMember(uint spawnGuid, bool instantly) => Spawned.Add(spawnGuid);

        public void DespawnMember(uint spawnGuid) => Spawned.Remove(spawnGuid);
    }
}

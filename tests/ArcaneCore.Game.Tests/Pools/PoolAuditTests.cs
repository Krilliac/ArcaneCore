using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Pools;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Pools;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.Pools.ClassicDbPoolRows;

namespace ArcaneCore.Game.Tests.Pools;

/// <summary>
/// <see cref="PoolAudit"/> (GM <c>.spawngroup poolaudit</c>; the whole-world run over the real dump is
/// ArcaneCore.World.Tests PoolWorldAuditTests): clean on the classic-db pools after load and after many rotations, and it reports each
/// broken invariant when one is broken.
/// </summary>
public sealed class PoolAuditTests
{
    private static uint[] BadlandsGuids => [.. BadlandsSpots.SelectMany(s => s.Nodes.Select(n => n.Guid))];

    private static GameObjectContent BadlandsContent()
    {
        int i = 0;
        GameObjectSpawn[] spawns = [.. BadlandsSpots.SelectMany(s =>
        {
            int spot = i++;
            return s.Nodes.Select(n => GoSpawn(n.Guid, n.Entry, 5 + (spot * 4), 0, spawnTimeSeconds: 300) with { SpawnTimeMaxSeconds = 900 });
        })];
        return new GameObjectContent(
            [GoTemplate(TruesilverDeposit, GameObjectType.Chest), GoTemplate(GoldVein, GameObjectType.Chest), GoTemplate(MithrilDeposit, GameObjectType.Chest)],
            spawns, [], [], [])
        {
            Pools = Catalog(BadlandsNodes, BadlandsSpots.SelectMany(s => s.Nodes.Select(n => (n.Guid, n.Entry))), BadlandsLinks),
        };
    }

    [Fact]
    public void BadlandsMultinode_AuditIsClean_AfterLoad_AndAfterEveryOfFiftyRotations()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, BadlandsContent()) { Random = new Random(17) };
        map.AddUpdater(system);
        world.AddPlayer(Player(1).Player);
        world.RunTick(50);

        PoolAuditReport loaded = system.AuditPools();
        Assert.True(loaded.Clean, string.Join("; ", loaded.Issues.Select(i => i.Problem)));
        Assert.Equal(6, loaded.PoolsChecked); // the mother and its five spots
        Assert.Equal(1, loaded.LiveMembers);

        var spots = new HashSet<uint>();
        for (int round = 0; round < 50; round++)
        {
            Assert.Equal(1, system.RotatePools());
            world.RunTick(1000);
            PoolAuditReport report = system.AuditPools();
            Assert.True(report.Clean, $"round {round}: " + string.Join("; ", report.Issues.Select(i => i.Problem)));
            Assert.Equal(1u, system.PoolSpawnedCount(BadlandsMother));
            uint node = Assert.Single(BadlandsGuids, system.IsPoolSpawned);
            Assert.True(system.GameObjects.Count(g => g.Spawn is { } s && BadlandsGuids.Contains(s.Guid)) <= 1, $"round {round}: two nodes in the world");
            spots.Add(BadlandsSpots.First(s => s.Nodes.Any(n => n.Guid == node)).Pool);
        }

        Assert.True(spots.Count >= 3, $"fifty rotations visited only spots {string.Join(", ", spots)}");
    }

    [Fact]
    public void ThurosLightfingers_AuditIsClean_ThroughDeathsAndRespawns()
    {
        var content = new CreatureContent(
            [Template(Thuros)], ThurosSpawns.Select((g, i) => Spawn(g, Thuros, 5 + (i * 4), 0, respawnSeconds: 5400) with { SpawnTimeMaxSeconds = 9000 }), [], [], [])
        {
            Pools = PoolCatalog.Build(Templates, [], [new PoolSpawnLink(Thuros, ThurosPool, 0f)], [], ThurosSpawns.ToDictionary(g => g, _ => (Thuros, 0u))),
        };
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new CreatureMapSystem(map, content, new CreatureOptions { CorpseDecayNormalSeconds = 60 }, random: new Random(3));
        map.AddUpdater(system);
        world.AddPlayer(Player(1).Player);
        world.RunTick(50);
        Assert.True(system.AuditPools().Clean);

        for (int death = 0; death < 4; death++)
        {
            Creature thuros = Assert.Single(system.Creatures, c => c.Spawn is not null && c.IsAlive);
            system.KillCreature(thuros);
            for (int second = 0; second < 9_100; second += 100)
            {
                world.RunTick(100_000);
                PoolAuditReport report = system.AuditPools();
                Assert.True(report.Clean, $"death {death}, {second} s: " + string.Join("; ", report.Issues.Select(i => i.Problem)));
                Assert.True(report.LiveMembers <= 1);
            }
        }

        Assert.Equal(1, system.RotatePools());
        Assert.True(system.AuditPools().Clean);
    }

    [Fact]
    public void Audit_ReportsALeakedSpawn_AndAPoolOverItsLimitInTheWorld()
    {
        (uint Pool, (uint Guid, uint Entry, float Chance)[] Nodes) spot = BadlandsSpots[1];
        PoolCatalog catalog = Catalog(spot.Nodes.Select(n => new PoolSpawnLink(n.Guid, spot.Pool, n.Chance)), spot.Nodes.Select(n => (n.Guid, n.Entry)));
        var state = new PoolSpawnState(catalog, 0, new Host(), new Random(1));
        state.Initialize();
        uint[] guids = [.. spot.Nodes.Select(n => n.Guid)];
        uint chosen = Assert.Single(guids, state.IsSpawned);

        Assert.True(PoolAudit.Run(state, guids, g => g == chosen).Clean);

        // Every node standing (a rotation that forgot to remove the old one): one leak per node not out, and the pool over its limit.
        PoolAuditReport report = PoolAudit.Run(state, guids, _ => true);
        Assert.Equal(2, report.Issues.Count(i => i.Problem.Contains("leaked", StringComparison.Ordinal)));
        Assert.Single(report.Issues, i => i.Problem.Contains("in the world, max_limit 1", StringComparison.Ordinal));
        Assert.Equal(3, report.LiveMembers);
    }

    [Fact]
    public void Audit_ReportsAChildPoolTheMotherDidNotChoose_AndASpawnWhoseRowWasDropped()
    {
        PoolCatalog catalog = Catalog(BadlandsNodes, BadlandsSpots.SelectMany(s => s.Nodes.Select(n => (n.Guid, n.Entry))), BadlandsLinks);
        var state = new PoolSpawnState(catalog, 0, new Host(), new Random(2));
        state.Initialize();
        uint chosenSpot = BadlandsSpots.Select(s => s.Pool).Single(state.IsPoolSpawned);
        uint otherNode = BadlandsSpots.First(s => s.Pool != chosenSpot).Nodes[0].Guid;

        PoolAuditReport report = PoolAudit.Run(state, [.. BadlandsGuids, 19897], g => g == otherNode || g == 19897 || state.IsSpawned(g));
        Assert.Contains(report.Issues, i => i.Problem.Contains($"spawn {otherNode} is in the world but its pool does not have it out", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Problem.Contains("2 child pool(s) in the world, max_limit 1", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Problem.Contains("spawn 19897 is in the world but in no pool", StringComparison.Ordinal));
    }

    private sealed class Host : IPoolHost
    {
        public bool CanSpawn(uint spawnGuid) => true;

        public void SpawnMember(uint spawnGuid, bool instantly)
        {
        }

        public void DespawnMember(uint spawnGuid)
        {
        }
    }
}

using System.Diagnostics.Tracing;
using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Pools;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The allocation per tick of a populated map's creature and pool paths (the wave 10 report: map allocation rose from 1.34 to 4.18 MiB per
/// tick with the spawns, patrols and formations): six players among 1,200 wandering creatures, 120 waypoint patrols, 40 five-member
/// formations walking their paths and 200 rotating pools of ore nodes and rares, ticked at 50 ms. Deterministic (manual clock, seeded
/// randoms); bytes are measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/> on the world thread, and the top allocating types
/// are sampled with the runtime's GCAllocationTick events and printed. Wall time is output only.
/// </summary>
[Collection("World tick load")]
public sealed class CreatureTickAllocationTests(ITestOutputHelper output)
{
    private const int Wanderers = 1200;
    private const int Patrols = 120;
    private const int Formations = 40;
    private const int NodePools = 150;
    private const int RarePools = 50;
    private const float Z = 83.5f;

    /// <summary>
    /// The steady-state budget (bytes per 50 ms tick). Wave 17 measured 198,605 bytes/tick (194.0 KiB) before its cuts and 13,949 (13.6 KiB)
    /// after; the budget leaves room for the packets themselves and fails long before the old per-creature LINQ and closures come back.
    /// </summary>
    public const long BudgetBytesPerTick = 64 * 1024;

    internal static (WorldRuntime World, CreatureMapSystem Creatures, GameObjectMapSystem Objects, Player[] Players) Build()
    {
        var random = new Random(5875);
        var templates = new List<CreatureTemplate> { Template(WolfEntry), Template(GuardEntry) };
        var spawns = new List<CreatureSpawn>();
        var waypoints = new List<(uint, CreatureWaypoint)>();
        var paths = new List<(uint Entry, uint PathId, CreatureWaypoint Point)>();
        var groups = new List<SpawnGroupDefinition>();
        uint guid = 1;
        (float X, float Y) Around(float radius) => ((random.NextSingle() - .5f) * radius, (random.NextSingle() - .5f) * radius);

        for (int i = 0; i < Wanderers; i++)
        {
            (float x, float y) = Around(400);
            spawns.Add(Spawn(guid++, WolfEntry, x, y, Z, movementType: 1, wander: 8, respawnSeconds: 60));
        }

        for (int i = 0; i < Patrols; i++)
        {
            (float x, float y) = Around(400);
            uint g = guid++;
            spawns.Add(Spawn(g, GuardEntry, x, y, Z, movementType: 2, respawnSeconds: 60));
            for (uint p = 1; p <= 6; p++)
            {
                waypoints.Add((g, new CreatureWaypoint(p, x + (MathF.Cos(p) * 25), y + (MathF.Sin(p) * 25), Z, 100f, p == 1 ? 3000u : 0u)));
            }
        }

        for (int f = 0; f < Formations; f++)
        {
            (float x, float y) = Around(400);
            uint pathId = 7000 + (uint)f;
            for (uint p = 1; p <= 8; p++)
            {
                paths.Add((CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | pathId,
                    new CreatureWaypoint(p, x + (MathF.Cos(p * .8f) * 40), y + (MathF.Sin(p * .8f) * 40), Z, 100f, p == 1 ? 5000u : 0u)));
            }

            var members = new List<SpawnGroupMember>();
            uint leader = guid;
            for (int m = 0; m < 5; m++)
            {
                uint g = guid++;
                spawns.Add(Spawn(g, GuardEntry, x + m, y, Z, respawnSeconds: 300));
                members.Add(new SpawnGroupMember(g, m, 0));
            }

            groups.Add(new SpawnGroupDefinition
            {
                Id = 100 + (uint)f, Name = "allocation formation", Type = SpawnGroupType.Creature, Members = members,
                Formation = new SpawnGroupFormation(4, 4, 0, pathId, 2, "allocation formation"),
            });
        }

        // Rares: one of four spots each (cmangos pool_creature, max_limit 1).
        var poolTemplates = new List<PoolTemplateData>();
        var creatureLinks = new List<PoolSpawnLink>();
        var creatureSpawnMap = new Dictionary<uint, (uint, uint)>();
        for (int r = 0; r < RarePools; r++)
        {
            uint pool = 1 + (uint)r;
            poolTemplates.Add(new PoolTemplateData(pool, 1, "allocation rare"));
            for (int s = 0; s < 4; s++)
            {
                (float x, float y) = Around(400);
                uint g = guid++;
                spawns.Add(Spawn(g, WolfEntry, x, y, Z, respawnSeconds: 30));
                creatureLinks.Add(new PoolSpawnLink(g, pool, 0f));
                creatureSpawnMap[g] = (WolfEntry, 0);
            }
        }

        var content = new CreatureContent(templates, spawns, waypoints, [], [], entryWaypoints: paths)
        {
            SpawnGroups = new SpawnGroupCatalog(groups),
            Pools = PoolCatalog.Build(poolTemplates, creatureLinks, [], [], creatureSpawnMap),
        };

        // Ore nodes: one of three per spot (pool_gameobject, max_limit 1), gathered and rotating.
        var nodeTemplates = new List<PoolTemplateData>();
        var nodeLinks = new List<PoolSpawnLink>();
        var nodeSpawns = new List<GameObjectSpawn>();
        var nodeMap = new Dictionary<uint, (uint, uint)>();
        uint goGuid = 1;
        for (int n = 0; n < NodePools; n++)
        {
            uint pool = 1000 + (uint)n;
            nodeTemplates.Add(new PoolTemplateData(pool, 1, "allocation node"));
            (float x, float y) = Around(400);
            for (int s = 0; s < 3; s++)
            {
                uint g = goGuid++;
                nodeSpawns.Add(GoSpawn(g, 1731, x + (s * 3), y, spawnTimeSeconds: 20));
                nodeLinks.Add(new PoolSpawnLink(g, pool, 0f));
                nodeMap[g] = (1731, 0);
            }
        }

        var objects = new GameObjectContent([GoTemplate(1731, GameObjectType.Chest)], nodeSpawns, [], [], [])
        {
            Pools = PoolCatalog.Build(nodeTemplates, nodeLinks, [], [], nodeMap),
        };

        WorldRuntime world = TestWorld.CreateRuntime();
        world.UseManualClock();
        Map map = world.GetMap(0);
        var creatureSystem = new CreatureMapSystem(map, content, random: new Random(1));
        map.AddUpdater(creatureSystem);
        var objectSystem = new GameObjectMapSystem(map, objects) { Random = new Random(2) };
        map.AddUpdater(objectSystem);
        var players = new Player[6];
        for (int i = 0; i < players.Length; i++)
        {
            players[i] = TestWorld.CreatePlayer((uint)i + 1, (i - 3) * 60, (i % 2) * 60, new FakeSession(i + 1));
            world.AddPlayer(players[i]);
        }

        return (world, creatureSystem, objectSystem, players);
    }

    /// <summary>One simulated tick of the harness: a gathered node and a killed rare every so often so the pools keep rotating.</summary>
    internal static void Step(WorldRuntime world, CreatureMapSystem creatures, GameObjectMapSystem objects, int tick)
    {
        if (tick % 20 == 0)
        {
            foreach (GameObject node in objects.GameObjects)
            {
                if (node.Spawn is not null && node.IsSpawned)
                {
                    objects.Despawn(node);
                    break;
                }
            }
        }

        world.RunTick(50);
    }

    [Fact]
    public void PopulatedMap_SteadyTick_ReportsAllocationPerTickAndTopAllocators()
    {
        (WorldRuntime w, CreatureMapSystem creatures, GameObjectMapSystem objects, Player[] players) = Build();
        using WorldRuntime world = w;
        const int warmup = 400, samples = 800;
        for (int tick = 0; tick < warmup; tick++)
        {
            Step(world, creatures, objects, tick);
        }

        int moving = creatures.Creatures.Count(c => c.IsMoving);
        using var sampler = new AllocationSampler();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = warmup; tick < warmup + samples; tick++)
        {
            Step(world, creatures, objects, tick);
        }

        long perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / samples;
        sampler.Dispose();
        Assert.True(creatures.Creatures.Count() > Wanderers, "the creatures are in the world");
        Assert.True(moving > 50, $"only {moving} creatures were moving");
        Assert.All(players, p => Assert.True(p.VisibleObjects.Count > 50));
        Assert.True(creatures.AuditPools().Clean);
        Assert.True(objects.AuditPools().Clean);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{creatures.Creatures.Count()} creatures ({moving} moving at warmup end), {objects.GameObjects.Count()} objects; allocated {perTick} bytes/tick ({perTick / 1024.0:F1} KiB) over {samples} ticks"));
        foreach ((string type, long bytes) in sampler.Top(25))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {bytes / samples,10} B/tick  {type}"));
        }

        Assert.True(perTick <= BudgetBytesPerTick, $"{perTick} bytes/tick, budget {BudgetBytesPerTick}");
    }

    /// <summary>GCAllocationTick samples (about one per 100 KB allocated) by type, scaled to bytes.</summary>
    private sealed class AllocationSampler : EventListener
    {
        private readonly Dictionary<string, long> _bytes = [];
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _on = true;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!_on || e.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null)
            {
                return;
            }

            int typeIndex = e.PayloadNames!.IndexOf("TypeName");
            int amountIndex = e.PayloadNames.IndexOf("AllocationAmount64");
            string type = typeIndex >= 0 ? e.Payload[typeIndex] as string ?? "?" : "?";
            long amount = amountIndex >= 0 ? Convert.ToInt64(e.Payload[amountIndex], CultureInfo.InvariantCulture) : 100_000;
            lock (_bytes)
            {
                _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
            }
        }

        public IEnumerable<(string Type, long Bytes)> Top(int count)
        {
            lock (_bytes)
            {
                return [.. _bytes.OrderByDescending(p => p.Value).Take(count).Select(p => (p.Key, p.Value))];
            }
        }

        public override void Dispose()
        {
            _on = false;
            base.Dispose();
        }
    }
}

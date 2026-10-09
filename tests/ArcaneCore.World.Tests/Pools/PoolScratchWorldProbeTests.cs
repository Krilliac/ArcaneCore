using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Pools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Pools;

/// <summary>
/// A measurement over a scratch world built from the real classic-db dump (never the live one: the path comes from
/// <c>ARCANECORE_TEST_POOLS_WORLD_DB</c>, a SQLite file the developer built with <c>arcane-content-importer import/refresh</c>; the dump is
/// GPL data and is not committed). Every grid of Eastern Kingdoms and Kalimdor that holds a spawn is loaded, then the live mining nodes,
/// herb nodes and chests are counted per zone (zones from the terrain in <c>ARCANECORE_TEST_TERRAIN_DIR</c> when set) and printed, and the
/// pool limits are checked: no pool has more members in the world than its <c>max_limit</c>. Skipped without the variable.
/// </summary>
public sealed class PoolScratchWorldProbeTests(ITestOutputHelper output)
{
    public const string Variable = "ARCANECORE_TEST_POOLS_WORLD_DB";

    private const uint LockKeySkill = 2;
    private const uint LockTypeHerbalism = 2;
    private const uint LockTypeMining = 3;

    private static readonly (uint Zone, string Name)[] Zones =
    [
        (12, "Elwynn Forest"), (1, "Dun Morogh"), (85, "Tirisfal Glades"), (14, "Durotar"), (215, "Mulgore"), (141, "Teldrassil"),
        (40, "Westfall"), (17, "The Barrens"), (33, "Stranglethorn Vale"), (3, "Badlands"), (139, "Eastern Plaguelands"),
    ];

    [ScratchWorldFact]
    public async Task ScratchWorld_LiveNodesAndChestsPerZone_StayWithinThePoolLimits()
    {
        string path = Environment.GetEnvironmentVariable(Variable)!;
        Assert.True(File.Exists(path), $"{Variable} names {path}, which does not exist");

        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Mode=ReadOnly;Pooling=False").Options;
        GameObjectContent objects;
        CreatureContent creatures;
        MapContent maps;
        await using (var db = new WorldDbContext(options))
        {
            objects = await new EfGameObjectDataStore(db).LoadAsync();
            creatures = await new EfCreatureDataStore(db).LoadAsync();
            maps = await new EfMapDataStore(db).LoadAsync();
        }

        TerrainManager? terrain = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR") is { Length: > 0 } root && Directory.Exists(root)
            ? new TerrainManager(root) { Areas = new AreaTable(maps.Areas, new MapRegistry(maps.Maps)) }
            : null;

        output.WriteLine($"world {Path.GetFileName(path)}: {objects.Pools.Count} game object pools, {creatures.Pools.Count} creature pools");
        var perZone = new Dictionary<(uint Zone, string Kind), int>();
        int pooledLive = 0, poolsChecked = 0, totalLive = 0, totalCreatures = 0;
        foreach (uint mapId in new uint[] { 0, 1 })
        {
            using var world = new WorldRuntime(new WorldRuntimeOptions(), new NullSaves(), NullLogger<WorldRuntime>.Instance);
            Map map = world.GetMap(mapId);
            var objectSystem = new GameObjectMapSystem(map, objects) { Random = new Random(20261008) };
            map.AddUpdater(objectSystem);
            var creatureSystem = new CreatureMapSystem(map, creatures, random: new Random(20261008));
            map.AddUpdater(creatureSystem);
            foreach (GameObjectSpawn spawn in objects.GetSpawns(mapId))
            {
                map.Grids.LoadGridsAround(spawn.X, spawn.Y, 0f);
            }

            foreach (CreatureSpawn spawn in creatures.GetSpawns(mapId))
            {
                map.Grids.LoadGridsAround(spawn.X, spawn.Y, 0f);
            }

            GameObject[] live = [.. objectSystem.GameObjects.Where(g => g.Spawn is not null && g.IsSpawned)];
            totalLive += live.Length;
            totalCreatures += creatureSystem.Creatures.Count(c => c.Spawn is not null && c.IsAlive);
            foreach (GameObject go in live)
            {
                string? kind = Kind(objects, go.Template);
                if (kind is null)
                {
                    continue;
                }

                uint zone = terrain?.For(mapId).GetZoneAndAreaId(go.X, go.Y, go.Z).ZoneId ?? 0;
                perZone[(zone, kind)] = perZone.GetValueOrDefault((zone, kind)) + 1;
                perZone[(uint.MaxValue - mapId, kind)] = perZone.GetValueOrDefault((uint.MaxValue - mapId, kind)) + 1;
            }

            // No pool has more of its spawns in the world than its limit (children count against the mother as one each).
            ILookup<uint, GameObject> livePerPool = live.Where(g => objects.Pools.PoolOf(g.Spawn!.Guid) != 0).ToLookup(g => objects.Pools.PoolOf(g.Spawn!.Guid));
            pooledLive += livePerPool.Sum(g => g.Count());
            foreach (IGrouping<uint, GameObject> group in livePerPool)
            {
                PoolDefinition pool = objects.Pools.Find(group.Key)!;
                Assert.True(group.Count() <= pool.MaxLimit, $"pool {pool.Id} '{pool.Description}' has {group.Count()} live members, limit {pool.MaxLimit}");
                poolsChecked++;
                if (pool.Mother != 0 && objects.Pools.Find(pool.Mother) is { } mother)
                {
                    int liveChildren = livePerPool.Count(g => objects.Pools.Find(g.Key)?.Mother == mother.Id);
                    Assert.True(liveChildren <= mother.MaxLimit, $"mother pool {mother.Id} '{mother.Description}' has {liveChildren} live child pools, limit {mother.MaxLimit}");
                }
            }
        }

        output.WriteLine($"live database game objects on maps 0 and 1: {totalLive} ({pooledLive} of them pooled, {poolsChecked} pools with a live member); live creatures {totalCreatures}");
        output.WriteLine("zone | mining nodes | herb nodes | chests");
        foreach ((uint zone, string name) in Zones.Concat([(uint.MaxValue, "Eastern Kingdoms (map 0)"), (uint.MaxValue - 1, "Kalimdor (map 1)")]))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name} | {perZone.GetValueOrDefault((zone, "mining"))} | {perZone.GetValueOrDefault((zone, "herb"))} | {perZone.GetValueOrDefault((zone, "chest"))}"));
        }
    }

    /// <summary>Mining node or herb (a chest whose lock needs the skill), a chest by name, or null.</summary>
    private static string? Kind(GameObjectContent content, GameObjectTemplate template)
    {
        if (template.Type != (uint)GameObjectType.Chest)
        {
            return null;
        }

        if (content.FindLock(template.GetData(0)) is { } lockEntry)
        {
            for (int i = 0; i < lockEntry.Types.Count; i++)
            {
                if (lockEntry.Types[i] == LockKeySkill && lockEntry.Indexes[i] == LockTypeMining)
                {
                    return "mining";
                }

                if (lockEntry.Types[i] == LockKeySkill && lockEntry.Indexes[i] == LockTypeHerbalism)
                {
                    return "herb";
                }
            }
        }

        return template.Name.Contains("Chest", StringComparison.OrdinalIgnoreCase) ? "chest" : null;
    }

    private sealed class ScratchWorldFactAttribute : FactAttribute
    {
        public ScratchWorldFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            {
                Skip = $"Set {Variable} to a scratch SQLite world built from the classic-db z2815 dump to measure the pools.";
            }
        }
    }

    private sealed class NullSaves : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

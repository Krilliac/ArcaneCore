using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Pools;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Pools;

/// <summary>
/// The whole-world pool audit over a scratch world built from the real classic-db dump (<c>ARCANECORE_TEST_POOLS_WORLD_DB</c>, as
/// <see cref="PoolScratchWorldProbeTests"/>; skipped without it): every map with a spawn is created, every grid holding a spawn loaded, and
/// <see cref="PoolAudit"/> checks every creature and game object pool against the spawns in the world after the load and after each of
/// <see cref="Rotations"/> rotations (every member out reaches its trigger, as a gathered node or a respawn time does).
/// </summary>
public sealed class PoolWorldAuditTests(ITestOutputHelper output)
{
    private const int Rotations = 25;

    [ScratchWorldFact]
    public async Task ScratchWorld_EveryPoolOfEveryMap_HoldsItsLimit_AfterLoadAndAfterRotations()
    {
        string path = Environment.GetEnvironmentVariable(PoolScratchWorldProbeTests.Variable)!;
        Assert.True(File.Exists(path), $"{PoolScratchWorldProbeTests.Variable} names {path}, which does not exist");
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Mode=ReadOnly;Pooling=False").Options;
        GameObjectContent objects;
        CreatureContent creatures;
        await using (var db = new WorldDbContext(options))
        {
            objects = await new EfGameObjectDataStore(db).LoadAsync();
            creatures = await new EfCreatureDataStore(db).LoadAsync();
        }

        uint[] maps = [.. objects.Spawns.Select(s => s.MapId).Concat(Enumerable.Range(0, 1000).Select(i => (uint)i).Where(m => creatures.GetSpawns(m).Count > 0)).Distinct().Order()];
        var issues = new List<string>();
        int pools = 0, rolled = 0, firstLive = 0, lastLive = 0;
        foreach (uint mapId in maps)
        {
            using var world = new WorldRuntime(new WorldRuntimeOptions(), new NullSaves(), NullLogger<WorldRuntime>.Instance);
            Map map = world.GetMap(mapId);
            var objectSystem = new GameObjectMapSystem(map, objects) { Random = new Random(20261010) };
            map.AddUpdater(objectSystem);
            var creatureSystem = new CreatureMapSystem(map, creatures, random: new Random(20261010));
            map.AddUpdater(creatureSystem);
            foreach (GameObjectSpawn spawn in objects.GetSpawns(mapId))
            {
                map.Grids.LoadGridsAround(spawn.X, spawn.Y, 0f);
            }

            foreach (CreatureSpawn spawn in creatures.GetSpawns(mapId))
            {
                map.Grids.LoadGridsAround(spawn.X, spawn.Y, 0f);
            }

            for (int round = 0; round <= Rotations; round++)
            {
                if (round > 0)
                {
                    rolled += objectSystem.RotatePools() + creatureSystem.RotatePools();
                }

                foreach (PoolAuditReport report in new[] { objectSystem.AuditPools(), creatureSystem.AuditPools() })
                {
                    if (round == 0)
                    {
                        pools += report.PoolsChecked;
                        firstLive += report.LiveMembers;
                    }
                    else if (round == Rotations)
                    {
                        lastLive += report.LiveMembers;
                    }

                    issues.AddRange(report.Issues.Select(i => string.Create(CultureInfo.InvariantCulture, $"map {mapId} round {round}: {i.Problem}")));
                }
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{maps.Length} maps, {pools} pools audited; {firstLive} pooled spawns in the world after load, {lastLive} after {Rotations} rotations ({rolled} rolls); {issues.Count} issue(s)"));
        foreach (string issue in issues.Take(50))
        {
            output.WriteLine(issue);
        }

        Assert.Empty(issues);
    }

    private sealed class ScratchWorldFactAttribute : FactAttribute
    {
        public ScratchWorldFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PoolScratchWorldProbeTests.Variable)))
            {
                Skip = $"Set {PoolScratchWorldProbeTests.Variable} to a scratch SQLite world built from the classic-db z2815 dump to audit the pools.";
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

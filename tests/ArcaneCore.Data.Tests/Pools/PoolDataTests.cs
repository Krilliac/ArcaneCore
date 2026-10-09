using System.Globalization;
using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Tests.WorldState;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.Pools;
using ArcaneCore.Data.World.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Pools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Pools;

/// <summary>
/// <see cref="PoolDataModule"/> (world 46) and <see cref="PoolDumpImporter"/>: the six cmangos pool tables. <see cref="Excerpt"/> holds
/// classic-db z2815 rows as the dump has them (the dump is GPL data and is not committed; these rows are fixtures, as the other real-row tests
/// keep theirs).
/// </summary>
public sealed class PoolDataTests : IAsyncLifetime
{
    /// <summary>
    /// Pool 2000 (Badlands multinode, max_limit 1) with two of its five child pools (3027, 3028) and their nodes; the Thuros Lightfingers
    /// entry pool 1069 with two of his eight spawns; pool 8648 (Plaguebloom, no template) with one spawn; pool 50003 (the largest entry).
    /// </summary>
    public const string Excerpt = """
        INSERT INTO `creature` (`guid`,`id`,`map`,`spawnMask`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecsmin`,`spawntimesecsmax`,`spawndist`,`MovementType`) VALUES
        (81107,61,0,1,-9303.19,-292.231,73.3,0,5400,9000,0,2),(134007,61,0,1,-9326.41,543.283,53.2,0,5400,9000,0,0);
        INSERT INTO `gameobject` (`guid`,`id`,`map`,`spawnMask`,`position_x`,`position_y`,`position_z`,`orientation`,`rotation0`,`rotation1`,`rotation2`,`rotation3`,`spawntimesecsmin`,`spawntimesecsmax`) VALUES
        (70976,2040,0,1,-7005,-3864,250,0,0,0,0,0,300,900),(70977,1734,0,1,-7005,-3864,250,0,0,0,0,0,300,900),(70979,2047,0,1,-7005,-3864,250,0,0,0,0,0,300,900),
        (71026,2040,0,1,-6978,-4089,250,0,0,0,0,0,300,900),(71027,1734,0,1,-6978,-4089,250,0,0,0,0,0,300,900),(71029,2047,0,1,-6978,-4089,250,0,0,0,0,0,300,900),
        (19897,176587,0,1,2300,-5000,80,0,0,0,0,0,300,300);
        INSERT INTO `pool_template` (`entry`,`max_limit`,`description`) VALUES
        (1069,1,'Thuros Lightfingers (61)'),(2000,1,'Mineral nodes - Badlands - multinodes subzone 2'),
        (3027,1,'Mineral nodes - Badlands - multinodes subzone 2'),(3028,1,'Mineral nodes - Badlands - multinodes subzone 2'),(50003,1,'Last');
        INSERT INTO `pool_creature_template` (`id`,`pool_entry`,`chance`,`description`) VALUES (61,1069,0,'Thuros Lightfingers (61)');
        INSERT INTO `pool_gameobject` (`guid`,`pool_entry`,`chance`,`description`) VALUES
        (70976,3027,0,'Badlands - Mithril Deposit'),(70977,3027,20,'Badlands - Gold Vein'),(70979,3027,20,'Badlands - Truesilver Deposit'),
        (71026,3028,0,'Badlands - Mithril Deposit'),(71027,3028,20,'Badlands - Gold Vein'),(71029,3028,20,'Badlands - Truesilver Deposit'),
        (19897,8648,0,'Plaguebloom - Eastern Plaguelands - subzone 1');
        INSERT INTO `pool_pool` (`pool_id`,`mother_pool`,`chance`,`description`) VALUES
        (3027,2000,0,'Mineral nodes - Badlands - multinodes subzone 2'),(3028,2000,0,'Mineral nodes - Badlands - multinodes subzone 2');
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static PoolDumpImporter Read(string dump)
    {
        var importer = new PoolDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void WorldStep_IsTheSixPoolTables_AfterTheSpawnGroups()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == PoolDataModule.Version);
        Assert.Equal(
            ["pool_template", "pool_creature", "pool_creature_template", "pool_gameobject", "pool_gameobject_template", "pool_pool"],
            step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(PoolDataModule.Version > SpawnGroupDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is PoolDataModule);
    }

    [Fact]
    public void TheExcerpt_IsReadByColumnName()
    {
        PoolImportReport report = Read(Excerpt).BuildReport();
        Assert.Equal((5, 0, 1, 7, 0, 2, 0), (report.Templates, report.Creatures, report.CreatureTemplates, report.GameObjects, report.GameObjectTemplates, report.PoolPools, report.RowsOutsidePatch));
        PoolDumpImporter importer = Read(Excerpt);
        Assert.Equal(20f, importer.GameObjectSnapshot().Single(r => r.Guid == 70977).Chance);
        Assert.Equal(2000u, importer.PoolSnapshot().Single(r => r.PoolId == 3027).MotherPool);
        Assert.Equal("Thuros Lightfingers (61)", importer.TemplateSnapshot().Single(r => r.Entry == 1069).Description);
    }

    [Fact]
    public void AVmangosLayout_KeepsOnlyTheRowsOfPatch10()
    {
        // vmangos pool tables carry patch_min/patch_max (PoolManager::LoadFromDB: WHERE 10 BETWEEN patch_min AND patch_max).
        PoolDumpImporter importer = Read("""
            INSERT INTO `pool_template` (`entry`,`max_limit`,`description`,`patch_min`,`patch_max`) VALUES (1,1,'old',0,4),(2,1,'kept',0,10);
            INSERT INTO `pool_gameobject` (`guid`,`pool_entry`,`chance`,`description`,`patch_min`,`patch_max`) VALUES (5,2,0,'a',0,10),(6,2,0,'b',5,10),(7,2,0,'c',11,11);
            """);
        PoolImportReport report = importer.BuildReport();
        Assert.Equal((1, 2, 2), (report.Templates, report.GameObjects, report.RowsOutsidePatch));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Rows_RoundTripThroughTheStores_AsCatalogsPerKind(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureSpawnRow>().AddRange(
                new CreatureSpawnRow { Guid = 81107, Entry = 61, MapId = 0 }, new CreatureSpawnRow { Guid = 134007, Entry = 61, MapId = 0 });
            db.Set<GameObjectSpawnRow>().AddRange(
                new uint[] { 70976, 70977, 70979, 71026, 71027, 71029, 19897 }.Select(g => new GameObjectSpawnRow { Guid = g, Entry = g == 19897 ? 176587u : 2040u, MapId = 0 }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await Read(Excerpt).WriteAsync(db, replace: false);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        GameObjectContent objects = await new EfGameObjectDataStore(read).LoadAsync();
        CreatureContent creatures = await new EfCreatureDataStore(read).LoadAsync();

        PoolDefinition mother = objects.Pools.Find(2000)!;
        Assert.True(mother.AutoSpawn);
        Assert.Equal([3027u, 3028u], mother.Children.Select(c => c.Id).Order());
        Assert.Equal(3027u, objects.Pools.PoolOf(70979));
        Assert.Equal([70977u, 70979u], objects.Pools.Find(3027)!.ExplicitlyChanced.Select(m => m.Id).Order());
        Assert.True(objects.Pools.IsPooled(19897));
        Assert.False(objects.Pools.Find(8648)!.AutoSpawn); // no template
        Assert.Equal(1069u, creatures.Pools.PoolOf(134007)); // pool_creature_template: every spawn of entry 61
        Assert.Equal(1069u, creatures.Pools.PoolOf(81107));
        Assert.Equal(0u, objects.Pools.PoolOf(81107)); // the guid spaces are per kind
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Refresh_FillsEmptyTables_OnlyWithTheWorldsOwnSpawns_AndASecondRunChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<GameObjectSpawnRow>().AddRange(
                new GameObjectSpawnRow { Guid = 70976, Entry = 2040, MapId = 0 },  // the dump's entry: kept
                new GameObjectSpawnRow { Guid = 70977, Entry = 1734, MapId = 0 },
                new GameObjectSpawnRow { Guid = 71026, Entry = 999, MapId = 0 }); // another object under a dump guid: left out
            await db.SaveChangesAsync();
        }

        PoolFillReport first;
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            first = await Read(Excerpt).FillAsync(db);
        }

        Assert.Equal((5, 0, 1, 2, 0, 2, 5), (first.Templates!.Value, first.Creatures, first.CreatureTemplates, first.GameObjects, first.GameObjectTemplates, first.PoolPools, first.SkippedMembers));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Null((await Read(Excerpt).FillAsync(db)).Templates);
        }

        await using WorldDbContext check = TestContexts.Create<WorldDbContext>(cs);
        Assert.Equal([70976u, 70977u], (await check.Set<PoolGameObjectRow>().Select(r => r.Guid).ToListAsync()).Order());
        Assert.Equal(5, await check.Set<PoolTemplateRow>().CountAsync());
    }

    /// <summary>
    /// The whole z2815 dump (env-gated): the six tables' rows, and what cmangos PoolManager::LoadFromDB keeps of them for the game objects:
    /// 191 Plaguebloom and 7 ore spawns name pools that have no template (they never spawn), and no row is otherwise dropped.
    /// </summary>
    [ClassicDbDumpFact]
    public void ClassicDb_z2815_PoolRows()
    {
        var importer = new PoolDumpImporter();
        using (var reader = new StreamReader(new GZipStream(File.OpenRead(ClassicDbDumpFactAttribute.DumpPath), CompressionMode.Decompress)))
        {
            importer.Read(reader);
        }

        PoolImportReport report = importer.BuildReport();
        Assert.Equal((4290, 168, 208, 20853, 42, 2986), (report.Templates, report.Creatures, report.CreatureTemplates, report.GameObjects, report.GameObjectTemplates, report.PoolPools));

        var objectSpawns = new Dictionary<uint, (uint, uint)>();
        using (var reader = new StreamReader(new GZipStream(File.OpenRead(ClassicDbDumpFactAttribute.DumpPath), CompressionMode.Decompress)))
        {
            foreach (object item in new MySqlDumpReader(reader).Read())
            {
                if (item is DumpRow { Table: "gameobject" } row && row.TryGet(out string? guid, "guid") && row.TryGet(out string? id, "id") && row.TryGet(out string? map, "map"))
                {
                    objectSpawns[uint.Parse(guid!, CultureInfo.InvariantCulture)] = (uint.Parse(id!, CultureInfo.InvariantCulture), uint.Parse(map!, CultureInfo.InvariantCulture));
                }
            }
        }

        PoolCatalog catalog = PoolCatalog.Build(
            importer.TemplateSnapshot().Select(t => new PoolTemplateData(t.Entry, t.MaxLimit, t.Description)),
            importer.GameObjectSnapshot().Select(r => new PoolSpawnLink(r.Guid, r.PoolEntry, r.Chance)),
            importer.GameObjectTemplateSnapshot().Select(r => new PoolSpawnLink(r.Id, r.PoolEntry, r.Chance)),
            importer.PoolSnapshot().Select(r => new PoolPoolLink(r.PoolId, r.MotherPool, r.Chance)),
            objectSpawns);
        Assert.Equal(20853 + 821, objectSpawns.Keys.Count(catalog.IsPooled)); // 821 spawns join through pool_gameobject_template
        Assert.Equal(198, catalog.Pools.Where(p => !p.HasTemplate).Sum(p => p.Spawns.Count()));
        Assert.Equal(1215, objectSpawns.Count(s => s.Value.Item1 == 0 && catalog.IsPooled(s.Key))); // the entry-0 pooled objects
    }
}

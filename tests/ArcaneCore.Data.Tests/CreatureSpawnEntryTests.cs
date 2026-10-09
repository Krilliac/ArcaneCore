using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <c>creature_spawn_entry</c> (<see cref="CreatureSpawnEntryDataModule"/>): the creature entries a spawn row can become. cmangos-classic
/// keys them by spawn guid and leaves <c>creature.id</c> at 0 (ObjectMgr.cpp:1826-1869, Creature::LoadFromDB); vmangos spells them
/// <c>creature.id</c>, <c>id2</c> ... <c>id5</c> (CreatureData::ChooseCreatureId). classic-db has 4,863 such rows for 2,280 spawns, and
/// 2,234 of its 66,310 spawns (3.4 percent) have <c>id = 0</c> and get their entry from these rows (566 of the other 568 get it from spawn groups). Rows here are hand-written; no GPL rows are copied.
/// </summary>
public sealed class CreatureSpawnEntryTests : IAsyncLifetime
{
    private const string CMangosDump = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`) VALUES (920201,'Variant A','',5,5,32),(920202,'Variant B','',6,6,32),(920203,'Fixed','',7,7,32);
        INSERT INTO `creature` (`guid`,`id`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecsmin`,`spawntimesecsmax`,`spawndist`,`MovementType`) VALUES
        (920201,0,0,10,20,30,1.5,300,300,0,0),(920202,920203,0,50,60,30,1.5,300,300,0,0);
        INSERT INTO `creature_spawn_entry` (`guid`,`entry`) VALUES (920201,920201),(920201,920202),(920201,920202);
        """;

    private const string VMangosDump = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`faction`) VALUES (920210,0,'V One','',5,5,14),(920211,0,'V Two','',5,5,14);
        INSERT INTO `creature` (`guid`,`id`,`id2`,`id3`,`id4`,`id5`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecsmin`,`spawntimesecsmax`,`wander_distance`,`movement_type`,`patch_min`,`patch_max`) VALUES
        (920210,920210,920211,0,0,0,0,1,2,3,0,60,60,0,0,0,10),(920211,920211,0,0,0,0,0,1,2,3,0,60,60,0,0,0,10);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static CreatureDumpImporter Read(string dump)
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void WorldStep_IsTheSpawnEntryTable()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureSpawnEntryDataModule.Version);
        Assert.Equal(["creature_spawn_entry"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Empty(step.Changes.OfType<AddColumnChange>());
        Assert.True(CreatureSpawnEntryDataModule.Version > CreatureMovementTemplateDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureSpawnEntryDataModule);
    }

    [Fact]
    public void CMangosRows_AreKept_AsADistinctSetPerSpawn()
    {
        CreatureDumpImporter importer = Read(CMangosDump);

        CreatureSpawnEntryRow[] rows = [.. importer.SpawnEntrySnapshot().OrderBy(r => r.SpawnGuid).ThenBy(r => r.Entry)];

        // The repeated (920201, 920202) row is one row: the key is (guid, entry).
        Assert.Equal([(920201u, 920201u), (920201u, 920202u)], rows.Select(r => (r.SpawnGuid, r.Entry)));
        Assert.Equal(2, importer.BuildReport().SpawnEntries);
        Assert.Equal(0u, importer.Snapshot().Spawns.Single(s => s.Guid == 920201).Entry); // the placeholder stays; the list resolves it
    }

    [Fact]
    public void VMangosIds2To5_BecomeEntryRows_AndAreNoLongerReportedAsDropped()
    {
        CreatureDumpImporter importer = Read(VMangosDump);

        // id (the entry of the guid) and the non-zero id2..id5 form the list of a spawn that has alternatives; a spawn with only id has none.
        Assert.Equal([(920210u, 920210u), (920210u, 920211u)], importer.SpawnEntrySnapshot().OrderBy(r => r.Entry).Select(r => (r.SpawnGuid, r.Entry)));
        Assert.DoesNotContain(importer.BuildReport().Warnings, w => w.Contains("id2", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Entries_RoundTripThroughTheStore_InInsertionIndependentOrder(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            CreatureImportReport report = await Read(CMangosDump).WriteAsync(db, replace: false);
            Assert.Equal(2, report.SpawnEntries);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        CreatureContent content = await new EfCreatureDataStore(read).LoadAsync();

        Assert.Equal([920201u, 920202u], content.GetSpawnEntries(920201));
        Assert.Empty(content.GetSpawnEntries(920202)); // a spawn with a fixed id and no rows
        Assert.Empty(content.GetSpawnEntries(1));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AReplaceImport_RemovesThePreviousEntries(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Read(CMangosDump).WriteAsync(db, replace: false);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await Read("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (5,'Only',1,1);").WriteAsync(db, replace: true);
        }

        await using WorldDbContext check = TestContexts.Create<WorldDbContext>(cs);
        Assert.Equal(0, await check.Set<CreatureSpawnEntryRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromThePreviousVersion_AddsTheTable_KeepsTheRows_AndRerunsCleanly(DatabaseProvider provider)
    {
        // MariaDB commits DDL implicitly, PostgreSQL DDL is transactional: either way a second EnsureAsync over the finished schema changes nothing.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int previous = CreatureSpawnEntryDataModule.Version - 1;
        SchemaDefinition prefix = new()
        {
            Component = WorldDbContext.Schema.Component,
            CurrentVersion = previous,
            Version1Tables = WorldDbContext.Schema.Version1Tables,
            Steps = [.. WorldDbContext.Schema.Steps.Where(s => s.Version <= previous)],
        };

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, prefix);
            db.Set<CreatureSpawnRow>().Add(new CreatureSpawnRow { Guid = 5, Entry = 0, MapId = 0 });
            await db.SaveChangesAsync();
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal(0u, (await db.Set<CreatureSpawnRow>().SingleAsync()).Entry);
            db.Set<CreatureSpawnEntryRow>().Add(new CreatureSpawnEntryRow { SpawnGuid = 5, Entry = 100 + (uint)pass });
            await db.SaveChangesAsync();
            Assert.Equal(1 + pass, await db.Set<CreatureSpawnEntryRow>().CountAsync());
        }
    }

    /// <summary>
    /// With <c>ARCANECORE_CLASSICDB_DUMP</c> set to the classic-db z2815 dump this imports the real rows and checks the figures the design was
    /// sized on (skipped when the variable is unset; fails when it is set and the file is missing).
    /// </summary>
    [ClassicDbDumpFact]
    public async Task RealClassicDbDump_TheEntryZeroSpawnsAreResolvedByEntryRowsOrSpawnGroups()
    {
        string path = Environment.GetEnvironmentVariable(ClassicDbDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(path), $"{ClassicDbDumpFactAttribute.Variable} is set but '{path}' does not exist");
        var importer = new CreatureDumpImporter();
        await using FileStream file = File.OpenRead(path);
        await using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        importer.Read(reader);

        IReadOnlyCollection<CreatureSpawnEntryRow> rows = importer.SpawnEntrySnapshot();
        Assert.Equal(4863, rows.Count);
        HashSet<uint> withEntries = [.. rows.Select(r => r.SpawnGuid)];
        Assert.Equal(2280, withEntries.Count);
        CreatureSpawnRow[] placeholders = [.. importer.Snapshot().Spawns.Where(s => s.Entry == 0)];

        // 2,802 spawns have id 0: 2,234 are resolved by creature_spawn_entry rows, 566 of the other 568 by cmangos spawn groups
        // (spawn_group_entry, world 43; SpawnGroupDataTests), and two have no entry in cmangos either.
        Assert.Equal(2802, placeholders.Length);
        Assert.Equal(2234, placeholders.Count(s => withEntries.Contains(s.Guid)));
    }

    private sealed class ClassicDbDumpFactAttribute : FactAttribute
    {
        public const string Variable = "ARCANECORE_CLASSICDB_DUMP";

        public ClassicDbDumpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            {
                Skip = $"Set {Variable} to the classic-db z2815 dump (.sql or .sql.gz) to import the real data.";
            }
        }
    }
}

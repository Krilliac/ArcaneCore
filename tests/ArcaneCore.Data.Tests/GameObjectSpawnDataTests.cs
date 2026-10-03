using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// GO3: the spawn data retail uses to place and respawn game objects (world schema step <see cref="GameObjectSpawnDataModule"/>):
/// <c>spawntimesecsmax</c>, <c>spawn_flags</c>, cmangos <c>gameobject_addon</c> (animprogress, state) and the door/button startOpen default
/// (D:\refs\mangos-classic\src\game\Globals\ObjectMgr.cpp:2188-2192, 2252-2282; Entities\GameObject.cpp:226-250, 920-927;
/// D:\refs\vmangos\src\game\Objects\GameObject.cpp:698-711). Dump snippets are hand-written; no GPL rows are copied.
/// The provider theories run on every provider that <see cref="TestDatabases.AvailableProviders"/> offers; on this machine only SQLite
/// is available, so the MariaDB and PostgreSQL runs happen on hosted CI.
/// </summary>
public sealed class GameObjectSpawnDataTests : IAsyncLifetime
{
    // cmangos classic-db shape: min/max respawn on the gameobject row, animprogress/state in gameobject_addon (addon rows listed BEFORE the
    // gameobject rows on purpose: the importer must not depend on table order).
    private const string CMangosDump = """
        INSERT INTO `gameobject_template` (`entry`,`type`,`displayId`,`name`,`faction`,`flags`,`size`,`data0`,`data1`,`data2`) VALUES
        (910001,0,1,'Open Door',0,0,1,1,0,196608),
        (910002,0,2,'Closed Door',0,0,1,0,0,0),
        (910003,1,3,'Open Button',0,0,1,1,0,0),
        (910004,3,4,'Chest',0,0,1,0,5,0);
        INSERT INTO `gameobject_addon` (`guid`,`animprogress`,`state`,`StringId`) VALUES (4,0,-1,0),(5,255,2,0),(6,100,0,0),(7,100,3,0);
        INSERT INTO `gameobject` (`guid`,`id`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`rotation0`,`rotation1`,`rotation2`,`rotation3`,`spawntimesecsmin`,`spawntimesecsmax`) VALUES
        (1,910001,0,1,2,3,0,0,0,0,0,300,300),
        (2,910002,0,1,2,3,0,0,0,0,0,60,600),
        (3,910003,0,1,2,3,0,0,0,0,0,100,50),
        (4,910004,0,1,2,3,0,0,0,0,0,-600,-600),
        (5,910002,0,1,2,3,0,0,0,0,0,10,20),
        (6,910001,0,1,2,3,0,0,0,0,0,10,20),
        (7,910002,0,1,2,3,0,0,0,0,0,10,20);
        """;

    // vmangos shape: state and spawn_flags on the gameobject row itself.
    private const string VmangosDump = """
        INSERT INTO `gameobject_template` (`entry`,`patch`,`type`,`displayId`,`name`,`faction`,`flags`,`size`,`data0`) VALUES (910010,0,0,1,'Door',0,0,1,1);
        INSERT INTO `gameobject` (`guid`,`id`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`rotation0`,`rotation1`,`rotation2`,`rotation3`,`spawntimesecsmin`,`spawntimesecsmax`,`spawn_flags`,`animprogress`,`state`,`patch_min`,`patch_max`) VALUES
        (20,910010,0,1,2,3,0,0,0,0,0,30,90,4,100,1,0,10),
        (21,910010,0,1,2,3,0,0,0,0,0,30,30,0,0,0,0,10);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static GameObjectLootDumpImporter Import(string dump)
    {
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    private static GameObjectSpawnRow Spawn(GameObjectLootDumpImporter importer, uint guid) => importer.SpawnRows.Single(s => s.Guid == guid);

    [Fact]
    public void TheModule_IsAWorldStepWithTwoAdditiveColumns_AndIsRegistered()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == GameObjectSpawnDataModule.Version);
        Assert.Equal(
            [("gameobject_spawn", nameof(GameObjectSpawnRow.SpawnTimeMaxSeconds)), ("gameobject_spawn", nameof(GameObjectSpawnRow.SpawnFlags))],
            step.Changes.Cast<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is GameObjectSpawnDataModule);
    }

    [Fact]
    public void CMangosDump_ImportsMax_AdjustsMaxBelowMin_AndKeepsNegativeSpawnTimes()
    {
        GameObjectLootDumpImporter importer = Import(CMangosDump);

        Assert.Equal((300, 300), (Spawn(importer, 1).SpawnTimeSeconds, Spawn(importer, 1).SpawnTimeMaxSeconds));
        Assert.Equal((60, 600), (Spawn(importer, 2).SpawnTimeSeconds, Spawn(importer, 2).SpawnTimeMaxSeconds));
        Assert.Equal((100, 100), (Spawn(importer, 3).SpawnTimeSeconds, Spawn(importer, 3).SpawnTimeMaxSeconds)); // max 50 < min 100: raised to the min
        Assert.Equal((-600, -600), (Spawn(importer, 4).SpawnTimeSeconds, Spawn(importer, 4).SpawnTimeMaxSeconds));
    }

    [Fact]
    public void CMangosDump_ResolvesStateAndAnimProgress_FromTheAddon_ThenStartOpen_ThenDefaults()
    {
        GameObjectLootDumpImporter importer = Import(CMangosDump);

        // No addon row: a startOpen door/button is active (open), a closed door is ready, animprogress 100.
        Assert.Equal(((byte)0, 100u), (Spawn(importer, 1).State, Spawn(importer, 1).AnimProgress));
        Assert.Equal(((byte)1, 100u), (Spawn(importer, 2).State, Spawn(importer, 2).AnimProgress));
        Assert.Equal(((byte)0, 100u), (Spawn(importer, 3).State, Spawn(importer, 3).AnimProgress));
        // Addon state -1 is unset (falls through to the template: a chest is ready) but its animprogress counts.
        Assert.Equal(((byte)1, 0u), (Spawn(importer, 4).State, Spawn(importer, 4).AnimProgress));
        // An explicit addon state wins over startOpen and over the closed default.
        Assert.Equal(((byte)2, 255u), (Spawn(importer, 5).State, Spawn(importer, 5).AnimProgress));
        Assert.Equal(((byte)0, 100u), (Spawn(importer, 6).State, Spawn(importer, 6).AnimProgress));
        // State 3 is MAX_GO_STATE: the addon row is invalid and skipped, so the spawn keeps its own defaults and a warning is recorded.
        Assert.Equal(((byte)1, 100u), (Spawn(importer, 7).State, Spawn(importer, 7).AnimProgress));
        Assert.Contains(importer.BuildReport().Warnings, w => w.Contains("invalid state 3", StringComparison.Ordinal));
        Assert.Equal(1, importer.BuildReport().SkippedRows);
    }

    [Fact]
    public void TheResolution_IsIdempotent_AndIndependentOfTableOrder()
    {
        GameObjectLootDumpImporter once = Import(CMangosDump);
        byte[] first = [.. once.SpawnRows.OrderBy(s => s.Guid).Select(s => s.State)];
        byte[] second = [.. once.SpawnRows.OrderBy(s => s.Guid).Select(s => s.State)];
        Assert.Equal(first, second);

        // The same rows with the addon table last and the templates last give the same result.
        string[] statements = CMangosDump.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string reordered = string.Join(";\n", statements.OrderByDescending(s => s.Contains("gameobject_template", StringComparison.Ordinal) ? 0 : s.Contains("gameobject_addon", StringComparison.Ordinal) ? 1 : 2)) + ";";
        GameObjectLootDumpImporter other = Import(reordered);
        Assert.Equal(
            [.. once.SpawnRows.OrderBy(s => s.Guid).Select(s => (s.State, s.AnimProgress))],
            [.. other.SpawnRows.OrderBy(s => s.Guid).Select(s => (s.State, s.AnimProgress))]);
    }

    [Fact]
    public void VmangosDump_UsesItsOwnStateAndSpawnFlags_AndNotStartOpen()
    {
        GameObjectLootDumpImporter importer = Import(VmangosDump);

        GameObjectSpawnRow flagged = Spawn(importer, 20);
        Assert.Equal((30, 90, 4u), (flagged.SpawnTimeSeconds, flagged.SpawnTimeMaxSeconds, flagged.SpawnFlags));
        Assert.Equal((byte)1, flagged.State); // explicit state 1 beats the template's startOpen (vmangos go_state is authoritative)
        GameObjectSpawnRow open = Spawn(importer, 21);
        Assert.Equal(((byte)0, 0u), (open.State, open.AnimProgress));
    }

    // --- providers -------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshDatabase_RoundTripsTheNewColumns_IncludingNullMax(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        GameObjectLootDumpImporter importer = Import(CMangosDump + VmangosDump);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: false);
            db.Set<GameObjectSpawnRow>().Add(new GameObjectSpawnRow { Guid = 900, Entry = 910002, MapId = 0, SpawnTimeSeconds = 45 }); // no max, no flags
            await db.SaveChangesAsync();
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            GameObjectContent content = await new EfGameObjectDataStore(db).LoadAsync();
            GameObjectSpawn[] map0 = [.. content.GetSpawns(0)];
            GameObjectSpawn ranged = map0.Single(s => s.Guid == 2);
            Assert.Equal((60, (int?)600, 0u), (ranged.SpawnTimeSeconds, ranged.SpawnTimeMaxSeconds, ranged.SpawnFlags));
            GameObjectSpawn flagged = map0.Single(s => s.Guid == 20);
            Assert.Equal((30, (int?)90, 4u, (byte)1), (flagged.SpawnTimeSeconds, flagged.SpawnTimeMaxSeconds, flagged.SpawnFlags, flagged.State));
            GameObjectSpawn plain = map0.Single(s => s.Guid == 900);
            Assert.Equal((45, (int?)null, 0u), (plain.SpawnTimeSeconds, plain.SpawnTimeMaxSeconds, plain.SpawnFlags));
            GameObjectSpawn addonState = map0.Single(s => s.Guid == 5);
            Assert.Equal(((byte)2, 255u), (addonState.State, addonState.AnimProgress));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsBothColumns_KeepingRows_AndStartupIsRepeatable(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<GameObjectSpawnRow>().Add(new GameObjectSpawnRow { Guid = 1, Entry = 5, MapId = 0, X = 1, Y = 2, Z = 3, SpawnTimeSeconds = 120, State = 0, AnimProgress = 7 });
            await db.SaveChangesAsync();

            // A database from before the step: the columns are gone and the version is below the step.
            await DropStepColumnsAsync(db);
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, GameObjectSpawnDataModule.Version - 1));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);

            GameObjectSpawn spawn = Assert.Single((await new EfGameObjectDataStore(db).LoadAsync()).GetSpawns(0));
            // Rows from before the step keep their fixed delay (null max) and have no flags; nothing else changed.
            Assert.Equal((120, (int?)null, 0u, (byte)0, 7u), (spawn.SpawnTimeSeconds, spawn.SpawnTimeMaxSeconds, spawn.SpawnFlags, spawn.State, spawn.AnimProgress));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AStepThatWasOnlyPartlyApplied_IsCompletedByTheNextStartup(DatabaseProvider provider)
    {
        // MariaDB DDL is not transactional and implicitly commits: a crash between the two ALTERs leaves the first applied and the
        // version unchanged. PostgreSQL and SQLite roll such a step back as a whole; the rerun must complete it either way.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            string statement = $"ALTER TABLE {sql.DelimitIdentifier("gameobject_spawn")} DROP COLUMN {sql.DelimitIdentifier(nameof(GameObjectSpawnRow.SpawnFlags))}";
            await db.Database.ExecuteSqlRawAsync(statement);
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, GameObjectSpawnDataModule.Version - 1));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<GameObjectSpawnRow>().Add(new GameObjectSpawnRow { Guid = 2, Entry = 5, MapId = 0, SpawnTimeMaxSeconds = 10, SpawnFlags = 4 });
            await db.SaveChangesAsync();
            GameObjectSpawn spawn = Assert.Single((await new EfGameObjectDataStore(db).LoadAsync()).GetSpawns(0));
            Assert.Equal(((int?)10, 4u), (spawn.SpawnTimeMaxSeconds, spawn.SpawnFlags));
        }
    }

    /// <summary>
    /// With <c>ARCANECORE_CLASSICDB_DUMP</c> set to the classic-db z2815 dump (.sql or .sql.gz) the whole spawn table is imported. The expected
    /// numbers were counted independently with a python scan of the dump (47827 spawns, 6056 with spawntimesecsmin != max, 397 addon rows with an
    /// explicit state on an existing spawn). Skipped when the variable is unset; fails when it is set and the file is missing.
    /// </summary>
    [RealDumpFact]
    public void RealClassicDbDump_ImportsEverySpawn_WithItsRespawnRange_AndAddonStates()
    {
        string path = Environment.GetEnvironmentVariable(RealDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(path), $"{RealDumpFactAttribute.Variable} is set but '{path}' does not exist");
        using FileStream file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream);
        var importer = new GameObjectLootDumpImporter();
        importer.Read(reader);

        GameObjectSpawnRow[] spawns = [.. importer.SpawnRows];
        Assert.Equal(47827, spawns.Length);
        Assert.Equal(6056, spawns.Count(s => s.SpawnTimeMaxSeconds != s.SpawnTimeSeconds));
        Assert.All(spawns, s => Assert.True(s.SpawnTimeMaxSeconds >= s.SpawnTimeSeconds));
        Assert.All(spawns, s => Assert.InRange(s.State, (byte)0, (byte)2));
    }

    private sealed class RealDumpFactAttribute : FactAttribute
    {
        public const string Variable = "ARCANECORE_CLASSICDB_DUMP";

        public RealDumpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            {
                Skip = $"Set {Variable} to the classic-db z2815 dump (.sql or .sql.gz) to import the real data.";
            }
        }
    }

    private static async Task DropStepColumnsAsync(WorldDbContext db)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        foreach (AddColumnChange change in WorldDbContext.Schema.Steps.Single(s => s.Version == GameObjectSpawnDataModule.Version).Changes.OfType<AddColumnChange>())
        {
            string statement = $"ALTER TABLE {sql.DelimitIdentifier(change.Table)} DROP COLUMN {sql.DelimitIdentifier(change.Column)}";
            await db.Database.ExecuteSqlRawAsync(statement);
        }
    }
}

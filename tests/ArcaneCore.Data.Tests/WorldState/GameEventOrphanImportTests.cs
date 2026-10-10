using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.WorldData.WorldState;
using Xunit;

namespace ArcaneCore.Data.Tests.WorldState;

/// <summary>
/// Event rows whose spawn is not in the dump being read are dropped and counted (cmangos GameEventMgr::LoadFromDB skips them; classic-db's
/// <c>Updates/4498_backport_errors.sql</c> deletes exactly z2815's 33 + 1126), in both dialects, and the kept rows reach every provider the
/// CI offers (MariaDB and PostgreSQL only where their test connection strings are set).
/// </summary>
public sealed class GameEventOrphanImportTests : IAsyncLifetime
{
    // cmangos layout: 300 is a creature spawn, 301 is not; 5000 is a gameobject spawn, 5001 and 5002 are not (5002 is a creature guid
    // filed under gameobject, like z2815's 157 such rows).
    private const string CMangosDump = """
        CREATE TABLE `game_event` (`entry` mediumint unsigned NOT NULL, `schedule_type` int NOT NULL, `occurence` bigint unsigned NOT NULL, `length` bigint unsigned NOT NULL, `holiday` mediumint unsigned NOT NULL, `linkedTo` mediumint unsigned NOT NULL, `description` varchar(255));
        INSERT INTO `game_event` VALUES (9,13,524160,7200,181,0,'Noblegarden');
        CREATE TABLE `creature` (`guid` int unsigned NOT NULL, `id` mediumint unsigned NOT NULL, `map` smallint unsigned NOT NULL);
        INSERT INTO `creature` VALUES (300,1,0),(5002,15535,1);
        CREATE TABLE `gameobject` (`guid` int unsigned NOT NULL, `id` mediumint unsigned NOT NULL, `map` smallint unsigned NOT NULL);
        INSERT INTO `gameobject` VALUES (5000,113768,0);
        CREATE TABLE `game_event_creature` (`guid` int unsigned NOT NULL, `event` smallint NOT NULL);
        INSERT INTO `game_event_creature` VALUES (300,9),(301,9),(300,-9);
        CREATE TABLE `game_event_gameobject` (`guid` int unsigned NOT NULL, `event` smallint NOT NULL);
        INSERT INTO `game_event_gameobject` VALUES (5000,9),(5001,9),(5002,9);
        """;

    // vmangos layout: patch-versioned spawn tables (one guid can appear more than once) and the date-only game_event.
    private const string VMangosDump = """
        CREATE TABLE `game_event` (`entry` int unsigned NOT NULL, `start_time` timestamp NULL, `end_time` timestamp NULL, `occurence` bigint unsigned NOT NULL, `length` bigint unsigned NOT NULL, `holiday` int unsigned NOT NULL, `description` varchar(255), `hardcoded` tinyint NOT NULL, `disabled` tinyint NOT NULL, `patch_min` tinyint NOT NULL, `patch_max` tinyint NOT NULL);
        INSERT INTO `game_event` VALUES (9,'2020-04-12 00:00:00','2030-12-31 22:59:59',525600,10080,181,'Noblegarden',0,0,0,10);
        CREATE TABLE `gameobject` (`guid` int unsigned NOT NULL, `id` int unsigned NOT NULL, `map` smallint unsigned NOT NULL, `patch_min` tinyint NOT NULL, `patch_max` tinyint NOT NULL);
        INSERT INTO `gameobject` VALUES (7000,113768,0,0,6),(7000,113768,0,7,10),(7001,113768,1,0,10);
        CREATE TABLE `game_event_gameobject` (`guid` int unsigned NOT NULL, `event` smallint NOT NULL);
        INSERT INTO `game_event_gameobject` VALUES (7000,9),(7001,9),(7999,9);
        CREATE TABLE `game_event_creature` (`guid` int unsigned NOT NULL, `event` smallint NOT NULL);
        INSERT INTO `game_event_creature` VALUES (8000,9);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void CMangos_RowsWithoutASpawnInTheDump_AreDroppedAndCounted()
    {
        var importer = new GameEventDumpImporter();
        importer.Read(new StringReader(CMangosDump));

        GameEventContent content = importer.BuildContent();
        GameEventImportReport report = importer.BuildReport();

        Assert.Equal([(300u, -9), (300u, 9)], content.Creatures.Select(c => (c.Guid, (int)c.Event)));
        Assert.Equal([5000u], content.GameObjects.Select(g => g.Guid));
        Assert.Equal((2, 1), (report.Creatures, report.GameObjects));
        Assert.Equal((1, 2), (report.OrphanCreatureRows, report.OrphanGameObjectRows));
    }

    [Fact]
    public void VMangos_PatchVersionedSpawns_CountOnce_AndAnEventsOnlyTableIsKept()
    {
        // the vmangos dump has gameobject rows but no creature table: its creature event rows have nothing to be checked against
        var importer = new GameEventDumpImporter();
        importer.Read(new StringReader(VMangosDump));

        GameEventImportReport report = importer.BuildReport();

        Assert.Equal([7000u, 7001u], importer.BuildContent().GameObjects.Select(g => g.Guid));
        Assert.Equal([8000u], importer.BuildContent().Creatures.Select(c => c.Guid));
        Assert.Equal((0, 1), (report.OrphanCreatureRows, report.OrphanGameObjectRows));
    }

    [Fact]
    public void SpawnTablesFromALaterFile_StillDecideTheOrphans()
    {
        var importer = new GameEventDumpImporter();
        importer.Read(new StringReader("""
            CREATE TABLE `game_event_gameobject` (`guid` int unsigned NOT NULL, `event` smallint NOT NULL);
            INSERT INTO `game_event_gameobject` VALUES (1,9),(2,9);
            """));
        Assert.Equal(2, importer.BuildReport().GameObjects); // nothing to check against yet
        importer.Read(new StringReader("""
            CREATE TABLE `gameobject` (`guid` int unsigned NOT NULL, `id` int unsigned NOT NULL);
            INSERT INTO `gameobject` VALUES (2,1);
            """));
        Assert.Equal([2u], importer.BuildContent().GameObjects.Select(g => g.Guid));
        Assert.Equal(1, importer.BuildReport().OrphanGameObjectRows);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_StoresOnlyTheKeptRows_OnEveryProvider(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new GameEventDumpImporter();
        importer.Read(new StringReader(CMangosDump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            GameEventImportReport report = await importer.WriteAsync(db, replace: false);
            Assert.Equal((1, 2), (report.OrphanCreatureRows, report.OrphanGameObjectRows));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            GameEventContent loaded = await new EfGameEventDataStore(db).LoadAsync();
            Assert.Equal([5000u], loaded.GameObjects.Select(g => g.Guid));
            Assert.Equal(2, loaded.Creatures.Count);
            Assert.All(loaded.Creatures, c => Assert.Equal(300u, c.Guid));
        }

        // a second import with --replace semantics writes the same kept rows again
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await importer.WriteAsync(db, replace: true);
            Assert.Single((await new EfGameEventDataStore(db).LoadAsync()).GameObjects);
        }
    }
}

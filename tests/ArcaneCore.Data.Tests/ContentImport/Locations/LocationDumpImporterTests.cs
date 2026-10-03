using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Locations;

/// <summary>
/// <c>areatrigger_teleport</c> (dungeon portals) and <c>game_tele</c> (GM teleport names). Dumps
/// are hand-written; the tables and their store already existed, only the importer is new.
/// </summary>
public sealed class LocationDumpImporterTests : IAsyncLifetime
{
    // cmangos classic-db z2815: status_failed_text, required_item/quest columns the row does not carry.
    private const string CMangos = """
        CREATE TABLE `areatrigger_teleport` (`id` mediumint unsigned NOT NULL, `name` text, `required_level` tinyint unsigned NOT NULL DEFAULT '0', `required_item` mediumint unsigned, `required_item2` mediumint unsigned, `required_quest_done` mediumint unsigned, `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL, `status_failed_text` text, `condition_id` int, PRIMARY KEY (`id`));
        INSERT INTO `areatrigger_teleport` VALUES (45,'Scarlet Monastery Graveyard - Entering',20,0,0,0,189,1687.27,1050.09,18.6773,1.5708,'You must be at least level 20 to enter.',0),(78,'Deadmines - Entering',10,0,0,0,36,-16.4,-383.07,61.78,1.9,NULL,0);
        CREATE TABLE `game_tele` (`id` mediumint unsigned NOT NULL, `position_x` float NOT NULL, `position_y` float NOT NULL, `position_z` float NOT NULL, `orientation` float NOT NULL, `map` smallint unsigned NOT NULL, `name` varchar(100) NOT NULL, PRIMARY KEY (`id`));
        INSERT INTO `game_tele` VALUES (1,1400.61,-1493.87,54.7844,4.08661,0,'RuinsOfAndorhal'),(2,1728.65,-1602.25,63.429,1.6558,0,'WesternPlaguelands');
        """;

    // vmangos: `message`, `required_condition` and a patch-versioned row per id (ObjectMgr.cpp:7712-7717).
    private const string VMangos = """
        CREATE TABLE `areatrigger_teleport` (`id` mediumint unsigned NOT NULL, `patch` tinyint unsigned NOT NULL, `name` text, `required_level` tinyint unsigned NOT NULL, `required_condition` int, `message` varchar(255), `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL, PRIMARY KEY (`id`, `patch`));
        INSERT INTO `areatrigger_teleport` VALUES (45,0,'Old',20,0,'old',189,1,2,3,4),(45,4,'Patched',25,0,'Reach level 25.',189,5,6,7,8),(45,11,'TBC',30,0,'tbc',189,9,9,9,9);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void CMangosPortals_MapByName_StatusFailedTextIsTheMessage()
    {
        AreaTriggerTeleportRow portal = Import(CMangos).Snapshot().Portals.Single(p => p.Id == 45);

        Assert.Equal(("Scarlet Monastery Graveyard - Entering", "You must be at least level 20 to enter.", (byte)20, 189u),
            (portal.Name, portal.Message, portal.RequiredLevel, portal.TargetMap));
        Assert.Equal((1687.27f, 1050.09f, 18.6773f, 1.5708f), (portal.TargetPositionX, portal.TargetPositionY, portal.TargetPositionZ, portal.TargetOrientation));
        Assert.Equal(string.Empty, Import(CMangos).Snapshot().Portals.Single(p => p.Id == 78).Message);
    }

    [Fact]
    public void GameTeles_MapByName()
    {
        GameTeleRow tele = Import(CMangos).Snapshot().Teleports.Single(t => t.Id == 2);

        Assert.Equal(("WesternPlaguelands", 0u, 1728.65f, -1602.25f, 63.429f, 1.6558f),
            (tele.Name, tele.Map, tele.PositionX, tele.PositionY, tele.PositionZ, tele.Orientation));
    }

    [Fact]
    public void VMangosPortals_UseTheHighestPatchUpToTen_AndTheMessageColumn()
    {
        LocationImportReport report = Import(VMangos).BuildReport();
        AreaTriggerTeleportRow portal = Import(VMangos).Snapshot().Portals.Single();

        Assert.Equal(("Patched", "Reach level 25.", (byte)25, 5f), (portal.Name, portal.Message, portal.RequiredLevel, portal.TargetPositionX));
        Assert.Equal((1, 0, 1), (report.Portals, report.Teleports, report.SkippedRows));
    }

    [Fact]
    public void ARenamedKeyColumn_IsASchemaError()
    {
        var importer = new LocationDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `game_tele` (`tele_id`,`name`) VALUES (1,'x');")));

        Assert.Equal(("game_tele", "id"), (ex.Table, ex.Column));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_ReplacesBothTables_AndLeavesTheDbcDerivedOnes(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new GameTeleRow { Id = 99, Name = "seed" });
            db.Add(new AreaTriggerTeleportRow { Id = 99, Name = "seed" });
            db.Add(new MapTemplateRow { Entry = 0, MapName = "kept" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            LocationImportReport report = await Import(CMangos).WriteAsync(db, replace: true);

            Assert.Equal((2, 2), (report.Portals, report.Teleports));
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal([45u, 78u], (await verify.Set<AreaTriggerTeleportRow>().AsNoTracking().ToListAsync()).Select(r => r.Id).Order());
        Assert.Equal([1u, 2u], (await verify.Set<GameTeleRow>().AsNoTracking().ToListAsync()).Select(r => r.Id).Order());
        Assert.Equal("kept", (await verify.Set<MapTemplateRow>().AsNoTracking().SingleAsync()).MapName);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAKeyConflict_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new GameTeleRow { Id = 2, Name = "existing" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await Assert.ThrowsAsync<DbUpdateException>(() => Import(CMangos).WriteAsync(db, replace: false));
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal("existing", (await verify.Set<GameTeleRow>().AsNoTracking().SingleAsync()).Name);
        Assert.Equal(0, await verify.Set<AreaTriggerTeleportRow>().CountAsync());
    }

    private static LocationDumpImporter Import(string dump)
    {
        var importer = new LocationDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }
}

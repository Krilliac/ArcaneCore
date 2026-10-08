using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Battlegrounds;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Battlegrounds;

/// <summary>
/// The battleground world tables (<c>battleground_template</c>, <c>creature_battleground</c>, <c>gameobject_battleground</c>,
/// <c>battlemaster_entry</c>): the schema step, the dump import of both dialects and the store on every available provider. The dumps are
/// hand-written in the column layouts of classic-db z2815 (checked by hand: 3 templates, 24 battlemasters) and of vmangos.
/// </summary>
public sealed class BattlegroundDataTests : IAsyncLifetime
{
    // cmangos classic-db: no patch, no mark spells.
    private const string ClassicDb = """
        CREATE TABLE `battleground_template` (`id` mediumint unsigned NOT NULL, `MinPlayersPerTeam` smallint unsigned NOT NULL DEFAULT '0', `MaxPlayersPerTeam` smallint unsigned NOT NULL DEFAULT '0', `MinLvl` tinyint unsigned NOT NULL DEFAULT '0', `MaxLvl` tinyint unsigned NOT NULL DEFAULT '0', `AllianceStartLoc` mediumint unsigned NOT NULL, `HordeStartLoc` mediumint unsigned NOT NULL, `StartMaxDist` float NOT NULL DEFAULT '0', `PlayerSkinReflootId` mediumint unsigned NOT NULL DEFAULT '0', PRIMARY KEY (`id`));
        INSERT INTO `battleground_template` VALUES (1,20,40,51,60,611,610,100,1),(2,5,10,10,60,769,770,75,0),(3,8,15,20,60,890,889,75,0),(7,8,15,61,70,1103,1104,75,0);
        CREATE TABLE `battlemaster_entry` (`entry` mediumint unsigned NOT NULL DEFAULT '0', `bg_template` mediumint unsigned NOT NULL DEFAULT '0', PRIMARY KEY (`entry`));
        INSERT INTO `battlemaster_entry` VALUES (347,1),(857,3),(2302,2);
        CREATE TABLE `creature_battleground` (`guid` int unsigned NOT NULL, `event1` tinyint unsigned NOT NULL, `event2` tinyint unsigned NOT NULL, PRIMARY KEY (`guid`));
        INSERT INTO `creature_battleground` VALUES (150000,2,0),(150001,2,0),(5290011,4,4);
        CREATE TABLE `gameobject_battleground` (`guid` int unsigned NOT NULL, `event1` tinyint unsigned NOT NULL, `event2` tinyint unsigned NOT NULL, PRIMARY KEY (`guid`));
        INSERT INTO `gameobject_battleground` VALUES (90000,0,0),(90001,1,0),(90008,254,0),(4000000000,253,0);
        """;

    // vmangos: patch-versioned rows with the mark spells, snake_case start columns.
    private const string VMangos = """
        CREATE TABLE `battleground_template` (`id` mediumint unsigned NOT NULL, `patch` tinyint unsigned NOT NULL DEFAULT '0', `min_players_per_team` smallint unsigned NOT NULL DEFAULT '0', `max_players_per_team` smallint unsigned NOT NULL DEFAULT '0', `min_level` tinyint unsigned NOT NULL DEFAULT '0', `max_level` tinyint unsigned NOT NULL DEFAULT '0', `alliance_win_spell` mediumint unsigned NOT NULL DEFAULT '0', `alliance_lose_spell` mediumint unsigned NOT NULL DEFAULT '0', `horde_win_spell` mediumint unsigned NOT NULL DEFAULT '0', `horde_lose_spell` mediumint unsigned NOT NULL DEFAULT '0', `alliance_start_location` mediumint unsigned NOT NULL DEFAULT '0', `horde_start_location` mediumint unsigned NOT NULL DEFAULT '0', `player_loot_id` mediumint unsigned NOT NULL DEFAULT '0', PRIMARY KEY (`id`,`patch`));
        INSERT INTO `battleground_template` VALUES (2,0,4,10,61,61,0,0,0,0,769,770,0),(2,5,10,10,10,60,24951,24950,24951,24950,769,770,0),(2,11,10,10,10,60,1,1,1,1,769,770,0);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static BattlegroundDumpImporter Import(string dump)
    {
        var importer = new BattlegroundDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void Module_IsDiscovered_AtItsReservedVersion_AndRegistersTheStore()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.World), m => m is BattlegroundWorldDataModule);

        Assert.Equal(BattlegroundWorldDataModule.Version, module.SchemaVersion);
        Assert.Equal(
            ["battleground_template", "creature_battleground", "gameobject_battleground", "battlemaster_entry"],
            module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= BattlegroundWorldDataModule.Version);
        var services = new ServiceCollection();
        DataModules.AddServices(services, DatabaseComponent.World);
        Assert.Contains(services, d => d.ServiceType == typeof(IBattlegroundContentStore) && d.ImplementationType == typeof(EfBattlegroundContentStore));
    }

    [Fact]
    public void ClassicDb_TemplatesEventsAndBattlemasters_MapByName_AndAnUnknownTypeIsSkipped()
    {
        BattlegroundDumpImporter importer = Import(ClassicDb);
        var snapshot = importer.Snapshot();

        Assert.Equal([1u, 2u, 3u], snapshot.Templates.Select(t => t.Id));
        BattlegroundTemplateRow wsg = snapshot.Templates.Single(t => t.Id == 2);
        Assert.Equal((5u, 10u, 10u, 60u, 769u, 770u, 75f, 0u), (wsg.MinPlayersPerTeam, wsg.MaxPlayersPerTeam, wsg.MinLevel, wsg.MaxLevel, wsg.AllianceStartLoc, wsg.HordeStartLoc, wsg.StartMaxDist, wsg.AllianceWinSpell));
        Assert.Equal(1u, snapshot.Templates.Single(t => t.Id == 1).PlayerSkinRefLootId);
        Assert.Equal([(347u, 1u), (857u, 3u), (2302u, 2u)], snapshot.Masters.Select(m => (m.Entry, m.BattlegroundTemplate)));
        Assert.Equal([(150000u, (byte)2, (byte)0), (150001u, (byte)2, (byte)0), (5290011u, (byte)4, (byte)4)], snapshot.Creatures.Select(c => (c.Guid, c.Event1, c.Event2)));
        Assert.Equal(4, snapshot.Objects.Count);

        BattlegroundImportReport report = importer.BuildReport();
        Assert.Equal((3, 3, 4, 3, 1), (report.Templates, report.CreatureEvents, report.GameObjectEvents, report.Battlemasters, report.SkippedRows));
        Assert.Contains(report.Warnings, w => w.Contains("other than 1, 2 or 3", StringComparison.Ordinal));
    }

    [Fact]
    public void VMangos_TheHighestPatchNotAboveTenWins_WithItsMarkSpells()
    {
        BattlegroundDumpImporter importer = Import(VMangos);

        BattlegroundTemplateRow wsg = Assert.Single(importer.Snapshot().Templates);
        Assert.Equal((10u, 10u, 60u, 24951u, 24950u, 24951u, 24950u), (wsg.MinPlayersPerTeam, wsg.MinLevel, wsg.MaxLevel, wsg.AllianceWinSpell, wsg.AllianceLoseSpell, wsg.HordeWinSpell, wsg.HordeLoseSpell));
        Assert.Contains(importer.BuildReport().Warnings, w => w.Contains("patch above", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_RoundTripsTheFourTables_IncludingLargeGuids(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            BattlegroundImportReport report = await Import(ClassicDb).WriteAsync(db, replace: true);
            Assert.Equal(3, report.Templates);
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        BattlegroundContent content = await new EfBattlegroundContentStore(verify).LoadAsync();

        Assert.Equal([1u, 2u, 3u], content.Templates.Select(t => t.Id));
        BattlegroundTemplateRecord av = content.Templates[0];
        Assert.Equal((20u, 40u, 51u, 60u, 611u, 610u), (av.MinPlayersPerTeam, av.MaxPlayersPerTeam, av.MinLevel, av.MaxLevel, av.AllianceStartLoc, av.HordeStartLoc));
        Assert.InRange(av.StartMaxDist, 99.99f, 100.01f);
        Assert.Equal(new BattlegroundEventIndex(4000000000, 253, 0), content.GameObjectEvents[^1]);
        Assert.Equal(3, content.CreatureEvents.Count);
        Assert.Equal(new BattlemasterRecord(2302, 2), content.Battlemasters[^1]);

        // A second replace-import leaves the same rows (the tables are emptied first).
        await using (WorldDbContext again = TestContexts.Create<WorldDbContext>(connection))
        {
            await Import(ClassicDb).WriteAsync(again, replace: true);
        }

        await using WorldDbContext verify2 = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(4, (await new EfBattlegroundContentStore(verify2).LoadAsync()).GameObjectEvents.Count);
    }
}

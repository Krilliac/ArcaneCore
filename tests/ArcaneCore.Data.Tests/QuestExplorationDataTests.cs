using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The area-trigger data path (docs/areas/area-triggers.md): the entry-requirement columns of <c>areatrigger_teleport</c> and the
/// <c>areatrigger_involvedrelation</c> table, from dump import through the world schema to the content stores the world reads.
/// Dumps are hand-written in the column layouts the importer already documents (cmangos classic-db, vmangos).
/// </summary>
public sealed class QuestExplorationDataTests : IAsyncLifetime
{
    // cmangos classic-db layout: required_item/required_item2/required_quest_done, status_failed_text, condition_id.
    private const string CMangos = """
        CREATE TABLE `areatrigger_teleport` (`id` mediumint unsigned NOT NULL, `name` text, `required_level` tinyint unsigned NOT NULL DEFAULT '0', `required_item` mediumint unsigned, `required_item2` mediumint unsigned, `required_quest_done` mediumint unsigned, `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL, `status_failed_text` text, `condition_id` int, PRIMARY KEY (`id`));
        INSERT INTO `areatrigger_teleport` VALUES (2166,'Molten Core - Entering',0,16309,0,0,409,1,2,3,4,'You must have the Drakefire Amulet.',0),(1466,'Onyxia - Entering',0,0,0,6602,249,5,6,7,8,NULL,0),(4000,'Gated',0,0,0,0,36,9,9,9,9,NULL,17);
        """;

    // vmangos layout: required_condition and message.
    private const string VMangos = """
        CREATE TABLE `areatrigger_teleport` (`id` mediumint unsigned NOT NULL, `patch` tinyint unsigned NOT NULL, `name` text, `required_level` tinyint unsigned NOT NULL, `required_condition` int, `message` varchar(255), `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL, PRIMARY KEY (`id`, `patch`));
        INSERT INTO `areatrigger_teleport` VALUES (4000,0,'Gated',0,23,'Gated text',36,9,9,9,9);
        """;

    private const string Relations = """
        CREATE TABLE `areatrigger_involvedrelation` (`id` mediumint unsigned NOT NULL, `quest` mediumint unsigned NOT NULL, PRIMARY KEY (`id`));
        INSERT INTO `areatrigger_involvedrelation` VALUES (87,5441),(522,6481),(522,6482),(87,5441);
        """;

    private const string PatchedRelations = """
        CREATE TABLE `areatrigger_involvedrelation` (`id` mediumint unsigned NOT NULL, `quest` mediumint unsigned NOT NULL, `patch_min` tinyint unsigned NOT NULL, `patch_max` tinyint unsigned NOT NULL, PRIMARY KEY (`id`, `patch_min`));
        INSERT INTO `areatrigger_involvedrelation` VALUES (87,5441,0,10),(87,5442,11,10),(522,6481,0,5),(522,6482,6,10),(900,1,0,0);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Module_IsDiscovered_CreatesTheRelationTable_AndAddsTheFourColumns()
    {
        IDataModule module = Assert.Single(DataModules.All, m => m is AreaTriggerQuestWorldModule);

        Assert.Equal(DatabaseComponent.World, module.Component);
        Assert.Equal(AreaTriggerQuestWorldModule.Version, module.SchemaVersion);
        Assert.Contains(new CreateTableChange("areatrigger_involvedrelation"), module.SchemaChanges);
        Assert.Equal(
            ["RequiredItem", "RequiredItem2", "RequiredQuestDone", "RequiredCondition"],
            module.SchemaChanges.OfType<AddColumnChange>().Select(c => c.Column));
        Assert.All(module.SchemaChanges.OfType<AddColumnChange>(), c => Assert.Equal("areatrigger_teleport", c.Table));
    }

    [Fact]
    public void CMangosPortals_ReadTheItemQuestAndConditionRequirements_ByColumnName()
    {
        var portals = Import(CMangos).Snapshot().Portals.ToDictionary(p => p.Id);

        AreaTriggerTeleportRow moltenCore = portals[2166];
        Assert.Equal((16309u, 0u, 0u, 0u), (moltenCore.RequiredItem, moltenCore.RequiredItem2, moltenCore.RequiredQuestDone, moltenCore.RequiredCondition));
        Assert.Equal("You must have the Drakefire Amulet.", moltenCore.Message);
        Assert.Equal(6602u, portals[1466].RequiredQuestDone);
        Assert.Equal(17u, portals[4000].RequiredCondition);
    }

    [Fact]
    public void VMangosPortals_ReadRequiredConditionIntoTheSameColumn()
    {
        AreaTriggerTeleportRow portal = Import(VMangos).Snapshot().Portals.Single();

        Assert.Equal((23u, "Gated text"), (portal.RequiredCondition, portal.Message));
        Assert.Equal((0u, 0u, 0u), (portal.RequiredItem, portal.RequiredItem2, portal.RequiredQuestDone));
    }

    [Fact]
    public void QuestTriggerRows_AreReadByName_AndDuplicatesCollapse()
    {
        LocationDumpImporter importer = Import(Relations);

        Assert.Equal([(87u, 5441u), (522u, 6481u), (522u, 6482u)], importer.Snapshot().QuestTriggers.Select(r => (r.Id, r.Quest)).Order());
        Assert.Equal(3, importer.BuildReport().QuestTriggers);
    }

    [Fact]
    public void QuestTriggerRows_FollowThePatchRange_OfTheOtherQuestRelations()
    {
        LocationDumpImporter importer = Import(PatchedRelations);

        // patch_min <= 10 <= patch_max: (87,5441) and (522,6482); the later patch, the earlier range and the empty range are skipped.
        Assert.Equal([(87u, 5441u), (522u, 6482u)], importer.Snapshot().QuestTriggers.Select(r => (r.Id, r.Quest)).Order());
        Assert.Equal(3, importer.BuildReport().SkippedRows);
    }

    [Fact]
    public void AQuestTriggerTableWithoutItsQuestColumn_IsASchemaError()
    {
        var importer = new LocationDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `areatrigger_involvedrelation` (`id`,`entry`) VALUES (87,5441);")));

        Assert.Equal(("areatrigger_involvedrelation", "quest"), (ex.Table, ex.Column));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_StoresTheRelations_ReplaceEmptiesThemFirst_AndTheStoresReadThemBack(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new AreaTriggerQuestRow { Id = 1, Quest = 1 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var importer = Import(CMangos);
            importer.Read(new StringReader(Relations));
            LocationImportReport report = await importer.WriteAsync(db, replace: true);

            Assert.Equal((3, 3), (report.Portals, report.QuestTriggers));
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        QuestContent quests = await new EfQuestContentStore(verify).LoadAsync();
        Assert.Equal([(87u, 5441u), (522u, 6481u), (522u, 6482u)], quests.AreaTriggerQuests.Select(r => (r.Id, r.Quest)));

        MapContent maps = await new EfMapDataStore(verify).LoadAsync();
        AreaTriggerTeleport moltenCore = maps.AreaTriggerTeleports.Single(t => t.Id == 2166);
        Assert.Equal((16309u, 0u, 0u, 0u), (moltenCore.RequiredItem, moltenCore.RequiredItem2, moltenCore.RequiredQuestDone, moltenCore.RequiredCondition));
        Assert.Equal(6602u, maps.AreaTriggerTeleports.Single(t => t.Id == 1466).RequiredQuestDone);
        Assert.Equal(17u, maps.AreaTriggerTeleports.Single(t => t.Id == 4000).RequiredCondition);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAnExistingRelation_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new AreaTriggerQuestRow { Id = 87, Quest = 5441 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await Assert.ThrowsAsync<DbUpdateException>(() => Import(Relations).WriteAsync(db, replace: false));
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Single(await verify.Set<AreaTriggerQuestRow>().AsNoTracking().ToListAsync());
    }

    private static LocationDumpImporter Import(string dump)
    {
        var importer = new LocationDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }
}

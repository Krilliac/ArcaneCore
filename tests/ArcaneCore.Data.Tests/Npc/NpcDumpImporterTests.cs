using System.IO;
using ArcaneCore.Data;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Npc;

public sealed class NpcDumpImporterTests : IAsyncLifetime
{
    [Fact]
    public void VendorTemplateRowsAreRecognizedForImport()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("INSERT INTO `npc_vendor_template` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (133,2901,2,60,102,0);"));
        Assert.Contains("npc_vendor_template", NpcDumpImporter.TrackedTables);
        Assert.True(NpcDumpImporter.ReadsColumn("npc_vendor_template", "condition_id"));
        Assert.Equal(1, importer.BuildReport().VendorTemplates);
    }

    [Fact]
    public async Task VendorTemplateImportAndCreatureLinkRoundTripThroughWorldStore()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("INSERT INTO `npc_vendor_template` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (133,2901,2,60,102,0);"));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 500, VendorTemplateId = 133 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await importer.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            NpcContent content = await new EfNpcContentStore(db).LoadAsync();
            Assert.Equal(133u, content.VendorTemplateIds[500]);
            VendorItem item = Assert.Single(content.VendorTemplateItems);
            Assert.Equal(2901u, item.Item);
            Assert.Equal(2u, item.MaxCount);
            Assert.Equal(60u, item.IncrTime);
            Assert.Equal(102u, item.Slot);
        }
    }

    [Fact]
    public async Task VendorTemplateRefreshReplacesSharedRowsAndLinkButKeepsDirectRows()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        var shared = new NpcDumpImporter();
        shared.Read(new StringReader("INSERT INTO `npc_vendor_template` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (134,2902,0,0,1,0);"));
        var creatures = new CreatureDumpImporter();
        creatures.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`VendorTemplateId`) VALUES (500,134);"));
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 500, VendorTemplateId = 133 });
        db.Set<VendorItem>().Add(new VendorItem { Entry = 500, Item = 2900 });
        db.Set<VendorTemplateItemRow>().Add(new VendorTemplateItemRow { Entry = 133, Item = 2901 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, await creatures.RefreshVendorTemplateIdsAsync(db));
        Assert.Equal(1, await shared.RefreshVendorTemplatesAsync(db));
        Assert.Equal(134u, (await db.Set<CreatureTemplateRow>().SingleAsync()).VendorTemplateId);
        Assert.Equal(2900u, (await db.Set<VendorItem>().SingleAsync()).Item);
        Assert.Equal(2902u, (await db.Set<VendorTemplateItemRow>().SingleAsync()).Item);

        var partial = new CreatureDumpImporter();
        partial.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`Name`) VALUES (500,'partial');"));
        Assert.Equal(0, await partial.RefreshVendorTemplateIdsAsync(db));
        Assert.Equal(134u, (await db.Set<CreatureTemplateRow>().SingleAsync()).VendorTemplateId);

        var emptied = new NpcDumpImporter();
        emptied.Read(new StringReader("CREATE TABLE `npc_vendor_template` (`entry` int, `item` int, `maxcount` int, `incrtime` int, `slot` int, `condition_id` int);"));
        Assert.True(emptied.SawVendorTemplateTable);
        Assert.Equal(0, await emptied.RefreshVendorTemplatesAsync(db));
        Assert.Empty(await db.Set<VendorTemplateItemRow>().ToListAsync());
        Assert.Equal(2900u, (await db.Set<VendorItem>().SingleAsync()).Item);
    }

    [Fact]
    public void AVendorTemplateTableWithoutSlotOrConditionId_IsSkippedWithAWarning_NotAnAbort()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `npc_vendor` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (100,200,0,0,0,0);
            INSERT INTO `npc_vendor_template` (`entry`,`item`,`maxcount`,`incrtime`) VALUES (133,2901,0,0),(133,2902,0,0);
            INSERT INTO `npc_trainer` (`entry`,`spell`,`spellcost`,`reqskill`,`reqskillvalue`,`reqlevel`) VALUES (7,8,9,0,0,1);
            """));

        NpcImportReport report = importer.BuildReport();
        Assert.Equal((1, 1, 0), (report.Vendors, report.Trainers, report.VendorTemplates));
        Assert.False(importer.SawVendorTemplateTable);
        Assert.Equal(1, report.Skipped);
        Assert.Contains(report.Diagnostics, d => d.Contains("npc_vendor_template", StringComparison.Ordinal) && d.Contains("slot", StringComparison.Ordinal)
            && d.Contains("condition_id", StringComparison.Ordinal));
    }

    [Fact]
    public void AMalformedVendorTemplateCreateTable_IsSkippedToo_AndAnyLaterRowsOfItAreIgnored()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("""
            CREATE TABLE `npc_vendor_template` (`entry` int, `item` int, `maxcount` int, `incrtime` int);
            INSERT INTO `npc_vendor_template` VALUES (133,2901,0,0);
            INSERT INTO `npc_vendor_template` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (133,2902,0,0,1,0);
            """));

        Assert.False(importer.SawVendorTemplateTable);
        Assert.Equal(0, importer.BuildReport().VendorTemplates);
        Assert.Equal(1, importer.BuildReport().Skipped);
    }

    private const string PoiDump = """
        INSERT INTO `points_of_interest` (`entry`,`x`,`y`,`icon`,`flags`,`data`,`icon_name`) VALUES
        (1,-9465.13,64.0,7,99,0,'Goldshire Inn'),
        (2,1583.5,-4417.25,6,99,0,'Orgrimmar Bank'),
        (0,10,10,7,99,0,'Invalid');
        """;

    [Fact]
    public void PointsOfInterestRowsAreRecognisedMappedAndCounted()
    {
        Assert.Contains("points_of_interest", NpcDumpImporter.TrackedTables);
        foreach (string column in new[] { "entry", "x", "y", "icon", "flags", "data", "icon_name" })
            Assert.True(NpcDumpImporter.ReadsColumn("points_of_interest", column), column);
        Assert.False(NpcDumpImporter.ReadsColumn("points_of_interest", "unowned_column"));
        Assert.True(ContentTableSpecs.Find("points_of_interest")!.IsMapped("icon_name"));

        var importer = new NpcDumpImporter();
        importer.Read(new StringReader(PoiDump));
        NpcImportReport report = importer.BuildReport();
        Assert.Equal(2, report.PointsOfInterest);
        Assert.Equal(1, report.Skipped); // entry 0 is rejected like the other NPC tables
    }

    [Fact]
    public async Task PointsOfInterestRoundTripThroughWorldStore_KeepingNegativeCoordinates()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader(PoiDump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            NpcContent content = await new EfNpcContentStore(db).LoadAsync();
            Assert.Equal(2, content.PointsOfInterest.Count);
            PointOfInterest inn = content.PointsOfInterest.Single(p => p.Entry == 1);
            Assert.Equal(-9465.13f, inn.X);
            Assert.Equal(64.0f, inn.Y);
            Assert.Equal(7u, inn.Icon);
            Assert.Equal(99u, inn.Flags);
            Assert.Equal(0u, inn.Data);
            Assert.Equal("Goldshire Inn", inn.IconName);
            Assert.Equal(-4417.25f, content.PointsOfInterest.Single(p => p.Entry == 2).Y);
        }
    }

    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void DefaultMenuZeroAndLaterColumnLayoutsPreserveClassicSemantics()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `gossip_menu_option` (`menu_id`,`id`,`option_icon`,`option_text`,`option_id`,`npc_option_npcflag`,`action_menu_id`,`action_poi_id`,`box_coded`,`box_text`,`condition_id`) VALUES (0,0,1,'Default vendor',3,128,0,0,0,'',0);
            INSERT INTO `npc_vendor` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (100,200,0,0,0,0);
            """));
        Assert.Equal(1, importer.BuildReport().GossipOptions);
        Assert.True(NpcDumpImporter.ReadsColumn("npc_text", "text7_1"));
        Assert.True(NpcDumpImporter.ReadsColumn("npc_text", "prob7"));
        Assert.Throws<InvalidDataException>(() => importer.Read(new StringReader(
            "INSERT INTO `npc_vendor` (`entry`,`item`) VALUES (100,201);")));
    }

    [Fact]
    public async Task FailedNpcWriteRollsBackEarlierBatchesAndPreservesCallerTransaction()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Add(new VendorItem { Entry = 100, Item = 200, MaxCount = 7 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Add(new VendorItem { Entry = 101, Item = 201, MaxCount = 8 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `npc_text` (`ID`,`text0_0`,`text0_1`,`lang0`,`prob0`,`em0_0`,`em0_1`,`em0_2`,`em0_3`,`em0_4`,`em0_5`) VALUES (55,'New text','New text',0,1,0,0,0,0,0,0);
            INSERT INTO `npc_vendor` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (100,200,3,60,1,0);
            """));

        await Assert.ThrowsAsync<DbUpdateException>(() => importer.WriteAsync(db, replace: false));

        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(0, await db.Set<NpcTextRow>().CountAsync());
        Assert.Equal(2, await db.Set<VendorItem>().CountAsync());
        Assert.Equal(7u, (await db.Set<VendorItem>().SingleAsync(row => row.Entry == 100)).MaxCount);
        Assert.Equal(8u, (await db.Set<VendorItem>().SingleAsync(row => row.Entry == 101)).MaxCount);
        await transaction.CommitAsync();
    }

    [Fact]
    public void ReadsExistingNpcEntitiesByColumnName_AndReplacesDuplicateKeys()
    {
        Assert.Contains("npc_vendor", NpcDumpImporter.TrackedTables);
        Assert.True(NpcDumpImporter.ReadsColumn("npc_vendor", "condition_id"));
        Assert.False(NpcDumpImporter.ReadsColumn("npc_vendor", "unowned_column"));
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `npc_vendor` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (100,200,3,60,1,9),(100,200,4,120,2,0);
            INSERT INTO `gossip_menu` (`entry`,`text_id`,`condition_id`) VALUES (7,55,0);
            INSERT INTO `gossip_menu_option` (`menu_id`,`id`,`option_icon`,`option_text`,`option_id`,`npc_option_npcflag`,`action_menu_id`,`action_poi_id`,`box_coded`,`box_text`,`condition_id`) VALUES (7,0,1,'Hello',1,1,-1,0,0,'',0);
            INSERT INTO `npc_trainer` (`entry`,`spell`,`spellcost`,`reqskill`,`reqskillvalue`,`reqlevel`) VALUES (100,300,40,95,125,10);
            """));

        NpcImportReport report = importer.BuildReport();
        Assert.Equal(1, report.Vendors);
        Assert.Equal(1, report.Replaced);
        Assert.Equal(1, report.GossipMenus);
        Assert.Equal(1, report.GossipOptions);
        Assert.Equal(1, report.Trainers);
    }

    [Fact]
    public void RejectsInvalidNumericNpcFieldsWithoutWritingSql()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("INSERT INTO `npc_vendor` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (bad,200,3,60,1,0);"));

        NpcImportReport report = importer.BuildReport();
        Assert.Equal(0, report.Vendors);
        Assert.Equal(1, report.Skipped);
        Assert.Contains(report.Diagnostics, d => d.Contains("entry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SkipsUnsupportedTrainerRequirementsWithoutLeakingSourceText_AndKeepsAScriptedGossipOption()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `npc_trainer` (`entry`,`spell`,`spellcost`,`reqskill`,`reqskillvalue`,`reqlevel`,`ReqAbility1`,`ReqAbility2`,`ReqAbility3`,`condition_id`) VALUES (100,300,40,95,125,10,123,0,0,0);
            INSERT INTO `gossip_menu_option` (`menu_id`,`id`,`option_icon`,`option_text`,`option_broadcast_text`,`option_id`,`npc_option_npcflag`,`action_menu_id`,`action_poi_id`,`action_script_id`,`box_coded`,`box_money`,`box_text`,`box_broadcast_text`,`condition_id`) VALUES (7,0,1,'secret source phrase',0,1,1,0,0,99,0,0,'',0,0);
            INSERT INTO `gossip_menu_option` (`menu_id`,`id`,`option_icon`,`option_text`,`option_broadcast_text`,`option_id`,`npc_option_npcflag`,`action_menu_id`,`action_poi_id`,`action_script_id`,`box_coded`,`box_money`,`box_text`,`box_broadcast_text`,`condition_id`) VALUES (7,1,1,'',5,1,1,0,0,0,0,0,'',0,0);
            """));

        NpcImportReport report = importer.BuildReport();
        Assert.Equal(0, report.Trainers);
        // World schema 42 runs gossip_menu_option.action_script_id (a dbscripts_on_gossip id): the scripted option is kept with it.
        Assert.Equal(1, report.GossipOptions);
        Assert.Equal(99u, Assert.Single(importer.ScriptedOptions).ActionScriptId);
        Assert.Equal(2, report.Skipped);
        Assert.DoesNotContain(report.Diagnostics, d => d.Contains("secret source phrase", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsInlineBroadcastTextWhenUnsupportedIdIsOnlyAnAlternate()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("INSERT INTO `gossip_menu_option` (`menu_id`,`id`,`option_icon`,`option_text`,`option_broadcast_text`,`option_id`,`npc_option_npcflag`,`action_menu_id`,`action_poi_id`,`action_script_id`,`box_coded`,`box_money`,`box_text`,`box_broadcast_text`,`condition_id`) VALUES (0,0,1,'Inline text',5,1,1,0,0,0,0,0,'Inline box',6,0);"));
        Assert.Equal(1, importer.BuildReport().GossipOptions);
    }

    [Fact]
    public void RejectsMalformedUnsupportedBehaviorNumbersWithoutTruncation()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("INSERT INTO `gossip_menu_option` (`menu_id`,`id`,`option_icon`,`option_text`,`option_id`,`npc_option_npcflag`,`action_menu_id`,`action_poi_id`,`action_script_id`,`box_coded`,`box_money`,`box_text`,`condition_id`) VALUES (0,0,1,'',1,1,-1,0,-1,0,0,'',0);"));
        NpcImportReport report = importer.BuildReport();
        Assert.Equal(1, report.Skipped);
        Assert.Contains(report.Diagnostics, d => d.Contains("action_script_id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StoresNpcTextVariantsAndRejectsMissingKeys()
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("INSERT INTO `npc_text` (`ID`,`text0_0`,`text0_1`,`lang0`,`prob0`,`em0_0`,`em0_1`,`em0_2`,`em0_3`,`em0_4`,`em0_5`) VALUES (55,'Hello','Hi',0,0.5,1,2,3,4,5,6);"));
        Assert.Equal(1, importer.BuildReport().NpcTexts);

        var missing = new NpcDumpImporter();
        Assert.Throws<InvalidDataException>(() => missing.Read(new StringReader("INSERT INTO `npc_vendor` (`entry`,`item`) VALUES (1,2);")));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsNpcTextVendorStockAndTrainerRequirements()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `npc_text` (`ID`,`text0_0`,`text0_1`,`lang0`,`prob0`,`em0_0`,`em0_1`,`em0_2`,`em0_3`,`em0_4`,`em0_5`) VALUES (55,'Hello','Hi',0,0.5,1,2,3,4,5,6);
            INSERT INTO `npc_vendor` (`entry`,`item`,`maxcount`,`incrtime`,`slot`,`condition_id`) VALUES (100,200,3,60,1,9);
            INSERT INTO `npc_trainer` (`entry`,`spell`,`spellcost`,`reqskill`,`reqskillvalue`,`reqlevel`) VALUES (100,300,40,95,125,10);
            """));

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            NpcTextRow text = await db.Set<NpcTextRow>().SingleAsync();
            VendorItem vendor = await db.Set<VendorItem>().SingleAsync();
            TrainerSpell trainer = await db.Set<TrainerSpell>().SingleAsync();
            Assert.Equal("Hello", text.Text0_0);
            Assert.Equal("Hi", text.Text0_1);
            Assert.Equal(0.5f, text.Prob0);
            Assert.Equal((uint)200, vendor.Item);
            Assert.Equal(3u, vendor.MaxCount);
            Assert.Equal((uint)95, trainer.ReqSkill);
            Assert.Equal((uint)125, trainer.ReqSkillValue);
            Assert.Equal((uint)10, trainer.ReqLevel);
        }
    }
}

using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.ItemsQuests;

/// <summary>
/// The item and quest importer: cmangos classic-db z2815 and vmangos layouts into the existing
/// item_template / quest_template / quest relation / playercreateinfo_item tables. Dumps are
/// hand-written; nothing is copied from either project's data.
/// </summary>
public sealed class ItemQuestDumpImporterTests : IAsyncLifetime
{
    // cmangos z2815 item_template column names (display id, stat_type1, RangedModRange, itemset, LanguageID, area, Map, ...).
    private const string CMangosItems = """
        CREATE TABLE `item_template` (`entry` mediumint unsigned NOT NULL, `class` tinyint, `subclass` tinyint, `name` varchar(255), `displayid` mediumint, `Quality` tinyint, `Flags` int, `BuyCount` tinyint, `BuyPrice` int, `SellPrice` int, `InventoryType` tinyint, `AllowableClass` mediumint, `AllowableRace` mediumint, `ItemLevel` tinyint, `RequiredLevel` tinyint, `requiredspell` mediumint, `requiredhonorrank` mediumint, `maxcount` smallint, `stackable` smallint, `stat_type1` tinyint, `stat_value1` smallint, `dmg_min1` float, `dmg_max1` float, `dmg_type1` tinyint, `armor` smallint, `holy_res` smallint, `delay` smallint, `ammo_type` tinyint, `RangedModRange` float, `spellid_1` mediumint, `spelltrigger_1` tinyint, `spellcharges_1` smallint, `spellppmRate_1` float, `spellcooldown_1` int, `spellcategory_1` smallint, `spellcategorycooldown_1` int, `bonding` tinyint, `description` varchar(255), `PageText` mediumint, `LanguageID` tinyint, `PageMaterial` tinyint, `startquest` mediumint, `lockid` mediumint, `itemset` smallint, `MaxDurability` smallint, `area` mediumint, `Map` smallint, `BagFamily` mediumint, `ScriptName` varchar(64), `DisenchantID` mediumint, `FoodType` tinyint, `minMoneyLoot` int, `maxMoneyLoot` int, `Duration` int, `ExtraFlags` tinyint, PRIMARY KEY (`entry`));
        INSERT INTO `item_template` VALUES (25,2,7,'Worn Shortsword',1542,1,0,1,35,7,13,-1,-1,0,0,0,0,0,1,3,-5,2.5,5.5,0,0,0,1900,0,2.5,17,1,-1,3.5,0,0,-1,1,'It is worn.',9,3,4,77,66,7,45,12,1,8,'mystery',55,2,10,20,3600,1);
        INSERT INTO `item_template` VALUES (26,4,1,'Plain Cloth \'Tunic\'',2000,0,0,1,0,0,4,-1,-1,1,1,0,0,0,1,0,0,0,0,0,5,0,0,0,0,0,0,0,0,0,0,0,0,'',0,0,0,0,0,0,0,0,0,0,'',0,0,0,0,0,0);
        """;

    // vmangos item_template: snake_case, patch-versioned rows.
    private const string VMangosItems = """
        CREATE TABLE `item_template` (`entry` mediumint unsigned NOT NULL, `patch` tinyint NOT NULL, `class` tinyint, `subclass` tinyint, `name` varchar(255), `display_id` mediumint, `quality` tinyint, `buy_price` int, `allowable_class` mediumint, `required_skill_rank` smallint, `range_mod` float, `page_language` tinyint, `set_id` smallint, `area_bound` mediumint, `map_bound` smallint, `other_team_entry` mediumint, `spellppmrate_1` float, PRIMARY KEY (`entry`, `patch`));
        INSERT INTO `item_template` VALUES (100,0,2,1,'Old Axe',10,1,100,-1,0,0,0,0,0,0,0,0),(100,6,2,1,'Patched Axe',11,2,200,-1,300,1.5,2,9,8,7,101,4.5),(100,11,2,1,'TBC Axe',12,3,300,-1,0,0,0,0,0,0,0,0);
        """;

    // cmangos z2815 quest_template, trimmed: no RewXP column, RewMoneyMaxLevel instead.
    private const string CMangosQuests = """
        CREATE TABLE `quest_template` (`entry` mediumint unsigned NOT NULL, `Method` tinyint, `ZoneOrSort` smallint, `MinLevel` tinyint, `MaxLevel` tinyint, `QuestLevel` smallint, `Type` smallint, `RequiredClasses` smallint, `RequiredRaces` smallint, `PrevQuestId` mediumint, `NextQuestId` mediumint, `ExclusiveGroup` mediumint, `NextQuestInChain` mediumint, `SrcItemId` mediumint, `SrcItemCount` tinyint, `Title` text, `Details` text, `ReqItemId1` mediumint, `ReqItemCount1` smallint, `ReqCreatureOrGOId1` mediumint, `ReqCreatureOrGOCount1` smallint, `RewChoiceItemId1` mediumint, `RewChoiceItemCount1` smallint, `RewItemId1` mediumint, `RewItemCount1` smallint, `RewOrReqMoney` int, `RewMoneyMaxLevel` int, `RewSpell` mediumint, `RewRepFaction1` smallint, `RewRepValue1` mediumint, `StartScript` mediumint, `IncompleteEmoteDelay` int, PRIMARY KEY (`entry`));
        INSERT INTO `quest_template` VALUES (783,2,12,1,0,3,0,0,0,0,784,0,0,0,0,'A Threat Within','Go speak with <name>.',0,0,1,2,0,0,0,0,100,2410,0,47,250,0,0);
        INSERT INTO `quest_template` VALUES (784,2,12,1,0,-1,0,0,0,-783,0,0,0,0,0,'Next','',6,2,-4,1,0,0,7,1,0,0,0,0,0,0,0);
        CREATE TABLE `creature_questrelation` (`id` mediumint unsigned NOT NULL, `quest` mediumint unsigned NOT NULL, PRIMARY KEY (`id`, `quest`));
        INSERT INTO `creature_questrelation` VALUES (197,783),(197,99999),(198,784);
        CREATE TABLE `creature_involvedrelation` (`id` mediumint unsigned NOT NULL, `quest` mediumint unsigned NOT NULL, PRIMARY KEY (`id`, `quest`));
        INSERT INTO `creature_involvedrelation` VALUES (197,783),(199,784),(199,88888);
        CREATE TABLE `playercreateinfo_item` (`race` tinyint unsigned NOT NULL, `class` tinyint unsigned NOT NULL, `itemid` mediumint unsigned NOT NULL, `amount` tinyint unsigned NOT NULL DEFAULT '1', PRIMARY KEY (`race`, `class`, `itemid`));
        INSERT INTO `playercreateinfo_item` VALUES (1,1,25,1),(1,1,6948,1),(1,2,4540,4);
        """;

    private const string VMangosQuests = """
        CREATE TABLE `quest_template` (`entry` mediumint unsigned NOT NULL, `patch` tinyint NOT NULL, `Method` tinyint, `MinLevel` tinyint, `MaxLevel` tinyint, `QuestLevel` smallint, `Title` text, `RewMoneyMaxLevel` int, `RewXP` int, PRIMARY KEY (`entry`, `patch`));
        INSERT INTO `quest_template` VALUES (500,0,2,1,0,5,'Old',1000,150),(500,3,2,1,60,5,'Patched',2000,250),(500,11,2,1,70,5,'TBC',3000,350);
        CREATE TABLE `creature_questrelation` (`id` mediumint unsigned NOT NULL, `quest` mediumint unsigned NOT NULL, `patch_min` tinyint NOT NULL DEFAULT '0', `patch_max` tinyint NOT NULL DEFAULT '10', PRIMARY KEY (`id`, `quest`, `patch_min`, `patch_max`));
        INSERT INTO `creature_questrelation` VALUES (1,500,0,10),(2,500,0,4),(3,500,11,11);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void CMangosItems_MapByName_IncludingTheRenamedColumns()
    {
        ItemTemplateRow item = Import(CMangosItems).Snapshot().Items.Single(i => i.Entry == 25);

        Assert.Equal(("Worn Shortsword", 2u, 7u, 1542u, 1u), (item.Name, item.Class, item.Subclass, item.DisplayId, item.Quality));
        Assert.Equal((35u, 7u, 13u, -1, -1), (item.BuyPrice, item.SellPrice, item.InventoryType, item.AllowableClass, item.AllowableRace));
        Assert.Equal((3u, -5, 2.5f, 5.5f), (item.StatType1, item.StatValue1, item.DmgMin1, item.DmgMax1));
        Assert.Equal(1900u, item.Delay);
        Assert.Equal(2.5f, item.RangeMod);                    // RangedModRange
        Assert.Equal((17u, 1u, -1, 3.5f), (item.SpellId1, item.SpellTrigger1, item.SpellCharges1, item.SpellPpmRate1)); // spellppmRate_1
        Assert.Equal(-1, item.SpellCategoryCooldown1);
        Assert.Equal((1u, "It is worn."), (item.Bonding, item.Description));
        Assert.Equal((9u, 3u, 4u, 77u, 66u), (item.PageText, item.PageLanguage, item.PageMaterial, item.StartQuest, item.LockId)); // LanguageID
        Assert.Equal((7u, 45u, 12u, 1u, 8u), (item.SetId, item.MaxDurability, item.AreaBound, item.MapBound, item.BagFamily)); // itemset, area, Map
        Assert.Equal((55u, 2u, 10u, 20u, 3600u, 1u), (item.DisenchantId, item.FoodType, item.MinMoneyLoot, item.MaxMoneyLoot, item.Duration, item.ExtraFlags));
    }

    [Fact]
    public void ItemStrings_KeepEscapedQuotes_AndEmptyTextIsEmpty()
    {
        ItemTemplateRow item = Import(CMangosItems).Snapshot().Items.Single(i => i.Entry == 26);

        Assert.Equal("Plain Cloth 'Tunic'", item.Name);
        Assert.Equal(string.Empty, item.Description);
    }

    [Fact]
    public void ItemMaterialMinusOne_KeepsItsBits_AsCmangosStoresItInAUint32()
    {
        // classic-db has Material -1 (consumables); cmangos reads it into a uint32 and the query response sends those bits.
        var importer = new ItemQuestDumpImporter();
        importer.Read(new StringReader("INSERT INTO `item_template` (`entry`,`name`,`Material`) VALUES (5,'Potion',-1),(6,'Plate',6);"));

        Dictionary<uint, uint> materials = importer.Snapshot().Items.ToDictionary(i => i.Entry, i => i.Material);

        Assert.Equal(new Dictionary<uint, uint> { [5] = 0xFFFFFFFFu, [6] = 6 }, materials);
        Assert.DoesNotContain(importer.BuildReport().Warnings, w => w.Contains("clamp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VMangosItems_UseTheHighestPatchUpToTen()
    {
        ItemTemplateRow item = Import(VMangosItems).Snapshot().Items.Single();

        Assert.Equal(("Patched Axe", 11u, 2u, 200u), (item.Name, item.DisplayId, item.Quality, item.BuyPrice));
        Assert.Equal((300u, 1.5f, 2u, 9u, 8u, 7u), (item.RequiredSkillRank, item.RangeMod, item.PageLanguage, item.SetId, item.AreaBound, item.MapBound));
        Assert.Equal((101u, 4.5f), (item.OtherTeamEntry, item.SpellPpmRate1));
    }

    [Fact]
    public void CMangosQuests_MapByName_AndRewXpIsDerivedBecauseTheSourceHasNone()
    {
        QuestTemplate quest = Import(CMangosQuests).Snapshot().Quests.Single(q => q.Entry == 783);

        Assert.Equal(("A Threat Within", "Go speak with <name>."), (quest.Title, quest.Details));
        Assert.Equal((2, 12, 1, 3), ((int)quest.Method, quest.ZoneOrSort, (int)quest.MinLevel, quest.QuestLevel));
        Assert.Equal((784, 2u), (quest.NextQuestId, quest.ReqCreatureOrGOCount1));
        Assert.Equal((100, 2410u, 47u, 250), (quest.RewOrReqMoney, quest.RewMoneyMaxLevel, quest.RewRepFaction1, quest.RewRepValue1));
        Assert.Equal(4017u, quest.RewXP); // ceil(2410 / 0.6) at quest level 3
    }

    [Fact]
    public void QuestsKeepSignedFields_NegativeQuestLevelPrevQuestAndCreatureOrGo()
    {
        QuestTemplate quest = Import(CMangosQuests).Snapshot().Quests.Single(q => q.Entry == 784);

        Assert.Equal((-1, -783, -4), (quest.QuestLevel, quest.PrevQuestId, quest.ReqCreatureOrGOId1));
        Assert.Equal((6u, 2u, 7u), (quest.ReqItemId1, quest.ReqItemCount1, quest.RewItemId1));
    }

    [Fact]
    public void VMangosQuests_UseTheHighestPatch_AndTheirOwnRewXp()
    {
        QuestTemplate quest = Import(VMangosQuests).Snapshot().Quests.Single();

        Assert.Equal(("Patched", 250u, 2000u, 60), (quest.Title, quest.RewXP, quest.RewMoneyMaxLevel, quest.MaxLevel));
    }

    [Fact]
    public void CMangosQuests_DeriveRewXpFromRewMoneyMaxLevel_AsCmangosXpValueDoes()
    {
        // cmangos Quest::XPValue (QuestDef.cpp:171-205): full XP = RewMoneyMaxLevel / 0.6 (level 1-60),
        // /1.2 (61), /2.4 (62), /3.6 (63), /4.8 (64), /6.0 (65 and above, and -1 through the uint32 cast);
        // level 0 gives none. 1000 money: 1666.67, 833.33, 416.67, 277.78, 208.33, 166.67 -> ceil.
        string dump = """
            CREATE TABLE `quest_template` (`entry` mediumint unsigned NOT NULL, `QuestLevel` smallint, `RewMoneyMaxLevel` int);
            INSERT INTO `quest_template` VALUES (1,10,1000),(2,61,1000),(3,62,1000),(4,63,1000),(5,64,1000),(6,65,1000),(7,-1,1000),(8,0,1000),(9,10,0),(10,60,1000);
            """;
        ItemQuestDumpImporter importer = Import(dump);

        Dictionary<uint, uint> xp = importer.Snapshot().Quests.ToDictionary(q => q.Entry, q => q.RewXP);

        Assert.Equal(new Dictionary<uint, uint> { [1] = 1667, [2] = 834, [3] = 417, [4] = 278, [5] = 209, [6] = 167, [7] = 167, [8] = 0, [9] = 0, [10] = 1667 }, xp);
        Assert.Equal(8, importer.BuildReport().DerivedQuestXp);
    }

    [Fact]
    public void QuestXpSourceNone_LeavesRewXpAtZero_AndExplicitRewXpIsNeverDerived()
    {
        string cmangos = "CREATE TABLE `quest_template` (`entry` int, `QuestLevel` int, `RewMoneyMaxLevel` int);\nINSERT INTO `quest_template` VALUES (1,10,1000);";
        var none = new ItemQuestDumpImporter { QuestXp = QuestXpSource.None };
        none.Read(new StringReader(cmangos));
        Assert.Equal(0u, none.Snapshot().Quests.Single().RewXP);
        Assert.Equal(0, none.BuildReport().DerivedQuestXp);

        string vmangos = "CREATE TABLE `quest_template` (`entry` int, `patch` int, `QuestLevel` int, `RewMoneyMaxLevel` int, `RewXP` int);\nINSERT INTO `quest_template` VALUES (1,0,10,1000,0);";
        ItemQuestDumpImporter explicitXp = Import(vmangos);
        Assert.Equal(0u, explicitXp.Snapshot().Quests.Single().RewXP);
        Assert.Equal(0, explicitXp.BuildReport().DerivedQuestXp);
    }

    [Fact]
    public void QuestRelations_DropQuestsThatDoNotExist_AndKeepTheRest_WithAWarning()
    {
        ItemQuestDumpImporter importer = Import(CMangosQuests);

        var snapshot = importer.Snapshot();

        Assert.Equal([(197u, 783u), (198u, 784u)], snapshot.Starters.Select(r => (r.Id, r.Quest)).Order());
        Assert.Equal([(197u, 783u), (199u, 784u)], snapshot.Enders.Select(r => (r.Id, r.Quest)).Order());
        ItemQuestImportReport report = importer.BuildReport();
        Assert.Equal((2, 2, 2), (report.Quests, report.QuestStarters, report.QuestEnders));
        Assert.Equal(2, report.SkippedRows);
        Assert.Contains(report.Warnings, w => w.Contains("99999", StringComparison.Ordinal) && w.Contains("creature_questrelation", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("88888", StringComparison.Ordinal));
    }

    [Fact]
    public void VMangosRelations_ApplyThePatchRange()
    {
        var snapshot = Import(VMangosQuests).Snapshot();

        Assert.Equal([(1u, 500u)], snapshot.Starters.Select(r => (r.Id, r.Quest)));
    }

    [Fact]
    public void StartingItems_AreMapped()
    {
        var items = Import(CMangosQuests).Snapshot().StartingItems;

        Assert.Equal([(1, 1, 25u, 1u), (1, 1, 6948u, 1u), (1, 2, 4540u, 4u)], items.Select(i => ((int)i.Race, (int)i.Class, i.ItemId, i.Amount)).Order());
    }

    [Fact]
    public void LaterFilesReplaceEarlierRowsWithTheSameKey()
    {
        var importer = new ItemQuestDumpImporter();
        importer.Read(new StringReader(CMangosItems));
        importer.Read(new StringReader("INSERT INTO `item_template` (`entry`,`name`,`class`) VALUES (25,'Replaced',2);"));

        Assert.Equal("Replaced", importer.Snapshot().Items.Single(i => i.Entry == 25).Name);
    }

    [Fact]
    public void ReadingTablesItDoesNotKnow_IsIgnored()
    {
        var importer = new ItemQuestDumpImporter();
        importer.Read(new StringReader("CREATE TABLE `creature_template` (`Entry` int);\nINSERT INTO `creature_template` VALUES (1);"));

        ItemQuestImportReport report = importer.BuildReport();
        Assert.Equal((0, 0, 0, 0, 0), (report.Items, report.Quests, report.QuestStarters, report.QuestEnders, report.StartingItems));
    }

    [Fact]
    public void ClampedValues_AreCountedInTheWarnings()
    {
        var importer = new ItemQuestDumpImporter();
        importer.Read(new StringReader("INSERT INTO `item_template` (`entry`,`name`,`Quality`) VALUES (5,'X',999);"));

        ItemQuestImportReport report = importer.BuildReport();

        Assert.Equal(999u, importer.Snapshot().Items.Single().Quality); // fits a uint: no clamp
        Assert.DoesNotContain(report.Warnings, w => w.Contains("clamp", StringComparison.OrdinalIgnoreCase));

        var clamped = new ItemQuestDumpImporter();
        clamped.Read(new StringReader("INSERT INTO `item_template` (`entry`,`name`,`Quality`) VALUES (5,'X',-4);"));
        Assert.Contains(clamped.BuildReport().Warnings, w => w.Contains("Quality", StringComparison.Ordinal) && w.Contains("-4", StringComparison.Ordinal));
        Assert.Equal(0u, clamped.Snapshot().Items.Single().Quality);
    }

    // --- writing --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_StoresEverything_AndReplaceEmptiesTheTablesFirst(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SeedAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            ItemQuestImportReport report = await Import(CMangosItems + CMangosQuests).WriteAsync(db, replace: true);

            Assert.Equal((2, 2, 2, 2, 3), (report.Items, report.Quests, report.QuestStarters, report.QuestEnders, report.StartingItems));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal([25u, 26u], (await verify.Set<ItemTemplateRow>().AsNoTracking().ToListAsync()).Select(i => i.Entry).Order());
        Assert.Equal("A Threat Within", (await verify.Set<QuestTemplate>().AsNoTracking().SingleAsync(q => q.Entry == 783)).Title);
        Assert.Equal(2, await verify.Set<CreatureQuestStarterRow>().CountAsync());
        Assert.Equal(3, await verify.Set<PlayerCreateInfoItemRow>().CountAsync());
        Assert.False(await verify.Set<ItemTemplateRow>().AnyAsync(i => i.Entry == 7));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAKeyConflict_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SeedAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await Import(CMangosItems).WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Import(CMangosItems + CMangosQuests).WriteAsync(db, replace: false));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(0, await verify.Set<QuestTemplate>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_InsideACallerTransaction_UsesASavepoint(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SeedAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await using var caller = await db.Database.BeginTransactionAsync();
            await Import(CMangosItems).WriteAsync(db, replace: true);
            Assert.Same(caller, db.Database.CurrentTransaction);
            Assert.Equal(2, await db.Set<ItemTemplateRow>().CountAsync());
            await caller.RollbackAsync();
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal([7u], (await verify.Set<ItemTemplateRow>().AsNoTracking().ToListAsync()).Select(i => i.Entry));
    }

    private static ItemQuestDumpImporter Import(string dump)
    {
        var importer = new ItemQuestDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    private async Task<DatabaseConnectionOptions> SeedAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Add(new ItemTemplateRow { Entry = 7, Name = "Original" });
        await db.SaveChangesAsync();
        return connection;
    }
}

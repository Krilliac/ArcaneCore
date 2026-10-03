using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.SpecialLoot;
using ArcaneCore.Kernel.WorldData.Loot;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The fishing, pickpocketing and disenchant loot tables, <c>skill_fishing_base_level</c> and the creature
/// pickpocket ids (<see cref="SpecialLootDataModule"/>). Dumps are hand-written in each source's column layout
/// (cmangos classic-db <c>PickpocketLootId</c>; vmangos <c>pickpocket_loot_id</c> with patch-versioned rows); no GPL
/// rows are copied, but the shapes mirror real classic-db rows (fishing 443 referencing 11103 group 1, base levels
/// -70/-20/55/330). Every store test is a provider theory over <see cref="TestDatabases.AvailableProviders"/>; when
/// the MariaDB/PostgreSQL servers are not reachable only SQLite runs (the hosted CI runs the rest).
/// </summary>
public sealed class SpecialLootDataTests : IAsyncLifetime
{
    private const string CMangosDump = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`LootId`,`PickpocketLootId`) VALUES (910001,'Thief',0,910001),(910002,'NoPockets',0,0);
        INSERT INTO `fishing_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES
        (443,0,100,1,-11103,1,0),(443,6358,-25,0,1,1,0),(0,6303,100,0,1,1,0);
        INSERT INTO `reference_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES (11103,6362,0,1,1,1,0),(11103,13422,100,0,1,1,0);
        INSERT INTO `pickpocketing_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES (910001,2589,20,0,1,2,0),(910001,4306,-15,0,1,1,0);
        INSERT INTO `disenchant_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`) VALUES (48,14343,100,1,1,1,0),(49,20725,0.5,0,1,1,0);
        INSERT INTO `skill_fishing_base_level` (`entry`,`skill`) VALUES (1,-70),(2,-20),(3,55),(3500,330);
        """;

    private const string VmangosDump = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`level_min`,`loot_id`,`pickpocket_loot_id`) VALUES
        (910010,0,'A',1,0,5),(910010,5,'B',1,0,6),(910010,11,'C',1,0,7),(910011,0,'Gone',1,0,9),(910011,3,'Gone',1,0,0);
        INSERT INTO `fishing_loot_template` (`entry`,`item`,`ChanceOrQuestChance`,`groupid`,`mincountOrRef`,`maxcount`,`condition_id`,`patch_min`,`patch_max`) VALUES
        (3,6358,50,0,1,1,0,0,10),(3,6359,50,0,1,1,0,11,11);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    /// <summary>Recreate a database from before this step: its five tables dropped, version below it (fixed identifiers only).</summary>
    private static async Task DowngradeAsync(WorldDbContext db)
    {
        Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper sql =
            Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>(db);
        foreach (string table in SpecialLootDataModule.Tables)
        {
            string statement = $"DROP TABLE {sql.DelimitIdentifier(table)}";
            await db.Database.ExecuteSqlRawAsync(statement);
        }

        await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, SpecialLootDataModule.Version - 1));
    }

    [Fact]
    public void Module_IsDiscovered_WithFiveCreateTables_AndNoAlter()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == SpecialLootDataModule.Version);
        Assert.Equal(SpecialLootDataModule.Tables, step.Changes.Cast<CreateTableChange>().Select(c => c.Table));
        Assert.Equal(5, step.Changes.Count);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is SpecialLootDataModule);
    }

    [Fact]
    public void LootContent_FromTheOriginalConstructor_HasEmptySpecialMaps()
    {
        var content = new LootContent([], []);
        Assert.Equal((0, 0, 0, 0u), (content.FishingBaseSkillCount, content.PickpocketCreatureCount, content.FishingBaseSkill(3), content.FindPickpocketLootId(910001)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshWorldDatabase_ImportAndLoad_RoundTrip_CMangosDialect(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader(CMangosDump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            GameObjectLootImportReport report = await importer.WriteAsync(db, replace: false);
            Assert.Equal((4, 1, 9), (report.FishingBaseLevels, report.PickpocketLootIds, report.LootRows));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            LootContent loot = await new EfLootDataStore(db).LoadAsync();
            AssertCMangosContent(loot);
        }

        // A replacing import empties the five new tables too.
        var second = new GameObjectLootDumpImporter();
        second.Read(new StringReader("INSERT INTO `skill_fishing_base_level` (`entry`,`skill`) VALUES (9,130);"));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await second.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            LootContent loot = await new EfLootDataStore(db).LoadAsync();
            Assert.Equal(0, loot.RowCount);
            Assert.Equal((1, 0, 130, 0), (loot.FishingBaseSkillCount, loot.PickpocketCreatureCount, loot.FishingBaseSkill(9), loot.FishingBaseSkill(1)));
        }
    }

    private static void AssertCMangosContent(LootContent loot)
    {
        IReadOnlyList<LootStoreRow> fishing = loot.GetRows(LootTableKind.Fishing, 443);
        Assert.Equal(2, fishing.Count);
        Assert.Contains(fishing, r => r is { Item: 0, GroupId: 1, MinCountOrRef: -11103, MaxCount: 1 });
        Assert.Contains(fishing, r => r is { Item: 6358, ChanceOrQuestChance: -25f });
        Assert.Equal(6303u, Assert.Single(loot.GetRows(LootTableKind.Fishing, 0)).Item);
        Assert.Equal(2, loot.GetRows(LootTableKind.Reference, 11103).Count);
        Assert.Equal(2, loot.GetRows(LootTableKind.Pickpocketing, 910001).Count);
        Assert.Equal(14343u, Assert.Single(loot.GetRows(LootTableKind.Disenchant, 48)).Item);
        Assert.Equal(0.5f, Assert.Single(loot.GetRows(LootTableKind.Disenchant, 49)).ChanceOrQuestChance);

        // Signed base levels survive (classic-db has -70 and -20); a missing area reads 0.
        Assert.Equal((-70, -20, 55, 330, 0), (loot.FishingBaseSkill(1), loot.FishingBaseSkill(2), loot.FishingBaseSkill(3), loot.FishingBaseSkill(3500), loot.FishingBaseSkill(4)));
        Assert.Equal((910001u, 0u), (loot.FindPickpocketLootId(910001), loot.FindPickpocketLootId(910002)));
        Assert.Equal(1, loot.PickpocketCreatureCount);
        Assert.Equal(4, loot.FishingBaseSkillCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task VmangosDialect_PicksTheHighestPatchAtOrBelow10_AndFiltersLootRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader(VmangosDump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: false);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            LootContent loot = await new EfLootDataStore(db).LoadAsync();
            Assert.Equal(6u, loot.FindPickpocketLootId(910010)); // patch 5 beats 0; patch 11 is later than 1.12
            Assert.Equal(0u, loot.FindPickpocketLootId(910011)); // the patch-3 template cleared the pocket id
            IReadOnlyList<LootStoreRow> fishing = loot.GetRows(LootTableKind.Fishing, 3);
            Assert.Equal(6358u, Assert.Single(fishing).Item);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromThePreviousVersion_CreatesTheFiveTables_AndKeepsExistingData(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureLootTemplateRow>().Add(new CreatureLootTemplateRow { Entry = 1, Item = 117 });
            await db.SaveChangesAsync();
            await DowngradeAsync(db);
            Assert.False(await SchemaCatalog.TableExistsAsync(db, "fishing_loot_template"));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            foreach (string table in SpecialLootDataModule.Tables)
            {
                Assert.True(await SchemaCatalog.TableExistsAsync(db, table), table);
            }

            Assert.Equal(1, await db.Set<CreatureLootTemplateRow>().CountAsync());
            Assert.Empty(await db.Set<FishingLootTemplateRow>().ToListAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StepInterruptedAfterTheSecondTable_ResumesCleanly(DatabaseProvider provider)
    {
        // MariaDB DDL is not transactional (each CREATE commits implicitly): two tables exist after the crash and the
        // step must resume without failing on them. PostgreSQL/SQLite DDL is transactional, so nothing is left behind;
        // both end with all five tables.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await DowngradeAsync(db);
        }

        var tap = new CommandTap(sql => CommandTap.IsDdl(sql) && sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase), faultOn: 2);
        await using (WorldDbContext db = SchemaProbe.CreateWith<WorldDbContext>(cs, tap))
        {
            await Assert.ThrowsAsync<InjectedFaultException>(() => SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            foreach (string table in SpecialLootDataModule.Tables)
            {
                Assert.True(await SchemaCatalog.TableExistsAsync(db, table), table);
            }

            var importer = new GameObjectLootDumpImporter();
            importer.Read(new StringReader(CMangosDump));
            await importer.WriteAsync(db, replace: true);
        }
    }
}
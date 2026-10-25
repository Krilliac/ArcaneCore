using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <c>creature_equip_template</c> and <c>creature_template.EquipmentTemplateId</c> (<see cref="CreatureEquipmentDataModule"/>, world
/// schema 56): the importer reads both dialects, the store loads them into <see cref="CreatureContent"/>, an existing world upgrades from
/// 55 and <c>RefreshEquipmentAsync</c> backfills it. Rows are hand-written in the classic-db layout; no GPL rows are copied.
/// </summary>
public sealed class CreatureEquipmentImportTests : IAsyncLifetime
{
    private const string ClassicDb = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`,`EquipmentTemplateId`) VALUES
        (920201,'Armed Guard','',60,60,11,920201),(920202,'Bare Villager','',1,1,12,0),(920203,'Lost Equipment','',5,5,12,920299);
        INSERT INTO `creature_equip_template` (`entry`,`equipentry1`,`equipentry2`,`equipentry3`) VALUES
        (920201,1899,143,2551),(920204,2557,0,2552);
        """;

    private const string VMangos = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`display_id1`,`equipment_id`) VALUES
        (920301,0,'Armed Raider','',10,10,1,920301);
        INSERT INTO `creature_equip_template` (`entry`,`probability`,`item1`,`item2`,`item3`,`patch_min`,`patch_max`) VALUES
        (920301,25,1111,0,0,0,10),(920301,75,2222,3333,4444,0,10),(920301,100,5555,0,0,11,12);
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
    public void WorldStep_IsTheEquipmentTableAndTheTemplateColumn()
    {
        Assert.Equal(56, CreatureEquipmentDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureEquipmentDataModule.Version);
        Assert.Equal(["creature_equip_template"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        AddColumnChange column = Assert.Single(step.Changes.OfType<AddColumnChange>());
        Assert.Equal(("creature_template", nameof(CreatureTemplateRow.EquipmentTemplateId)), (column.Table, column.Column));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureEquipmentDataModule);
    }

    [Fact]
    public void Importer_ReadsTheClassicDbEquipmentIdAndTheItemEntries()
    {
        CreatureDumpImporter importer = Read(ClassicDb);

        Assert.True(importer.HasEquipmentTemplateIdColumn);
        Assert.True(importer.HasEquipTemplateTable);
        Assert.Equal(2, importer.BuildReport().Equipment);
        Assert.Equal([(920201u, 1899u, 143u, 2551u), (920204u, 2557u, 0u, 2552u)],
            importer.EquipmentSnapshot().OrderBy(e => e.Entry).Select(e => (e.Entry, e.EquipEntry1, e.EquipEntry2, e.EquipEntry3)));
        Assert.Equal([(920201u, 920201u), (920202u, 0u), (920203u, 920299u)],
            importer.Snapshot().Templates.OrderBy(t => t.Entry).Select(t => (t.Entry, t.EquipmentTemplateId)));
    }

    [Fact]
    public void Importer_ReadsTheVMangosEquipmentIdAndKeepsTheMostProbableRowInThePatch()
    {
        CreatureDumpImporter importer = Read(VMangos);

        Assert.True(importer.HasEquipmentTemplateIdColumn);
        CreatureEquipmentRow row = Assert.Single(importer.EquipmentSnapshot());
        Assert.Equal((920301u, 2222u, 3333u, 4444u), (row.Entry, row.EquipEntry1, row.EquipEntry2, row.EquipEntry3)); // 75 beats 25; the 11..12 row is another patch
        Assert.Equal(920301u, Assert.Single(importer.Snapshot().Templates).EquipmentTemplateId);
    }

    [Fact]
    public void Importer_WithoutTheTableOrTheColumn_ReportsNeither()
    {
        CreatureDumpImporter importer = Read("INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`) VALUES (920205,'Old Dump','',1,1);");

        Assert.False(importer.HasEquipmentTemplateIdColumn);
        Assert.False(importer.HasEquipTemplateTable);
        Assert.Equal(0, importer.BuildReport().Equipment);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ImportedEquipment_LoadsIntoTheCreatureContent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            CreatureImportReport report = await Read(ClassicDb).WriteAsync(db, replace: false);
            Assert.Equal(2, report.Equipment);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            Assert.Equal(2, content.EquipmentCount);
            Assert.Equal(920201u, content.FindTemplate(920201)!.EquipmentTemplateId);
            CreatureEquipment set = content.FindEquipment(920201)!;
            Assert.Equal((1899u, 143u, 2551u), (set.MainHand, set.OffHand, set.Ranged));
            Assert.Equal([1899u, 143u, 2551u], Enumerable.Range(0, 3).Select(set.ItemOf));
            Assert.Null(content.FindEquipment(0));
            Assert.Null(content.FindEquipment(920299));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromWorld55_AddsTheTableAndTheColumn_AndTheRefreshBackfillsIt(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureTemplateRow>().AddRange(
                new CreatureTemplateRow { Entry = 920201, Name = "Armed Guard" },
                new CreatureTemplateRow { Entry = 920202, Name = "Bare Villager", TrainerSpell = 9 });
            await db.SaveChangesAsync();

            // Recreate a world from before this step: neither the table nor the column, version 55.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            List<string> ddl = [$"DROP TABLE {sql.DelimitIdentifier("creature_equip_template")}"];
            foreach (AddColumnChange change in WorldDbContext.Schema.Steps.Single(s => s.Version == CreatureEquipmentDataModule.Version).Changes.OfType<AddColumnChange>())
            {
                ddl.Add($"ALTER TABLE {sql.DelimitIdentifier(change.Table)} DROP COLUMN {sql.DelimitIdentifier(change.Column)}");
            }

            foreach (string statement in ddl)
            {
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, CreatureEquipmentDataModule.Version - 1));
        }

        var first = new CommandTap();
        await using (WorldDbContext db = SchemaProbe.CreateWith<WorldDbContext>(cs, first))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        Assert.True(first.Ddl >= 2, $"the 55 -> 56 step issued {first.Ddl} DDL statement(s)");

        var second = new CommandTap();
        await using (WorldDbContext db = SchemaProbe.CreateWith<WorldDbContext>(cs, second))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        Assert.Equal(0, second.Ddl);

        // The upgraded world keeps its creatures with no equipment; the refresh then fills the table and the links.
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal(0, await db.Set<CreatureEquipmentRow>().CountAsync());
            Assert.All(await db.Set<CreatureTemplateRow>().AsNoTracking().ToListAsync(), r => Assert.Equal(0u, r.EquipmentTemplateId));

            (int rows, int links) = await Read(ClassicDb).RefreshEquipmentAsync(db);
            Assert.Equal((2, 1), (rows, links)); // 920203 is not in the world; 920202 already has 0
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Equal(920201u, (await db.Set<CreatureTemplateRow>().SingleAsync(r => r.Entry == 920201)).EquipmentTemplateId);
            Assert.Equal(9u, (await db.Set<CreatureTemplateRow>().SingleAsync(r => r.Entry == 920202)).TrainerSpell);
            Assert.Equal(2, await db.Set<CreatureEquipmentRow>().CountAsync());

            // A second refresh changes no link and rewrites the same rows.
            (int rows, int links) = await Read(ClassicDb).RefreshEquipmentAsync(db);
            Assert.Equal((2, 0), (rows, links));
        }
    }

    [Fact]
    public async Task Refresh_WithADumpThatHasNeither_LeavesTheWorldAsItIs()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<CreatureEquipmentRow>().Add(new CreatureEquipmentRow { Entry = 5, EquipEntry1 = 6 });
        db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 920205, Name = "Kept", EquipmentTemplateId = 5 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var importer = Read("INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`) VALUES (920205,'Kept','',1,1);");
        Assert.Equal((0, 0), await importer.RefreshEquipmentAsync(db));
        Assert.Equal(5u, (await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync()).EquipmentTemplateId);
        Assert.Equal(1, await db.Set<CreatureEquipmentRow>().CountAsync());
    }

    [Fact]
    public void TheSpecListsTheEquipmentTableAndColumns()
    {
        Assert.Contains(ContentTableSpecs.All, s => s.Table == "creature_equip_template");
        Assert.True(ContentTableSpecs.All.Single(s => s.Table == "creature_template").IsMapped("EquipmentTemplateId"));
    }

    /// <summary>The whole classic-db z2815 dump: 5318 equipment rows, 5298 templates that name one, and the guards' sets.</summary>
    [ClassicDbDumpFact]
    public void RealClassicDbDump_EquipsTheGuardsWithTheirSwordShieldAndRangedItems()
    {
        string path = Environment.GetEnvironmentVariable(ClassicDbDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(path), $"{ClassicDbDumpFactAttribute.Variable} is set but '{path}' does not exist");
        var importer = new CreatureDumpImporter();
        using TextReader reader = DumpFiles.OpenText(path);
        importer.Read(reader);

        Assert.Equal(5318, importer.BuildReport().Equipment);
        CreatureTemplateRow[] templates = [.. importer.Snapshot().Templates];
        Assert.Equal(5298, templates.Count(t => t.EquipmentTemplateId != 0));
        Dictionary<uint, CreatureEquipmentRow> sets = importer.EquipmentSnapshot().ToDictionary(e => e.Entry);

        // entry 68 Stormwind City Guard -> equipment 68 -> items 1899 / 143 / 2551; the values are item entries, not display ids
        Assert.Equal(68u, templates.Single(t => t.Entry == 68).EquipmentTemplateId);
        Assert.Equal((1899u, 143u, 2551u), (sets[68].EquipEntry1, sets[68].EquipEntry2, sets[68].EquipEntry3));
        Assert.Equal((2557u, 0u, 2552u), (sets[727].EquipEntry1, sets[727].EquipEntry2, sets[727].EquipEntry3));
        Assert.Equal((5289u, 0u, 2507u), (sets[3296].EquipEntry1, sets[3296].EquipEntry2, sets[3296].EquipEntry3));
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

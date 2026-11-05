using System.Data.Common;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The creature spell lists (<see cref="CreatureSpellListDataModule"/>, world schema 68): the importer reads the five cmangos tables and
/// <c>creature_template.SpellList</c>, the store builds the catalog, a bad row costs that row only, a refresh backfills an existing world, and the shipped
/// content file installs twice with the same rows. Rows are hand-written in the classic-db layout; no GPL rows are copied.
/// </summary>
public sealed class CreatureSpellListImportTests : IAsyncLifetime
{
    private const string ClassicDb = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`,`SpellList`) VALUES
        (930401,'Listed','',60,60,11,93040101),(930402,'Plain','',1,1,12,0);
        INSERT INTO `creature_spell_list_entry` (`Id`,`Name`,`ChanceSupportAction`,`ChanceRangedAttack`) VALUES
        (93040101,'Listed - test',0,100),(93040102,'',0,0);
        INSERT INTO `creature_spell_list` (`Id`,`Position`,`SpellId`,`Flags`,`CombatCondition`,`TargetId`,`ScriptId`,`Availability`,`Probability`,`InitialMin`,`InitialMax`,`RepeatMin`,`RepeatMax`,`Comments`) VALUES
        (93040101,1,133,2,-1,1,0,100,50,1000,2000,3000,4000,'a comment'),
        (93040101,2,116,0,7,100,9,50,0,0,0,5000,5000,''),
        (93040101,3,118,1,-1,1,0,100,0,0,0,0,0,''),
        (93040101,4,119,0,-1,555,0,100,0,0,0,0,0,''),
        (93040199,1,120,0,-1,1,0,100,0,0,0,0,0,'');
        INSERT INTO `creature_spell_targeting` (`Id`,`Type`,`Param1`,`Param2`,`Param3`,`UnitCondition`,`Comments`) VALUES
        (1,0,0,0,0,-1,'Hardcoded - current'),(100,1,0,0,2,-1,'Attack - random player');
        INSERT INTO `combat_condition` (`Id`,`WorldStateExpressionID`,`SelfConditionID`,`TargetConditionID`,`FriendConditionLogic`,`EnemyConditionLogic`,
        `FriendConditionID_0`,`FriendConditionID_1`,`FriendConditionOp_0`,`FriendConditionOp_1`,`FriendConditionCount_0`,`FriendConditionCount_1`,
        `EnemyConditionID_0`,`EnemyConditionID_1`,`EnemyConditionOp_0`,`EnemyConditionOp_1`,`EnemyConditionCount_0`,`EnemyConditionCount_1`) VALUES
        (7,0,0,0,0,0,0,0,0,0,0,0,8,0,5,0,0,0);
        INSERT INTO `unit_condition` (`Id`,`Flags`,`Variable_0`,`Variable_1`,`Variable_2`,`Variable_3`,`Variable_4`,`Variable_5`,`Variable_6`,`Variable_7`,
        `Op_0`,`Op_1`,`Op_2`,`Op_3`,`Op_4`,`Op_5`,`Op_6`,`Op_7`,`Value_0`,`Value_1`,`Value_2`,`Value_3`,`Value_4`,`Value_5`,`Value_6`,`Value_7`) VALUES
        (8,1,33,35,0,0,0,0,0,0,1,1,0,0,0,0,0,0,1,1,0,0,0,0,0,0);
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

    private async Task<DatabaseConnectionOptions> CleanWorldAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        return cs;
    }

    [Fact]
    public void WorldStep_IsTheFiveTablesAndTheTemplateColumn()
    {
        Assert.Equal(68, CreatureSpellListDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureSpellListDataModule.Version);
        Assert.Equal(
            ["creature_spell_list_entry", "creature_spell_list", "creature_spell_targeting", "combat_condition", "unit_condition"],
            step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Equal([("creature_template", nameof(CreatureTemplateRow.SpellList))], step.Changes.OfType<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureSpellListDataModule);
    }

    [Fact]
    public void Importer_ReadsEveryTable_AndTheTemplateColumn()
    {
        CreatureDumpImporter importer = Read(ClassicDb);

        Assert.True(importer.HasSpellListTables);
        Assert.True(importer.HasSpellListColumn);
        CreatureImportReport report = importer.BuildReport();
        Assert.Equal((2, 5, 2, 1, 1), (report.SpellLists, report.SpellListEntries, report.SpellTargeting, report.CombatConditions, report.UnitConditions));
        Assert.Equal([(930401u, 93040101u), (930402u, 0u)], importer.Snapshot().Templates.OrderBy(t => t.Entry).Select(t => (t.Entry, t.SpellList)));

        CreatureSpellListRow row = importer.SpellListSnapshot().Entries.Single(r => r is { Id: 93040101, Position: 2 });
        Assert.Equal((116u, 0u, 7, 100u, 9u, 50u), (row.SpellId, row.Flags, row.CombatCondition, row.TargetId, row.ScriptId, row.Availability));
        Assert.Equal(-1, importer.SpellListSnapshot().Targeting.Single(r => r.Id == 100).UnitCondition);
        Assert.Equal((8, 5u), importer.SpellListSnapshot().CombatConditions.Select(r => (r.EnemyConditionId0, r.EnemyConditionOp0)).Single());
        Assert.Equal((1u, 33u, 35u), importer.SpellListSnapshot().UnitConditions.Select(r => (r.Flags, r.Variable0, r.Variable1)).Single());
    }

    [Fact]
    public void Importer_VMangosSpellListId_IsNotTheCmangosList()
    {
        const string VMangos = """
            INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`display_id1`,`spell_list_id`) VALUES
            (930403,0,'Other Table','',10,10,1,4242);
            """;
        CreatureDumpImporter importer = Read(VMangos);
        Assert.Equal(0u, Assert.Single(importer.Snapshot().Templates).SpellList);
        Assert.False(importer.HasSpellListColumn);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Store_BuildsTheCatalog_LeavingOutTheRowsItRefuses(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CleanWorldAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await Read(ClassicDb).WriteAsync(db, replace: true);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        CreatureContent content = await new EfCreatureDataStore(read).LoadAsync();

        Assert.Equal(93040101u, content.FindTemplate(930401)!.SpellList);
        CreatureSpellListCatalog catalog = content.Ai.SpellLists;
        Assert.Equal(1, catalog.Count); // the list without a name is left out
        CreatureSpellList list = catalog.Find(93040101)!;
        Assert.Equal(100u, list.ChanceRangedAttack);

        // Position 3 is a support entry of a list with chance 0, position 4 names no targeting row, and list 93040199 does not exist.
        Assert.Equal([1u, 2u], list.Entries.Select(e => e.Position));
        CreatureSpellListEntry first = list.Entries[0];
        Assert.Equal((133u, CreatureSpellListFlags.RangedAction, 50u, 1000u, 2000u, 3000u, 4000u),
            (first.SpellId, first.Flags, first.Probability, first.InitialMin, first.InitialMax, first.RepeatMin, first.RepeatMax));
        Assert.False(first.HasCombatCondition);
        Assert.Equal(7, list.Entries[1].CombatCondition);

        Assert.Equal(CreatureSpellListTargetingType.Attack, catalog.FindTargeting(100)!.Type);
        Assert.Equal(8, catalog.Conditions.FindCombat(7)!.Enemy0.ConditionId);
        UnitConditionDefinition unit = catalog.Conditions.FindUnit(8)!;
        Assert.True(unit.AnyOf);
        Assert.Equal([33u, 35u], unit.Terms.Select(t => t.Variable)); // the unused slots are gone
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Refresh_FillsAnExistingWorld_AndADumpWithoutTheTablesLeavesIt(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CleanWorldAsync(provider);
        const string Bare = """
            INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`) VALUES (930401,'Listed','',60,60,11);
            """;
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await Read(Bare).WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            (int rows, int templates) = await Read(ClassicDb).RefreshSpellListsAsync(db);
            Assert.Equal((5, 1), (rows, templates));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            (int rows, int templates) = await Read(Bare).RefreshSpellListsAsync(db);
            Assert.Equal((0, 0), (rows, templates));
            Assert.Equal(5, await db.Set<CreatureSpellListRow>().CountAsync());
            Assert.Equal(93040101u, (await db.Set<CreatureTemplateRow>().SingleAsync()).SpellList);
        }
    }

    private static string ContentFile(DatabaseProvider provider)
    {
        string root = RepositorySource.FindRoot() ?? throw new Xunit.Sdk.XunitException("the ArcaneCore checkout was not found");
        string folder = provider switch
        {
            DatabaseProvider.Sqlite => "sqlite",
            DatabaseProvider.MariaDb or DatabaseProvider.MySql => "mariadb",
            DatabaseProvider.PostgreSql => "postgresql",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        return Path.Combine(root, "sql", "content", "world", folder, "0006_creature_spell_lists_av.sql");
    }

    private static async Task RunAsync(WorldDbContext db, string script)
    {
        DbConnection connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AvContentFile_InstallsCleanly_ARerunIsANoOp_AndOtherListsStay(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CleanWorldAsync(provider);
        string script = await File.ReadAllTextAsync(ContentFile(provider));
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        db.Set<CreatureSpellListHeaderRow>().Add(new CreatureSpellListHeaderRow { Id = 77, Name = "kept" });
        await db.SaveChangesAsync();

        await RunAsync(db, script);
        await RunAsync(db, script);

        Assert.Equal(8, await db.Set<CreatureSpellListHeaderRow>().AsNoTracking().CountAsync());
        Assert.Equal(14, await db.Set<CreatureSpellListRow>().AsNoTracking().CountAsync());
        Assert.Equal(
            [(1u, 15572u), (2u, 15580u)],
            (await db.Set<CreatureSpellListRow>().AsNoTracking().Where(r => r.Id == 1317901).OrderBy(r => r.Position).ToListAsync()).Select(r => (r.Position, r.SpellId)));
    }
}

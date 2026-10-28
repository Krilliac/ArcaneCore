using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The six creature school resistances (<see cref="CreatureSchoolResistanceDataModule"/>, world schema 57): the importer reads the
/// classic-db <c>Resistance&lt;School&gt;</c> and vmangos <c>&lt;school&gt;_res</c> columns, the store loads them into
/// <see cref="CreatureTemplate"/>, and a dump without the columns leaves them at 0. Rows are hand-written; no GPL rows are copied.
/// </summary>
public sealed class CreatureSchoolResistanceImportTests : IAsyncLifetime
{
    private const string ClassicDb = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Armor`,`ResistanceHoly`,`ResistanceFire`,`ResistanceNature`,`ResistanceFrost`,`ResistanceShadow`,`ResistanceArcane`) VALUES
        (930101,'Fire Elemental','',60,60,100,0,100,-5,25,30,40),(930102,'Plain Beast','',5,5,0,0,0,0,0,0,0);
        """;

    private const string VMangos = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`display_id1`,`holy_res`,`fire_res`,`nature_res`,`frost_res`,`shadow_res`,`arcane_res`) VALUES
        (930201,0,'Frost Giant','',60,60,1,10,20,30,40,50,60);
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

    private static int[] Resistances(CreatureTemplateRow t) =>
        [t.ResistanceHoly, t.ResistanceFire, t.ResistanceNature, t.ResistanceFrost, t.ResistanceShadow, t.ResistanceArcane];

    [Fact]
    public void WorldStep_IsTheSixTemplateColumns()
    {
        Assert.Equal(57, CreatureSchoolResistanceDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureSchoolResistanceDataModule.Version);
        Assert.Empty(step.Changes.OfType<CreateTableChange>());
        Assert.Equal(
            [
                ("creature_template", nameof(CreatureTemplateRow.ResistanceHoly)),
                ("creature_template", nameof(CreatureTemplateRow.ResistanceFire)),
                ("creature_template", nameof(CreatureTemplateRow.ResistanceNature)),
                ("creature_template", nameof(CreatureTemplateRow.ResistanceFrost)),
                ("creature_template", nameof(CreatureTemplateRow.ResistanceShadow)),
                ("creature_template", nameof(CreatureTemplateRow.ResistanceArcane)),
            ],
            step.Changes.OfType<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureSchoolResistanceDataModule);
    }

    [Fact]
    public void Importer_ReadsTheClassicDbResistanceColumns_IncludingNegativeValues()
    {
        CreatureDumpImporter importer = Read(ClassicDb);

        CreatureTemplateRow[] rows = [.. importer.Snapshot().Templates.OrderBy(t => t.Entry)];
        Assert.Equal([0, 100, -5, 25, 30, 40], Resistances(rows[0]));
        Assert.Equal(100u, rows[0].Armor);
        Assert.Equal([0, 0, 0, 0, 0, 0], Resistances(rows[1]));
    }

    [Fact]
    public void Importer_ReadsTheVMangosResistanceColumns()
    {
        CreatureDumpImporter importer = Read(VMangos);

        Assert.Equal([10, 20, 30, 40, 50, 60], Resistances(Assert.Single(importer.Snapshot().Templates)));
    }

    [Fact]
    public void Importer_WithoutTheColumns_LeavesTheResistancesAtZero()
    {
        CreatureDumpImporter importer = Read("INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`) VALUES (930301,'Old Dump','',1,1);");

        Assert.Equal([0, 0, 0, 0, 0, 0], Resistances(Assert.Single(importer.Snapshot().Templates)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ImportedResistances_LoadIntoTheCreatureTemplate(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Read(ClassicDb).WriteAsync(db, replace: false);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            CreatureTemplate fire = content.FindTemplate(930101)!;
            Assert.Equal(
                [0, 100, -5, 25, 30, 40],
                [fire.ResistanceHoly, fire.ResistanceFire, fire.ResistanceNature, fire.ResistanceFrost, fire.ResistanceShadow, fire.ResistanceArcane]);
            Assert.Equal(0, content.FindTemplate(930102)!.ResistanceFire);
        }
    }
}

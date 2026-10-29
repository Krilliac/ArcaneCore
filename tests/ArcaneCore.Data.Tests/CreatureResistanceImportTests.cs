using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// World schema 57 (<see cref="CreatureResistanceDataModule"/>): the six magic resistances of creature_template reach the loaded
/// template from a cmangos-classic dump (Resistance*) and a vmangos dump (*_res). Rows are hand-written.
/// </summary>
public sealed class CreatureResistanceImportTests : IAsyncLifetime
{
    private const string CMangosDump = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`,`ResistanceHoly`,`ResistanceFire`,`ResistanceNature`,`ResistanceFrost`,`ResistanceShadow`,`ResistanceArcane`) VALUES
        (930001,'Resistant Elemental','',60,60,32,10,100,200,-30,400,500),
        (930002,'Plain Rat','',1,1,32,0,0,0,0,0,0);
        """;

    private const string VMangosDump = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`faction`,`holy_res`,`fire_res`,`nature_res`,`frost_res`,`shadow_res`,`arcane_res`) VALUES
        (930010,0,'Vmangos Elemental','',60,60,32,1,2,3,4,5,-6);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private async Task<CreatureContent> ImportAndLoadAsync(DatabaseProvider provider, string dump)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: false);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        return await new EfCreatureDataStore(read).LoadAsync();
    }

    [Fact]
    public void WorldStep_AddsTheSixResistanceColumns()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureResistanceDataModule.Version);
        Assert.Equal(
            [
                ("creature_template", "HolyRes"), ("creature_template", "FireRes"), ("creature_template", "NatureRes"),
                ("creature_template", "FrostRes"), ("creature_template", "ShadowRes"), ("creature_template", "ArcaneRes"),
            ],
            step.Changes.OfType<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureResistanceDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CMangosResistanceColumns_ReachTheTemplate(DatabaseProvider provider)
    {
        CreatureContent content = await ImportAndLoadAsync(provider, CMangosDump);

        CreatureTemplate t = content.FindTemplate(930001)!;
        Assert.Equal((10, 100, 200, -30, 400, 500), (t.HolyRes, t.FireRes, t.NatureRes, t.FrostRes, t.ShadowRes, t.ArcaneRes));
        CreatureTemplate rat = content.FindTemplate(930002)!;
        Assert.Equal((0, 0, 0, 0, 0, 0), (rat.HolyRes, rat.FireRes, rat.NatureRes, rat.FrostRes, rat.ShadowRes, rat.ArcaneRes));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task VMangosResistanceColumns_ReachTheTemplate(DatabaseProvider provider)
    {
        CreatureContent content = await ImportAndLoadAsync(provider, VMangosDump);

        CreatureTemplate t = content.FindTemplate(930010)!;
        Assert.Equal((1, 2, 3, 4, 5, -6), (t.HolyRes, t.FireRes, t.NatureRes, t.FrostRes, t.ShadowRes, t.ArcaneRes));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Refresh_BackfillsTheResistancesOfTemplatesAlreadyInTheWorld(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var first = new CreatureDumpImporter();
        first.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`) VALUES (930020,'Old Row','',60,60,32);"));
        var refresh = new CreatureDumpImporter();
        refresh.Read(new StringReader(CMangosDump.Replace("930001", "930020", StringComparison.Ordinal)));
        Assert.True(refresh.HasResistanceColumns);
        Assert.False(first.HasResistanceColumns);

        int changed;
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await first.WriteAsync(db, replace: false);
            Assert.Equal(0, await first.RefreshResistancesAsync(db)); // a dump without the columns leaves the world alone
            changed = await refresh.RefreshResistancesAsync(db);
        }

        Assert.Equal(1, changed); // the plain rat (930002) is not in the world, and 930020 changed
        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        CreatureTemplate t = (await new EfCreatureDataStore(read).LoadAsync()).FindTemplate(930020)!;
        Assert.Equal((10, 100, 200, -30, 400, 500), (t.HolyRes, t.FireRes, t.NatureRes, t.FrostRes, t.ShadowRes, t.ArcaneRes));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

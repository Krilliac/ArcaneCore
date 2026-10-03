using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Reputation;

/// <summary>
/// <c>creature_onkill_reputation</c>: the world table, its importer and the EF source the
/// reputation feature resolves (<see cref="IReputationOnKillSource"/>, until now registered by
/// nothing, so no creature granted reputation). Dumps are hand-written.
/// </summary>
public sealed class OnKillReputationImportTests : IAsyncLifetime
{
    // cmangos classic-db z2815 column order: MaxStanding before IsTeamAward.
    private const string CMangos = """
        CREATE TABLE `creature_onkill_reputation` (`creature_id` mediumint unsigned NOT NULL, `RewOnKillRepFaction1` smallint, `RewOnKillRepFaction2` smallint, `MaxStanding1` tinyint, `IsTeamAward1` tinyint, `RewOnKillRepValue1` mediumint, `MaxStanding2` tinyint, `IsTeamAward2` tinyint, `RewOnKillRepValue2` mediumint, `TeamDependent` tinyint unsigned, PRIMARY KEY (`creature_id`));
        INSERT INTO `creature_onkill_reputation` VALUES (737,87,169,5,0,5,7,0,-25,0),(900,21,0,6,1,10,0,1,0,1);
        """;

    // vmangos: patch-versioned, and its loader lists IsTeamAward before MaxStanding (ObjectMgr.cpp:8897-8899).
    private const string VMangos = """
        CREATE TABLE `creature_onkill_reputation` (`creature_id` mediumint unsigned NOT NULL, `patch` tinyint NOT NULL, `RewOnKillRepFaction1` smallint, `RewOnKillRepFaction2` smallint, `IsTeamAward1` tinyint, `MaxStanding1` tinyint, `RewOnKillRepValue1` mediumint, `IsTeamAward2` tinyint, `MaxStanding2` tinyint, `RewOnKillRepValue2` mediumint, `TeamDependent` tinyint unsigned, PRIMARY KEY (`creature_id`, `patch`));
        INSERT INTO `creature_onkill_reputation` VALUES (700,0,1,2,1,3,10,0,4,20,0),(700,5,5,6,0,7,30,1,8,40,1),(700,11,9,9,9,9,9,9,9,9,9);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Module_IsDiscovered_AtItsConstantVersion_AndRegistersTheKillSource()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.World), m => m is CreatureOnKillReputationWorldModule);

        Assert.Equal(CreatureOnKillReputationWorldModule.Version, module.SchemaVersion);
        Assert.Equal(["creature_onkill_reputation"], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= CreatureOnKillReputationWorldModule.Version);
        var services = new ServiceCollection();
        DataModules.AddServices(services, DatabaseComponent.World);
        Assert.Contains(services, d => d.ServiceType == typeof(IReputationOnKillSource) && d.ImplementationType == typeof(EfReputationOnKillSource));
    }

    [Fact]
    public void CMangosRows_MapByName()
    {
        var snapshot = Import(CMangos).Snapshot().ToDictionary(r => r.CreatureId);

        CreatureOnKillReputationRow a = snapshot[737];
        Assert.Equal((87u, 169u, (byte)5, false, 5, (byte)7, false, -25, false),
            (a.RewOnKillRepFaction1, a.RewOnKillRepFaction2, a.MaxStanding1, a.IsTeamAward1, a.RewOnKillRepValue1, a.MaxStanding2, a.IsTeamAward2, a.RewOnKillRepValue2, a.TeamDependent));
        CreatureOnKillReputationRow b = snapshot[900];
        Assert.Equal((21u, 0u, (byte)6, true, 10, (byte)0, true, 0, true),
            (b.RewOnKillRepFaction1, b.RewOnKillRepFaction2, b.MaxStanding1, b.IsTeamAward1, b.RewOnKillRepValue1, b.MaxStanding2, b.IsTeamAward2, b.RewOnKillRepValue2, b.TeamDependent));
    }

    [Fact]
    public void ColumnOrder_NeverSwapsMaxStandingAndIsTeamAward()
    {
        // vmangos lists IsTeamAward first, classic-db MaxStanding first: positional reading would swap them.
        CreatureOnKillReputationRow row = Import(VMangos).Snapshot().Single();

        Assert.Equal((5u, 6u, false, (byte)7, 30, true, (byte)8, 40, true),
            (row.RewOnKillRepFaction1, row.RewOnKillRepFaction2, row.IsTeamAward1, row.MaxStanding1, row.RewOnKillRepValue1, row.IsTeamAward2, row.MaxStanding2, row.RewOnKillRepValue2, row.TeamDependent));
    }

    [Fact]
    public void VMangosRows_UseTheHighestPatchUpToTen()
    {
        ReputationOnKillImportReport report = Import(VMangos).BuildReport();

        Assert.Equal(1, report.Entries);
        Assert.Equal(1, report.SkippedRows); // patch 11 is above 10; patch 0 was simply replaced by patch 5
    }

    [Fact]
    public void ARenamedKeyColumn_IsASchemaError_NotEntryZero()
    {
        var importer = new OnKillReputationDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `creature_onkill_reputation` (`creature`,`RewOnKillRepFaction1`) VALUES (737,87);")));

        Assert.Equal("creature_onkill_reputation", ex.Table);
        Assert.Equal("creature_id", ex.Column);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_StoresTheRows_AndTheSourceLoadsThemAsEntries(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new CreatureOnKillReputationRow { CreatureId = 5, RewOnKillRepFaction1 = 1 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            ReputationOnKillImportReport report = await Import(CMangos).WriteAsync(db, replace: true);

            Assert.Equal(2, report.Entries);
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        IReadOnlyList<ReputationOnKillEntry> entries = await new EfReputationOnKillSource(verify).LoadAsync();
        Assert.Equal([737u, 900u], entries.Select(e => e.CreatureEntry).Order());
        ReputationOnKillEntry e737 = entries.Single(e => e.CreatureEntry == 737);
        Assert.Equal(new ReputationOnKillEntry(737, 87, 169, 5, false, 5, 7, false, -25, false), e737);
        Assert.Equal(new ReputationOnKillEntry(900, 21, 0, 6, true, 10, 0, true, 0, true), entries.Single(e => e.CreatureEntry == 900));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAKeyConflict_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Import(CMangos).WriteAsync(db, replace: false);
            await Assert.ThrowsAsync<DbUpdateException>(() => Import(CMangos + "\nINSERT INTO `creature_onkill_reputation` (`creature_id`) VALUES (1000);").WriteAsync(db, replace: false));
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(2, await verify.Set<CreatureOnKillReputationRow>().CountAsync());
    }

    private static OnKillReputationDumpImporter Import(string dump)
    {
        var importer = new OnKillReputationDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }
}

using ArcaneCore.Data.Content;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Reputation;

/// <summary>
/// <c>reputation_spillover_template</c> and <c>reputation_reward_rate</c> (<see cref="ReputationTemplatesWorldModule"/>).
/// Every store test is a provider theory over <see cref="TestDatabases.AvailableProviders"/>: SQLite always runs, MariaDB and
/// PostgreSQL run where the server variables are set (the hosted CI). MariaDB DDL is not transactional and implicitly commits,
/// so the interrupted-step test leaves one table behind there and the step must resume; PostgreSQL and SQLite DDL is
/// transactional, so nothing is left behind and the step applies in one go. Rows mirror the classic-db shapes
/// (Stormwind spills 0.25 into three factions at rank 7); no GPL data is copied.
/// </summary>
public sealed class ReputationTemplatesStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static string[] Tables => ["reputation_spillover_template", "reputation_reward_rate"];

    private static async Task DowngradeAsync(WorldDbContext db)
    {
        Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper sql =
            Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>(db);
        foreach (string table in Tables)
        {
            string statement = $"DROP TABLE {sql.DelimitIdentifier(table)}";
            await db.Database.ExecuteSqlRawAsync(statement);
        }

        await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, ReputationTemplatesWorldModule.Version - 1));
    }

    [Fact]
    public void Module_IsDiscovered_WithTwoCreateTablesAndNoInlineData_AndRegistersTheSource()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == ReputationTemplatesWorldModule.Version);
        Assert.Equal(Tables, step.Changes.Cast<CreateTableChange>().Select(c => c.Table));
        Assert.Equal(2, step.Changes.Count);
        Assert.Equal(DatabaseComponent.World, new ReputationTemplatesWorldModule().Component);
        Assert.True(WorldDbContext.Schema.CurrentVersion >= ReputationTemplatesWorldModule.Version);
        var services = new ServiceCollection();
        DataModules.AddServices(services, DatabaseComponent.World);
        Assert.Contains(services, d => d.ServiceType == typeof(IReputationContentSource) && d.ImplementationType == typeof(EfReputationContentSource));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Rows_RoundTripThroughTheSource_IncludingFractionalRatesAndUnusedSlots(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Empty(await db.Set<ReputationSpilloverTemplateRow>().ToListAsync());
            db.AddRange(
                new ReputationSpilloverTemplateRow
                {
                    Faction = 72, Faction1 = 47, Rate1 = 0.25f, Rank1 = 7, Faction2 = 54, Rate2 = 0.25f, Rank2 = 7, Faction3 = 69, Rate3 = 0.25f, Rank3 = 7,
                },
                new ReputationSpilloverTemplateRow { Faction = 21, Faction1 = 369, Rate1 = 0.5f, Rank1 = 7, Faction4 = 577, Rate4 = 0.125f, Rank4 = 6 },
                new ReputationRewardRateRow { Faction = 529, QuestRate = 1f, CreatureRate = 0.5f, SpellRate = 0f });
            await db.SaveChangesAsync();
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(cs);
        ReputationContentRows rows = await new EfReputationContentSource(verify).LoadAsync();
        Assert.Equal([21u, 72u], rows.Spillovers.Select(s => s.Faction));
        ReputationSpilloverTemplate stormwind = rows.Spillovers.Single(s => s.Faction == 72);
        Assert.Equal(ReputationSpilloverTemplate.MaxTargets, stormwind.Targets.Count);
        Assert.Equal(new ReputationSpillover(47, 0.25f, 7), stormwind.Targets[0]);
        Assert.Equal(new ReputationSpillover(69, 0.25f, 7), stormwind.Targets[2]);
        Assert.Equal(new ReputationSpillover(0, 0f, 0), stormwind.Targets[3]);
        Assert.Equal(new ReputationSpillover(577, 0.125f, 6), rows.Spillovers.Single(s => s.Faction == 21).Targets[3]);
        Assert.Equal(new ReputationRewardRate(529, 1f, 0.5f, 0f), Assert.Single(rows.Rates));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DuplicateKeys_AreRefused_AndRewardRateDefaultsToOne(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Add(new ReputationSpilloverTemplateRow { Faction = 72, Faction1 = 47, Rate1 = 0.25f, Rank1 = 7 });
        db.Add(new ReputationRewardRateRow { Faction = 576 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Add(new ReputationSpilloverTemplateRow { Faction = 72 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Add(new ReputationRewardRateRow { Faction = 576 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        Assert.Equal(new ReputationRewardRate(576, 1f, 1f, 1f), (await new EfReputationContentSource(db).LoadAsync()).Rates.Single());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EnsuringTwice_AndReapplyingTheStep_ConvergeWithoutLosingRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Add(new ReputationRewardRateRow { Faction = 609, QuestRate = 2f });
        await db.SaveChangesAsync();

        // The version row says the step did not finish while its tables exist: the step must accept them and keep the data.
        await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, ReputationTemplatesWorldModule.Version - 1));
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        Assert.Equal(2f, (await new EfReputationContentSource(db).LoadAsync()).Rates.Single().QuestRate);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StepInterruptedAfterTheFirstTable_ResumesCleanly(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await DowngradeAsync(db);
        }

        var tap = new CommandTap(sql => CommandTap.IsDdl(sql) && sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase), faultOn: 1);
        await using (WorldDbContext db = SchemaProbe.CreateWith<WorldDbContext>(cs, tap))
        {
            await Assert.ThrowsAsync<InjectedFaultException>(() => SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            foreach (string table in Tables)
            {
                Assert.True(await SchemaCatalog.TableExistsAsync(db, table), table);
            }

            Assert.Equal(WorldDbContext.Schema.CurrentVersion, await db.Set<SchemaVersionRow>().Select(r => r.Version).SingleAsync());
        }
    }
}

using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests.Bans;

/// <summary>
/// The published account status is the effective one (the strictest ban still in force), and an unban is one
/// transaction: the deactivation and its audit row commit together, and Active is published only after that commit.
/// </summary>
public sealed class BanStoreEffectiveStatusTests : IAsyncLifetime
{
    private const long T0 = 1_700_000_000;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TemporaryBanOnTopOfAPermanentOne_PublishesTheEffectivePermanentStatus(DatabaseProvider provider)
    {
        var events = new AccountStatusEvents();
        var seen = new List<AccountStatusChange>();
        events.StatusChanged += seen.Add;
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
        var store = new EfBanStore(db, new BanClock(T0), events);

        await store.BanAccountAsync(new BanRequest(7, 0, "permanent", "GM"));
        await store.BanAccountAsync(new BanRequest(7, 600, "temporary", "GM", AuthorAccountId: 99));

        Assert.Equal([new AccountStatusChange(7, AccountStatus.Banned), new AccountStatusChange(7, AccountStatus.Banned, 99)], seen);
        AccountBanRecord effective = (await store.GetActiveAccountBanAsync(7))!;
        Assert.True(effective.BanDate == effective.UnbanDate); // the permanent row is still the effective ban
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UnbanWhoseAuditInsertFails_RollsTheDeactivationBack_AndPublishesNothing(DatabaseProvider provider)
    {
        var events = new AccountStatusEvents();
        var seen = new List<AccountStatusChange>();
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(cs))
        {
            await new EfBanStore(setup, new BanClock(T0)).BanAccountAsync(new BanRequest(7, 0, "permanent", "GM"));
        }

        events.StatusChanged += seen.Add;
        var builder = new DbContextOptionsBuilder<AuthDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, cs);
        builder.AddInterceptors(new FailAuditInsert());
        await using (var failing = new AuthDbContext(builder.Options))
        {
            var store = new EfBanStore(failing, new BanClock(T0 + 1), events);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UnbanAccountAsync(7, "GM", "appeal"));
        }

        await using AuthDbContext fresh = TestContexts.Create<AuthDbContext>(cs);
        Assert.NotNull(await new EfBanStore(fresh, new BanClock(T0 + 1)).GetActiveAccountBanAsync(7));
        Assert.Empty(seen);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Unban_CommitsDeactivationAndAudit_ThenPublishesActive(DatabaseProvider provider)
    {
        var events = new AccountStatusEvents();
        var seen = new List<AccountStatusChange>();
        events.StatusChanged += seen.Add;
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
        var store = new EfBanStore(db, new BanClock(T0), events);
        await store.BanAccountAsync(new BanRequest(7, 0, "permanent", "GM"));
        await store.BanAccountAsync(new BanRequest(7, 600, "temporary", "GM"));
        seen.Clear();

        Assert.True(await store.UnbanAccountAsync(7, "GM", "appeal"));

        Assert.Equal([new AccountStatusChange(7, AccountStatus.Active)], seen);
        Assert.Null(await store.GetActiveAccountBanAsync(7));
        Assert.Equal(3, (await store.GetHistoryAsync(7)).Count);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        return cs;
    }

    /// <summary>Fails the SaveChanges that inserts the unban audit row (an inactive account_banned row).</summary>
    private sealed class FailAuditInsert : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AccountBanRow>().Any(e => e.State == EntityState.Added && !e.Entity.Active))
            {
                throw new InvalidOperationException("injected audit insert failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}

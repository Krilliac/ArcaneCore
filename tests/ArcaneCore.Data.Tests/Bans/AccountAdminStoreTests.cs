using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Bans;

/// <summary>
/// The status column operations of <see cref="IAccountAdmin"/> and the events they raise. Run on every
/// engine available; locally only SQLite (MariaDB/PostgreSQL run on hosted CI).
/// </summary>
public sealed class AccountAdminStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SetStatus_RoundTrips_AndReportsUnknownAccounts(DatabaseProvider provider)
    {
        await using Fixture f = await Fixture.CreateAsync(_databases, provider);
        Account a = await f.Accounts.CreateAsync(NewAccount("ALICE"));

        Assert.True(await f.Admin.SetStatusAsync("alice", AccountStatus.Banned));
        Assert.Equal(AccountStatus.Banned, await StatusAsync(f, "ALICE"));
        Assert.True(await f.Admin.SetStatusAsync("ALICE", AccountStatus.Suspended));
        Assert.Equal(AccountStatus.Suspended, await StatusAsync(f, "ALICE"));
        Assert.True(await f.Admin.SetStatusAsync("ALICE", AccountStatus.Active));
        Assert.Equal(AccountStatus.Active, await StatusAsync(f, "ALICE"));
        Assert.False(await f.Admin.SetStatusAsync("NOBODY", AccountStatus.Banned));
        Assert.NotEqual(0, a.Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StatusChanged_FiresOncePerRealChange_AfterCommit_AndASubscriberFaultIsIsolated(DatabaseProvider provider)
    {
        var events = new AccountStatusEvents();
        var seen = new List<(AccountStatusChange Change, AccountStatus Stored)>();
        DatabaseConnectionOptions? cs = null;
        events.StatusChanged += _ => throw new InvalidOperationException("faulty subscriber first");
        events.StatusChanged += c =>
        {
            using AuthDbContext fresh = TestContexts.Create<AuthDbContext>(cs!);
            seen.Add((c, fresh.Accounts.Single(x => x.Id == c.AccountId).Status));
        };
        var faults = new List<Exception>();
        events.SubscriberFaulted += faults.Add;

        await using Fixture f = await Fixture.CreateAsync(_databases, provider, events);
        cs = f.Connection;
        Account a = await f.Accounts.CreateAsync(NewAccount("BOB"));

        await f.Admin.SetStatusAsync("BOB", AccountStatus.Banned, actorAccountId: 77);
        await f.Admin.SetStatusAsync("BOB", AccountStatus.Banned); // no change, no event
        await f.Admin.SetStatusAsync("BOB", AccountStatus.Active);

        Assert.Equal(
            [(new AccountStatusChange(a.Id, AccountStatus.Banned, 77), AccountStatus.Banned),
                (new AccountStatusChange(a.Id, AccountStatus.Active), AccountStatus.Active)],
            seen);
        Assert.Equal(2, faults.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RevokeSessionKey_NullsTheKey_AndReportsUnknownIds(DatabaseProvider provider)
    {
        await using Fixture f = await Fixture.CreateAsync(_databases, provider);
        Account a = await f.Accounts.CreateAsync(NewAccount("CAROL", key: new byte[40]));

        Assert.True(await f.Admin.RevokeSessionKeyAsync(a.Id));
        f.Db.ChangeTracker.Clear();
        Assert.Null((await f.Accounts.FindByUsernameAsync("CAROL"))!.SessionKey);
        Assert.False(await f.Admin.RevokeSessionKeyAsync(99999));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FindNonActive_ReturnsOnlyNonActiveIdsOfTheInput(DatabaseProvider provider)
    {
        await using Fixture f = await Fixture.CreateAsync(_databases, provider);
        Account a = await f.Accounts.CreateAsync(NewAccount("A1"));
        Account b = await f.Accounts.CreateAsync(NewAccount("B1"));
        Account c = await f.Accounts.CreateAsync(NewAccount("C1"));
        await f.Admin.SetStatusAsync("A1", AccountStatus.Banned);
        await f.Admin.SetStatusAsync("B1", AccountStatus.Suspended);

        IReadOnlySet<int> found = await f.Admin.FindNonActiveAsync([a.Id, c.Id, 4242]);
        Assert.Equal([a.Id], found);
        Assert.Equal([a.Id, b.Id], (await f.Admin.FindNonActiveAsync([a.Id, b.Id, c.Id])).OrderBy(i => i));
        Assert.Empty(await f.Admin.FindNonActiveAsync([]));
    }

    [Fact]
    public void ExistingConstructionSites_StillCompile()
    {
        // 'new EfAccountStore(db)' (no events) is used by older tests; the events parameter is optional.
        using AuthDbContext db = TestContexts.Create<AuthDbContext>(
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" });
        Assert.NotNull(new EfAccountStore(db));
    }

    private static async Task<AccountStatus> StatusAsync(Fixture f, string name)
    {
        f.Db.ChangeTracker.Clear();
        return (await f.Accounts.FindByUsernameAsync(name))!.Status;
    }

    private static Account NewAccount(string name, byte[]? key = null)
        => new() { Username = name, Salt = new byte[32], Verifier = new byte[32], SessionKey = key };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(DatabaseConnectionOptions cs, AuthDbContext db, EfAccountStore store)
        {
            Connection = cs;
            Db = db;
            Accounts = store;
        }

        public DatabaseConnectionOptions Connection { get; }

        public AuthDbContext Db { get; }

        public EfAccountStore Accounts { get; }

        public IAccountAdmin Admin => Accounts;

        public static async Task<Fixture> CreateAsync(TestDatabases databases, DatabaseProvider provider, AccountStatusEvents? events = null)
        {
            DatabaseConnectionOptions cs = await databases.CreateAsync(provider);
            AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
            return new Fixture(cs, db, new EfAccountStore(db, events));
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}

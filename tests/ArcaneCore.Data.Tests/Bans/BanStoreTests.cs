using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Bans;

/// <summary>A TimeProvider the test moves by hand (all ban times are unix seconds from it).</summary>
internal sealed class BanClock(long unixSeconds) : TimeProvider
{
    public long Seconds { get; set; } = unixSeconds;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Seconds);
}

/// <summary>
/// The ban tables and their retail semantics (vmangos sql/logon.sql:75-86,139-146; AuthSocket.cpp:464;
/// World.cpp:2461-2486,2639-2665) on every engine available. Locally only SQLite runs: the MariaDB and
/// PostgreSQL paths (duplicate-key aborts, implicit DDL commit between the two table steps) are written
/// against their real semantics but are exercised on hosted CI only.
/// </summary>
public sealed class BanStoreTests : IAsyncLifetime
{
    private const long T0 = 1_700_000_000;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AuthDatabaseAtV2_UpgradesToBanVersion_KeepsAccounts_AndSecondStartupIsNoOp(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (AuthM5Context m5 = TestContexts.Create<AuthM5Context>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(m5, AuthM5Context.Schema);
            m5.Accounts.Add(new AccountV1Row { Username = "KEEPER", Salt = new byte[32], Verifier = new byte[32] });
            await m5.SaveChangesAsync();
        }

        await using (AuthDbContext v2 = TestContexts.Create<AuthDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(v2, SchemaProbe.ThroughVersion(AuthDbContext.Schema, BanDataModule.Version - 1));
            Assert.Equal(BanDataModule.Version - 1, (await v2.Set<SchemaVersionRow>().SingleAsync()).Version);
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
            Assert.Equal(AuthDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal("KEEPER", (await db.Accounts.SingleAsync()).Username);
            Assert.Empty(await db.Set<AccountBanRow>().ToListAsync());
            Assert.Empty(await db.Set<IpBanRow>().ToListAsync());
            await SchemaProbe.AssertIndexParityAsync(db, "auth with bans");
        }

        Assert.True(AuthDbContext.Schema.CurrentVersion >= BanDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PermanentBan_HasEqualDates_AndIsActiveForever(DatabaseProvider provider)
    {
        var clock = new BanClock(T0);
        await using Fixture f = await Fixture.CreateAsync(_databases, provider, clock);
        await f.Store.BanAccountAsync(new BanRequest(7, 0, "cheating", "GM"));

        AccountBanRecord ban = (await f.Store.GetActiveAccountBanAsync(7))!;
        Assert.True(ban.IsPermanent);
        Assert.Equal(ban.BanDate, ban.UnbanDate);
        Assert.Equal("cheating", ban.Reason);

        clock.Seconds = T0 + (50L * 365 * 86400);
        Assert.NotNull(await f.Store.GetActiveAccountBanAsync(7));
        Assert.Null(await f.Store.GetActiveAccountBanAsync(8)); // other accounts unaffected
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TemporaryBan_IsActiveWhileUnbanDateIsInTheFuture_AndInactiveAtTheBoundary(DatabaseProvider provider)
    {
        var clock = new BanClock(T0);
        await using Fixture f = await Fixture.CreateAsync(_databases, provider, clock);
        await f.Store.BanAccountAsync(new BanRequest(7, 3600, "spam", "GM"));

        AccountBanRecord ban = (await f.Store.GetActiveAccountBanAsync(7))!;
        Assert.False(ban.IsPermanent);
        Assert.Equal(T0 + 3600, ban.UnbanDate);

        clock.Seconds = T0 + 3599;
        Assert.NotNull(await f.Store.GetActiveAccountBanAsync(7));
        clock.Seconds = T0 + 3600; // unbandate > now is false at equality (AuthSocket.cpp:464)
        Assert.Null(await f.Store.GetActiveAccountBanAsync(7));
        Assert.Empty(await f.Store.FindBannedAccountsAsync([7]));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TwoBansInTheSameSecond_YieldTwoRows(DatabaseProvider provider)
    {
        await using Fixture f = await Fixture.CreateAsync(_databases, provider, new BanClock(T0));
        await f.Store.BanAccountAsync(new BanRequest(7, 60, "one", "GM"));
        await f.Store.BanAccountAsync(new BanRequest(7, 120, "two", "GM"));

        Assert.Equal(["one", "two"], (await f.Store.GetHistoryAsync(7)).Select(r => r.Reason));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FindAccountsWithHistory_ReturnsAccountsWithAnyRow_ActiveOrNot(DatabaseProvider provider)
    {
        await using Fixture f = await Fixture.CreateAsync(_databases, provider, new BanClock(T0));
        await f.Store.BanAccountAsync(new BanRequest(7, 60, "a", "GM"));
        await f.Store.BanAccountAsync(new BanRequest(8, 0, "b", "GM"));
        await f.Store.UnbanAccountAsync(8, "GM", "done"); // inactive rows only

        Assert.Equal([7, 8], (await f.Store.FindAccountsWithHistoryAsync([7, 8, 9, 7])).Order());
        Assert.Empty(await f.Store.FindAccountsWithHistoryAsync([9, 10]));
        Assert.Empty(await f.Store.FindAccountsWithHistoryAsync([]));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Unban_DeactivatesEveryRow_AndWritesTheInactiveAuditRow(DatabaseProvider provider)
    {
        var clock = new BanClock(T0);
        await using Fixture f = await Fixture.CreateAsync(_databases, provider, clock);
        await f.Store.BanAccountAsync(new BanRequest(7, 0, "a", "GM"));
        await f.Store.BanAccountAsync(new BanRequest(7, 600, "b", "GM"));

        Assert.True(await f.Store.UnbanAccountAsync(7, "Admin", "appeal won"));

        Assert.Null(await f.Store.GetActiveAccountBanAsync(7));
        IReadOnlyList<AccountBanRecord> history = await f.Store.GetHistoryAsync(7);
        Assert.Equal(3, history.Count);
        Assert.All(history, r => Assert.False(r.Active));
        AccountBanRecord audit = history.Single(r => r.Reason.StartsWith("UNBAN: ", StringComparison.Ordinal));
        Assert.Equal("UNBAN: appeal won", audit.Reason);
        Assert.Equal("Admin", audit.BannedBy);
        Assert.Equal(audit.BanDate + 1, audit.UnbanDate); // World.cpp:2465
        Assert.False(audit.IsPermanent);

        // Nothing was in force the second time: false, but the audit row is still written (retail WarnAccount).
        Assert.False(await f.Store.UnbanAccountAsync(7, "Admin", "again"));
        Assert.Equal(4, (await f.Store.GetHistoryAsync(7)).Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task IpBan_KeepsTheActiveRow_ReplacesAnExpiredOne_AndEncodesPermanent(DatabaseProvider provider)
    {
        var clock = new BanClock(T0);
        await using Fixture f = await Fixture.CreateAsync(_databases, provider, clock);

        Assert.True(await f.Store.BanIpAsync(new IpBanRequest("10.0.0.5", 100, "first", "GM")));
        Assert.False(await f.Store.BanIpAsync(new IpBanRequest("10.0.0.5", 0, "second", "GM"))); // active row stays
        Assert.Equal("first", (await f.Store.GetActiveIpBanAsync("10.0.0.5"))!.Reason);

        clock.Seconds = T0 + 100; // expired
        Assert.Null(await f.Store.GetActiveIpBanAsync("10.0.0.5"));
        Assert.True(await f.Store.BanIpAsync(new IpBanRequest("10.0.0.5", 0, "third", "GM")));
        IpBanRecord permanent = (await f.Store.GetActiveIpBanAsync("10.0.0.5"))!;
        Assert.True(permanent.IsPermanent);
        Assert.Equal("third", permanent.Reason);

        // An IPv4-mapped IPv6 spelling is the same address.
        Assert.NotNull(await f.Store.GetActiveIpBanAsync("::ffff:10.0.0.5"));
        Assert.True(await f.Store.UnbanIpAsync("10.0.0.5"));
        Assert.False(await f.Store.UnbanIpAsync("10.0.0.5"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentIpBans_OfOneAddress_LeaveOneRow_AndNeitherThrows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (AuthDbContext init = TestContexts.Create<AuthDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(init, AuthDbContext.Schema);
        }

        var clock = new BanClock(T0);
        using var gate = new ManualResetEventSlim();
        Task<bool> One(string reason) => Task.Run(async () =>
        {
            await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
            var store = new EfBanStore(db, clock);
            gate.Wait();
            return await store.BanIpAsync(new IpBanRequest("10.9.9.9", 0, reason, "GM"));
        });

        Task<bool>[] calls = [One("a"), One("b"), One("c"), One("d")];
        gate.Set();
        bool[] wrote = await Task.WhenAll(calls);

        Assert.InRange(wrote.Count(w => w), 1, 4);
        await using AuthDbContext check = TestContexts.Create<AuthDbContext>(cs);
        Assert.Single(await check.Set<IpBanRow>().ToListAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PurgeExpired_DeactivatesExpiredAccountRows_DeletesExpiredIpRows_KeepsTheRest(DatabaseProvider provider)
    {
        var clock = new BanClock(T0);
        await using Fixture f = await Fixture.CreateAsync(_databases, provider, clock);
        await f.Store.BanAccountAsync(new BanRequest(1, 60, "short", "GM"));
        await f.Store.BanAccountAsync(new BanRequest(2, 0, "perma", "GM"));
        await f.Store.BanAccountAsync(new BanRequest(3, 99999, "long", "GM"));
        await f.Store.BanIpAsync(new IpBanRequest("1.1.1.1", 60, "short", "GM"));
        await f.Store.BanIpAsync(new IpBanRequest("2.2.2.2", 0, "perma", "GM"));

        clock.Seconds = T0 + 61;
        Assert.Equal(2, await f.Store.PurgeExpiredAsync());

        await using AuthDbContext check = TestContexts.Create<AuthDbContext>(f.Connection);
        Assert.Equal([false, true, true], await check.Set<AccountBanRow>().OrderBy(r => r.AccountId).Select(r => r.Active).ToListAsync());
        Assert.Equal(["2.2.2.2"], await check.Set<IpBanRow>().Select(r => r.Ip).ToListAsync());
        Assert.Equal(0, await f.Store.PurgeExpiredAsync()); // idempotent
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FindBannedAccounts_ChunksLargeInputs(DatabaseProvider provider)
    {
        await using Fixture f = await Fixture.CreateAsync(_databases, provider, new BanClock(T0));
        await f.Store.BanAccountAsync(new BanRequest(3, 0, "x", "GM"));
        await f.Store.BanAccountAsync(new BanRequest(1150, 0, "y", "GM"));

        IReadOnlySet<int> banned = await f.Store.FindBannedAccountsAsync(Enumerable.Range(1, 1200).ToArray());
        Assert.Equal([3, 1150], banned.OrderBy(i => i));
        Assert.Equal(["8.8.8.8"], (await FindIpAsync(f)).ToArray());
    }

    private static async Task<IReadOnlySet<string>> FindIpAsync(Fixture f)
    {
        await f.Store.BanIpAsync(new IpBanRequest("8.8.8.8", 0, "x", "GM"));
        return await f.Store.FindBannedIpsAsync(["8.8.8.8", "9.9.9.9"]);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task BanAndUnban_PublishStatusEvents_AfterCommit(DatabaseProvider provider)
    {
        var events = new AccountStatusEvents();
        var seen = new List<AccountStatusChange>();
        var ipSeen = new List<IpBanChange>();
        var visibleDuringEvent = new List<int>();
        DatabaseConnectionOptions? cs = null;
        events.StatusChanged += c =>
        {
            seen.Add(c);
            using AuthDbContext fresh = TestContexts.Create<AuthDbContext>(cs!);
            visibleDuringEvent.Add(fresh.Set<AccountBanRow>().Count(r => r.AccountId == c.AccountId));
        };
        events.IpBanned += ipSeen.Add;
        events.StatusChanged += _ => throw new InvalidOperationException("faulty subscriber");
        var faults = new List<Exception>();
        events.SubscriberFaulted += faults.Add;

        await using Fixture f = await Fixture.CreateAsync(_databases, provider, new BanClock(T0), events);
        cs = f.Connection;
        await f.Store.BanAccountAsync(new BanRequest(7, 0, "x", "GM", AuthorAccountId: 99));
        await f.Store.BanAccountAsync(new BanRequest(8, 600, "x", "GM"));
        Assert.True(await f.Store.BanIpAsync(new IpBanRequest("5.5.5.5", 0, "x", "GM", 99)));
        Assert.False(await f.Store.BanIpAsync(new IpBanRequest("5.5.5.5", 0, "x", "GM"))); // no row, no event
        await f.Store.UnbanAccountAsync(7, "GM", "ok");

        Assert.Equal(
            [new AccountStatusChange(7, AccountStatus.Banned, 99), new AccountStatusChange(8, AccountStatus.Suspended),
                new AccountStatusChange(7, AccountStatus.Active)],
            seen);
        Assert.Equal([1, 1, 2], visibleDuringEvent); // the rows were committed (visible to a fresh context) when each event ran
        Assert.Equal([new IpBanChange("5.5.5.5", 99)], ipSeen);
        Assert.Equal(3, faults.Count); // the throwing subscriber never failed a mutation
    }

    [Fact]
    public void Evaluator_UsesTheRetailPredicate()
    {
        var perm = new AccountBanRecord(1, 1, 100, 100, "a", "r", true, 1);
        var temp = new AccountBanRecord(2, 1, 100, 200, "a", "r", true, 1);
        var inactive = perm with { Active = false };
        Assert.True(AccountBanEvaluator.IsActive(perm, 1_000_000));
        Assert.True(AccountBanEvaluator.IsActive(temp, 199));
        Assert.False(AccountBanEvaluator.IsActive(temp, 200));
        Assert.False(AccountBanEvaluator.IsActive(inactive, 100));

        Assert.Equal(AccountStatus.Banned, AccountBanEvaluator.Effective(AccountStatus.Active, perm));
        Assert.Equal(AccountStatus.Suspended, AccountBanEvaluator.Effective(AccountStatus.Active, temp));
        Assert.Equal(AccountStatus.Active, AccountBanEvaluator.Effective(AccountStatus.Active, null));
        Assert.Equal(AccountStatus.Suspended, AccountBanEvaluator.Effective(AccountStatus.Suspended, null));
        Assert.Equal(AccountStatus.Banned, AccountBanEvaluator.Effective(AccountStatus.Banned, temp)); // column override wins
    }

    [Theory]
    [InlineData("127.0.0.1:5000", "127.0.0.1")]
    [InlineData("[::ffff:10.1.2.3]:99", "10.1.2.3")]
    [InlineData("[::1]:80", "::1")]
    [InlineData("test", null)]
    [InlineData("", null)]
    public void AddressOfEndpoint_ParsesAndNormalises(string endpoint, string? expected)
        => Assert.Equal(expected, AccountBanEvaluator.AddressOfEndpoint(endpoint));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AuthDbContext _db;

        private Fixture(DatabaseConnectionOptions connection, AuthDbContext db, EfBanStore store)
        {
            Connection = connection;
            _db = db;
            Store = store;
        }

        public DatabaseConnectionOptions Connection { get; }

        public EfBanStore Store { get; }

        public static async Task<Fixture> CreateAsync(
            TestDatabases databases, DatabaseProvider provider, TimeProvider clock, AccountStatusEvents? events = null)
        {
            DatabaseConnectionOptions cs = await databases.CreateAsync(provider);
            AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
            return new Fixture(cs, db, new EfBanStore(db, clock, events));
        }

        public ValueTask DisposeAsync() => _db.DisposeAsync();
    }
}

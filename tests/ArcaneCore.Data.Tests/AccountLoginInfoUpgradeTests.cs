using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class AccountLoginInfoUpgradeTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void AuthSchema_IncludesVersion6_WithTheLoginInfoModule()
    {
        Assert.Equal(6, AccountLoginInfoDataModule.Version);
        Assert.True(AuthDbContext.Schema.CurrentVersion >= AccountLoginInfoDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.Auth), m => m is AccountLoginInfoDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AuthV5_UpgradesToV6_PreservesAccountAndRoundTripsLoginInfo(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (AuthM5Context old = TestContexts.Create<AuthM5Context>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(old, AuthM5Context.Schema);
            old.Accounts.Add(new AccountV1Row { Username = "KEEPER", Salt = new byte[32], Verifier = new byte[32] });
            await old.SaveChangesAsync();
        }
        await using (AuthDbContext v5 = TestContexts.Create<AuthDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(v5, SchemaProbe.ThroughVersion(AuthDbContext.Schema, 5));
            Assert.Equal(5, (await v5.Set<SchemaVersionRow>().SingleAsync()).Version);
        }
        for (int pass = 0; pass < 2; pass++)
        {
            await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
            await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
            Assert.Equal(AuthDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version); // v6 and anything after it
            Account row = await db.Accounts.SingleAsync();
            Assert.Equal("KEEPER", row.Username);
            Assert.Equal(string.Empty, row.LastIp);
            Assert.Null(row.LastLoginUtc);
            Assert.Equal(string.Empty, row.LastOs);
            Assert.Equal(string.Empty, row.LastPlatform);
            Assert.Equal(string.Empty, row.LastLocale);
            Assert.Equal(0, row.LastBuild);
        }

        DateTime when = new(2026, 10, 24, 12, 30, 15, DateTimeKind.Utc);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            var store = new EfAccountStore(db);
            AccountLoginInfo? before = await store.GetLoginInfoAsync("keeper");
            Assert.Equal(new AccountLoginInfo(null, string.Empty, string.Empty, string.Empty, 0), before);
            await store.RecordLoginAsync("keeper", new AccountLoginInfo(when, "Win", "x86", "enUS", 5875));
            await store.RecordLoginAsync("NOBODY", new AccountLoginInfo(when, "Win", "x86", "enUS", 5875)); // unknown: ignored
            Assert.Null(await store.GetLoginInfoAsync("NOBODY"));
        }
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            AccountLoginInfo? stored = await new EfAccountStore(db).GetLoginInfoAsync("KEEPER");
            Assert.Equal(new AccountLoginInfo(when, "Win", "x86", "enUS", 5875), stored);
            Assert.Equal(DateTimeKind.Utc, stored!.LastLoginUtc!.Value.Kind);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RecordLogin_OverwritesPreviousRecord_MatchesUsernameCaseInsensitively_AndKeepsFullBuildRange(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        db.Accounts.Add(new Account { Username = "ALPHA", Salt = new byte[32], Verifier = new byte[32] });
        db.Accounts.Add(new Account { Username = "BRAVO", Salt = new byte[32], Verifier = new byte[32] });
        await db.SaveChangesAsync();
        var store = new EfAccountStore(db);

        DateTime first = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DateTime second = new(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        await store.RecordLoginAsync("AlPhA", new AccountLoginInfo(first, "Win", "x86", "enUS", 5875));
        await store.RecordLoginAsync("alpha", new AccountLoginInfo(second, "OSX", "PPC", "deDE", ushort.MaxValue));

        Assert.Equal(new AccountLoginInfo(second, "OSX", "PPC", "deDE", ushort.MaxValue), await store.GetLoginInfoAsync("ALPHA"));
        // The other account is untouched.
        Assert.Equal(new AccountLoginInfo(null, string.Empty, string.Empty, string.Empty, 0), await store.GetLoginInfoAsync("BRAVO"));

        await store.RecordLoginAsync("ALPHA", new AccountLoginInfo(null, string.Empty, string.Empty, string.Empty, 0));
        Assert.Equal(new AccountLoginInfo(null, string.Empty, string.Empty, string.Empty, 0), await store.GetLoginInfoAsync("alpha"));
    }

    [Fact]
    public async Task RecordLogin_RejectsNullInfo()
    {
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(await _databases.CreateAsync(DatabaseProvider.Sqlite));
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        await Assert.ThrowsAsync<ArgumentNullException>(() => new EfAccountStore(db).RecordLoginAsync("X", null!));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

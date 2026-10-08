using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class AccountLoginSecurityUpgradeTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AuthV4_UpgradesToV5_PreservesAccountAndSupportsFactorWrites(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (AuthM5Context old = TestContexts.Create<AuthM5Context>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(old, AuthM5Context.Schema);
            old.Accounts.Add(new AccountV1Row { Username = "KEEPER", Salt = new byte[32], Verifier = new byte[32] });
            await old.SaveChangesAsync();
        }
        await using (AuthDbContext v4 = TestContexts.Create<AuthDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(v4, SchemaProbe.ThroughVersion(AuthDbContext.Schema, 4));
            Assert.Equal(4, (await v4.Set<SchemaVersionRow>().SingleAsync()).Version);
        }
        for (int pass = 0; pass < 2; pass++)
        {
            await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
            await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
            Assert.Equal(5, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            Account row = await db.Accounts.SingleAsync();
            Assert.Equal("KEEPER", row.Username);
            Assert.Equal(AccountLockFlags.None, row.LockFlags);
            Assert.Equal(string.Empty, row.SecurityInfo);
            Assert.Equal(string.Empty, row.LastIp);
        }
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            var store = new ArcaneCore.Data.Stores.EfAccountStore(db);
            Assert.True(await store.SetAsync("KEEPER", AccountLockFlags.FixedPin | AccountLockFlags.AlwaysEnforce, "123456"));
            Assert.Equal("123456", (await store.FindByUsernameAsync("KEEPER"))!.SecurityInfo);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("KEEPER", AccountLockFlags.Totp, "not a base32 secret!"));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

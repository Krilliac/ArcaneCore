using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Login;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Login;

public sealed class AccountCredentialStoreTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 6, 19, 0, 0, DateTimeKind.Utc);
    private readonly TestDatabases _databases = new();

    [Fact]
    public void CredentialModule_IsAuthVersion9_AndDoesNotRegisterCredentialsGlobally()
    {
        Assert.Equal(9, AccountCredentialDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.Auth), module => module is AccountCredentialDataModule);
        var services = new ServiceCollection();
        new AccountCredentialDataModule().AddServices(services);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAccountCredentialStore));
    }

    [Fact]
    public async Task CredentialLookupNormalizesEmailAndReturnsCopies()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, AuthDbContext.Schema);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            db.Accounts.Add(new Account { Id = 7, Username = "TEST", Salt = new byte[32], Verifier = new byte[32] });
            await db.SaveChangesAsync();
            await new EfAccountCredentialStore(db).CreateAsync(Credential(7, "User@Example.com"));
        }
        await using (AuthDbContext fresh = TestContexts.Create<AuthDbContext>(connection))
        {
            AccountCredential? found = await new EfAccountCredentialStore(fresh).FindByEmailNormalizedAsync(" USER@example.COM ");
            Assert.NotNull(found);
            found!.SecurityStamp[0] ^= 0xff;
            AccountCredential? unchanged = await new EfAccountCredentialStore(fresh).FindByEmailNormalizedAsync("user@example.com");
            Assert.Equal(0x42, unchanged!.SecurityStamp[0]);
        }
    }

    [Fact]
    public async Task FailureLockoutAndCompletionAreAtomicAndStampBound()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, AuthDbContext.Schema);
        byte[] stamp = Enumerable.Repeat((byte)0x42, 16).ToArray();
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            db.Accounts.Add(new Account { Id = 8, Username = "LOCK", Salt = new byte[32], Verifier = new byte[32] });
            await db.SaveChangesAsync();
            await new EfAccountCredentialStore(db).CreateAsync(Credential(8, "lock@example.com", stamp));
        }
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            var store = new EfAccountCredentialStore(db);
            Assert.True(await store.TryRecordFailureAsync(8, stamp, Now, 2, TimeSpan.FromMinutes(1)));
            Assert.True(await store.TryRecordFailureAsync(8, stamp, Now.AddSeconds(1), 2, TimeSpan.FromMinutes(1)));
            Assert.False(await store.TryCompleteLoginAsync(8, stamp, Now.AddSeconds(2), null));
            Assert.False(await store.TryRecordFailureAsync(8, new byte[16], Now.AddSeconds(2), 2, TimeSpan.FromMinutes(1)));
            Assert.True(await store.TryCompleteLoginAsync(8, stamp, Now.AddMinutes(2), null));
        }
    }

    [Fact]
    public async Task CompletionRequiresVerificationAndRejectsTotpReplay()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, AuthDbContext.Schema);
        byte[] stamp = Enumerable.Repeat((byte)0x52, 16).ToArray();
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            db.Accounts.Add(new Account { Id = 9, Username = "TOTP", Salt = new byte[32], Verifier = new byte[32] });
            await db.SaveChangesAsync();
            var store = new EfAccountCredentialStore(db);
            AccountCredential initial = Credential(9, "totp@example.com", stamp);
            initial.EmailVerifiedUtc = null;
            initial.TotpKeyId = 1;
            initial.TotpEnabledUtc = Now;
            initial.TotpSecretEncrypted = new byte[29];
            await store.CreateAsync(initial);
            Assert.False(await store.TryCompleteLoginAsync(9, stamp, Now, 10));
        }
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            AccountCredential credential = await db.Set<AccountCredential>().SingleAsync(x => x.AccountId == 9);
            credential.EmailVerifiedUtc = Now;
            await db.SaveChangesAsync();
        }
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            var store = new EfAccountCredentialStore(db);
            Assert.True(await store.TryCompleteLoginAsync(9, stamp, Now, 10));
            Assert.False(await store.TryCompleteLoginAsync(9, stamp, Now, 10));
        }
    }

    private static AccountCredential Credential(int accountId, string email, byte[]? stamp = null, DateTime? verified = null) => new()
    {
        AccountId = accountId, Email = email, EmailNormalized = IAccountCredentialStore.NormalizeEmail(email),
        EmailVerifiedUtc = verified ?? Now, PasswordHash = "hash", PasswordChangedUtc = Now,
        SecurityStamp = stamp?.ToArray() ?? Enumerable.Repeat((byte)0x42, 16).ToArray(),
    };

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

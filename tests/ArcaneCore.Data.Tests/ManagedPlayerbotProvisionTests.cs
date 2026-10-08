using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Auth.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ManagedPlayerbotProvisionTests
{
    [Fact]
    public async Task Sqlite_Auth3ToCurrentUpgrade_CreatesProvisionJournal()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options;
        await using AuthDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, 3));
        await db.Database.ExecuteSqlRawAsync("DROP TABLE managed_playerbot_provision");

        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);

        Assert.Equal(AuthDbContext.Schema.CurrentVersion,
            (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'managed_playerbot_provision'").SingleAsync());
    }

    [Fact]
    public async Task Sqlite_CreateLoadAndComplete_IsAtomicAndIdempotent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options;
        await using AuthDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        var store = new EfManagedPlayerbotProvisionStore(db);
        Guid botId = Guid.NewGuid();
        Account account = NewAccount("PBNEW0001");

        Account stored = await store.CreateAsync(botId, account);
        Assert.True(stored.Id > 0);
        ManagedPlayerbotProvision pending = Assert.Single(await store.LoadPendingAsync());
        Assert.Equal((botId, stored.Id, "PBNEW0001"), (pending.BotId, pending.AccountId, pending.AccountName));
        Assert.True(await store.CompleteAsync(botId, stored.Id));
        Assert.False(await store.CompleteAsync(botId, stored.Id));
        Assert.Empty(await store.LoadPendingAsync());
        Assert.NotNull(await db.Accounts.SingleOrDefaultAsync(a => a.Id == stored.Id));
    }

    [Fact]
    public async Task Sqlite_ExistingHumanOrNonP0Account_IsRefusedWithoutJournalChange()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options;
        await using AuthDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        db.Accounts.Add(NewAccount("HUMAN"));
        db.Accounts.Add(NewAccount("STAFF", AccountSecurity.GameMaster));
        db.Accounts.Add(NewAccount("KEYED", AccountSecurity.Player, new byte[40]));
        await db.SaveChangesAsync();
        var store = new EfManagedPlayerbotProvisionStore(db);

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(Guid.NewGuid(), new Account
        {
            Id = 1, Username = "HUMAN", Salt = new byte[32], Verifier = new byte[32],
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(Guid.NewGuid(), NewAccount("STAFF", AccountSecurity.GameMaster)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(Guid.NewGuid(), NewAccount("KEYED", AccountSecurity.Player, new byte[40])));

        Assert.Empty(await store.LoadPendingAsync());
        Assert.Equal(3, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task Sqlite_RollbackRequiresExactUnchangedP0Owner_AndIsIdempotent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).Options;
        await using AuthDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        var store = new EfManagedPlayerbotProvisionStore(db);
        Guid botId = Guid.NewGuid();
        Account stored = await store.CreateAsync(botId, NewAccount("PBROLLBK"));

        stored.Security = AccountSecurity.Moderator;
        await db.SaveChangesAsync();
        Assert.False(await store.RollbackEmptyOwnerAsync(botId, stored.Id));
        Assert.NotNull(await db.Accounts.SingleOrDefaultAsync(a => a.Id == stored.Id));

        stored.Security = AccountSecurity.Player;
        stored.SessionKey = new byte[40];
        await db.SaveChangesAsync();
        Assert.False(await store.RollbackEmptyOwnerAsync(botId, stored.Id));

        stored.SessionKey = null;
        await db.SaveChangesAsync();
        Assert.True(await store.RollbackEmptyOwnerAsync(botId, stored.Id));
        Assert.False(await store.RollbackEmptyOwnerAsync(botId, stored.Id));
        Assert.Null(await db.Accounts.SingleOrDefaultAsync(a => a.Id == stored.Id));
        Assert.Empty(await store.LoadPendingAsync());
    }

    private static Account NewAccount(string username, AccountSecurity security = AccountSecurity.Player, byte[]? sessionKey = null)
        => new() { Username = username, Salt = new byte[32], Verifier = new byte[32], Security = security, SessionKey = sessionKey };
}

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

    /// <summary>Two workers' stores: two contexts (a context is one worker's) over one SQLite file, as the split cluster shares one auth database.</summary>
    private sealed class TwoWorkers : IAsyncDisposable
    {
        private readonly string _path;

        private TwoWorkers(AuthDbContext a, AuthDbContext b, string path)
        {
            _path = path;
            DbA = a;
            DbB = b;
            A = new EfManagedPlayerbotProvisionStore(a);
            B = new EfManagedPlayerbotProvisionStore(b);
        }

        public AuthDbContext DbA { get; }

        public AuthDbContext DbB { get; }

        public EfManagedPlayerbotProvisionStore A { get; }

        public EfManagedPlayerbotProvisionStore B { get; }

        public static async Task<TwoWorkers> StartAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), $"arcanecore-provision-{Guid.NewGuid():N}.db");
            DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var a = new AuthDbContext(options);
            await SchemaBootstrapper.EnsureAsync(a, AuthDbContext.Schema);
            return new TwoWorkers(a, new AuthDbContext(options), path);
        }

        public async ValueTask DisposeAsync()
        {
            await DbA.DisposeAsync();
            await DbB.DisposeAsync();
            try { File.Delete(_path); }
            catch (IOException) { /* a temp file the OS still holds is removed with the temp folder */ }
        }
    }

    [Fact]
    public async Task Sqlite_TwoWorkers_SeeEachOthersPendingRows_AndOnlyOneCompletesARow()
    {
        await using TwoWorkers workers = await TwoWorkers.StartAsync();
        Guid botId = Guid.NewGuid();
        Account stored = await workers.B.CreateAsync(botId, NewAccount("PBTWOW001"));

        ManagedPlayerbotProvision seen = Assert.Single(await workers.A.LoadPendingAsync()); // worker A reads worker B's pending row
        Assert.Equal((botId, stored.Id, "PBTWOW001"), (seen.BotId, seen.AccountId, seen.AccountName));
        Assert.True(await workers.A.CompleteAsync(botId, stored.Id)); // what a sweep that took a young row would do ...
        Assert.False(await workers.B.CompleteAsync(botId, stored.Id)); // ... makes the creator's own completion refused: the reason the split sweep takes only old rows
        Assert.Empty(await workers.B.LoadPendingAsync());
    }

    [Fact]
    public async Task Sqlite_TwoWorkers_RollBackTheSameRowOnce_AndTheSecondIsRefusedWithoutAThrow()
    {
        await using TwoWorkers workers = await TwoWorkers.StartAsync();
        Guid botId = Guid.NewGuid();
        Account stored = await workers.B.CreateAsync(botId, NewAccount("PBTWOW002"));

        Assert.True(await workers.A.RollbackEmptyOwnerAsync(botId, stored.Id));
        Assert.False(await workers.B.RollbackEmptyOwnerAsync(botId, stored.Id));
        Assert.False(await workers.B.CompleteAsync(botId, stored.Id)); // the creator that lost the race learns it from the refusal

        Assert.Null(await workers.DbA.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == stored.Id));
        Assert.Null(await workers.DbB.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == stored.Id));
        Assert.Empty(await workers.A.LoadPendingAsync());
    }

    [Fact]
    public async Task Sqlite_TwoWorkers_CreatingTheSameAccountName_RefuseTheSecond_AndLeaveOneRow()
    {
        await using TwoWorkers workers = await TwoWorkers.StartAsync();
        Account first = await workers.A.CreateAsync(Guid.NewGuid(), NewAccount("PBTWOW003"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => workers.B.CreateAsync(Guid.NewGuid(), NewAccount("PBTWOW003")));

        ManagedPlayerbotProvision only = Assert.Single(await workers.B.LoadPendingAsync());
        Assert.Equal(first.Id, only.AccountId);
        Assert.Equal(1, await workers.DbB.Accounts.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Sqlite_TwoWorkers_CreatingTheSameAccountNameAtTheSameTime_ExactlyOneWins()
    {
        // Both creators run on their own thread and context against one file, so both can pass the AnyAsync pre-check and race into the insert: the loser is refused by the pre-check, the unique
        // index or the database lock, whichever it hits, and leaves nothing behind.
        for (int round = 0; round < 8; round++)
        {
            await using TwoWorkers workers = await TwoWorkers.StartAsync();
            string name = $"PBRACE{round:D3}";
            using var gate = new ManualResetEventSlim(false);
            Task<Account> Create(EfManagedPlayerbotProvisionStore store) => Task.Run(() =>
            {
                gate.Wait();
                return store.CreateAsync(Guid.NewGuid(), NewAccount(name));
            });
            Task<Account> first = Create(workers.A);
            Task<Account> second = Create(workers.B);
            gate.Set();

            try { await Task.WhenAll(first, second); }
            catch (Exception) when (first.IsFaulted || second.IsFaulted) { /* the loser's refusal */ }
            Assert.Equal(1, new[] { first, second }.Count(t => t.IsCompletedSuccessfully));
            Assert.Equal(1, new[] { first, second }.Count(t => t.IsFaulted));
            Account winner = (first.IsCompletedSuccessfully ? first : second).Result;
            ManagedPlayerbotProvision only = Assert.Single(await workers.A.LoadPendingAsync());
            Assert.Equal(winner.Id, only.AccountId);
            Assert.Equal(1, await workers.DbB.Accounts.AsNoTracking().CountAsync(a => a.Username == name));
        }
    }

    [Fact]
    public async Task Sqlite_TwoWorkers_RollingBackTheSameRowAtTheSameTime_RollBackExactlyOnce()
    {
        // What two sweeps taking the same stale row do: only one removes the empty account and its journal row, the other is refused (or loses the database lock) without leaving a half state.
        for (int round = 0; round < 8; round++)
        {
            await using TwoWorkers workers = await TwoWorkers.StartAsync();
            Guid botId = Guid.NewGuid();
            Account stored = await workers.A.CreateAsync(botId, NewAccount($"PBRBACK{round:D3}"));
            using var gate = new ManualResetEventSlim(false);
            Task<bool> Rollback(EfManagedPlayerbotProvisionStore store) => Task.Run(() =>
            {
                gate.Wait();
                return store.RollbackEmptyOwnerAsync(botId, stored.Id);
            });
            Task<bool> first = Rollback(workers.A);
            Task<bool> second = Rollback(workers.B);
            gate.Set();

            try { await Task.WhenAll(first, second); }
            catch (Exception) when (first.IsFaulted || second.IsFaulted) { /* a worker that lost the database lock: it did not roll back */ }
            Assert.Equal(1, new[] { first, second }.Count(t => t.IsCompletedSuccessfully && t.Result));
            Assert.Null(await workers.DbA.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == stored.Id));
            Assert.Empty(await workers.B.LoadPendingAsync());
        }
    }

    private static Account NewAccount(string username, AccountSecurity security = AccountSecurity.Player, byte[]? sessionKey = null)
        => new() { Username = username, Salt = new byte[32], Verifier = new byte[32], Security = security, SessionKey = sessionKey };
}

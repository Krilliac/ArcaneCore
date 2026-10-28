using System.Text;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Microsoft.EntityFrameworkCore;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// Account data writes against the real scoped EF store (a pooled <see cref="CharacterDbContext"/> on a SQLite file, the production wiring
/// of <c>AddCharacterDatabase</c>), not an in-memory singleton: the store a session holds belongs to its socket's scope and context, so the
/// throttle must neither flush through it from another thread nor lose a held value when the session or the world ends.
/// </summary>
public sealed class AccountDataPersistenceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private const int LongIntervalMs = 60_000;

    [Fact]
    public async Task Disconnect_InsideTheInterval_StillPersistsTheLatestBlob()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(LongIntervalMs);
        byte[] key = await host.AddAccountAsync("ADLOGOUT");
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("ADLOGOUT", key);
        await client.CollectAsync();
        int accountId = (await host.Accounts.FindByUsernameAsync("ADLOGOUT"))!.Id;

        // The 1.12 client's logout burst: the first change is written at once, the rest are held inside the interval.
        await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 1"));
        await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 2"));
        await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 3"));
        await client.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await client.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        await WorldTestHost.WaitForAsync(() => db.DataWrites == 1, "the first change to be written at once");

        await client.DisposeAsync(); // the interval (60 s) is nowhere near over

        await db.WaitForBlobAsync(accountId, 0, "SET a 3");
        Assert.Equal(2, db.DataWrites); // the first change, then the latest value once: the held middle value was coalesced away
    }

    [Fact]
    public async Task WorldStop_DrainsTheValuesStillHeld()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(LongIntervalMs);
        AccountDataFeature feature = host.WorldServices.GetRequiredService<AccountDataFeature>();
        Assert.True(feature.IsAttached);

        // No session involved: only the world-stop drain can persist what the throttle holds.
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        feature.SaveData(77, 2, new AccountDataEntry(now, Encoding.ASCII.GetBytes("first")), LongIntervalMs);
        feature.SaveData(77, 2, new AccountDataEntry(now, Encoding.ASCII.GetBytes("held 1")), LongIntervalMs);
        feature.SaveData(77, 2, new AccountDataEntry(now, Encoding.ASCII.GetBytes("held 2")), LongIntervalMs);
        feature.SaveTutorials(77, [1, 2, 3, 4, 5, 6, 7, 8], LongIntervalMs);
        feature.SaveTutorials(77, [9, 9, 9, 9, 9, 9, 9, 9], LongIntervalMs);

        await feature.StopAsync().WaitAsync(Budget);

        await db.WaitForBlobAsync(77, 2, "held 2");
        Assert.Equal(new uint[] { 9, 9, 9, 9, 9, 9, 9, 9 }, await db.ReadTutorialsAsync(77));
        Assert.InRange(db.DataWrites, 1, 2); // "first" at once (the queue may still coalesce it with the drained value), "held 2" at the drain
    }

    [Fact]
    public async Task Burst_OfManyWrites_IsCoalesced_AndTheLastValueIsStored()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(LongIntervalMs);
        byte[] key = await host.AddAccountAsync("ADBURST");
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("ADBURST", key);
        await client.CollectAsync();
        int accountId = (await host.Accounts.FindByUsernameAsync("ADBURST"))!.Id;

        const int burst = 40;
        for (int i = 1; i <= burst; i++)
        {
            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, $"SET a {i}"));
        }

        await client.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await client.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        await WorldTestHost.WaitForAsync(() => db.DataWrites == 1, "the first change to be written at once");
        await Task.Delay(300);
        Assert.Equal(1, db.DataWrites); // 40 packets, one write while the connection is open

        await client.DisposeAsync();
        await db.WaitForBlobAsync(accountId, 0, $"SET a {burst}");
        Assert.Equal(2, db.DataWrites); // at most the first value and the last one
    }

    [Fact]
    public async Task ASecondSession_OfTheSameAccount_SharesTheThrottle_AndLosesNothing()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(LongIntervalMs);
        byte[] key = await host.AddAccountAsync("ADRECON");
        int accountId = (await host.Accounts.FindByUsernameAsync("ADRECON"))!.Id;

        WorldTestClient first = await host.ConnectAsync();
        await first.AuthenticateAsync("ADRECON", key);
        await first.CollectAsync();
        await first.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(1, "first session"));
        await first.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(1, "first session, latest"));
        await first.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await first.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        await first.DisposeAsync();
        await db.WaitForBlobAsync(accountId, 1, "first session, latest");

        WorldTestClient second = await host.ConnectAsync();
        await second.AuthenticateAsync("ADRECON", key);
        await second.CollectAsync();
        await second.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(1, "second session"));
        await second.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await second.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        await second.DisposeAsync();
        await db.WaitForBlobAsync(accountId, 1, "second session");
    }

    [Fact]
    public async Task ASecondSession_ConnectingBeforeTheFirstOnesFlush_ServesTheLatestBlob()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(LongIntervalMs);
        byte[] key = await host.AddAccountAsync("ADRACE");
        int accountId = (await host.Accounts.FindByUsernameAsync("ADRACE"))!.Id;

        WorldTestClient first = await host.ConnectAsync();
        await first.AuthenticateAsync("ADRACE", key);
        await first.CollectAsync();
        await first.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 1"));
        await first.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 2")); // held inside the interval
        await first.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await first.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        await WorldTestHost.WaitForAsync(() => db.DataWrites == 1, "the first change to be written at once");

        // The first session is still open: its held value has not been flushed when the second one authenticates.
        WorldTestClient second = await host.ConnectAsync();
        await second.AuthenticateAsync("ADRACE", key);
        await second.CollectAsync();
        await second.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(0u));
        byte[] reply = await second.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        Assert.Equal("SET a 2", await Inflate(reply));
        await second.DisposeAsync(); // the last session of the account out queues what the throttle still holds
        await db.WaitForBlobAsync(accountId, 0, "SET a 2");
    }

    [Fact]
    public async Task ASlowWrite_OfOneAccount_DoesNotDelayAnotherAccountsLogin_AndAReconnectStillServesTheNewestBlob()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(LongIntervalMs);
        byte[] keyA = await host.AddAccountAsync("ADSLOWA");
        byte[] keyB = await host.AddAccountAsync("ADSLOWB");
        int accountA = (await host.Accounts.FindByUsernameAsync("ADSLOWA"))!.Id;
        db.BlockWritesOf(accountA);
        try
        {
            WorldTestClient a1 = await host.ConnectAsync();
            await a1.AuthenticateAsync("ADSLOWA", keyA);
            await a1.CollectAsync();
            await a1.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a slow"));
            await a1.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
            await a1.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
            await WorldTestHost.WaitForAsync(() => db.BlockedWrites == 1, "the account's write to be in progress (and stuck)");

            // The queue's consumer is stuck in A's write: no login may wait for it.
            WorldTestClient b = await host.ConnectAsync();
            await b.AuthenticateAsync("ADSLOWB", keyB).WaitAsync(TimeSpan.FromSeconds(5));

            // A reconnect of A is not delayed either, and serves the value that is not in the database yet.
            WorldTestClient a2 = await host.ConnectAsync();
            await a2.AuthenticateAsync("ADSLOWA", keyA).WaitAsync(TimeSpan.FromSeconds(5));
            await a2.CollectAsync();
            await a2.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(0u));
            byte[] reply = await a2.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
            Assert.Equal("SET a slow", await Inflate(reply));
        }
        finally
        {
            db.ReleaseWrites();
        }

        await db.WaitForBlobAsync(accountA, 0, "SET a slow");
    }

    [Fact]
    public async Task AGatewayBackedSession_ServesTheNewestBlob_OfAnEarlierSocketSession_AndReleasesTheAccount()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(LongIntervalMs);
        AccountDataFeature feature = host.WorldServices.GetRequiredService<AccountDataFeature>();
        byte[] key = await host.AddAccountAsync("ADGATE");
        int accountId = (await host.Accounts.FindByUsernameAsync("ADGATE"))!.Id;

        WorldTestClient first = await host.ConnectAsync();
        await first.AuthenticateAsync("ADGATE", key);
        await first.CollectAsync();
        await first.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 1"));
        await first.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 2")); // held inside the interval
        await first.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await first.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);

        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        WorldSession remote = WorldSession.CreateRemote(
            new NullRemoteOutput(), "127.0.0.1:41002", scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        Assert.Equal(AuthResponseCode.Ok, await remote.AuthenticateRemoteAsync(accountId, "ADGATE", 5875, CancellationToken.None));
        Assert.Equal("SET a 2", Encoding.ASCII.GetString(remote.Settings.Data[0]!.Data));

        remote.AbandonRemote();
        await db.WaitForBlobAsync(accountId, 0, "SET a 2");
        await WorldTestHost.WaitForAsync(() => feature.TrackedAccounts == 0, "the account's throttle state to be released");
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task ShortInterval_TheFlushTimer_PersistsTheHeldValue_WhileTheSessionIsStillOpen()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(ShortIntervalMs);
        byte[] key = await host.AddAccountAsync("ADTIMER");
        int accountId = (await host.Accounts.FindByUsernameAsync("ADTIMER"))!.Id;

        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("ADTIMER", key);
        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 1"));
        await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "SET a 2")); // held; the timer queues it
        await client.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await client.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);

        // The session never disconnects: only the flush timer can write the second value.
        await db.WaitForBlobAsync(accountId, 0, "SET a 2");
        Assert.Equal(2, db.DataWrites);
        await client.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task AStaleFlushTimer_OfAnEndedSession_DoesNotRemoveTheNextSessionsState_AndTheHeldValueSurvivesStop()
    {
        const int interval = 2000;
        await using var db = await TestDatabase.CreateAsync();
        await using var host = db.StartHost(interval);
        AccountDataFeature feature = host.WorldServices.GetRequiredService<AccountDataFeature>();
        byte[] key = await host.AddAccountAsync("ADSTALE");
        int accountId = (await host.Accounts.FindByUsernameAsync("ADSTALE"))!.Id;

        // Session A: one write at once, one held (a flush timer is scheduled for t0 + interval), then A leaves and the held value is confirmed.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        WorldTestClient a = await host.ConnectAsync();
        await a.AuthenticateAsync("ADSTALE", key);
        await a.CollectAsync();
        await a.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "A 1"));
        await a.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "A 2"));
        await a.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await a.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        await a.DisposeAsync();
        await db.WaitForBlobAsync(accountId, 0, "A 2");
        await WorldTestHost.WaitForAsync(() => feature.TrackedAccounts == 0, "A's state to be released");

        // Session B starts half an interval later: its own flush timer is due at t(B) + interval, after A's stale one.
        await Task.Delay(interval / 2);
        WorldTestClient b = await host.ConnectAsync();
        await b.AuthenticateAsync("ADSTALE", key);
        await b.CollectAsync();
        await b.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "B 1"));
        await b.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, "B 2")); // held
        await b.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await b.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
        await db.WaitForBlobAsync(accountId, 0, "B 1");
        Assert.True(clock.ElapsedMilliseconds < interval + interval / 2 - 100, "B must still be inside the interval");

        // Past A's timer (t0 + interval) but before B's (about t0 + 1.5 interval).
        long wait = interval + 150 - clock.ElapsedMilliseconds;
        if (wait > 0)
        {
            await Task.Delay((int)wait);
        }

        Assert.Equal(1, feature.TrackedAccounts); // A's timer must not have forgotten B's state
        Assert.True(clock.ElapsedMilliseconds < interval + interval / 2 + 100, "B's own timer must not have fired yet");

        await feature.StopAsync().WaitAsync(Budget); // queues what is held in the accounts still tracked
        await db.WaitForBlobAsync(accountId, 0, "B 2");
        await b.DisposeAsync();
    }

    private const int ShortIntervalMs = 300;

    private static async Task<string> Inflate(byte[] reply)
    {
        using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(reply, 8, reply.Length - 8), System.IO.Compression.CompressionMode.Decompress);
        using var text = new MemoryStream();
        await zlib.CopyToAsync(text);
        return Encoding.ASCII.GetString(text.ToArray());
    }

    private sealed class NullRemoteOutput : IRemoteSessionOutput
    {
        public bool TrySend(WorldOpcode opcode, ReadOnlySpan<byte> payload) => true;
    }

    private static byte[] AccountData(uint type, string text)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(type));
        bytes.AddRange(BitConverter.GetBytes((uint)data.Length));
        using var buffer = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        bytes.AddRange(buffer.ToArray());
        return [.. bytes];
    }

    /// <summary>A SQLite file holding the characters schema, and the production-shaped wiring of the account data store over it.</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private readonly DbContextOptions<CharacterDbContext> _options;
        private int _dataWrites;
        private int _blockedAccount = -1;
        private int _blockedWrites;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TestDatabase(string path)
        {
            _path = path;
            _options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(ConnectionString(path)).Options;
        }

        /// <summary>Calls to <see cref="IAccountDataStore.SaveDataAsync"/> that reached the EF store.</summary>
        public int DataWrites => Volatile.Read(ref _dataWrites);

        public static async Task<TestDatabase> CreateAsync()
        {
            var database = new TestDatabase(Path.Combine(Path.GetTempPath(), $"arcanecore-account-data-{Guid.NewGuid():N}.db"));
            await using var context = new CharacterDbContext(database._options);
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        /// <summary>
        /// A world host whose <see cref="IAccountDataStore"/> is the scoped <see cref="EfAccountDataStore"/> over a pooled
        /// <see cref="CharacterDbContext"/> (pool size 1: a context is reused as soon as its scope ends), as <c>AddCharacterDatabase</c> wires it.
        /// </summary>
        public WorldTestHost StartHost(int intervalMs) => WorldTestHost.Start(
            configure: o => o.AccountDataWriteIntervalMs = intervalMs,
            configureServices: services =>
            {
                services.AddDbContextPool<CharacterDbContext>(builder => builder.UseSqlite(ConnectionString(_path)), poolSize: 1);
                services.AddScoped<IAccountDataStore>(provider => new CountingStore(
                    new EfAccountDataStore(provider.GetRequiredService<CharacterDbContext>()), async accountId =>
                    {
                        Interlocked.Increment(ref _dataWrites);
                        if (accountId == Volatile.Read(ref _blockedAccount))
                        {
                            Interlocked.Increment(ref _blockedWrites);
                            await _release.Task.WaitAsync(TimeSpan.FromSeconds(60));
                        }
                    }));
            });

        /// <summary>Writes in progress that <see cref="BlockWritesOf"/> is holding.</summary>
        public int BlockedWrites => Volatile.Read(ref _blockedWrites);

        /// <summary>From now on a blob write of this account does not finish until <see cref="ReleaseWrites"/> (a slow or hung database).</summary>
        public void BlockWritesOf(int accountId) => Volatile.Write(ref _blockedAccount, accountId);

        public void ReleaseWrites() => _release.TrySetResult();

        public async Task WaitForBlobAsync(int accountId, int type, string expected)
        {
            using var timeout = new CancellationTokenSource(Budget);
            string? last = null;
            while (true)
            {
                await using var context = new CharacterDbContext(_options);
                AccountDataRow? row = await context.AccountData.AsNoTracking().FirstOrDefaultAsync(r => r.AccountId == accountId && r.Type == type);
                last = row is null ? null : Encoding.ASCII.GetString(row.Data);
                if (last == expected)
                {
                    return;
                }

                if (timeout.IsCancellationRequested)
                {
                    Assert.Fail($"account {accountId} type {type}: expected '{expected}' in the database, found '{last}'");
                }

                await Task.Delay(20, CancellationToken.None);
            }
        }

        public async Task<uint[]> ReadTutorialsAsync(int accountId)
        {
            await using var context = new CharacterDbContext(_options);
            AccountTutorialRow row = await context.AccountTutorials.AsNoTracking().SingleAsync(r => r.AccountId == accountId);
            return [row.Tut0, row.Tut1, row.Tut2, row.Tut3, row.Tut4, row.Tut5, row.Tut6, row.Tut7];
        }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // a leftover temp file
            }

            return ValueTask.CompletedTask;
        }

        private static string ConnectionString(string path) => $"Data Source={path};Pooling=False";
    }

    private sealed class CountingStore(IAccountDataStore inner, Func<int, Task> beforeSaveData) : IAccountDataStore
    {
        public Task<AccountSettings> GetAsync(int accountId, CancellationToken cancellationToken = default) => inner.GetAsync(accountId, cancellationToken);

        public async Task SaveDataAsync(int accountId, int type, AccountDataEntry entry, CancellationToken cancellationToken = default)
        {
            await beforeSaveData(accountId);
            await inner.SaveDataAsync(accountId, type, entry, cancellationToken);
        }

        public Task SaveTutorialsAsync(int accountId, IReadOnlyList<uint> tutorials, CancellationToken cancellationToken = default)
            => inner.SaveTutorialsAsync(accountId, tutorials, cancellationToken);
    }
}

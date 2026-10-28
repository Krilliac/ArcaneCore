using System.Text;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(reply, 8, reply.Length - 8), System.IO.Compression.CompressionMode.Decompress);
        using var text = new MemoryStream();
        await zlib.CopyToAsync(text);
        Assert.Equal("SET a 2", Encoding.ASCII.GetString(text.ToArray()));
        await db.WaitForBlobAsync(accountId, 0, "SET a 2");
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
                    new EfAccountDataStore(provider.GetRequiredService<CharacterDbContext>()), () => Interlocked.Increment(ref _dataWrites)));
            });

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

    private sealed class CountingStore(IAccountDataStore inner, Action onSaveData) : IAccountDataStore
    {
        public Task<AccountSettings> GetAsync(int accountId, CancellationToken cancellationToken = default) => inner.GetAsync(accountId, cancellationToken);

        public Task SaveDataAsync(int accountId, int type, AccountDataEntry entry, CancellationToken cancellationToken = default)
        {
            onSaveData();
            return inner.SaveDataAsync(accountId, type, entry, cancellationToken);
        }

        public Task SaveTutorialsAsync(int accountId, IReadOnlyList<uint> tutorials, CancellationToken cancellationToken = default)
            => inner.SaveTutorialsAsync(accountId, tutorials, cancellationToken);
    }
}

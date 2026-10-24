using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// A real <see cref="LogonSession"/> over loopback with a simulated 1.12.1 client: the client details of a proven logon
/// reach <see cref="IAccountLoginInfoStore"/>, and a store that throws never fails the logon.
/// </summary>
public sealed class LogonRecordsLoginInfoTests
{
    private const string Username = "TESTER";
    private const string Password = "SECRET";

    [Fact]
    public async Task ProvenLogon_RecordsOsPlatformLocaleBuildAndTime()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        DateTime before = DateTime.UtcNow;

        await using NetworkStream client = await StartSessionAsync(accounts, accounts);
        Assert.Equal((byte)AuthResult.Success, await LogOnAsync(client, salt));
        await DrainAsync(client);

        AccountLoginInfo? info = await accounts.GetLoginInfoAsync(Username);
        Assert.NotNull(info);
        Assert.Equal("Win", info!.Os);
        Assert.Equal("x86", info.Platform);
        Assert.Equal("enUS", info.Locale);
        Assert.Equal(ClientBuild.Vanilla1121, info.Build);
        Assert.NotNull(info.LastLoginUtc);
        Assert.InRange(info.LastLoginUtc!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task RejectedProof_RecordsNothing()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);

        await using NetworkStream client = await StartSessionAsync(accounts, accounts);
        await client.WriteAsync(BuildChallenge(Username));
        (byte result, byte[] b, _) = await ReadChallengeAsync(client);
        Assert.Equal((byte)AuthResult.Success, result);
        await client.WriteAsync(BuildProof(ComputeClient(salt, b), wrongProof: true));
        byte[] reply = await ReadExactAsync(client, 2);
        Assert.NotEqual((byte)AuthResult.Success, reply[1]);

        Assert.Equal(new AccountLoginInfo(null, string.Empty, string.Empty, string.Empty, 0), await accounts.GetLoginInfoAsync(Username));
    }

    [Fact]
    public async Task RecordingFailure_StillLetsTheLogonSucceed()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        var failing = new ThrowingLoginInfoStore();

        await using NetworkStream client = await StartSessionAsync(accounts, failing);
        Assert.Equal((byte)AuthResult.Success, await LogOnAsync(client, salt));
        await DrainAsync(client);
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public async Task AutocreatedAccount_RecordsTheLoginInfoOfItsFirstLogon()
    {
        var accounts = new InMemoryAccountStore();

        await using NetworkStream client = await StartSessionAsync(accounts, accounts, autocreate: true);
        await client.WriteAsync(BuildChallenge(Username));
        (byte result, byte[] b, byte[] salt) = await ReadChallengeAsync(client);
        Assert.Equal((byte)AuthResult.Success, result);
        // The auto-create candidate's salt arrives in the challenge; the proof is computed against it (password == username).
        await client.WriteAsync(BuildProof(ComputeClient(salt, b, password: Username)));
        Assert.Equal((byte)AuthResult.Success, (await ReadExactAsync(client, 2))[1]);
        await DrainAsync(client);

        AccountLoginInfo? info = await accounts.GetLoginInfoAsync(Username);
        Assert.NotNull(info);
        Assert.Equal(new AccountLoginInfo(info!.LastLoginUtc, "Win", "x86", "enUS", ClientBuild.Vanilla1121), info);
        Assert.NotNull(info.LastLoginUtc);
    }

    [Fact]
    public async Task SecondLogon_OverwritesTheFirstRecord_WithTheNewLocaleAndTime()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        var clock = new StepClock(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero));

        await using (NetworkStream first = await StartSessionAsync(accounts, accounts, time: clock))
        {
            Assert.Equal((byte)AuthResult.Success, await LogOnAsync(first, salt));
            await DrainAsync(first);
        }
        Assert.Equal(new AccountLoginInfo(new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc), "Win", "x86", "enUS", ClientBuild.Vanilla1121),
            await accounts.GetLoginInfoAsync(Username));

        clock.Set(new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero));
        await using (NetworkStream second = await StartSessionAsync(accounts, accounts, time: clock))
        {
            Assert.Equal((byte)AuthResult.Success, await LogOnAsync(second, salt, wireLocale: "RFrf")); // frFR
            await DrainAsync(second);
        }
        Assert.Equal(new AccountLoginInfo(new DateTime(2026, 10, 25, 9, 30, 0, DateTimeKind.Utc), "Win", "x86", "frFR", ClientBuild.Vanilla1121),
            await accounts.GetLoginInfoAsync(Username));
    }

    [Fact]
    public async Task RecordedTime_IsUtcAndComesFromTheSessionClock()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        // A +05:00 offset: the stored instant must be the UTC equivalent, not the local wall clock.
        var clock = new StepClock(new DateTimeOffset(2026, 3, 1, 5, 0, 0, TimeSpan.FromHours(5)));

        await using NetworkStream client = await StartSessionAsync(accounts, accounts, time: clock);
        Assert.Equal((byte)AuthResult.Success, await LogOnAsync(client, salt));
        await DrainAsync(client);

        AccountLoginInfo? info = await accounts.GetLoginInfoAsync(Username);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), info!.LastLoginUtc);
        Assert.Equal(DateTimeKind.Utc, info.LastLoginUtc!.Value.Kind);
    }

    [Fact]
    public async Task NoLoginInfoStore_LogonStillSucceeds()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);

        await using NetworkStream client = await StartSessionAsync(accounts, null);
        Assert.Equal((byte)AuthResult.Success, await LogOnAsync(client, salt));
        await DrainAsync(client);
        Assert.Equal(new AccountLoginInfo(null, string.Empty, string.Empty, string.Empty, 0), await accounts.GetLoginInfoAsync(Username));
    }

    [Fact]
    public async Task ProofSuccessReply_IsSentBeforeTheLoginInfoWriteCompletes()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        var gated = new GatedLoginInfoStore();

        await using NetworkStream client = await StartSessionAsync(accounts, gated);
        await client.WriteAsync(BuildChallenge(Username));
        (_, byte[] b, _) = await ReadChallengeAsync(client);
        await client.WriteAsync(BuildProof(ComputeClient(salt, b)));

        // The write is parked behind the gate; the whole proof reply must still arrive.
        byte[] reply = new byte[26];
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await client.ReadExactlyAsync(reply, timeout.Token);
        }
        Assert.Equal((byte)AuthResult.Success, reply[1]);
        Assert.False(gated.Release.Task.IsCompleted);

        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gated.Release.SetResult();
        await SendRealmListAndReadReplyAsync(client);
        Assert.Equal(1, gated.Calls);
    }

    [Fact]
    public async Task CancelledRecording_AfterTheSuccessReply_EndsTheSession()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        using var shutdown = new CancellationTokenSource();
        var cancelling = new CancellingLoginInfoStore(shutdown);

        await using NetworkStream client = await StartSessionAsync(accounts, cancelling, sessionToken: shutdown.Token);
        await client.WriteAsync(BuildChallenge(Username));
        (_, byte[] b, _) = await ReadChallengeAsync(client);
        await client.WriteAsync(BuildProof(ComputeClient(salt, b)));

        // The success reply is already on the wire; the shutdown during the write then ends the session.
        byte[] reply = new byte[26];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ReadExactlyAsync(reply, timeout.Token);
        Assert.Equal((byte)AuthResult.Success, reply[1]);
        Assert.Equal(0, await client.ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(1, cancelling.Calls);
    }

    [Fact]
    public async Task StoreTimeoutSurfacingAsCancellation_WhileTheSessionIsLive_StillLetsTheLogonSucceed()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        var timingOut = new TimingOutLoginInfoStore();

        await using NetworkStream client = await StartSessionAsync(accounts, timingOut);
        Assert.Equal((byte)AuthResult.Success, await LogOnAsync(client, salt));
        await DrainAsync(client);
        Assert.Equal(1, timingOut.Calls);
    }

    [Fact]
    public async Task LogonServer_ComposedAsProgramComposesIt_RecordsTheLoginInfoThroughTheGuardedStore()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-logininfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "auth.db");
        try
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={file}",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthDatabase(config);
            services.AddRealmResilience(config);
            await using ServiceProvider provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AuthDbInitializer>().InitializeAsync();

            byte[] salt = WowSrp6.GenerateSalt();
            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                Assert.Equal("GuardedAccountLoginInfoStore", scope.ServiceProvider.GetRequiredService<IAccountLoginInfoStore>().GetType().Name);
                await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAsync(new Account
                {
                    Username = Username,
                    Salt = salt,
                    Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
                });
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var options = new AuthOptions { BindAddress = "127.0.0.1", Port = port, MaxSessionDurationSeconds = 0 };
            var server = new LogonServer(
                provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
                provider.GetRequiredService<ILoggerFactory>(), provider.GetRequiredService<ILogger<LogonServer>>());
            await server.StartAsync(CancellationToken.None);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                NetworkStream client = tcp.GetStream();
                Assert.Equal((byte)AuthResult.Success, await LogOnAsync(client, salt).WaitAsync(TimeSpan.FromSeconds(30)));
                await DrainAsync(client).WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                await server.StopAsync(CancellationToken.None);
            }

            await using AsyncServiceScope check = provider.CreateAsyncScope();
            AccountLoginInfo? info = await check.ServiceProvider.GetRequiredService<IAccountLoginInfoStore>().GetLoginInfoAsync(Username);
            Assert.NotNull(info);
            Assert.Equal("Win", info!.Os);
            Assert.Equal("x86", info.Platform);
            Assert.Equal("enUS", info.Locale);
            Assert.Equal(ClientBuild.Vanilla1121, info.Build);
            Assert.NotNull(info.LastLoginUtc);
        }
        finally
        {
            using (var own = new SqliteConnection($"Data Source={file}")) SqliteConnection.ClearPool(own);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 100)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    [Fact]
    public void ClientTags_KeepOnlyPrintableAscii()
    {
        byte[] packet = BuildChallenge(Username);
        byte[] body = packet[4..]; // command, error, u16 size
        "\0\u0001iW"u8.CopyTo(body.AsSpan(13, 4)); // os tag with a leading NUL and a control byte, reversed on the wire
        Assert.True(LogonChallengeRequest.TryParse(body, out LogonChallengeRequest? request));
        Assert.Equal("Wi", request!.Os);
        Assert.Equal("x86", request.Platform);
    }

    private static async Task<byte[]> SeedAsync(InMemoryAccountStore accounts)
    {
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username,
            Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
        });
        return salt;
    }

    private static async Task<byte> LogOnAsync(NetworkStream client, byte[] salt, string wireLocale = "SUne")
    {
        await client.WriteAsync(BuildChallenge(Username, wireLocale));
        (byte result, byte[] b, _) = await ReadChallengeAsync(client);
        Assert.Equal((byte)AuthResult.Success, result);
        await client.WriteAsync(BuildProof(ComputeClient(salt, b)));
        byte[] reply = await ReadExactAsync(client, 2);
        return reply[1];
    }

    /// <summary>
    /// Finishes reading the proof reply (24 bytes after the 2 read by <see cref="LogOnAsync"/>) and round-trips a realm list.
    /// The session is serial, so the realm list reply means the login-info write that follows the proof reply has finished.
    /// </summary>
    private static async Task DrainAsync(NetworkStream client)
    {
        await ReadExactAsync(client, 24);
        await SendRealmListAndReadReplyAsync(client);
    }

    private static async Task SendRealmListAndReadReplyAsync(NetworkStream client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.WriteAsync(new byte[] { (byte)AuthCommand.RealmList, 0, 0, 0, 0 }, timeout.Token);
        byte[] header = new byte[3];
        await client.ReadExactlyAsync(header, timeout.Token);
        byte[] body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
        await client.ReadExactlyAsync(body, timeout.Token);
    }

    private static async Task<NetworkStream> StartSessionAsync(
        IAccountStore accounts, IAccountLoginInfoStore? loginInfo, bool autocreate = false, TimeProvider? time = null,
        CancellationToken sessionToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = server.GetStream();
            var session = new LogonSession(
                stream, accounts, new InMemoryRealmStore([]), new AuthOptions { AutocreateAccounts = autocreate },
                NullLogger.Instance, "test", timeProvider: time, loginInfoStore: loginInfo);
            await session.RunAsync(sessionToken);
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client.GetStream();
    }

    private static byte[] BuildChallenge(string username, string wireLocale = "SUne")
    {
        byte[] name = Encoding.ASCII.GetBytes(username);
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());
        body.AddRange([1, 12, 1]);
        body.AddRange(LeU16(ClientBuild.Vanilla1121));
        body.AddRange("68x\0"u8.ToArray());  // platform "x86", byte-reversed on the wire
        body.AddRange("niW\0"u8.ToArray());  // os "Win"
        body.AddRange(Encoding.ASCII.GetBytes(wireLocale)); // country, byte-reversed on the wire ("SUne" is "enUS")
        body.AddRange(LeU32(0));
        body.AddRange(LeU32(0));
        body.Add((byte)name.Length);
        body.AddRange(name);

        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        packet.AddRange(LeU16((ushort)body.Count));
        packet.AddRange(body);
        return [.. packet];
    }

    private static byte[] BuildProof(ClientProof client, bool wrongProof = false)
    {
        var packet = new List<byte> { (byte)AuthCommand.LogonProof };
        packet.AddRange(client.PublicKey);
        packet.AddRange(wrongProof ? new byte[20] : client.Proof);
        packet.AddRange(new byte[20]); // crc_hash
        packet.Add(0);                 // number_of_keys
        packet.Add(0);                 // security flags
        return [.. packet];
    }

    private static async Task<(byte Result, byte[] B, byte[] Salt)> ReadChallengeAsync(NetworkStream stream)
    {
        byte[] head = await ReadExactAsync(stream, 3);
        if (head[2] != (byte)AuthResult.Success)
        {
            return (head[2], [], []);
        }

        // B[32], g_len, g, N_len, N[32], salt[32], crc[16], securityFlag
        byte[] rest = await ReadExactAsync(stream, 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        return (head[2], rest[..32], rest[67..99]);
    }

    private static ClientProof ComputeClient(byte[] salt, byte[] serverB, string password = Password)
    {
        Span<byte> aBytes = stackalloc byte[19];
        RandomNumberGenerator.Fill(aBytes);
        BigInteger a = new(aBytes, isUnsigned: true, isBigEndian: false);
        BigInteger publicA = BigInteger.ModPow(WowSrp6.G, a, WowSrp6.N);
        BigInteger b = WowSrp6.FromLittleEndian(serverB);

        BigInteger x = WowSrp6.ComputeX(salt, Username, password);
        BigInteger u = Srp6Math.Scrambler(publicA, b);
        BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
        BigInteger numerator = ((b - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N;
        BigInteger s = BigInteger.ModPow(numerator, a + u * x, WowSrp6.N);

        byte[] sessionKey = Srp6Math.Interleave(s);
        return new ClientProof(
            WowSrp6.ToFixedLittleEndian(publicA, WowSrp6.KeyLength),
            Srp6Math.ClientProof(Username, salt, publicA, b, sessionKey));
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static byte[] LeU16(ushort v) { byte[] b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    private static byte[] LeU32(uint v) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    private sealed class StepClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Set(DateTimeOffset value) => _now = value;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CancellingLoginInfoStore(CancellationTokenSource shutdown) : IAccountLoginInfoStore
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task RecordLoginAsync(string username, AccountLoginInfo info, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            shutdown.Cancel();
            throw new OperationCanceledException("shutdown", shutdown.Token);
        }

        public Task<AccountLoginInfo?> GetLoginInfoAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult<AccountLoginInfo?>(null);
    }

    private sealed class GatedLoginInfoStore : IAccountLoginInfoStore
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RecordLoginAsync(string username, AccountLoginInfo info, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<AccountLoginInfo?> GetLoginInfoAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult<AccountLoginInfo?>(null);
    }

    private sealed record ClientProof(byte[] PublicKey, byte[] Proof);

    private sealed class TimingOutLoginInfoStore : IAccountLoginInfoStore
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task RecordLoginAsync(string username, AccountLoginInfo info, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            // The session token is not cancelled: this is a command timeout, not a shutdown.
            throw new TaskCanceledException("command timeout");
        }

        public Task<AccountLoginInfo?> GetLoginInfoAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult<AccountLoginInfo?>(null);
    }

    private sealed class ThrowingLoginInfoStore : IAccountLoginInfoStore
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task RecordLoginAsync(string username, AccountLoginInfo info, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("login info store is down");
        }

        public Task<AccountLoginInfo?> GetLoginInfoAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult<AccountLoginInfo?>(null);
    }
}

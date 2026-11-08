using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests;

[Collection(LogonSessionsCollection.Name)]
public sealed class AutoPatchTests : IDisposable
{
    private const string Username = "TESTER";
    private readonly string _dir = Directory.CreateTempSubdirectory("ac-patch").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Catalog_UnreadablePatchFile_OffersNoPatchInsteadOfThrowing()
    {
        string file = Path.Combine(_dir, "5464enUS.mpq");
        File.WriteAllBytes(file, [1, 2, 3]);
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // A writer or scanner holds the file: no patch is offered, so the client is answered VersionInvalid.
            Assert.Null(catalog.Find(5464, "enUS"));
        }

        Assert.NotNull(catalog.Find(5464, "enUS")); // nothing was cached from the failed read
    }

    [Fact]
    public void Catalog_IsOffByDefault_AndHonoursEntriesPatternAndFolderBounds()
    {
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "any.mpq"), [4, 5]);
        var options = new AutoPatchOptions { Directory = _dir };
        var catalog = new PatchCatalog(() => options);
        Assert.Null(catalog.Find(5464, "enUS"));

        options.Enabled = true; // read per call: no rebuild needed
        ClientPatch? byPattern = catalog.Find(5464, "enUS");
        Assert.NotNull(byPattern);
        Assert.Equal(3, byPattern.Size);
        Assert.Equal(MD5.HashData(new byte[] { 1, 2, 3 }), byPattern.Md5);
        Assert.Null(catalog.Find(5464, "deDE"));

        options.Patches.Add(new AutoPatchEntry { Build = 5464, File = "any.mpq" });
        Assert.Equal(2, catalog.Find(5464, "deDE")!.Size);
        options.Patches.Add(new AutoPatchEntry { Build = 5302, Locale = "enUS", File = "../escape.mpq" });
        Assert.Null(catalog.Find(5302, "enUS"));
        options.FileNamePattern = string.Empty;
        Assert.Null(catalog.Find(4000, "enUS"));
    }

    [Fact]
    public void Warm_HashesEveryConfiguredPatch_UnderTheKeyFindUses_SoAChallengeHashesNothing()
    {
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "5464deDE.mpq"), [9, 9]);
        File.WriteAllBytes(Path.Combine(_dir, "extra.mpq"), [4, 5, 6, 7]);
        File.WriteAllBytes(Path.Combine(_dir, "notes.txt"), [1]);
        var options = new AutoPatchOptions { Enabled = true, Directory = _dir };
        options.Patches.Add(new AutoPatchEntry { Build = 5302, Locale = "enUS", File = "extra.mpq" });
        options.Patches.Add(new AutoPatchEntry { Build = 5303, Locale = "enUS", File = "../escape.mpq" });
        var catalog = new PatchCatalog(() => options);

        Assert.Equal(3, catalog.Warm()); // the two pattern files and the entry; not notes.txt, not the one outside the folder
        Assert.Equal(3, catalog.CachedHashes);

        Assert.Equal(MD5.HashData(new byte[] { 1, 2, 3 }), catalog.Find(5464, "enUS")!.Md5);
        Assert.Equal(MD5.HashData(new byte[] { 9, 9 }), catalog.Find(5464, "deDE")!.Md5);
        Assert.Equal(MD5.HashData(new byte[] { 4, 5, 6, 7 }), catalog.Find(5302, "enUS")!.Md5);
        Assert.Equal(3, catalog.CachedHashes); // every challenge was a cache hit: Warm and Find share the key

        options.Enabled = false;
        Assert.Equal(0, new PatchCatalog(() => options).Warm());
    }

    [Fact]
    public async Task LogonServer_Start_WarmsThePatchCatalogOffTheAcceptLoop()
    {
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), [1, 2, 3]);
        var options = new AutoPatchOptions { Enabled = true, Directory = _dir };
        await using ServiceProvider provider = BuildLogonServices();
        var server = new LogonServer(
            provider.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(ServerOptions(FreePort())),
            NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance, autoPatch: new FixedMonitor(() => options));

        Assert.Equal(0, server.Patches.CachedHashes); // nothing is hashed before the daemon runs
        await server.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => server.Patches.CachedHashes == 1);
            Assert.Equal(MD5.HashData(new byte[] { 1, 2, 3 }), server.Patches.Find(5464, "enUS")!.Md5);
            Assert.Equal(1, server.Patches.CachedHashes); // the challenge-time lookup was a cache hit: the server warmed the same key
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LogonServer_FailedPatchWarm_RecordsTheSharedPatchWarmSite_AndTheDaemonKeepsListening()
    {
        const string message = "patch-warm-failure-probe-3f9c1d";
        int port = FreePort();
        await using ServiceProvider provider = BuildLogonServices();
        var server = new LogonServer(
            provider.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(ServerOptions(port)),
            NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance,
            autoPatch: new FixedMonitor(() => throw new InvalidOperationException(message)));

        await server.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => FaultLedger.Shared.Snapshot().Any(f => f.Site == LogonFaults.PatchWarmSite && f.SampleMessage.Contains(message, StringComparison.Ordinal)));

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port); // the failed warm did not take the listener down
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LogonServer_HandOffToTheSession_LeavesTheAcceptLoop()
    {
        int port = FreePort();
        await using ServiceProvider provider = BuildLogonServices();
        using var release = new ManualResetEventSlim(false);
        var scopes = new BlockingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>(), release);
        var server = new LogonServer(
            scopes, Microsoft.Extensions.Options.Options.Create(ServerOptions(port)),
            NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            using var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            await WaitUntilAsync(() => scopes.Entered >= 1); // session one is now parked inside CreateScope on a pool thread

            // Had HandleClientAsync run inline on the accept loop (no Task.Yield hop), the loop would be stuck in that CreateScope and never admit this one.
            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            await WaitUntilAsync(() => scopes.Entered >= 2);
            Assert.Equal(2, scopes.Entered);
        }
        finally
        {
            release.Set();
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static ServiceProvider BuildLogonServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        services.AddSingleton<IRealmStore>(new InMemoryRealmStore([]));
        return services.BuildServiceProvider();
    }

    private static AuthOptions ServerOptions(int port) => new()
    {
        BindAddress = "127.0.0.1", Port = port, MaxConnections = 100, MaxConnectionsPerIp = 100, MaxSessionDurationSeconds = 0,
    };

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 10 s");
            await Task.Delay(20);
        }
    }

    private sealed class FixedMonitor(Func<AutoPatchOptions> current) : IOptionsMonitor<AutoPatchOptions>
    {
        public AutoPatchOptions CurrentValue => current();

        public AutoPatchOptions Get(string? name) => current();

        public IDisposable? OnChange(Action<AutoPatchOptions, string?> listener) => null;
    }

    /// <summary>Counts CreateScope calls and parks the first caller until released; later callers pass through.</summary>
    private sealed class BlockingScopeFactory(IServiceScopeFactory inner, ManualResetEventSlim release) : IServiceScopeFactory
    {
        private int _entered;

        public int Entered => Volatile.Read(ref _entered);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _entered) == 1)
                release.Wait(TimeSpan.FromSeconds(30));
            return inner.CreateScope();
        }
    }

    [Fact]
    public async Task WrongBuild_WithPatch_GetsVersionUpdateXferInitAndResumableData()
    {
        byte[] data = RandomNumberGenerator.GetBytes(LogonSession.XferChunkSize + 100);
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), data);
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, "X"), WowSrp6.KeyLength),
        });

        await using NetworkStream client = await StartAsync(accounts, catalog);
        await client.WriteAsync(Challenge(5464));
        byte[] head = await Read(client, 3);
        Assert.Equal(0, head[2]); // the challenge succeeds so the client sends a proof
        await Read(client, 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);

        byte[] proof = new byte[1 + 32 + 20 + 20 + 1 + 1];
        proof[0] = (byte)AuthCommand.LogonProof;
        proof[1] = 1;
        await client.WriteAsync(proof);
        byte[] offer = await Read(client, 2 + 1 + 1 + 5 + 8 + 16);
        Assert.Equal([0x01, 0x0A, 0x30, 5], offer[..4]);
        Assert.Equal("Patch", Encoding.ASCII.GetString(offer, 4, 5));
        Assert.Equal((ulong)data.Length, BinaryPrimitives.ReadUInt64LittleEndian(offer.AsSpan(9)));
        Assert.Equal(MD5.HashData(data), offer[17..]);

        // Resume after the first 4000 bytes and read until the rest has arrived.
        byte[] resume = new byte[9];
        resume[0] = (byte)AuthCommand.XferResume;
        BinaryPrimitives.WriteUInt64LittleEndian(resume.AsSpan(1), 4000);
        await client.WriteAsync(resume);
        var received = new List<byte>();
        while (received.Count < data.Length - 4000)
        {
            byte[] chunkHead = await Read(client, 3);
            Assert.Equal(0x31, chunkHead[0]);
            received.AddRange(await Read(client, BinaryPrimitives.ReadUInt16LittleEndian(chunkHead.AsSpan(1))));
        }

        Assert.Equal(data[4000..], received.ToArray());

        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferCancel });
        Assert.Equal(0, await client.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task WrongBuild_WithoutPatch_IsStillVersionInvalid()
    {
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });
        await using NetworkStream client = await StartAsync(new InMemoryAccountStore(), catalog);
        await client.WriteAsync(Challenge(5464));
        byte[] head = await Read(client, 3);
        Assert.Equal((byte)AuthResult.VersionInvalid, head[2]);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task ActivePatchDownload_ExtendsTheSessionCap_ButAnIdleConnectionStillCloses()
    {
        byte[] data = RandomNumberGenerator.GetBytes(100);
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), data);
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, "X"), WowSrp6.KeyLength),
        });

        // The server arms its cap between the connect and its first reply, so those two moments bound it: the stopwatch
        // (started before the connect) is never ahead of the cap, and the first reply is never behind it. The accept is
        // timed from the earliest bound so it lands inside the cap; the resume from the latest, so it lands past the
        // original cap however late the server started - a cap that the download did not restart has then closed.
        const int capSeconds = 6;
        var elapsed = Stopwatch.StartNew();
        await using NetworkStream client = await StartAsync(accounts, catalog, new AuthOptions { MaxSessionDurationSeconds = capSeconds });
        await client.WriteAsync(Challenge(5464));
        await Read(client, 3 + 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        TimeSpan capArmedBy = elapsed.Elapsed;
        byte[] proof = new byte[1 + 32 + 20 + 20 + 1 + 1];
        proof[0] = (byte)AuthCommand.LogonProof;
        await client.WriteAsync(proof);
        await Read(client, 2 + 1 + 1 + 5 + 8 + 16);

        // Most of the cap is used before the download starts, and the accept comes well after the cap's latest start, so
        // the chunk's restart outlives the resume below.
        await DelayUntil(elapsed, Max(TimeSpan.FromSeconds(capSeconds * 0.75), capArmedBy + TimeSpan.FromSeconds(1.5)));
        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferAccept });
        byte[] chunkHead = await Read(client, 3);
        Assert.Equal(data, await Read(client, BinaryPrimitives.ReadUInt16LittleEndian(chunkHead.AsSpan(1))));

        await DelayUntil(elapsed, capArmedBy + TimeSpan.FromSeconds(capSeconds + 0.5)); // past the original cap: the chunk restarted it, so the connection is still open
        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferResume, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(0x31, (await Read(client, 3))[0]);
        await Read(client, data.Length);

        // Idle afterwards: the cap closes the connection.
        Assert.Equal(0, await client.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(capSeconds + 14)));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static async Task DelayUntil(Stopwatch elapsed, TimeSpan target)
    {
        TimeSpan remaining = target - elapsed.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }
    }

    [Fact]
    public async Task PatchOffer_CountsTwoLogonPackets_AndXferResumeAndDataPerCommand()
    {
        using var meter = new Meter("ArcaneCore.Test." + Guid.NewGuid().ToString("N"));
        OpcodeTable table = new OpcodeTrafficMeter(meter).Register("logon", LogonPacketMetrics.Commands);
        using MeterListener listener = Listen(meter);
        byte[] data = RandomNumberGenerator.GetBytes(LogonSession.XferChunkSize + 100);
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), data);
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });
        var accounts = new InMemoryAccountStore();
        await CreateAccountAsync(accounts);

        await using NetworkStream client = await StartAsync(accounts, catalog, traffic: table);
        await client.WriteAsync(Challenge(5464));
        await Read(client, 3 + 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        byte[] proof = new byte[1 + 32 + 20 + 20 + 1 + 1];
        proof[0] = (byte)AuthCommand.LogonProof;
        await client.WriteAsync(proof);
        byte[] offer = await Read(client, 2 + 1 + 1 + 5 + 8 + 16);

        Assert.Equal(33, offer.Length);

        const int resumeAt = 4000;
        byte[] resume = new byte[9];
        resume[0] = (byte)AuthCommand.XferResume;
        BinaryPrimitives.WriteUInt64LittleEndian(resume.AsSpan(1), resumeAt);
        await client.WriteAsync(resume);
        int remaining = data.Length - resumeAt;
        int received = 0;
        int chunks = 0;
        while (received < remaining)
        {
            byte[] chunkHead = await Read(client, 3);
            received += (await Read(client, BinaryPrimitives.ReadUInt16LittleEndian(chunkHead.AsSpan(1)))).Length;
            chunks++;
        }

        // The cancel closes the session, so every server-side count has been recorded once the connection reads EOF.
        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferCancel });
        Assert.Equal(0, await client.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(remaining, received);

        // One 33-byte socket write is two logon packets: the 2-byte proof failure and the 31-byte XFER_INITIATE.
        Assert.Equal(1, table.Read((uint)AuthCommand.LogonProof).PacketsOut);
        Assert.Equal(2, table.Read((uint)AuthCommand.LogonProof).BytesOut);
        Assert.Equal(new OpcodeCounts(0, 0, 1, 31), table.Read((uint)AuthCommand.XferInitiate));
        Assert.Equal(new OpcodeCounts(1, 1 + 8, 0, 0), table.Read((uint)AuthCommand.XferResume));
        Assert.Equal(new OpcodeCounts(0, 0, chunks, remaining + (3 * chunks)), table.Read((uint)AuthCommand.XferData));
        Assert.Equal(new OpcodeCounts(1, 1, 0, 0), table.Read((uint)AuthCommand.XferCancel));
        Assert.Equal(1, table.Read((uint)AuthCommand.LogonProof).PacketsIn);
    }

    [Fact]
    public async Task XferAccept_CountsTheRequestAndEachDataChunk()
    {
        using var meter = new Meter("ArcaneCore.Test." + Guid.NewGuid().ToString("N"));
        OpcodeTable table = new OpcodeTrafficMeter(meter).Register("logon", LogonPacketMetrics.Commands);
        using MeterListener listener = Listen(meter);
        byte[] data = RandomNumberGenerator.GetBytes(100);
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), data);
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });
        var accounts = new InMemoryAccountStore();
        await CreateAccountAsync(accounts);

        await using NetworkStream client = await StartAsync(accounts, catalog, traffic: table);
        await client.WriteAsync(Challenge(5464));
        await Read(client, 3 + 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        byte[] proof = new byte[1 + 32 + 20 + 20 + 1 + 1];
        proof[0] = (byte)AuthCommand.LogonProof;
        await client.WriteAsync(proof);
        await Read(client, 2 + 1 + 1 + 5 + 8 + 16);

        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferAccept });
        byte[] chunkHead = await Read(client, 3);
        Assert.Equal(data, await Read(client, BinaryPrimitives.ReadUInt16LittleEndian(chunkHead.AsSpan(1))));
        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferCancel });
        Assert.Equal(0, await client.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(new OpcodeCounts(1, 1, 0, 0), table.Read((uint)AuthCommand.XferAccept));
        Assert.Equal(new OpcodeCounts(0, 0, 1, 103), table.Read((uint)AuthCommand.XferData));
    }

    private static MeterListener Listen(Meter meter)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();
        return listener;
    }

    private static async Task CreateAccountAsync(IAccountStore accounts)
    {
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, "X"), WowSrp6.KeyLength),
        });
    }

    private static async Task<NetworkStream> StartAsync(IAccountStore accounts, PatchCatalog catalog, AuthOptions? options = null, OpcodeTable? traffic = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = server.GetStream();
            var session = new LogonSession(stream, accounts, new InMemoryRealmStore([]), options ?? new AuthOptions(),
                NullLogger.Instance, "test", patches: catalog, traffic: traffic);
            await session.RunAsync(CancellationToken.None);
        });
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client.GetStream();
    }

    private static byte[] Challenge(ushort build)
    {
        byte[] name = Encoding.ASCII.GetBytes(Username);
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());
        body.AddRange([1, 11, 2]);
        body.AddRange(BitConverter.GetBytes(build));
        body.AddRange("68x\0"u8.ToArray());
        body.AddRange("niW\0"u8.ToArray());
        body.AddRange("SUne"u8.ToArray());
        body.AddRange(new byte[8]);
        body.Add((byte)name.Length);
        body.AddRange(name);
        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        packet.AddRange(BitConverter.GetBytes((ushort)body.Count));
        packet.AddRange(body);
        return [.. packet];
    }

    private static async Task<byte[]> Read(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }
}

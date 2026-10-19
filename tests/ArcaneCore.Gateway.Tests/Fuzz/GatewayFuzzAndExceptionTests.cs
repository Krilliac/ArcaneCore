using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Net;
using ArcaneCore.World.Net.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests.Fuzz;

/// <summary>
/// Exception, boundary, and deterministic seeded fuzz tests for <c>ArcaneCore.Gateway</c> and its wire/cipher surfaces.
/// </summary>
public sealed class GatewayFuzzAndExceptionTests
{
    private const string ValidSecret = "0123456789abcdef-gateway-fuzz-secret";

    /// <summary>Deterministic seeds required for all Gateway fuzz theories.</summary>
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    [Fact]
    public void GatewayOptions_Defaults_AreValidAndProduceValidGatewayManifest()
    {
        var options = new GatewayOptions();

        IReadOnlyList<string> problems = options.Validate();
        Assert.Empty(problems);

        var incarnation = Guid.NewGuid();
        ClusterNodeManifest manifest = options.ToManifest(incarnation);
        Assert.Equal(ClusterRole.Gateway, manifest.Role);
        Assert.Equal(incarnation, manifest.NodeIncarnation);
        Assert.Equal(options.NodeId, manifest.NodeId);
        Assert.Equal(options.RealmId, manifest.RealmId);
        Assert.Equal(options.ContentRevision, manifest.ContentRevision);
        Assert.Equal(options.CharactersSchemaVersion, manifest.CharactersSchemaVersion);
        Assert.Equal(options.WorldSchemaVersion, manifest.WorldSchemaVersion);
        Assert.True(manifest.IsValid(out string? reason));
        Assert.Null(reason);
        Assert.False(options.ToManifest(Guid.Empty).IsValid(out _));
    }

    [Fact]
    public void GatewayOptions_Validate_ReportsEveryInvalidProperty()
    {
        var options = new GatewayOptions
        {
            BindAddress = "not-an-ip",
            Port = -1,
            MaxConnections = -5,
            MaxConnectionsPerIp = -2,
            PreAuthTimeout = TimeSpan.FromSeconds(-1),
            MaxOutboundBytes = 0,
            WorkerAddress = "   ",
            WorkerPort = 0,
            LinkRetryDelay = TimeSpan.FromMilliseconds(-10),
            NodeId = "",
            RealmId = 0,
            ContentRevision = " ",
            CharactersSchemaVersion = 0,
            WorldSchemaVersion = -1,
        };

        IReadOnlyList<string> problems = options.Validate();
        Assert.Contains(problems, p => p.StartsWith("Gateway:BindAddress:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:Port:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:MaxConnections:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:MaxConnectionsPerIp:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:PreAuthTimeout:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:MaxOutboundBytes:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:WorkerAddress:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:WorkerPort:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:LinkRetryDelay:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:NodeId:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:RealmId:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:ContentRevision:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:CharactersSchemaVersion:", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Gateway:WorldSchemaVersion:", StringComparison.Ordinal));
        Assert.False(options.ToManifest(Guid.NewGuid()).IsValid(out _));

        var highPortOptions = new GatewayOptions
        {
            Port = 70000,
            WorkerPort = 70000,
        };
        IReadOnlyList<string> highPortProblems = highPortOptions.Validate();
        Assert.Equal(2, highPortProblems.Count);
    }

    [Fact]
    public void AddGateway_NullArguments_ThrowArgumentNullException()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>("services", () => GatewayServiceCollectionExtensions.AddGateway(null!, configuration));
        Assert.Throws<ArgumentNullException>("configuration", () => services.AddGateway(null!));
    }

    [Fact]
    public void AddGateway_InvalidLoggingConfiguration_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:ArcaneCore:Json:Enabled"] = "not-a-bool",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => services.AddGateway(configuration));
    }

    [Theory]
    [InlineData("Gateway:BindAddress", "invalid-ip")]
    [InlineData("Gateway:Port", "-1")]
    [InlineData("Gateway:Port", "65536")]
    [InlineData("Gateway:MaxConnections", "-1")]
    [InlineData("Gateway:MaxConnectionsPerIp", "-1")]
    [InlineData("Gateway:PreAuthTimeout", "-00:00:01")]
    [InlineData("Gateway:MaxOutboundBytes", "0")]
    [InlineData("Gateway:MaxOutboundBytes", "-100")]
    [InlineData("Gateway:WorkerAddress", "not-an-ip")]
    [InlineData("Gateway:WorkerPort", "0")]
    [InlineData("Gateway:WorkerPort", "65536")]
    [InlineData("Gateway:LinkRetryDelay", "-00:00:01")]
    [InlineData("Gateway:NodeId", "   ")]
    [InlineData("Gateway:RealmId", "0")]
    [InlineData("Gateway:ContentRevision", "   ")]
    [InlineData("Gateway:CharactersSchemaVersion", "0")]
    [InlineData("Gateway:WorldSchemaVersion", "0")]
    public void AddGateway_InvalidGatewayOptions_ThrowsOptionsValidationException(string key, string badValue)
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = badValue,
            })
            .Build();

        services.AddGateway(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<GatewayOptions>>().Value);
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);

        Assert.Throws<OptionsValidationException>(() => _ = provider.GetRequiredService<GatewayServer>());
    }

    [Fact]
    public void AddGateway_ValidConfiguration_RegistersGatewayServerAndHostedService()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:BindAddress"] = "127.0.0.1",
                ["Gateway:Port"] = "0",
                ["Gateway:WorkerAddress"] = "127.0.0.1",
                ["Gateway:WorkerPort"] = "8095",
                ["Gateway:LinkSecret"] = ValidSecret,
            })
            .Build();

        services.AddGateway(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        GatewayServer server = provider.GetRequiredService<GatewayServer>();
        IHostedService hosted = provider.GetRequiredService<IHostedService>();
        Assert.Same(server, hosted);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 8095), server.WorkerEndPoint);
    }

    [Fact]
    public void GatewayServer_Constructor_NullArguments_ThrowArgumentNullException()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        IOptions<GatewayOptions> options = Options.Create(new GatewayOptions());
        IOptions<GatewayOptions> nullValueOptions = Options.Create<GatewayOptions>(null!);

        Assert.Throws<ArgumentNullException>("scopes", () => new GatewayServer(null!, options, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>("options", () => new GatewayServer(scopes, null!, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>("options.Value", () => new GatewayServer(scopes, nullValueOptions, NullLoggerFactory.Instance));
        Assert.Throws<ArgumentNullException>("loggers", () => new GatewayServer(scopes, options, null!));
    }

    [Fact]
    public async Task GatewayServer_BeforeStart_ThrowsInvalidOperationExceptionAndRespectsCancellation()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var server = new GatewayServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions { LinkSecret = ValidSecret }),
            NullLoggerFactory.Instance);

        Assert.Null(server.Guard);
        Assert.Null(server.Link);
        Assert.Equal(0, server.ConnectionCount);
        Assert.Throws<InvalidOperationException>(() => _ = server.ListenEndPoint);

        MethodInfo executeAsync = typeof(GatewayServer).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)executeAsync.Invoke(server, [CancellationToken.None])!);

        await Assert.ThrowsAsync<ArgumentNullException>("worker", () => server.RetargetWorkerAsync(null!, CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.RetargetWorkerAsync(new IPEndPoint(IPAddress.Loopback, 8095), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.WaitForLinkAsync(cancelled.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-secret")]
    [InlineData("15chars-secret!")]
    public async Task GatewayServer_StartAsync_WeakLinkSecret_ThrowsArgumentException(string secret)
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var server = new GatewayServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                LinkSecret = secret,
            }),
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GatewayServer_StartAsync_InvalidOptionsValues_ThrowExpectedExceptions()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        GatewayServer Create(Action<GatewayOptions> configure)
        {
            var opts = new GatewayOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                WorkerAddress = "127.0.0.1",
                WorkerPort = 8095,
                LinkSecret = ValidSecret,
            };
            configure(opts);
            return new GatewayServer(scopes, Options.Create(opts), NullLoggerFactory.Instance);
        }

        using (GatewayServer badBind = Create(o => o.BindAddress = " "))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => badBind.StartAsync(CancellationToken.None));
        }

        using (GatewayServer badWorkerAddr = Create(o => o.WorkerAddress = ""))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => badWorkerAddr.StartAsync(CancellationToken.None));
        }

        using (GatewayServer unparseableBind = Create(o => o.BindAddress = "not-an-ip"))
        {
            await Assert.ThrowsAsync<FormatException>(() => unparseableBind.StartAsync(CancellationToken.None));
        }

        using (GatewayServer unparseableWorker = Create(o => o.WorkerAddress = "not-an-ip"))
        {
            Assert.Throws<FormatException>(() => _ = unparseableWorker.WorkerEndPoint);
        }

        using (GatewayServer negPort = Create(o => o.Port = -1))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => negPort.StartAsync(CancellationToken.None));
        }

        using (GatewayServer highPort = Create(o => o.Port = 65536))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => highPort.StartAsync(CancellationToken.None));
        }

        using (GatewayServer zeroWorkerPort = Create(o => o.WorkerPort = 0))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => zeroWorkerPort.StartAsync(CancellationToken.None));
        }

        using (GatewayServer highWorkerPort = Create(o => o.WorkerPort = 65536))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => highWorkerPort.StartAsync(CancellationToken.None));
        }

        using (GatewayServer negMaxConn = Create(o => o.MaxConnections = -1))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => negMaxConn.StartAsync(CancellationToken.None));
        }

        using (GatewayServer negPerIp = Create(o => o.MaxConnectionsPerIp = -1))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => negPerIp.StartAsync(CancellationToken.None));
        }

        using (GatewayServer zeroOutbound = Create(o => o.MaxOutboundBytes = 0))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => zeroOutbound.StartAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task GatewayWorldHandoffRouter_GuardsAndCancellation_ThrowExpectedExceptions()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var server = new GatewayServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions { LinkSecret = ValidSecret }),
            NullLoggerFactory.Instance);
        var target = new IPEndPoint(IPAddress.Loopback, 8099);

        Assert.Throws<ArgumentNullException>("gateway", () => new GatewayWorldHandoffRouter(null!, target));
        Assert.Throws<ArgumentNullException>("target", () => new GatewayWorldHandoffRouter(server, null!));

        var router = new GatewayWorldHandoffRouter(server, target);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.RerouteAsync(cancelled.Token));
        Assert.Equal(target, server.WorkerEndPoint);
    }

    [Fact]
    public void GatewayAccountClaims_ValidationAndSupersedeSemantics()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var server = new GatewayServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions { LinkSecret = ValidSecret }),
            NullLoggerFactory.Instance);
        var guard = new NetGuard(new NetProtectionOptions(), () => 0, () => 0, NullLogger.Instance);
        using var tcp1 = new TcpClient();
        using var tcp2 = new TcpClient();
        var conn1 = new GatewayClientConnection(server, tcp1, guard, NullLogger.Instance);
        var conn2 = new GatewayClientConnection(server, tcp2, guard, NullLogger.Instance);

        var claims = new GatewayAccountClaims();
        Assert.Throws<ArgumentOutOfRangeException>("accountId", () => claims.Claim(0, conn1));
        Assert.Throws<ArgumentOutOfRangeException>("accountId", () => claims.Claim(-42, conn1));
        Assert.Throws<ArgumentNullException>("connection", () => claims.Claim(1, null!));
        Assert.Throws<ArgumentNullException>("connection", () => claims.Release(1, null!));

        Assert.Null(claims.Find(7));
        claims.Claim(7, conn1);
        Assert.Equal(1, claims.Count);
        Assert.Same(conn1, claims.Find(7));
        Assert.Null(conn1.KickReason);

        // Claiming with the same connection instance does not kick it.
        claims.Claim(7, conn1);
        Assert.Null(conn1.KickReason);

        // Claiming with a newer connection kicks the previous one and replaces the entry.
        claims.Claim(7, conn2);
        Assert.Equal("superseded by a newer login of the account", conn1.KickReason);
        Assert.Null(conn2.KickReason);
        Assert.Same(conn2, claims.Find(7));

        // Releasing the superseded connection does not remove the active connection's claim.
        claims.Release(7, conn1);
        Assert.Same(conn2, claims.Find(7));
        Assert.Equal(1, claims.Count);

        claims.Release(7, conn2);
        Assert.Null(claims.Find(7));
        Assert.Equal(0, claims.Count);
    }

    [Fact]
    public void GatewayClientConnection_GuardsAndSinkInvariants_ThrowExpectedExceptions()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var server = new GatewayServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions { LinkSecret = ValidSecret, MaxOutboundBytes = 32 }),
            NullLoggerFactory.Instance);
        var guard = new NetGuard(new NetProtectionOptions(), () => 0, () => 0, NullLogger.Instance);
        using var tcp = new TcpClient();

        Assert.Throws<ArgumentNullException>("server", () => new GatewayClientConnection(null!, tcp, guard, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>("client", () => new GatewayClientConnection(server, null!, guard, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>("guard", () => new GatewayClientConnection(server, tcp, null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>("logger", () => new GatewayClientConnection(server, tcp, guard, null!));

        var conn = new GatewayClientConnection(server, tcp, guard, NullLogger.Instance);
        Assert.Equal("unknown", conn.RemoteEndpoint);
        Assert.Equal(0, conn.AccountId);
        Assert.Equal(0, conn.SessionGeneration);

        Assert.Throws<ArgumentNullException>("reason", () => conn.Kick(null!));
        Assert.Throws<ArgumentException>("reason", () => conn.Kick(""));
        Assert.Throws<ArgumentException>("reason", () => conn.Kick("   "));

        IGatewaySessionSink sink = conn;
        Assert.Throws<InvalidOperationException>(
            () => sink.OnOpened(GatewaySessionOpenReply.Accept(1)));

        // Oversize server packet (> WorldSession.MaxServerPayload) is dropped without queueing or kicking.
        byte[] oversize = new byte[WorldSession.MaxServerPayload + 1];
        sink.OnServerPacket(WorldOpcode.SmsgPong, oversize);
        Assert.Null(conn.KickReason);

        // Exceeding MaxOutboundBytes (32 bytes) kicks the connection with the outbound limit reason.
        sink.OnServerPacket(WorldOpcode.SmsgPong, new byte[40]);
        Assert.Equal("outbound queue over the byte limit", conn.KickReason);

        // Subsequent Kick calls preserve the first KickReason.
        conn.Kick("later reason");
        Assert.Equal("outbound queue over the byte limit", conn.KickReason);

        // OnClosed on a fresh connection sets the worker-closed reason.
        using var tcp2 = new TcpClient();
        var conn2 = new GatewayClientConnection(server, tcp2, guard, NullLogger.Instance);
        ((IGatewaySessionSink)conn2).OnClosed(ClusterSessionDetachReason.WorkerClosed);
        Assert.Equal("the worker closed the session (WorkerClosed)", conn2.KickReason);
    }

    [Fact]
    public void WorldHeaderCrypt_ExceptionAndInvariantGuards()
    {
        var crypt = new WorldHeaderCrypt();
        Assert.False(crypt.IsInitialized);
        Assert.Equal(6, WorldHeaderCrypt.IncomingHeaderLength);
        Assert.Equal(4, WorldHeaderCrypt.OutgoingHeaderLength);

        Assert.Throws<ArgumentNullException>("sessionKey", () => crypt.Initialize(null!));
        Assert.Throws<ArgumentException>("sessionKey", () => crypt.Initialize([]));

        // Uninitialized encrypt/decrypt is a no-op even on arbitrary spans.
        byte[] raw = [1, 2, 3, 4, 5, 6];
        crypt.DecryptHeader(raw);
        crypt.EncryptHeader(raw.AsSpan(0, 4));
        Assert.Equal([1, 2, 3, 4, 5, 6], raw);

        // Non-40-byte key records an Invariant.Check failure.
        using (InvariantCapture scope = Invariant.Capture())
        {
            var shortKeyCrypt = new WorldHeaderCrypt();
            shortKeyCrypt.Initialize(new byte[16]);
            Assert.True(shortKeyCrypt.IsInitialized);
            Assert.Single(scope.Failures);
            Assert.Contains("40-byte session key", scope.Failures[0].Message, StringComparison.Ordinal);
        }

        // Double Initialize records an Invariant.Check failure and keeps the original key stream.
        byte[] validKey = new byte[WowSrp6.SessionKeyLength];
        for (int i = 0; i < validKey.Length; i++)
        {
            validKey[i] = (byte)(i + 1);
        }

        crypt.Initialize(validKey);
        Assert.True(crypt.IsInitialized);

        using (InvariantCapture scope = Invariant.Capture())
        {
            crypt.Initialize(new byte[WowSrp6.SessionKeyLength]);
            Assert.Single(scope.Failures);
            Assert.Contains("initialised once per connection", scope.Failures[0].Message, StringComparison.Ordinal);
        }

#if DEBUG
        // Wrong header lengths on an initialized cipher trigger Invariant.Assert.
        using (InvariantCapture scope = Invariant.Capture())
        {
            Assert.Throws<InvariantViolationException>(() => crypt.EncryptHeader(new byte[3]));
            Assert.Throws<InvariantViolationException>(() => crypt.EncryptHeader(new byte[5]));
            Assert.Throws<InvariantViolationException>(() => crypt.DecryptHeader(new byte[2]));
            Assert.Throws<InvariantViolationException>(() => crypt.DecryptHeader(new byte[7]));
            Assert.Equal(4, scope.Failures.Count);
        }
#endif
    }

    [Fact]
    public async Task GatewaySessionWireAndLink_ExceptionGuards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>("linkSecret", () => GatewaySessionWire.ComputeProof(null!, new byte[GatewaySessionWire.NonceBytes]));
        Assert.Throws<ArgumentException>("bytes", () => GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, new byte[31]));
        Assert.Throws<ArgumentException>("bytes", () => GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, new byte[33]));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeNonceFrame(new byte[31], "LinkChallenge"));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeNonceFrame(new byte[33], "LinkChallenge"));

        string overlong = new('A', GatewaySessionWire.MaxTextBytes + 1);
        Assert.Throws<ArgumentException>("request", () => GatewaySessionWire.EncodeOpen(1, new GatewaySessionOpenRequest(1, 1, overlong, 5875, "127.0.0.1:1")));
        Assert.Throws<ArgumentException>("request", () => GatewaySessionWire.EncodeOpen(1, new GatewaySessionOpenRequest(1, 1, "ALICE", 5875, overlong)));

        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            using var clientSide = new TcpClient();
            await clientSide.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using TcpClient serverSide = await listener.AcceptTcpClientAsync();
            await using var channel = new GatewayLinkChannel(clientSide);
            byte[] hugeFrame = new byte[LoopbackGatewayWorkerHost.MaxFrameBytes + 1];
            Assert.Throws<InvalidOperationException>(() => channel.TryEnqueue(hugeFrame));
        }

        ClusterNodeManifest validGatewayManifest = GatewayLinkManifest.Create(ClusterRole.Gateway, "gw-1", Guid.NewGuid(), 1, "dev", 1, 1);
        ClusterNodeManifest validWorkerManifest = GatewayLinkManifest.Create(ClusterRole.Worker, "worker-1", Guid.NewGuid(), 1, "dev", 1, 1);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 8095);

        Assert.Throws<ArgumentNullException>("gatewayManifest", () => new GatewaySessionLinkClient(null!, endpoint, ValidSecret, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>("workerEndPoint", () => new GatewaySessionLinkClient(validGatewayManifest, null!, ValidSecret, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>("linkSecret", () => new GatewaySessionLinkClient(validGatewayManifest, endpoint, null!, NullLogger.Instance));
        Assert.Throws<ArgumentException>(() => new GatewaySessionLinkClient(validGatewayManifest, endpoint, "short", NullLogger.Instance));
        Assert.Throws<ArgumentException>("gatewayManifest", () => new GatewaySessionLinkClient(validWorkerManifest, endpoint, ValidSecret, NullLogger.Instance));

        await using var client = new GatewaySessionLinkClient(validGatewayManifest, endpoint, ValidSecret, NullLogger.Instance);
        var sink = new CapturingSink();
        await Assert.ThrowsAsync<ArgumentNullException>("request", () => client.OpenAsync(null!, sink, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>("nextGeneration", () => client.OpenAsync(null!, g => new GatewaySessionOpenRequest(g, 1, "A", 5875, "127.0.0.1:1"), sink, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>("requestFor", () => client.OpenAsync(() => 1, null!, sink, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>("sink", () => client.OpenAsync(() => 1, g => new GatewaySessionOpenRequest(g, 1, "A", 5875, "127.0.0.1:1"), null!, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenAsync(new GatewaySessionOpenRequest(1, 1, "A", 5875, "127.0.0.1:1"), sink, CancellationToken.None));

        var acceptor = new FakeWorkerAcceptor();
        Assert.Throws<ArgumentNullException>("workerManifest", () => new GatewaySessionLinkHost(null!, acceptor, () => 1, ValidSecret, endpoint));
        Assert.Throws<ArgumentNullException>("acceptor", () => new GatewaySessionLinkHost(validWorkerManifest, null!, () => 1, ValidSecret, endpoint));
        Assert.Throws<ArgumentNullException>("ownerEpoch", () => new GatewaySessionLinkHost(validWorkerManifest, acceptor, null!, ValidSecret, endpoint));
        Assert.Throws<ArgumentNullException>("linkSecret", () => new GatewaySessionLinkHost(validWorkerManifest, acceptor, () => 1, null!, endpoint));
        Assert.Throws<ArgumentNullException>("bindEndPoint", () => new GatewaySessionLinkHost(validWorkerManifest, acceptor, () => 1, ValidSecret, null!));
        Assert.Throws<ArgumentException>(() => new GatewaySessionLinkHost(validWorkerManifest, acceptor, () => 1, "short", endpoint));

        var admission = new WorkerAdmission();
        Assert.Throws<ArgumentNullException>("reason", () => admission.Close(null!));
        Assert.Throws<ArgumentException>("reason", () => admission.Close(""));
        Assert.Throws<ArgumentException>("reason", () => admission.Close("   "));
    }

    [Fact]
    public async Task GatewaySessionLinkClient_ConnectedMisuse_ThrowsInvalidOperationException()
    {
        await using var worker = new WorkerRig();
        worker.Start();
        ClusterNodeManifest gatewayManifest = GatewayLinkManifest.Create(ClusterRole.Gateway, "gw-1", Guid.NewGuid(), 1, "dev", 1, 1);

        await using var client = new GatewaySessionLinkClient(gatewayManifest, worker.Host.BoundEndPoint, ValidSecret, NullLogger.Instance);
        await client.ConnectAsync(CancellationToken.None);

        // Calling ConnectAsync a second time throws InvalidOperationException.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(CancellationToken.None));

        // Mismatched generation from requestFor throws InvalidOperationException.
        var sink = new CapturingSink();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.OpenAsync(() => 10, _ => new GatewaySessionOpenRequest(99, 1, "ALICE", 5875, "127.0.0.1:1"), sink, CancellationToken.None));

        // Opening an already-open session generation on the same link throws InvalidOperationException.
        GatewaySessionOpenReply opened = await client.OpenAsync(
            new GatewaySessionOpenRequest(1, 1, "ALICE", 5875, "127.0.0.1:1"),
            sink,
            CancellationToken.None);
        Assert.True(opened.Accepted);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.OpenAsync(new GatewaySessionOpenRequest(1, 2, "BOB", 5875, "127.0.0.1:2"), sink, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 0u)]
    [InlineData(2, 0u)]
    [InlineData(3, 0u)]
    [InlineData(WorldSession.MaxClientPacketSize + 1, (uint)WorldOpcode.CmsgPing)]
    [InlineData(0xFFFF, (uint)WorldOpcode.CmsgPing)]
    [InlineData(4, 0x0001_0000u)]
    [InlineData(4, 0xFFFF_FFFFu)]
    public async Task GatewayClientConnection_InvalidHeaderSizeOrOpcode_DisconnectsCleanly(int size, uint rawOpcode)
    {
        await using var rig = await GatewayTestRig.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
        NetworkStream stream = tcp.GetStream();

        _ = await ReadChallengeAsync(stream);

        byte[] header = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), (ushort)size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), rawOpcode);
        await stream.WriteAsync(header);

        Assert.True(await ExpectEofAsync(stream));
    }

    [Fact]
    public async Task GatewayClientConnection_MalformedPingAndPreAuthNonAuthOpcode_Disconnect()
    {
        await using var rig = await GatewayTestRig.StartAsync();

        // 1. CMSG_PING with 3-byte payload (< 4 bytes required) disconnects.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            _ = await ReadChallengeAsync(stream);

            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgPing, new byte[] { 1, 2, 3 });
            Assert.True(await ExpectEofAsync(stream));
        }

        // 2. CMSG_CHAR_ENUM before authentication disconnects immediately.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            _ = await ReadChallengeAsync(stream);

            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgCharEnum, Array.Empty<byte>());
            Assert.True(await ExpectEofAsync(stream));
        }
    }

    [Fact]
    public async Task GatewayClientConnection_FrameReadTimeout_DisconnectsStalledPartialHeader()
    {
        var protection = new NetProtectionOptions
        {
            FrameReadTimeout = TimeSpan.FromMilliseconds(120),
        };
        await using var rig = await GatewayTestRig.StartAsync(protection: protection);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
        NetworkStream stream = tcp.GetStream();
        _ = await ReadChallengeAsync(stream);

        // Send only 1 byte of the 6-byte client header and stall.
        await stream.WriteAsync(new byte[] { 0x00 });
        Assert.True(await ExpectEofAsync(stream));
        await WaitUntilAsync(() => rig.Gateway.Guard!.Limiter.Count == 0);
    }

    [Fact]
    public async Task GatewayClientConnection_PreAuthTimeoutAndUnavailableWorkerLink_DisconnectAndRefuse()
    {
        // 1. Pre-auth timeout disconnects an idle client that never authenticates.
        await using (GatewayTestRig timeoutRig = await GatewayTestRig.StartAsync(
            configureGateway: o => o.PreAuthTimeout = TimeSpan.FromMilliseconds(120)))
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(timeoutRig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            _ = await ReadChallengeAsync(stream);
            Assert.True(await ExpectEofAsync(stream));
        }

        // 2. When the worker link is down, CMSG_AUTH_SESSION is refused with AuthResponseCode.Unavailable.
        await using (GatewayTestRig unlinkedRig = await GatewayTestRig.StartAsync(startWorker: false))
        {
            (_, byte[] key) = await unlinkedRig.SeedAccountAsync("NOWORKER");
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(unlinkedRig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload("NOWORKER", build: 5875, clientSeed: 1, serverSeed, key);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);
            (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
            Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
            Assert.Equal((byte)AuthResponseCode.Unavailable, body[0]);
        }
    }

    [Fact]
    public async Task GatewayClientConnection_AuthenticationRefusalPaths_ReturnExpectedAuthCodes()
    {
        using InvariantCapture scope = Invariant.Capture();
        var protection = new NetProtectionOptions
        {
            AuthFailureBurstPerIp = 4,
            AuthFailuresPerMinutePerIp = 0,
        };
        await using var rig = await GatewayTestRig.StartAsync(protection: protection);

        byte[] validKey = new byte[WowSrp6.SessionKeyLength];
        for (int i = 0; i < validKey.Length; i++)
        {
            validKey[i] = (byte)(0x20 + i);
        }

        // Seed accounts: corrupt session key length (12 bytes), suspended account, banned account, and active account for IP ban test.
        await rig.SeedRawAccountAsync("BADKEY", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], AccountStatus.Active);
        await rig.SeedRawAccountAsync("SUSPENDED", validKey, AccountStatus.Suspended);
        int bannedId = await rig.SeedRawAccountAsync("BANNEDACC", validKey, AccountStatus.Active);
        rig.SeedAccountBan(bannedId);
        await rig.SeedRawAccountAsync("IPBANNED", validKey, AccountStatus.Active);

        // 1. Unsupported client build -> VersionMismatch (does not consume failure budget).
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload("ANY", build: 9999, clientSeed: 1, serverSeed, validKey);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);
            (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
            Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
            Assert.Equal((byte)AuthResponseCode.VersionMismatch, body[0]);
        }

        // 2. Corrupt stored SessionKey length != 40 -> Invariant.Check failure + AuthResponseCode.Failed (does not consume failure budget).
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload("BADKEY", build: 5875, clientSeed: 2, serverSeed, validKey);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);
            (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
            Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
            Assert.Equal((byte)AuthResponseCode.Failed, body[0]);
            Assert.Single(scope.Failures);
            Assert.Contains("holds a session key of 12 bytes", scope.Failures[0].Message, StringComparison.Ordinal);
        }

        // 3. UnknownAccount (consumes failure budget 1/4).
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload("NOBODY", build: 5875, clientSeed: 3, serverSeed, validKey);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);
            (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
            Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
            Assert.Equal((byte)AuthResponseCode.UnknownAccount, body[0]);
        }

        // 4. Suspended account -> Banned (consumes failure budget 2/4).
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload("SUSPENDED", build: 5875, clientSeed: 4, serverSeed, validKey);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);
            (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
            Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
            Assert.Equal((byte)AuthResponseCode.Banned, body[0]);
        }

        // 5. Active account ban -> Banned (consumes failure budget 3/4).
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload("BANNEDACC", build: 5875, clientSeed: 5, serverSeed, validKey);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);
            (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
            Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
            Assert.Equal((byte)AuthResponseCode.Banned, body[0]);
        }

        // 6. Active IP ban -> Banned (consumes failure budget 4/4).
        rig.SeedIpBan("127.0.0.1");
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload("IPBANNED", build: 5875, clientSeed: 6, serverSeed, validKey);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);
            (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
            Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
            Assert.Equal((byte)AuthResponseCode.Banned, body[0]);
        }

        // 7. Auth failure burst exhausted (5th attempt) -> Guard refuses with AuthResponseCode.Failed before DB lookup.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload("NOBODY", build: 5875, clientSeed: 7, serverSeed, validKey);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);
            (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
            Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
            Assert.Equal((byte)AuthResponseCode.Failed, body[0]);
            Assert.Equal(1, rig.Gateway.Guard!.RefusedAuthAttempts);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_WorldHeaderCrypt_RoundTripAndMutation_Seeded(int seed)
    {
        var rng = new Random(seed);
        for (int keyIndex = 0; keyIndex < 16; keyIndex++)
        {
            byte[] sessionKey = new byte[WowSrp6.SessionKeyLength];
            rng.NextBytes(sessionKey);

            var serverCrypt = new WorldHeaderCrypt();
            serverCrypt.Initialize(sessionKey);

            var clientCrypt = new ClientSideHeaderCipher();
            clientCrypt.Initialize(sessionKey);

            byte[] originalClientHeader = new byte[WorldHeaderCrypt.IncomingHeaderLength];
            byte[] clientWireHeader = new byte[WorldHeaderCrypt.IncomingHeaderLength];
            byte[] originalServerHeader = new byte[WorldHeaderCrypt.OutgoingHeaderLength];
            byte[] serverWireHeader = new byte[WorldHeaderCrypt.OutgoingHeaderLength];

            for (int step = 0; step < 128; step++)
            {
                rng.NextBytes(originalClientHeader);
                originalClientHeader.CopyTo(clientWireHeader, 0);
                clientCrypt.EncryptClientHeader(clientWireHeader);
                serverCrypt.DecryptHeader(clientWireHeader);
                Assert.Equal(originalClientHeader, clientWireHeader);

                rng.NextBytes(originalServerHeader);
                originalServerHeader.CopyTo(serverWireHeader, 0);
                serverCrypt.EncryptHeader(serverWireHeader);
                clientCrypt.DecryptServerHeader(serverWireHeader);
                Assert.Equal(originalServerHeader, serverWireHeader);
            }

            // Single-bit mutation in byte 0 flips that bit in byte 0 (_recvPrev == 0) and propagates into byte 1 via rolling _recvPrev.
            var serverMutA = new WorldHeaderCrypt();
            var serverMutB = new WorldHeaderCrypt();
            serverMutA.Initialize(sessionKey);
            serverMutB.Initialize(sessionKey);

            byte[] cipherA = new byte[WorldHeaderCrypt.IncomingHeaderLength];
            rng.NextBytes(cipherA);
            byte[] cipherB = (byte[])cipherA.Clone();
            byte bitMask = (byte)(1 << rng.Next(8));
            cipherB[0] ^= bitMask;

            serverMutA.DecryptHeader(cipherA);
            serverMutB.DecryptHeader(cipherB);
            Assert.Equal(cipherA[0] ^ bitMask, cipherB[0]);
            Assert.NotEqual(cipherA[1], cipherB[1]);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_AuthSessionParserAndAddonInfo_RandomAndMutatedPayloads_Seeded(int seed)
    {
        var rng = new Random(seed);
        var codec = ClientProtocols.PreAuth.Auth;

        // 1. Pure random payloads of varying lengths (0..512 bytes) never throw.
        for (int i = 0; i < 250; i++)
        {
            byte[] randomPayload = new byte[rng.Next(0, 513)];
            rng.NextBytes(randomPayload);
            _ = codec.TryParseAuthSession(randomPayload, out _);
            byte[] addonResp = AddonInfo.BuildResponse(randomPayload);
            Assert.NotNull(addonResp);
        }

        // 2. Construct valid CMSG_AUTH_SESSION payloads and fuzz via truncation and byte mutations.
        byte[] dummyKey = new byte[WowSrp6.SessionKeyLength];
        rng.NextBytes(dummyKey);
        byte[] validAddonBlock = BuildAddonBlock(["Blizzard_AuctionUI", "Blizzard_RaidUI"]);
        byte[] validPayload = BuildAuthSessionPayload("FUZZUSER", 5875, 0x12345678u, 0x87654321u, dummyKey, validAddonBlock);

        Assert.True(codec.TryParseAuthSession(validPayload, out AuthSessionRequest parsed));
        Assert.Equal("FUZZUSER", parsed.Account);
        Assert.Equal(5875u, parsed.Build);
        Assert.NotEmpty(AddonInfo.BuildResponse(parsed.AddonBlock.Span));

        // Every strict prefix truncation of the minimum fixed fields must either fail cleanly or parse without throwing.
        for (int cut = 0; cut < validPayload.Length; cut++)
        {
            ReadOnlyMemory<byte> slice = validPayload.AsMemory(0, cut);
            bool ok = codec.TryParseAuthSession(slice, out AuthSessionRequest truncatedReq);
            if (ok)
            {
                _ = AddonInfo.BuildResponse(truncatedReq.AddonBlock.Span);
            }
        }

        // Mutate random bytes in validPayload (including account string, NUL terminator, digest, and compressed addon block).
        for (int i = 0; i < 200; i++)
        {
            byte[] mutated = (byte[])validPayload.Clone();
            int mutations = rng.Next(1, 6);
            for (int m = 0; m < mutations; m++)
            {
                mutated[rng.Next(mutated.Length)] = (byte)rng.Next(256);
            }

            if (codec.TryParseAuthSession(mutated, out AuthSessionRequest mutatedReq))
            {
                byte[] response = AddonInfo.BuildResponse(mutatedReq.AddonBlock.Span);
                Assert.NotNull(response);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_GatewaySessionWire_AllDecoders_RandomAndMutatedPayloads_Seeded(int seed)
    {
        var rng = new Random(seed);

        byte[] sampleNonce = new byte[GatewaySessionWire.NonceBytes];
        rng.NextBytes(sampleNonce);
        ClusterNodeManifest manifest = GatewayLinkManifest.Create(ClusterRole.Gateway, "gw-fuzz", Guid.NewGuid(), 1, "dev", 1, 1);

        byte[][] validBodies =
        [
            GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, sampleNonce)[1..],
            LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(manifest)[1..],
            GatewaySessionWire.EncodeOpen(1, new GatewaySessionOpenRequest(1, 42, "ALICE", 5875, "127.0.0.1:5000"))[1..],
            GatewaySessionWire.EncodeOpenReply(1, GatewaySessionOpenReply.Accept(7))[1..],
            GatewaySessionWire.EncodeClose(1, 42, ClusterSessionDetachReason.ClientDisconnect)[1..],
            GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionClientPacket, 1, 42, 0, 7, 1, (uint)WorldOpcode.CmsgPing, [1, 2, 3, 4])[1..],
        ];

        for (int i = 0; i < 300; i++)
        {
            byte[] buf;
            if (i % 2 == 0)
            {
                buf = new byte[rng.Next(0, 160)];
                rng.NextBytes(buf);
            }
            else
            {
                byte[] template = validBodies[rng.Next(validBodies.Length)];
                int len = rng.Next(0, template.Length + 1);
                buf = template[..len];
                if (buf.Length > 0 && rng.Next(2) == 0)
                {
                    buf[rng.Next(buf.Length)] ^= (byte)(1 << rng.Next(8));
                }
            }

            ExerciseWireDecoder(buf, b => _ = GatewaySessionWire.DecodeNonceFrame(b.Span, "FuzzNonce"));
            ExerciseWireDecoder(buf, b => _ = GatewaySessionWire.DecodeOpen(b.Span));
            ExerciseWireDecoder(buf, b => _ = GatewaySessionWire.DecodeOpenReply(b.Span));
            ExerciseWireDecoder(buf, b => _ = GatewaySessionWire.DecodeClose(b.Span));
            ExerciseWireDecoder(buf, b => _ = GatewaySessionWire.DecodePacket(b));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Fuzz_GatewayClientListener_RandomMalformedAndTruncatedByteStreams_Seeded(int seed)
    {
        var rng = new Random(seed);
        await using var rig = await GatewayTestRig.StartAsync();
        (_, byte[] sessionKey) = await rig.SeedAccountAsync("SURVIVOR");

        for (int i = 0; i < 18; i++)
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(rig.Gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);

            int mode = i % 6;
            if (mode == 0)
            {
                // Pure random bytes (1..96 bytes) then close.
                byte[] noise = new byte[rng.Next(1, 97)];
                rng.NextBytes(noise);
                await stream.WriteAsync(noise);
            }
            else if (mode == 1)
            {
                // Valid CMSG_PING followed by a corrupt header with size < 4 or > MaxClientPacketSize.
                byte[] pingPayload = new byte[4];
                rng.NextBytes(pingPayload);
                await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgPing, pingPayload);
                (ushort opcode, byte[] pong) = await ReadPlainServerPacketAsync(stream);
                Assert.Equal((ushort)WorldOpcode.SmsgPong, opcode);
                Assert.Equal(pingPayload, pong);

                byte[] badHeader = new byte[6];
                ushort badSize = rng.Next(2) == 0 ? (ushort)rng.Next(0, 4) : (ushort)rng.Next(WorldSession.MaxClientPacketSize + 1, ushort.MaxValue + 1);
                BinaryPrimitives.WriteUInt16BigEndian(badHeader.AsSpan(0, 2), badSize);
                BinaryPrimitives.WriteUInt32LittleEndian(badHeader.AsSpan(2, 4), (uint)WorldOpcode.CmsgPing);
                await stream.WriteAsync(badHeader);
                Assert.True(await ExpectEofAsync(stream));
            }
            else if (mode == 2)
            {
                // CMSG_AUTH_SESSION with random garbage payload.
                byte[] garbageAuth = new byte[rng.Next(1, 128)];
                rng.NextBytes(garbageAuth);
                await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, garbageAuth);
            }
            else if (mode == 3)
            {
                // Header declaring 64 bytes of payload, but client disconnects after sending only a partial body.
                byte[] header = new byte[6];
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), 68);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), (uint)WorldOpcode.CmsgAuthSession);
                await stream.WriteAsync(header);
                byte[] partial = new byte[rng.Next(1, 30)];
                rng.NextBytes(partial);
                await stream.WriteAsync(partial);
            }
            else if (mode == 4)
            {
                // Valid CMSG_AUTH_SESSION structure but with a flipped bit in the 20-byte SHA-1 digest.
                byte[] badDigestPayload = BuildAuthSessionPayload("SURVIVOR", 5875, (uint)rng.Next(), serverSeed, sessionKey);
                badDigestPayload[badDigestPayload.Length - 6] ^= 0x01;
                await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, badDigestPayload);
                (ushort opcode, byte[] body) = await ReadPlainServerPacketAsync(stream);
                Assert.Equal((ushort)WorldOpcode.SmsgAuthResponse, opcode);
                Assert.Equal((byte)AuthResponseCode.Failed, body[0]);
            }
            else
            {
                // Immediate EOF right after reading SMSG_AUTH_CHALLENGE.
            }
        }

        // Verify the gateway remains completely healthy and accepts a valid authenticated session after all fuzz streams.
        await using var goodClient = await GatewayToyClient.ConnectAndLoginAsync(rig.Gateway.ListenEndPoint, "SURVIVOR", sessionKey);
        Assert.Equal(AuthResponseCode.Ok, goodClient.AuthCode);
        await goodClient.SendAsync(WorldOpcode.CmsgPing, new byte[] { 9, 8, 7, 6 });
        (WorldOpcode replyOpcode, byte[] replyPayload) = await goodClient.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgPong, replyOpcode);
        Assert.Equal([9, 8, 7, 6], replyPayload);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Fuzz_GatewayForwardingStateMachine_EncryptedFramesAndMutations_Seeded(int seed)
    {
        var rng = new Random(seed);
        await using var rig = await GatewayTestRig.StartAsync();
        string username = $"FWD{seed}";
        (_, byte[] sessionKey) = await rig.SeedAccountAsync(username);

        await using var client = await GatewayToyClient.ConnectAndLoginAsync(rig.Gateway.ListenEndPoint, username, sessionKey);
        Assert.Equal(AuthResponseCode.Ok, client.AuthCode);

        // Exchange a seeded sequence of encrypted CMSG_PING and forwarded client packets.
        for (int step = 0; step < 20; step++)
        {
            byte[] ping = new byte[rng.Next(4, 32)];
            rng.NextBytes(ping);
            await client.SendAsync(WorldOpcode.CmsgPing, ping);
            (WorldOpcode op, byte[] pong) = await client.ReadAsync();
            Assert.Equal(WorldOpcode.SmsgPong, op);
            Assert.Equal(ping[..4], pong);

            byte[] customPayload = new byte[rng.Next(0, 256)];
            rng.NextBytes(customPayload);
            await client.SendAsync(WorldOpcode.CmsgCharEnum, customPayload);
        }

        (WorldOpcode forwardedOp, _) = await rig.Worker.NextForwardedAsync();
        Assert.True(forwardedOp is WorldOpcode.CmsgPing or WorldOpcode.CmsgCharEnum);

        // Finally, send an encrypted header with an invalid size (< 4) and verify the gateway drops the session cleanly.
        await client.SendCorruptEncryptedHeaderAsync(size: 2, rawOpcode: (uint)WorldOpcode.CmsgPing);
        Assert.True(await client.ExpectEofAsync());
        await WaitUntilAsync(() => rig.Gateway.Claims.Count == 0);
    }

    private static void ExerciseWireDecoder(ReadOnlyMemory<byte> payload, Action<ReadOnlyMemory<byte>> action)
    {
        try
        {
            action(payload);
        }
        catch (InvalidDataException)
        {
            // Expected wire rejection for malformed/truncated fuzz inputs.
        }
    }

    private static async Task<uint> ReadChallengeAsync(NetworkStream stream)
    {
        (ushort opcode, byte[] payload) = await ReadPlainServerPacketAsync(stream);
        Assert.Equal((ushort)WorldOpcode.SmsgAuthChallenge, opcode);
        Assert.Equal(4, payload.Length);
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    private static async Task WritePlainClientPacketAsync(NetworkStream stream, uint rawOpcode, byte[] payload)
    {
        byte[] header = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), rawOpcode);
        await stream.WriteAsync(header);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload);
        }
    }

    private static async Task<(ushort Opcode, byte[] Payload)> ReadPlainServerPacketAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cts.Token);
        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        byte[] payload = new byte[size - 2];
        if (payload.Length > 0)
        {
            await stream.ReadExactlyAsync(payload, cts.Token);
        }

        return (opcode, payload);
    }

    private static async Task<bool> ExpectEofAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] buf = new byte[16];
        try
        {
            while (await stream.ReadAsync(buf, cts.Token) > 0)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return true;
        }
    }

    private static byte[] BuildAuthSessionPayload(
        string account,
        uint build,
        uint clientSeed,
        uint serverSeed,
        byte[] sessionKey,
        byte[]? addonBlock = null)
    {
        byte[] accountBytes = Encoding.ASCII.GetBytes(account);
        byte[] seedBytes = new byte[4];
        byte[] serverSeedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, clientSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(serverSeedBytes, serverSeed);
        byte[] digest = Sha1.Hash(accountBytes, new byte[4], seedBytes, serverSeedBytes, sessionKey);
        byte[] addons = addonBlock ?? [];

        byte[] payload = new byte[8 + accountBytes.Length + 1 + 4 + 20 + addons.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), build);
        accountBytes.CopyTo(payload.AsSpan(8));
        int offset = 8 + accountBytes.Length + 1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), clientSeed);
        digest.CopyTo(payload.AsSpan(offset + 4, 20));
        addons.CopyTo(payload.AsSpan(offset + 24));
        return payload;
    }

    private static byte[] BuildAddonBlock(IReadOnlyList<string> addonNames)
    {
        using var raw = new MemoryStream();
        foreach (string name in addonNames)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(name);
            raw.Write(ascii, 0, ascii.Length);
            raw.WriteByte(0);
            raw.WriteByte(1); // flags
            raw.Write([0x4C, 0x1C, 0x77, 0x6D]); // standard Blizzard public key CRC
            raw.Write([0, 0, 0, 0]); // url CRC
        }

        byte[] uncompressed = raw.ToArray();
        using var output = new MemoryStream();
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBytes, uncompressed.Length);
        output.Write(lenBytes);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(uncompressed, 0, uncompressed.Length);
        }

        return output.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int i = 0; i < 200 && !predicate(); i++)
        {
            await Task.Delay(15);
        }

        Assert.True(predicate());
    }

    private sealed class ClientSideHeaderCipher
    {
        private byte[] _key = [];
        private int _sendIndex;
        private int _recvIndex;
        private byte _sendPrev;
        private byte _recvPrev;

        public void Initialize(byte[] sessionKey)
        {
            _key = (byte[])sessionKey.Clone();
            _sendIndex = 0;
            _recvIndex = 0;
            _sendPrev = 0;
            _recvPrev = 0;
        }

        public void EncryptClientHeader(Span<byte> header)
        {
            for (int i = 0; i < header.Length; i++)
            {
                byte encrypted = unchecked((byte)((header[i] ^ _key[_sendIndex]) + _sendPrev));
                header[i] = encrypted;
                _sendPrev = encrypted;
                _sendIndex = (_sendIndex + 1) % _key.Length;
            }
        }

        public void DecryptServerHeader(Span<byte> header)
        {
            for (int i = 0; i < header.Length; i++)
            {
                byte encrypted = header[i];
                header[i] = (byte)(unchecked((byte)(encrypted - _recvPrev)) ^ _key[_recvIndex]);
                _recvPrev = encrypted;
                _recvIndex = (_recvIndex + 1) % _key.Length;
            }
        }
    }

    private sealed class CapturingSink : IGatewaySessionSink
    {
        public void OnOpened(GatewaySessionOpenReply reply)
        {
        }

        public void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void OnClosed(ClusterSessionDetachReason reason)
        {
        }
    }

    private sealed class FakeAccountStore : IAccountStore
    {
        private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
        private int _nextId;

        public int Add(string username, byte[] sessionKey, AccountStatus status)
        {
            int id = Interlocked.Increment(ref _nextId);
            _accounts[username] = new Account
            {
                Id = id,
                Username = username.ToUpperInvariant(),
                Salt = new byte[32],
                Verifier = new byte[32],
                SessionKey = sessionKey,
                Status = status,
            };
            return id;
        }

        public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            Task.FromResult(_accounts.GetValueOrDefault(username));

        public Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateCredentialsAsync(string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateSessionKeyAsync(string username, byte[] sessionKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateLoginAsync(string username, byte[] sessionKey, string? address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdateSecurityAsync(string username, AccountSecurity security, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeBanStore : IBanStore
    {
        private readonly ConcurrentDictionary<int, AccountBanRecord> _accountBans = new();
        private readonly ConcurrentDictionary<string, IpBanRecord> _ipBans = new(StringComparer.OrdinalIgnoreCase);

        public void AddAccountBan(int accountId) =>
            _accountBans[accountId] = new AccountBanRecord(1, accountId, 100, 100, "test", "fuzz test ban", true, 1);

        public void AddIpBan(string ip) =>
            _ipBans[ip] = new IpBanRecord(ip, 100, 100, "test", "fuzz ip ban");

        public Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_accountBans.GetValueOrDefault(accountId));

        public Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default) =>
            Task.FromResult(_ipBans.GetValueOrDefault(ip));

        public Task<IReadOnlySet<int>> FindBannedAccountsAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> FindBannedIpsAsync(IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<int>> FindAccountsWithHistoryAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkerAcceptor : IGatewaySessionAcceptor
    {
        private readonly Channel<(WorldOpcode Opcode, byte[] Payload)> _forwarded =
            Channel.CreateUnbounded<(WorldOpcode Opcode, byte[] Payload)>();

        public ChannelReader<(WorldOpcode Opcode, byte[] Payload)> Forwarded => _forwarded.Reader;

        public ConcurrentQueue<FakeRemoteSession> Sessions { get; } = new();

        public ValueTask<GatewaySessionAcceptResult> OpenAsync(
            GatewaySessionOpenRequest request,
            IGatewaySessionOutput output,
            CancellationToken cancellationToken)
        {
            var session = new FakeRemoteSession(output, _forwarded.Writer);
            Sessions.Enqueue(session);
            return ValueTask.FromResult(GatewaySessionAcceptResult.Accepted(session));
        }
    }

    private sealed class FakeRemoteSession(
        IGatewaySessionOutput output,
        ChannelWriter<(WorldOpcode Opcode, byte[] Payload)> forwarded) : IGatewayRemoteSession
    {
        public IGatewaySessionOutput Output { get; } = output;

        public ClusterSessionDetachReason? ClosedBy { get; private set; }

        public void Start()
        {
        }

        public void Deliver(WorldOpcode opcode, byte[] payload) => forwarded.TryWrite((opcode, payload));

        public void CloseFromGateway(ClusterSessionDetachReason reason) => ClosedBy = reason;
    }

    private sealed class GatewayTestRig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly FakeAccountStore _accounts;
        private readonly FakeBanStore _bans;

        private GatewayTestRig(
            ServiceProvider services,
            FakeAccountStore accounts,
            FakeBanStore bans,
            WorkerRig worker,
            GatewayServer gateway)
        {
            _services = services;
            _accounts = accounts;
            _bans = bans;
            Worker = worker;
            Gateway = gateway;
        }

        public WorkerRig Worker { get; }

        public GatewayServer Gateway { get; }

        public static async Task<GatewayTestRig> StartAsync(
            NetProtectionOptions? protection = null,
            Action<GatewayOptions>? configureGateway = null,
            bool startWorker = true)
        {
            var accounts = new FakeAccountStore();
            var bans = new FakeBanStore();
            var services = new ServiceCollection();
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IBanStore>(bans);
            ServiceProvider provider = services.BuildServiceProvider();

            var worker = new WorkerRig();
            int workerPort;
            if (startWorker)
            {
                worker.Start();
                workerPort = worker.Host.BoundEndPoint.Port;
            }
            else
            {
                using var tempListener = new TcpListener(IPAddress.Loopback, 0);
                tempListener.Start();
                workerPort = ((IPEndPoint)tempListener.LocalEndpoint).Port;
                tempListener.Stop();
            }

            NetProtectionOptions netOptions = protection ?? new NetProtectionOptions();
            netOptions.ExemptLoopbackOnLoopbackBind = false;

            var gatewayOptions = new GatewayOptions
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                WorkerAddress = "127.0.0.1",
                WorkerPort = workerPort,
                LinkSecret = ValidSecret,
                LinkRetryDelay = TimeSpan.FromMilliseconds(25),
            };
            configureGateway?.Invoke(gatewayOptions);

            var gateway = new GatewayServer(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(gatewayOptions),
                NullLoggerFactory.Instance,
                Options.Create(netOptions));

            await gateway.StartAsync(CancellationToken.None);
            if (startWorker)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await gateway.WaitForLinkAsync(cts.Token);
            }

            return new GatewayTestRig(provider, accounts, bans, worker, gateway);
        }

        public Task<(int Id, byte[] SessionKey)> SeedAccountAsync(string username)
        {
            byte[] key = new byte[WowSrp6.SessionKeyLength];
            for (int i = 0; i < key.Length; i++)
            {
                key[i] = (byte)(i + username[0]);
            }

            int id = _accounts.Add(username, key, AccountStatus.Active);
            return Task.FromResult((id, key));
        }

        public Task<int> SeedRawAccountAsync(string username, byte[] sessionKey, AccountStatus status) =>
            Task.FromResult(_accounts.Add(username, sessionKey, status));

        public void SeedAccountBan(int accountId) => _bans.AddAccountBan(accountId);

        public void SeedIpBan(string ip) => _bans.AddIpBan(ip);

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Gateway.StopAsync(cts.Token);
            Gateway.Dispose();
            await Worker.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class WorkerRig : IAsyncDisposable
    {
        private readonly FakeWorkerAcceptor _acceptor = new();

        public WorkerRig()
        {
            ClusterNodeManifest manifest = GatewayLinkManifest.Create(ClusterRole.Worker, "worker-fuzz", Guid.NewGuid(), 1, "dev", 1, 1);
            Host = new GatewaySessionLinkHost(manifest, _acceptor, () => 1, ValidSecret, new IPEndPoint(IPAddress.Loopback, 0), NullLogger.Instance);
        }

        public GatewaySessionLinkHost Host { get; }

        public void Start() => Host.Start();

        public async Task<(WorldOpcode Opcode, byte[] Payload)> NextForwardedAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _acceptor.Forwarded.ReadAsync(cts.Token);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class GatewayToyClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly ClientSideHeaderCipher _crypt = new();

        private GatewayToyClient(TcpClient tcp, NetworkStream stream)
        {
            _tcp = tcp;
            _stream = stream;
        }

        public AuthResponseCode AuthCode { get; private set; }

        public static async Task<GatewayToyClient> ConnectAndLoginAsync(IPEndPoint endpoint, string account, byte[] sessionKey)
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(endpoint);
            NetworkStream stream = tcp.GetStream();

            uint serverSeed = await ReadChallengeAsync(stream);
            byte[] payload = BuildAuthSessionPayload(account, 5875, 0xCAFEBABEu, serverSeed, sessionKey);
            await WritePlainClientPacketAsync(stream, (uint)WorldOpcode.CmsgAuthSession, payload);

            var client = new GatewayToyClient(tcp, stream);
            client._crypt.Initialize(sessionKey);

            (WorldOpcode op, byte[] response) = await client.ReadAsync();
            Assert.Equal(WorldOpcode.SmsgAuthResponse, op);
            var code = (AuthResponseCode)response[0];
            if (code == AuthResponseCode.Ok)
            {
                (WorldOpcode addonOp, _) = await client.ReadAsync();
                Assert.Equal(WorldOpcode.SmsgAddonInfo, addonOp);
            }

            client.AuthCode = code;
            return client;
        }

        public async Task SendAsync(WorldOpcode opcode, byte[] payload)
        {
            byte[] header = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), (ushort)(payload.Length + 4));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), (uint)opcode);
            _crypt.EncryptClientHeader(header);
            await _stream.WriteAsync(header);
            if (payload.Length > 0)
            {
                await _stream.WriteAsync(payload);
            }
        }

        public async Task SendCorruptEncryptedHeaderAsync(ushort size, uint rawOpcode)
        {
            byte[] header = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), size);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), rawOpcode);
            _crypt.EncryptClientHeader(header);
            await _stream.WriteAsync(header);
        }

        public async Task<(WorldOpcode Opcode, byte[] Payload)> ReadAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] header = new byte[4];
            await _stream.ReadExactlyAsync(header, cts.Token);
            _crypt.DecryptServerHeader(header);
            ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            byte[] payload = new byte[size - 2];
            if (payload.Length > 0)
            {
                await _stream.ReadExactlyAsync(payload, cts.Token);
            }

            return ((WorldOpcode)opcode, payload);
        }

        public Task<bool> ExpectEofAsync() => GatewayFuzzAndExceptionTests.ExpectEofAsync(_stream);

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

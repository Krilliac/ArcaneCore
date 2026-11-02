using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Peer;

/// <summary>
/// The peer link over real loopback TCP with in-process hosts and clients (no processes): admission, refusal, holder change, reconnect, timeouts, idempotency.
/// Every wait is a bounded poll on an observable condition, never a fixed sleep (the one exception proves a negative and is marked).
/// </summary>
public sealed class PeerLinkTests : IAsyncLifetime
{
    private const string Secret = "peer-secret-0123456789";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly InMemoryRealmAuthorityLeaseStore _store = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly Dictionary<string, string> _nodes = new(StringComparer.Ordinal);

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2.9.3 only honours IAsyncLifetime (it never calls IAsyncDisposable on a test class).
    public async Task DisposeAsync()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }
    }

    private sealed class StoreLeaseSource(IRealmAuthorityLeaseStore store, string nodeId) : IPeerLeaseSource
    {
        public string NodeId => nodeId;

        public IReadOnlyList<PeerLeaseToken> HeldLeases()
        {
            var held = new List<PeerLeaseToken>();
            foreach (string scope in new[] { RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy })
            {
                if (store.GetCurrent(scope, DateTimeOffset.UtcNow) is { } lease && lease.OwnerNodeId == nodeId)
                {
                    held.Add(new PeerLeaseToken(scope, lease.FencingToken));
                }
            }

            return held;
        }
    }

    private static ClusterNodeManifest Manifest(string node, Guid incarnation, string content = "rev")
        => GatewayLinkManifest.Create(ClusterRole.Worker, node, incarnation, 1, content, 1, 1);

    private static PeerLinkOptions FastOptions() => new()
    {
        Port = 0,
        ReconnectInitialMs = 20,
        ReconnectMaxMs = 100,
        HolderPollMs = 30,
        HandshakeTimeoutMs = 5000,
        RequestTimeoutMs = 3000,
    };

    private (PeerLinkHost Host, Guid Incarnation) StartHolder(string node, bool takeLeases = true, string secret = Secret)
    {
        var incarnation = Guid.NewGuid();
        if (takeLeases)
        {
            _store.TryAcquire(RealmAuthorityScopes.Social, node, incarnation, Hour, DateTimeOffset.UtcNow);
            _store.TryAcquire(RealmAuthorityScopes.Economy, node, incarnation, Hour, DateTimeOffset.UtcNow);
        }

        var host = new PeerLinkHost(FastOptions(), secret, Manifest(node, incarnation), new StoreLeaseSource(_store, node), NullLogger.Instance);
        host.Start();
        _disposables.Add(host);
        _nodes[node] = $"127.0.0.1:{host.LocalEndPoint.Port}";
        return (host, incarnation);
    }

    private PeerLinkClient StartClient(string node, string secret = Secret, string content = "rev", PeerLinkOptions? options = null)
    {
        PeerLinkOptions opts = options ?? FastOptions();
        foreach ((string id, string endpoint) in _nodes)
        {
            opts.Nodes[id] = endpoint;
        }

        var locator = new PeerHolderLocator(_store, opts);
        var client = new PeerLinkClient(opts, secret, Manifest(node, Guid.NewGuid(), content), new StoreLeaseSource(_store, node), locator.Locate, NullLogger.Instance);
        client.Start();
        _disposables.Add(client);
        return client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, $"not met in time: {what}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Link_AdmitsAPeer_HelloCarriesNodeIncarnationAndLeaseTokens_AndEchoes()
    {
        (PeerLinkHost host, Guid holderIncarnation) = StartHolder("node-a");
        PeerLinkClient client = StartClient("node-c");
        await WaitUntilAsync(() => client.IsConnected, "client admitted");

        PeerHelloInfo peer = Assert.IsType<PeerHelloInfo>(client.ConnectedPeer);
        Assert.Equal("node-a", peer.Manifest.NodeId);
        Assert.Equal(holderIncarnation, peer.Manifest.NodeIncarnation);
        long socialToken = _store.GetCurrent(RealmAuthorityScopes.Social, DateTimeOffset.UtcNow)!.FencingToken;
        Assert.Contains(new PeerLeaseToken(RealmAuthorityScopes.Social, socialToken), peer.Leases);
        Assert.Equal(1, host.AdmittedLinkCount);

        byte[] echoed = await client.EchoAsync(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, echoed);
    }

    [Fact]
    public async Task Link_RefusesAWrongSecret()
    {
        (PeerLinkHost host, _) = StartHolder("node-a");
        PeerLinkClient client = StartClient("node-c", secret: "another-secret-0123456789");

        await WaitUntilAsync(() => host.RefusedLinkCount >= 1 && client.LastRefusal is not null, "wrong secret refused");
        Assert.Contains("secret", client.LastRefusal);
        Assert.False(client.IsConnected);
        Assert.Equal(0, client.ConnectCount);
        Assert.Equal(0, host.AdmittedLinkCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EchoAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task Link_RefusesAManifestMismatch()
    {
        (PeerLinkHost host, _) = StartHolder("node-a");
        PeerLinkClient client = StartClient("node-c", content: "other-revision");

        await WaitUntilAsync(() => host.RefusedLinkCount >= 1 && client.LastRefusal is not null, "manifest mismatch refused");
        Assert.Contains("content revision", client.LastRefusal);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Link_WhenTheHolderChanges_ReRoutesToTheNewHolder()
    {
        (PeerLinkHost hostA, Guid incA) = StartHolder("node-a");
        (PeerLinkHost hostB, _) = StartHolder("node-b", takeLeases: false);
        PeerLinkClient client = StartClient("node-c");
        await WaitUntilAsync(() => client.IsConnected && client.ConnectedPeer?.Manifest.NodeId == "node-a", "linked to node-a");

        foreach (string scope in new[] { RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy })
        {
            long token = _store.GetCurrent(scope, DateTimeOffset.UtcNow)!.FencingToken;
            Assert.Equal(AuthorityLeaseOutcome.Released, _store.TryRelease(scope, "node-a", incA, token).Outcome);
        }

        Assert.Equal(AuthorityLeaseOutcome.Granted, _store.TryAcquire(RealmAuthorityScopes.Social, "node-b", Guid.NewGuid(), Hour, DateTimeOffset.UtcNow).Outcome);

        await WaitUntilAsync(() => client.IsConnected && client.ConnectedPeer?.Manifest.NodeId == "node-b", "re-routed to node-b");
        Assert.Equal(1, hostB.AdmittedLinkCount);
        await WaitUntilAsync(() => hostA.AdmittedLinkCount == 0, "link to node-a gone");
        Assert.Equal(new byte[] { 9 }, await client.EchoAsync(new byte[] { 9 }));
    }

    [Fact]
    public async Task Link_WhenDropped_Reconnects()
    {
        (PeerLinkHost host, _) = StartHolder("node-a");
        PeerLinkClient client = StartClient("node-c");
        await WaitUntilAsync(() => client.IsConnected, "first link");
        Assert.Equal(1, client.ConnectCount);

        host.DropLinksForTests();

        await WaitUntilAsync(() => client.ConnectCount == 2 && client.IsConnected, "reconnected");
        Assert.Equal(new byte[] { 7 }, await client.EchoAsync(new byte[] { 7 }));
    }

    [Fact]
    public async Task Request_WithoutAnAnswer_TimesOut_AndLeavesNothingPending()
    {
        (PeerLinkHost host, _) = StartHolder("node-a");
        host.SuppressReplyForTests = _ => true;
        PeerLinkClient client = StartClient("node-c");
        await WaitUntilAsync(() => client.IsConnected, "client admitted");

        await Assert.ThrowsAsync<TimeoutException>(() => client.EchoAsync(new byte[] { 1 }, timeout: TimeSpan.FromMilliseconds(150)));
        Assert.Equal(0, client.PendingRequestCount);
        await WaitUntilAsync(() => host.EchoExecutions >= 1, "host recorded the execution");
        Assert.Equal(1, host.EchoExecutions);
    }

    [Fact]
    public async Task Request_RetriedWithTheSameIdempotencyId_IsAnsweredWithoutExecutingAgain()
    {
        (PeerLinkHost host, _) = StartHolder("node-a");
        PeerLinkClient client = StartClient("node-c");
        await WaitUntilAsync(() => client.IsConnected, "client admitted");

        await client.EchoAsync(new byte[] { 5 }, idempotencyId: 4242);
        byte[] second = await client.EchoAsync(new byte[] { 6 }, idempotencyId: 4242);

        Assert.Equal(new byte[] { 5 }, second);
        Assert.Equal(1, host.EchoExecutions);
        Assert.Equal(1, host.DuplicateRequests);
    }

    [Fact]
    public async Task Requests_AreBoundedByMaxPendingRequests()
    {
        (PeerLinkHost host, _) = StartHolder("node-a");
        host.SuppressReplyForTests = _ => true;
        PeerLinkOptions options = FastOptions();
        options.MaxPendingRequests = 2;
        PeerLinkClient client = StartClient("node-c", options: options);
        await WaitUntilAsync(() => client.IsConnected, "client admitted");

        Task first = client.EchoAsync(new byte[] { 1 }, timeout: TimeSpan.FromSeconds(10));
        Task second = client.EchoAsync(new byte[] { 2 }, timeout: TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => client.PendingRequestCount == 2, "two requests pending");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EchoAsync(new byte[] { 3 }));

        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        await Assert.ThrowsAnyAsync<Exception>(() => second);
    }

    [Fact]
    public async Task Request_WhenTheLinkIsLostMeanwhile_FailsWithIoException()
    {
        (PeerLinkHost host, _) = StartHolder("node-a");
        host.SuppressReplyForTests = _ => true;
        PeerLinkClient client = StartClient("node-c");
        await WaitUntilAsync(() => client.IsConnected, "client admitted");

        Task<byte[]> request = client.EchoAsync(new byte[] { 1 }, timeout: TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => host.EchoExecutions == 1, "request reached the holder");
        host.DropLinksForTests();

        await Assert.ThrowsAsync<IOException>(() => request);
    }

    [Fact]
    public async Task Link_ToAHolderWithoutAnEndpointEntry_IsNotDialled()
    {
        _store.TryAcquire(RealmAuthorityScopes.Social, "node-x", Guid.NewGuid(), Hour, DateTimeOffset.UtcNow);
        PeerLinkClient client = StartClient("node-c");

        // Proves a negative: several poll intervals (30 ms each) pass and no dial happens.
        await Task.Delay(200);
        Assert.False(client.IsConnected);
        Assert.Equal(0, client.ConnectCount);
        Assert.Null(client.LastRefusal);
        Assert.Null(client.LastError);
    }

    [Fact]
    public void Locator_FindsTheSocialHolder_ElseTheEconomyHolder()
    {
        var options = new PeerLinkOptions();
        options.Nodes["node-a"] = "10.0.0.1:8097";
        options.Nodes["node-b"] = "10.0.0.2:8097";
        var locator = new PeerHolderLocator(_store, options);
        Assert.Null(locator.Locate());

        _store.TryAcquire(RealmAuthorityScopes.Economy, "node-b", Guid.NewGuid(), Hour, DateTimeOffset.UtcNow);
        PeerHolder economy = Assert.IsType<PeerHolder>(locator.Locate());
        Assert.Equal("node-b", economy.NodeId);
        Assert.Equal("10.0.0.2", economy.Host);
        Assert.Equal(8097, economy.Port);
        Assert.Equal(RealmAuthorityScopes.Economy, economy.Scope);

        _store.TryAcquire(RealmAuthorityScopes.Social, "node-a", Guid.NewGuid(), Hour, DateTimeOffset.UtcNow);
        PeerHolder social = Assert.IsType<PeerHolder>(locator.Locate());
        Assert.Equal("node-a", social.NodeId);
        Assert.Equal(RealmAuthorityScopes.Social, social.Scope);
    }

    [Fact]
    public async Task KeeperLeaseSource_ReportsTheLeasesTheKeeperHolds_AndGatesTheListener()
    {
        await using var keeper = new RealmAuthorityLeaseKeeper(
            _store,
            Microsoft.Extensions.Options.Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker }),
            Microsoft.Extensions.Options.Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = "node-k" }),
            NullLogger<RealmAuthorityLeaseKeeper>.Instance,
            null,
            null,
            _ => { },
            null);
        var source = new KeeperPeerLeaseSource(keeper);
        Assert.Equal("node-k", source.NodeId);
        Assert.Empty(source.HeldLeases());
        Assert.False(PeerLinkHost.HoldsPeerScope(source));

        await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(
            new[] { RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy, RealmAuthorityScopes.Instances },
            source.HeldLeases().Select(l => l.Scope).ToArray());
        Assert.True(PeerLinkHost.HoldsPeerScope(source));
        await keeper.ReleaseAllAsync();
    }

    [Fact]
    public void Host_RejectsAShortSecret_AndOptionsValidate()
    {
        Assert.Throws<ArgumentException>(() => new PeerLinkHost(FastOptions(), "short", Manifest("n", Guid.NewGuid()), new StoreLeaseSource(_store, "n")));
        Assert.Empty(new PeerLinkOptions().Validate());
        var bad = new PeerLinkOptions { BindAddress = "not-an-ip", ReconnectInitialMs = 0 };
        bad.Nodes["x"] = "nope";
        Assert.Equal(3, bad.Validate().Count());
    }

    [Fact]
    public void Endpoints_BindFromCommandLine_WithColonsInNodeIds_AndFeedTheLocator()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine(
        [
            "--Cluster:Peer:Endpoints:0:NodeId=worker:EK", "--Cluster:Peer:Endpoints:0:Address=127.0.0.1:8105",
            "--Cluster:Peer:Endpoints:1:NodeId=standby:EK", "--Cluster:Peer:Endpoints:1:Address=127.0.0.1:8108",
            "--Cluster:Peer:Nodes:plain=127.0.0.1:8110",
        ]).Build();
        var options = new PeerLinkOptions();
        configuration.GetSection(PeerLinkOptions.SectionName).Bind(options);

        Assert.Empty(options.Validate());
        Dictionary<string, string> nodes = options.ResolveNodes();
        Assert.Equal(3, nodes.Count);
        Assert.Equal("127.0.0.1:8105", nodes["worker:EK"]);
        Assert.Equal("127.0.0.1:8108", nodes["standby:EK"]);
        Assert.Equal("127.0.0.1:8110", nodes["plain"]);

        _store.TryAcquire(RealmAuthorityScopes.Social, "worker:EK", Guid.NewGuid(), Hour, DateTimeOffset.UtcNow);
        PeerHolder holder = Assert.IsType<PeerHolder>(new PeerHolderLocator(_store, options).Locate());
        Assert.Equal("worker:EK", holder.NodeId);
        Assert.Equal(8105, holder.Port);

        var bad = new PeerLinkOptions { Endpoints = [new PeerEndpointEntry { NodeId = "", Address = "1.2.3.4:5" }, new PeerEndpointEntry { NodeId = "n", Address = "nope" }] };
        Assert.Equal(2, bad.Validate().Count());
    }

    [Theory]
    [InlineData("127.0.0.1:8097", "127.0.0.1", 8097, true)]
    [InlineData("[::1]:9000", "::1", 9000, true)]
    [InlineData("host", "", 0, false)]
    [InlineData("host:0", "", 0, false)]
    [InlineData("host:99999", "", 0, false)]
    [InlineData(":80", "", 0, false)]
    [InlineData("", "", 0, false)]
    public void TryParseEndpoint_SplitsHostAndPort(string text, string host, int port, bool ok)
    {
        Assert.Equal(ok, PeerLinkOptions.TryParseEndpoint(text, out string parsedHost, out int parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }
}

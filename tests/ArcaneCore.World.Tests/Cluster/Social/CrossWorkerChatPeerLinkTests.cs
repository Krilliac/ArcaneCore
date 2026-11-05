using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Chat traffic over the real peer link (64 KB payload limit): a worker reaches the holder's <see cref="PresenceHolderService"/> through a <see cref="PeerPresenceClient"/>.
/// A backlog above one frame must arrive across successive bounded drains, the Route reply must not echo deliveries, and the shared gateway queue must stay bounded.
/// </summary>
public sealed class CrossWorkerChatPeerLinkTests : IAsyncLifetime
{
    private const string Secret = "peer-secret-0123456789";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly InMemoryRealmAuthorityLeaseStore _store = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly InMemoryRealmPresenceDirectory _directory = new();
    private readonly InMemoryClusterSocialRouter _router;
    private readonly PresenceHolderService _holder;
    private readonly PeerLinkHost _host;
    private readonly PeerLinkClient _link;
    private readonly PeerPresenceClient _client;

    public CrossWorkerChatPeerLinkTests()
    {
        _router = new InMemoryClusterSocialRouter(_directory);
        _holder = new PresenceHolderService(_directory, _router);
        var incarnation = Guid.NewGuid();
        _store.TryAcquire(RealmAuthorityScopes.Social, "node-h", incarnation, Hour, DateTimeOffset.UtcNow);
        _host = new PeerLinkHost(FastOptions(), Secret, Manifest("node-h", incarnation), new StoreLeaseSource(_store, "node-h"), NullLogger.Instance)
        {
            PayloadHandler = _holder.Handle,
        };
        _host.Start();
        _disposables.Add(_host);

        PeerLinkOptions options = FastOptions();
        options.Nodes["node-h"] = $"127.0.0.1:{_host.LocalEndPoint.Port}";
        var locator = new PeerHolderLocator(_store, options);
        _link = new PeerLinkClient(options, Secret, Manifest("node-a", Guid.NewGuid()), new StoreLeaseSource(_store, "node-a"), locator.Locate, NullLogger.Instance);
        _link.Start();
        _disposables.Add(_link);
        _client = new PeerPresenceClient(_link);
    }

    public async Task InitializeAsync()
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!_link.IsConnected)
        {
            Assert.True(DateTime.UtcNow < end, "worker admitted by the holder");
            await Task.Delay(5);
        }
    }

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
            if (store.GetCurrent(RealmAuthorityScopes.Social, DateTimeOffset.UtcNow) is { } lease && lease.OwnerNodeId == nodeId)
            {
                held.Add(new PeerLeaseToken(RealmAuthorityScopes.Social, lease.FencingToken));
            }

            return held;
        }
    }

    private static ClusterNodeManifest Manifest(string node, Guid incarnation)
        => GatewayLinkManifest.Create(ClusterRole.Worker, node, incarnation, 1, "rev", 1, 1);

    private static PeerLinkOptions FastOptions() => new()
    {
        Port = 0,
        ReconnectInitialMs = 20,
        ReconnectMaxMs = 100,
        HolderPollMs = 30,
        HandshakeTimeoutMs = 5000,
        RequestTimeoutMs = 3000,
    };

    private long Register(ulong guid, string name, string worker, byte security = 0)
    {
        var entry = new RealmPresenceEntry(
            guid, name, (int)guid + 1000, worker, "gateway", 0, 12, 0, 10, 1, 1, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow, Security: security);
        PresenceUpsertReply reply = _holder.Register(entry);
        Assert.Equal(PresenceMutationResult.Applied, reply.Result);
        return reply.Current!.SessionGeneration;
    }

    private static ClusterSocialMessage Whisper(long senderGeneration, int sequence, string text)
        => new(Guid.NewGuid(), SocialRouteChannel.Whisper, 1, "Alice", 1001, senderGeneration, "worker-a", "gateway", sequence, text, TargetCharacterGuid: 2);

    [Fact]
    public async Task ABacklogAbove64KB_ArrivesAcrossSuccessiveBoundedDrains_AndRouteDoesNotEchoIt()
    {
        long generation = Register(1, "Alice", "worker-a");
        Register(2, "Bob", "worker-b");
        string filler = new('x', 1000);
        const int Count = 200; // about 200 KB queued for worker-b, over three times one frame

        for (int i = 1; i <= Count; i++)
        {
            ClusterSocialMessage message = Whisper(generation, i, i.ToString("D4") + filler);
            SocialRouteResult routed = await Task.Run(() => _client.Route(message));
            Assert.Equal(SocialRouteStatus.Delivered, routed.Status);
            Assert.Empty(routed.Deliveries); // the reply carries a count, never the deliveries
        }

        var received = new List<RoutedSocialDelivery>();
        int batches = 0;
        while (true)
        {
            // A reply over the link's payload limit would throw here; every batch must fit.
            IReadOnlyList<RoutedSocialDelivery> batch = await Task.Run(() => _client.DrainWorkerDeliveriesBatch("worker-b"));
            if (batch.Count == 0)
            {
                break;
            }

            Assert.True(batch.Sum(static d => PresenceWireCodec.DeliveryWireSize(d)) <= InMemoryClusterSocialRouter.MaxDrainBatchBytes);
            received.AddRange(batch);
            batches++;
        }

        Assert.True(batches > 1, "the backlog needed several drains");
        Assert.Equal(Count, received.Count);
        Assert.Equal(
            Enumerable.Range(1, Count).Select(static i => i.ToString("D4")).ToArray(),
            received.Select(static d => d.PayloadText[..4]).ToArray());
        Assert.Equal(received.Select(static d => d.DeliverySequence).Order().ToArray(), received.Select(static d => d.DeliverySequence).ToArray());
        Assert.Equal(Count, received.Select(static d => d.DeliverySequence).Distinct().Count());
        Assert.All(received, static d => Assert.Equal(2UL, d.RecipientGuid));
    }

    [Fact]
    public async Task TheUnboundedDrain_OverTheLink_TakesEveryBatch_AndTheGatewayQueueStaysBoundedAndDrainable()
    {
        long generation = Register(1, "Alice", "worker-a");
        Register(2, "Bob", "worker-b");
        string filler = new('y', 1000);
        const int Count = 100;
        for (int i = 1; i <= Count; i++)
        {
            ClusterSocialMessage message = Whisper(generation, i, filler);
            await Task.Run(() => _client.Route(message));
        }

        // Both workers share the gateway id; its queue holds the newest deliveries only, and one drain of it fits one reply.
        IReadOnlyList<RoutedSocialDelivery> gateway = await Task.Run(() => _client.DrainGatewayDeliveries("gateway"));
        Assert.InRange(gateway.Count, 1, InMemoryClusterSocialRouter.MaxGatewayQueueDeliveries);
        Assert.True(gateway.Sum(static d => PresenceWireCodec.DeliveryWireSize(d)) <= InMemoryClusterSocialRouter.MaxGatewayQueueBytes);
        Assert.Empty(await Task.Run(() => _client.DrainGatewayDeliveries("gateway")));

        IReadOnlyList<RoutedSocialDelivery> all = await Task.Run(() => _client.DrainWorkerDeliveries("worker-b"));
        Assert.Equal(Count, all.Count);
        Assert.Empty(await Task.Run(() => _client.DrainWorkerDeliveries("worker-b")));
    }

    [Fact]
    public async Task ChannelModerationAndWho_TravelTheLink()
    {
        long alice = Register(1, "Alice", "worker-a");
        long bob = Register(2, "Bob", "worker-b");
        Register(3, "Hidden", "worker-b", security: 2);
        Assert.True(await Task.Run(() => _client.JoinChannel("X", 1, alice)));
        Assert.True(await Task.Run(() => _client.JoinChannel("X", 2, bob)));

        Assert.True(await Task.Run(() => _client.ModerateChannel("X", ChannelModerationAction.ClaimOwner, 1, 1)));
        Assert.True(await Task.Run(() => _client.ModerateChannel("X", ChannelModerationAction.Ban, 1, 2)));
        Assert.False(await Task.Run(() => _client.ModerateChannel("X", ChannelModerationAction.Unban, 2, 2)), "a banned plain member is no moderator");

        ChannelModerationState state = Assert.IsType<ChannelModerationState>(await Task.Run(() => _client.GetChannelModeration("X")));
        Assert.Equal(1UL, state.Owner);
        Assert.Equal([1UL], state.Moderators);
        Assert.Equal([2UL], state.Banned);
        Assert.Null(await Task.Run(() => _client.GetChannelModeration("Nope")));
        Assert.False(await Task.Run(() => _client.JoinChannel("X", 2, bob)), "the holder refuses a banned character's subscribe");

        // Staff security travels in the presence entries a who query returns.
        IReadOnlyList<RealmPresenceEntry> who = await Task.Run(() => _client.QueryWho(new RealmWhoFilter(MinLevel: 1, MaxLevel: 100, MaxResults: 10)));
        Assert.Equal(2, who.Single(static e => e.CharacterName == "Hidden").Security + 0);
    }
}

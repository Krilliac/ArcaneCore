using ArcaneCore.Game;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The four battleground methods of <see cref="PeerPresenceClient"/> over a real peer link: a worker's client talks to a <see cref="PresenceHolderService"/> served by the node that holds the
/// Social lease. Submit, take (with and without the preferred flag), post and drain each cross the link and reach the hub. Unproven until the integration gate (written without a build).
/// </summary>
public sealed class PeerPresenceClientBattlegroundTests : IAsyncLifetime
{
    private const string Secret = "peer-secret-0123456789";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly ObjectGuid Someone = ObjectGuid.Player(9);

    private readonly InMemoryRealmAuthorityLeaseStore _store = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly PresenceHolderService _holder;
    private readonly PeerLinkHost _host;
    private readonly PeerLinkClient _link;
    private readonly PeerPresenceClient _client;

    public PeerPresenceClientBattlegroundTests()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        _holder = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
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
        _link = new PeerLinkClient(options, Secret, Manifest("node-w", Guid.NewGuid()), new StoreLeaseSource(_store, "node-w"), locator.Locate, NullLogger.Instance);
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

    [Fact]
    public async Task SubmitAndTake_CarryACommandToTheHolder_AndSubmitReportsWhetherAHolderIsLive()
    {
        bool live = await Task.Run(() => _client.SubmitBattlegroundCommands("worker-ek", [new BattlegroundStatusCommand(Someone, 7)]));
        Assert.False(live);   // no holder has asked yet; the command waits at the hub
        Assert.Equal(1, _holder.Battlegrounds.QueuedCommands);

        BattlegroundTakeResult taken = await Task.Run(() => _client.TakeBattlegroundCommands("worker-instances"));

        Assert.True(taken.IsHolder);
        BattlegroundQueuedCommand item = Assert.Single(taken.Commands);
        Assert.Equal("worker-ek", item.WorkerId);
        Assert.Equal(new BattlegroundStatusCommand(Someone, 7), item.Command);
        Assert.True(await Task.Run(() => _client.SubmitBattlegroundCommands("worker-ek", [new BattlegroundLogoutCommand(Someone)])));   // a holder is live now
    }

    [Fact]
    public async Task TakeWithThePreferredFlag_ReachesTheElection_AFallbackHolderGivesWayToIt()
    {
        Assert.True((await Task.Run(() => _client.TakeBattlegroundCommands("owns-everything", preferred: false))).IsHolder);

        BattlegroundTakeResult listing = await Task.Run(() => _client.TakeBattlegroundCommands("lists-maps", preferred: true));

        Assert.True(listing.IsHolder);
        Assert.Equal("lists-maps", _holder.Battlegrounds.Holder);
        Assert.False((await Task.Run(() => _client.TakeBattlegroundCommands("owns-everything", preferred: false))).IsHolder);
    }

    [Fact]
    public async Task PostAndDrain_CarryANoticeFromTheHolderToTheWorker_AndANonHolderPostIsRefused()
    {
        Assert.True((await Task.Run(() => _client.TakeBattlegroundCommands("holder"))).IsHolder);
        var notice = new BattlegroundQueuedNotice("worker-ek", new BattlegroundPrepareToPortNotice(Someone));

        Assert.False(await Task.Run(() => _client.PostBattlegroundNotices("someone-else", [notice])));
        Assert.True(await Task.Run(() => _client.PostBattlegroundNotices("holder", [notice])));

        IReadOnlyList<BattlegroundNotice> drained = await Task.Run(() => _client.DrainBattlegroundNotices("worker-ek"));
        Assert.Equal(new BattlegroundPrepareToPortNotice(Someone), Assert.Single(drained));
        Assert.Empty(await Task.Run(() => _client.DrainBattlegroundNotices("worker-ek")));
        Assert.Empty(await Task.Run(() => _client.DrainBattlegroundNotices(" ")));   // a blank worker id never leaves the node
    }
}

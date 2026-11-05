using System.Collections.Concurrent;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Realm presence over the peer link with in-process workers (no processes): one node holds the Social lease and serves <see cref="PresenceHolderService"/> through its
/// <see cref="PeerLinkHost"/>; workers A and B publish through a <see cref="PeerPresenceClient"/> each. Flushes are driven by the tests (no timers), waits are bounded polls.
/// </summary>
public sealed class PresenceWireTests : IAsyncLifetime
{
    private const string Secret = "peer-secret-0123456789";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly InMemoryRealmAuthorityLeaseStore _store = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly PresenceHolderService _holder;
    private readonly PeerLinkHost _host;
    private readonly ConcurrentQueue<int> _kicksA = new();
    private readonly ConcurrentQueue<int> _kicksB = new();
    private readonly PeerLinkClient _linkA;
    private readonly PeerLinkClient _linkB;
    private readonly ClusterPresencePublisher _publisherA;
    private readonly ClusterPresencePublisher _publisherB;

    public PresenceWireTests()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        _holder = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));

        var holderIncarnation = Guid.NewGuid();
        _store.TryAcquire(RealmAuthorityScopes.Social, "node-h", holderIncarnation, Hour, DateTimeOffset.UtcNow);
        _host = new PeerLinkHost(FastOptions(), Secret, Manifest("node-h", holderIncarnation), new StoreLeaseSource(_store, "node-h"), NullLogger.Instance)
        {
            PayloadHandler = _holder.Handle,
        };
        _host.Start();
        _disposables.Add(_host);

        _linkA = StartClient("node-a");
        _linkB = StartClient("node-b");
        _publisherA = new ClusterPresencePublisher(new PeerPresenceClient(_linkA), "node-a", "gw", _kicksA.Enqueue);
        _publisherB = new ClusterPresencePublisher(new PeerPresenceClient(_linkB), "node-b", "gw", _kicksB.Enqueue);
        _disposables.Add(_publisherA);
        _disposables.Add(_publisherB);
    }

    public async Task InitializeAsync()
    {
        await WaitUntilAsync(() => _linkA.IsConnected && _linkB.IsConnected, "both workers admitted by the holder");
    }

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

    private PeerLinkClient StartClient(string node)
    {
        PeerLinkOptions options = FastOptions();
        options.Nodes["node-h"] = $"127.0.0.1:{_host.LocalEndPoint.Port}";
        var locator = new PeerHolderLocator(_store, options);
        var client = new PeerLinkClient(options, Secret, Manifest(node, Guid.NewGuid()), new StoreLeaseSource(_store, node), locator.Locate, NullLogger.Instance);
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

    private static PresenceSnapshot Snapshot(ulong guid, int account, long generation, uint map = 0, uint zone = 12, byte level = 10)
        => new(guid, $"Char{guid}", account, generation, map, zone, 0, level, 1, 1, 0, 0, null, 0, false, false);

    [Fact]
    public async Task LoginOnB_KicksTheSameAccountOnA_AndTheFirstEntryGoesAway()
    {
        _publisherA.PublishLogin(Snapshot(guid: 1, account: 7, generation: 1));
        Assert.True(await _publisherA.FlushAsync());
        Assert.Equal("node-a", _holder.Directory.FindByGuid(1)!.WorkerId);

        // The same account on another worker: its own claim store also says generation 1.
        _publisherB.PublishLogin(Snapshot(guid: 2, account: 7, generation: 1));
        Assert.True(await _publisherB.FlushAsync());

        Assert.Null(_holder.Directory.FindByGuid(1));
        RealmPresenceEntry onB = Assert.IsType<RealmPresenceEntry>(_holder.Directory.FindByGuid(2));
        Assert.Equal("node-b", onB.WorkerId);
        Assert.True(onB.SessionGeneration >= 2, "the authority stamps a generation above the account's earlier registration");
        Assert.Empty(_kicksB);

        Assert.True(await _publisherA.FlushAsync());
        Assert.Equal(new[] { 7 }, _kicksA.ToArray());

        // Delivered once: a further flush on A kicks nothing more.
        Assert.True(await _publisherA.FlushAsync());
        Assert.Single(_kicksA);
    }

    [Fact]
    public async Task SameCharacterRelogsOnB_KicksA_AndALateLogoutFromADoesNotRemoveBsEntry()
    {
        _publisherA.PublishLogin(Snapshot(guid: 5, account: 9, generation: 3));
        Assert.True(await _publisherA.FlushAsync());

        _publisherB.PublishLogin(Snapshot(guid: 5, account: 9, generation: 1));
        Assert.True(await _publisherB.FlushAsync());
        Assert.Equal("node-b", _holder.Directory.FindByGuid(5)!.WorkerId);

        Assert.True(await _publisherA.FlushAsync());
        Assert.Equal(new[] { 9 }, _kicksA.ToArray());

        _publisherA.PublishLogout(5); // the kicked session logs out late
        Assert.True(await _publisherA.FlushAsync());
        Assert.Equal("node-b", _holder.Directory.FindByGuid(5)!.WorkerId);
        Assert.Equal(0, _publisherA.RegisteredCount);
        Assert.Equal(1, _publisherB.RegisteredCount);
    }

    [Fact]
    public async Task Logout_UnregistersTheCharacter()
    {
        _publisherA.PublishLogin(Snapshot(guid: 3, account: 11, generation: 1));
        Assert.True(await _publisherA.FlushAsync());
        Assert.Equal(1, _holder.Directory.Count);
        Assert.Equal(1, _publisherA.RegisteredCount);

        _publisherA.PublishLogout(3);
        Assert.True(await _publisherA.FlushAsync());

        Assert.Null(_holder.Directory.FindByGuid(3));
        Assert.Equal(0, _holder.Directory.Count);
        Assert.Equal(0, _publisherA.RegisteredCount);
        Assert.Equal(0, _publisherA.PendingCount);
    }

    [Fact]
    public async Task MapChange_UpdatesTheEntry_WithAHigherVersion()
    {
        _publisherA.PublishLogin(Snapshot(guid: 4, account: 13, generation: 1, map: 0, zone: 12));
        Assert.True(await _publisherA.FlushAsync());
        RealmPresenceEntry before = _holder.Directory.FindByGuid(4)!;

        _publisherA.PublishUpdate(Snapshot(guid: 4, account: 13, generation: 1, map: 1, zone: 14, level: 11));
        Assert.True(await _publisherA.FlushAsync());

        RealmPresenceEntry after = _holder.Directory.FindByGuid(4)!;
        Assert.Equal(1u, after.MapId);
        Assert.Equal(14u, after.ZoneId);
        Assert.Equal(11, after.Level);
        Assert.True(after.PresenceVersion > before.PresenceVersion);
        Assert.Equal(before.SessionGeneration, after.SessionGeneration);
        Assert.Equal("node-a", after.WorkerId);
    }

    [Fact]
    public async Task UpdateOfAnUntrackedCharacter_IsIgnored_AndALoginThenLogoutBeforeAFlushSendsNothing()
    {
        _publisherA.PublishUpdate(Snapshot(guid: 6, account: 15, generation: 1));
        Assert.Equal(0, _publisherA.PendingCount);

        _publisherA.PublishLogin(Snapshot(guid: 6, account: 15, generation: 1));
        _publisherA.PublishLogout(6);
        Assert.Equal(0, _publisherA.PendingCount);
        Assert.True(await _publisherA.FlushAsync());
        Assert.Equal(0, _holder.Directory.Count);
    }

    [Fact]
    public async Task ClientReaders_SeeTheAuthoritativeDirectory_OverTheLink()
    {
        _publisherA.PublishLogin(Snapshot(guid: 8, account: 21, generation: 1, level: 30));
        Assert.True(await _publisherA.FlushAsync());
        var client = new PeerPresenceClient(_linkB);

        RealmPresenceEntry? byName = await Task.Run(() => client.FindByName("char8"));
        Assert.Equal(8UL, byName!.CharacterGuid);
        Assert.Single(await Task.Run(() => client.FindByAccount(21)));
        Assert.Single(await Task.Run(() => client.QueryWho(new RealmWhoFilter(MinLevel: 25, MaxLevel: 35))));
        Assert.Empty(await Task.Run(() => client.QueryWho(new RealmWhoFilter(MinLevel: 40, MaxLevel: 60))));
        Assert.Equal(1, await Task.Run(() => client.Count));
        Assert.Null(await Task.Run(() => client.FindByGuid(999)));
    }

    [Fact]
    public void HolderHandle_AnswersAMalformedRequestWithAnErrorFrame_NotAnException()
    {
        byte[] reply = _holder.Handle(new byte[] { 200 });
        Assert.Equal(1, reply[0]);
        reply = _holder.Handle(new byte[] { (byte)PresenceOp.Register, 1, 2 });
        Assert.Equal(1, reply[0]);
    }

    [Fact]
    public async Task InProcessAuthority_IsTheCombinedFastPath_AndKicksAcrossTwoPublishers()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var holder = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
        var routed = new LeaseRoutedPresence(holder, static () => true, static () => null);
        var kicks = new ConcurrentQueue<int>();
        await using var first = new ClusterPresencePublisher(routed, "w1", "gw", kicks.Enqueue);
        await using var second = new ClusterPresencePublisher(routed, "w2", "gw", static _ => { });

        first.PublishLogin(Snapshot(guid: 30, account: 40, generation: 1));
        Assert.True(await first.FlushAsync());
        second.PublishLogin(Snapshot(guid: 31, account: 40, generation: 1));
        Assert.True(await second.FlushAsync());
        Assert.True(await first.FlushAsync());

        Assert.Equal(new[] { 40 }, kicks.ToArray());
        Assert.Equal(1, routed.Count);
    }

    [Fact]
    public async Task WithNoHolderAndNoPeerLink_TheFlushKeepsItsOperations()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var holder = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
        bool holds = false;
        var routed = new LeaseRoutedPresence(holder, () => holds, static () => null);
        await using var publisher = new ClusterPresencePublisher(routed, "w1", "gw", static _ => { });

        publisher.PublishLogin(Snapshot(guid: 50, account: 60, generation: 1));
        Assert.False(await publisher.FlushAsync());
        Assert.Equal(1, publisher.PendingCount);
        Assert.Equal(0, directory.Count);

        holds = true;
        Assert.True(await publisher.FlushAsync());
        Assert.Equal(0, publisher.PendingCount);
        Assert.Equal(1, directory.Count);
    }

    [Fact]
    public void SessionRegistry_KickSuperseded_IsFalseWithoutASession()
    {
        using var registry = new SessionRegistry();
        Assert.False(registry.KickSuperseded(123));
    }
}

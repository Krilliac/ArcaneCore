using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Group commands of a worker on the real path to the Social holder: <see cref="ClusterGroupRouting"/>, <see cref="PeerPresenceClient"/>, <see cref="PeerLinkClient"/>, a TCP link, <see cref="PeerLinkHost"/> and
/// <see cref="PresenceHolderService.Handle"/>, with the holder's own <see cref="GroupAuthority"/>. A retry must run once, and a node that does not hold the lease must never take a command.
/// </summary>
public sealed class GroupCommandPeerPathTests : IAsyncLifetime
{
    private const string Secret = "an-unit-test-peer-link-secret-of-ample-length";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private readonly InMemoryRealmAuthorityLeaseStore _store = new();
    private readonly PresenceHolderService _holder;
    private readonly PeerLinkHost _host;
    private readonly PeerLinkClient _link;
    private readonly PeerPresenceClient _presence;
    private readonly ClusterGroupRouting _routing;
    private volatile bool _holdsSocial = true;

    public GroupCommandPeerPathTests()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        _holder = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory), () => _holdsSocial);
        _holder.Groups.Restore(
            [new GroupRecord(1, 1, (byte)LootMethod.GroupLoot, 1, Group.DefaultLootThreshold, true, new ulong[8], [new GroupMemberRecord(1, 0, false), new GroupMemberRecord(2, 1, false)])],
            new Lookup());

        var incarnation = Guid.NewGuid();
        _store.TryAcquire(RealmAuthorityScopes.Social, "node-h", incarnation, Hour, DateTimeOffset.UtcNow);
        _host = new PeerLinkHost(Options(), Secret, Manifest("node-h", incarnation), new NoLeases("node-h"), NullLogger.Instance)
        {
            PayloadHandler = _holder.Handle,
            RememberAnswer = PresenceHolderService.RememberAnswer,
        };
        _host.Start();

        PeerLinkOptions options = Options();
        options.Nodes["node-h"] = $"127.0.0.1:{_host.LocalEndPoint.Port}";
        var locator = new PeerHolderLocator(_store, options);
        _link = new PeerLinkClient(options, Secret, Manifest("node-a", Guid.NewGuid()), new NoLeases("node-a"), locator.Locate, NullLogger.Instance);
        _link.Start();
        _presence = new PeerPresenceClient(_link);
        _routing = new ClusterGroupRouting(_presence, "node-a");
    }

    public async Task InitializeAsync()
    {
        using var safety = new CancellationTokenSource(Safety);
        while (!_link.IsConnected)
        {
            await Task.Delay(5, safety.Token);
        }
    }

    public async Task DisposeAsync()
    {
        await _routing.DisposeAsync();
        await _link.DisposeAsync();
        await _host.DisposeAsync();
    }

    private sealed class Lookup : ICharacterLookup
    {
        private readonly Dictionary<uint, CharacterInfo> _byId = new()
        {
            [1] = new CharacterInfo(1, 101, "Alice", Race.Human, Class.Warrior),
            [2] = new CharacterInfo(2, 102, "Bob", Race.Human, Class.Warrior),
        };

        public CharacterInfo? Find(uint characterId) => _byId.GetValueOrDefault(characterId);

        public CharacterInfo? FindByName(string name) => _byId.Values.FirstOrDefault(c => c.Name == name);
    }

    private sealed class NoLeases(string nodeId) : IPeerLeaseSource
    {
        public string NodeId => nodeId;

        public IReadOnlyList<PeerLeaseToken> HeldLeases() => [];
    }

    private static ClusterNodeManifest Manifest(string node, Guid incarnation) => GatewayLinkManifest.Create(ClusterRole.Worker, node, incarnation, 1, "rev", 1, 1);

    private static PeerLinkOptions Options() => new()
    {
        Port = 0,
        ReconnectInitialMs = 20,
        ReconnectMaxMs = 100,
        HolderPollMs = 30,
        HandshakeTimeoutMs = 5000,
        RequestTimeoutMs = 1000,
    };

    private static GroupCommand Swap() => new(GroupCommandKind.SwapSubGroup, ObjectGuid.Player(1).Value, "Alice", 0, false, TargetName: "Alice", OtherName: "Bob");

    private static GroupCommand StartReadyCheck() => new(GroupCommandKind.ReadyCheck, ObjectGuid.Player(1).Value, "Alice", 0, false, Value: 0);

    private byte SubGroupOf(uint character) => _holder.Groups.GetGroup(ObjectGuid.Player(1))!.Find(ObjectGuid.Player(character))!.SubGroup;

    /// <summary>The first request is run by the holder but its reply never comes back: to the sender that is a timeout on a live link.</summary>
    private void LoseTheFirstReply()
    {
        int requests = 0;
        _host.SuppressReplyForTests = _ => Interlocked.Increment(ref requests) == 1;
    }

    private async Task WaitForRetriesAsync(int atLeast)
    {
        using var safety = new CancellationTokenSource(Safety);
        while (_routing.SendRetried < atLeast)
        {
            await Task.Delay(5, safety.Token);
        }
    }

    [Fact]
    public async Task A_swap_retried_after_a_lost_reply_runs_once()
    {
        LoseTheFirstReply();

        _routing.Forward(Swap());
        await _routing.FlushAsync().WaitAsync(Safety);

        Assert.True(_routing.SendRetried >= 1);
        Assert.Equal(0, _routing.SendFailed);
        Assert.True(_host.DuplicateRequests >= 1);
        Assert.Equal(1, SubGroupOf(1)); // run twice it would be back at 0, with nobody told
        Assert.Equal(0, SubGroupOf(2));
    }

    [Fact]
    public async Task A_ready_check_retried_after_a_lost_reply_is_broadcast_once()
    {
        LoseTheFirstReply();

        _routing.Forward(StartReadyCheck());
        await _routing.FlushAsync().WaitAsync(Safety);

        Assert.True(_routing.SendRetried >= 1);
        Assert.Equal(1, _holder.Groups.PendingFor("node-a"));
    }

    [Fact]
    public async Task Two_commands_are_two_commands_even_when_they_are_the_same()
    {
        _routing.Forward(Swap());
        _routing.Forward(Swap());
        await _routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(0, SubGroupOf(1));
        Assert.Equal(1, SubGroupOf(2));
    }

    [Fact]
    public async Task A_node_without_the_lease_takes_nothing_and_the_same_command_runs_once_it_holds_it()
    {
        _holdsSocial = false;

        _routing.Forward(Swap());
        await WaitForRetriesAsync(2);
        Assert.Equal(0, SubGroupOf(1)); // not run, not held for later
        Assert.Equal(0, _holder.Groups.HeldCommandCount);

        _holdsSocial = true; // the "not the holder" answers are not replayed to this retry
        await _routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(1, SubGroupOf(1));
        Assert.Equal(0, _routing.SendFailed);
    }

    [Fact]
    public void A_drain_at_a_node_without_the_lease_fails_as_a_link_failure_and_leaves_the_events()
    {
        _holder.Groups.Execute("node-a", StartReadyCheck());
        Assert.Equal(1, _holder.Groups.PendingFor("node-a"));

        _holdsSocial = false;
        Assert.Throws<PresenceNotHolderException>(() => _presence.DrainGroupEvents("node-a"));
        Assert.Equal(1, _holder.Groups.PendingFor("node-a"));

        _holdsSocial = true;
        Assert.Single(_presence.DrainGroupEvents("node-a"));
    }

    [Fact]
    public async Task A_command_acked_into_a_held_table_runs_on_release()
    {
        _holder.Groups.HoldForReload();

        _routing.Forward(Swap());
        await _routing.FlushAsync().WaitAsync(Safety);
        Assert.Equal(1, _holder.Groups.HeldCommandCount);
        Assert.Equal(0, SubGroupOf(1));

        Assert.Equal(0, _holder.Groups.ReleaseHeldCommands());
        Assert.Equal(1, SubGroupOf(1));
    }

    [Fact]
    public async Task A_closed_table_answers_like_a_node_without_the_lease_so_the_sender_keeps_the_command()
    {
        _holder.Groups.HoldForReload();
        _holder.Groups.CloseHeldTable();

        _routing.Forward(Swap());
        await WaitForRetriesAsync(2);

        Assert.Equal(0, SubGroupOf(1));
        Assert.Equal(0, _routing.SendFailed);
    }

    [Fact]
    public async Task With_the_production_wiring_a_node_that_lost_the_lease_takes_no_command_although_it_keeps_the_token()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var leases = new InMemoryRealmAuthorityLeaseStore();
        var incarnation = Guid.NewGuid();
        await using var keeper = new RealmAuthorityLeaseKeeper(leases,
            Microsoft.Extensions.Options.Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
            Microsoft.Extensions.Options.Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = "node-d" }),
            NullLogger<RealmAuthorityLeaseKeeper>.Instance, null, time, _ => { });
        await keeper.AcquireAtStartAsync(incarnation, CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton(keeper);
        services.AddClusterPresence();
        await using ServiceProvider provider = services.BuildServiceProvider();
        PresenceHolderService holder = provider.GetRequiredService<PresenceHolderService>();
        LeaseRoutedPresence routed = provider.GetRequiredService<LeaseRoutedPresence>();
        byte[] command = PresenceWireCodec.EncodeRequest(PresenceOp.GroupCommand, w =>
        {
            w.Write("node-a");
            GroupWireCodec.WriteCommand(w, new GroupCommand(GroupCommandKind.Invite, ObjectGuid.Player(1).Value, "Alice", 0, false, TargetName: "Nobody"));
        });
        byte[] drain = PresenceWireCodec.EncodeRequest(PresenceOp.GroupDrain, w => w.Write("node-a"));

        Assert.True(routed.IsAuthorityReachable);
        PresenceWireCodec.OpenReply(holder.Handle(command)).Dispose(); // the live holder takes it
        int pending = holder.Groups.PendingFor("node-a");
        Assert.True(pending > 0);

        long token = keeper.TokenFor(RealmAuthorityScopes.Social)!.Value;
        leases.TryRelease(RealmAuthorityScopes.Social, "node-d", incarnation, token);
        Assert.Equal(AuthorityLeaseOutcome.Granted, leases.TryAcquire(RealmAuthorityScopes.Social, "node-e", Guid.NewGuid(), Hour, time.GetUtcNow()).Outcome);
        time.Advance(TimeSpan.FromSeconds(10)); // the renewal tick finds the loss
        Assert.False(keeper.Holds(RealmAuthorityScopes.Social));
        Assert.Equal(token, keeper.TokenFor(RealmAuthorityScopes.Social));

        Assert.Throws<PresenceNotHolderException>(() => PresenceWireCodec.OpenReply(holder.Handle(command)));
        Assert.Throws<PresenceNotHolderException>(() => PresenceWireCodec.OpenReply(holder.Handle(drain)));
        holder.Groups.HoldForReload();
        Assert.Throws<PresenceNotHolderException>(() => PresenceWireCodec.OpenReply(holder.Handle(command)));
        Assert.Equal(0, holder.Groups.HeldCommandCount);
        Assert.Equal(pending, holder.Groups.PendingFor("node-a"));
        Assert.False(routed.IsAuthorityReachable); // this node's own commands wait for the real holder too
    }

    [Fact]
    public void Only_a_taken_group_command_that_is_not_a_relay_is_remembered()
    {
        byte[] ok = PresenceWireCodec.EncodeReply();
        byte[] Command(GroupCommand c) => PresenceWireCodec.EncodeRequest(PresenceOp.GroupCommand, w =>
        {
            w.Write("node-a");
            GroupWireCodec.WriteCommand(w, c);
        });

        Assert.True(PresenceHolderService.RememberAnswer(Command(Swap()), ok));
        Assert.False(PresenceHolderService.RememberAnswer(Command(Swap()), PresenceWireCodec.EncodeNotHolder()));
        Assert.False(PresenceHolderService.RememberAnswer(Command(Swap()), PresenceWireCodec.EncodeError("no")));
        Assert.False(PresenceHolderService.RememberAnswer(Command(new GroupCommand(GroupCommandKind.Relay, 1, "Alice", 0, false)), ok));
        Assert.False(PresenceHolderService.RememberAnswer(PresenceWireCodec.EncodeRequest(PresenceOp.GroupDrain, w => w.Write("node-a")), ok));
    }
}

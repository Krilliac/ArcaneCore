using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The normal "not linked to the Social holder yet" state of a worker, asked through <see cref="LeaseRoutedPresence.IsAuthorityReachable"/> instead of by calling and catching: the
/// presence publisher keeps its operations without calling, guild requests are refused cleanly, and the holder keeps its in-process fast path.
/// </summary>
public sealed class ClusterNotLinkedTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private static PresenceHolderService NewHolder()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        return new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
    }

    private sealed class CountingAuthority : IPresenceAuthority
    {
        public int Calls;

        public Task<PresenceUpsertReply> RegisterAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("must not be called while not reachable");
        }

        public Task<PresenceUpsertReply> UpsertAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default) => RegisterAsync(entry, cancellationToken);

        public Task<PresenceMutationResult> RemoveAsync(ulong characterGuid, long presenceVersion, long sessionGeneration, string workerId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("must not be called while not reachable");
        }

        public Task<IReadOnlyList<PresenceKick>> DrainKicksAsync(string workerId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("must not be called while not reachable");
        }
    }

    private sealed class GuildTransport : IGuildHolderTransport
    {
        public int Forwarded;

        public bool Reachable { get; set; }

        public bool IsAuthorityReachable => Reachable;

        public void ForwardGuildRequest(string workerId, long requestId, GuildRequest request) => Interlocked.Increment(ref Forwarded);

        public IReadOnlyList<GuildLinkEvent> DrainGuildEvents(string workerId) => [];
    }

    [Fact]
    public void A_node_without_the_lease_and_without_a_peer_client_is_not_reachable_and_the_check_throws_nothing()
    {
        var routed = new LeaseRoutedPresence(NewHolder(), static () => false, static () => null);

        Assert.False(routed.IsAuthorityReachable);
        Assert.False(((IGroupAuthorityTransport)routed).IsAuthorityReachable);
        Assert.False(((IGuildHolderTransport)routed).IsAuthorityReachable);
        Assert.Throws<InvalidOperationException>(() => routed.DrainGroupEvents("worker-a")); // a call without the check is still a misuse
    }

    [Fact]
    public void The_lease_holder_is_always_reachable_through_the_local_fast_path()
    {
        var routed = new LeaseRoutedPresence(NewHolder(), static () => true, static () => null);

        Assert.True(routed.IsAuthorityReachable);
        Assert.Empty(routed.DrainGroupEvents("worker-a"));
    }

    [Fact]
    public async Task The_publisher_keeps_its_operations_and_calls_nothing_while_not_reachable()
    {
        var authority = new CountingAuthority();
        bool reachable = false;
        await using var publisher = new ClusterPresencePublisher(
            authority, "worker-a", "gateway", static _ => { }, authorityReachable: () => Volatile.Read(ref reachable));
        publisher.PublishLogin(new PresenceSnapshot(700, "Feed700", 70, 1, 0, 12, 0, 10, 1, 1, 0, 0, null, 0, false, false));

        bool flushed = await publisher.FlushAsync().WaitAsync(Safety);

        Assert.False(flushed);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(1, publisher.PendingCount);
    }

    [Fact]
    public async Task A_guild_request_is_refused_cleanly_while_the_link_is_not_up_and_queued_once_it_is()
    {
        var transport = new GuildTransport();
        var local = new GuildHolderService(new InMemoryRealmPresenceDirectory());
        await using var link = new PeerGuildHolderLink(transport, local, "worker-a", static () => false);
        var request = new GuildRequest(GuildRequestKind.Info, ObjectGuid.Empty);
        bool completed = false;

        Assert.False(link.Forward(request, _ => completed = true));
        Assert.False(completed);
        Assert.Equal(0, link.QueueDropped);

        transport.Reachable = true;
        Assert.True(link.Forward(request, null));
    }

    [Fact]
    public async Task A_guild_request_is_forwarded_while_linked_and_refused_at_the_door_after_the_link_drops()
    {
        var transport = new GuildTransport { Reachable = true };
        var local = new GuildHolderService(new InMemoryRealmPresenceDirectory());
        await using var link = new PeerGuildHolderLink(transport, local, "worker-a", static () => false);
        var request = new GuildRequest(GuildRequestKind.Info, ObjectGuid.Empty);

        Assert.True(link.Forward(request, null));
        await link.FlushAsync().WaitAsync(Safety);
        Assert.Equal(1, transport.Forwarded);

        transport.Reachable = false;
        Assert.False(link.Forward(request, null)); // refused at the door once the flag is down

        Assert.Equal(0, link.SendFailed);
    }
}

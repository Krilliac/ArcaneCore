using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The normal "not linked to the Social holder yet" state of a worker, asked through <see cref="LeaseRoutedPresence.IsAuthorityReachable"/> instead of by calling and catching: the
/// presence publisher keeps its operations without calling and sends them once reachable, the pumps drain nothing, guild requests are refused at the door (counted, logged once per
/// outage), and the holder keeps its in-process fast path.
/// </summary>
public sealed class ClusterNotLinkedTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private static PresenceHolderService NewHolder()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        return new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
    }

    /// <summary>Delegates to an in-process holder and counts every call, so a test can see that none was made.</summary>
    private sealed class CountingAuthority(IPresenceAuthority inner) : IPresenceAuthority
    {
        public int Calls;

        public Task<PresenceUpsertReply> RegisterAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return inner.RegisterAsync(entry, cancellationToken);
        }

        public Task<PresenceUpsertReply> UpsertAsync(RealmPresenceEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return inner.UpsertAsync(entry, cancellationToken);
        }

        public Task<PresenceMutationResult> RemoveAsync(ulong characterGuid, long presenceVersion, long sessionGeneration, string workerId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return inner.RemoveAsync(characterGuid, presenceVersion, sessionGeneration, workerId, cancellationToken);
        }

        public Task<IReadOnlyList<PresenceKick>> DrainKicksAsync(string workerId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return inner.DrainKicksAsync(workerId, cancellationToken);
        }
    }

    /// <summary>A guild transport that can be linked or not; a call while not linked throws, as the lease-routed transport did. A send can be blocked to hold the sender inside it.</summary>
    private sealed class GuildTransport : IGuildHolderTransport
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new(true);
        public int Forwarded;
        public int Drains;

        public volatile bool Reachable;

        public bool IsAuthorityReachable => Reachable;

        /// <summary>Completes when the sender entered its first send.</summary>
        public Task Entered => _entered.Task;

        public void Block() => _gate.Reset();

        public void Release() => _gate.Set();

        public void ForwardGuildRequest(string workerId, long requestId, GuildRequest request)
        {
            _entered.TrySetResult();
            _gate.Wait();
            if (!Reachable && Volatile.Read(ref Forwarded) > 0)
            {
                throw new InvalidOperationException("this node does not hold the Social lease and has no peer link to the node that does");
            }

            Interlocked.Increment(ref Forwarded);
        }

        public IReadOnlyList<GuildLinkEvent> DrainGuildEvents(string workerId)
        {
            Interlocked.Increment(ref Drains);
            if (!Reachable)
            {
                throw new InvalidOperationException("this node does not hold the Social lease and has no peer link to the node that does");
            }

            return [];
        }
    }

    private static GuildRequest InfoRequest() => new(GuildRequestKind.Info, ObjectGuid.Empty);

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
    public async Task The_publisher_keeps_its_operations_and_calls_nothing_while_not_reachable_then_sends_them_once_it_is()
    {
        var authority = new CountingAuthority(NewHolder());
        bool reachable = false;
        await using var publisher = new ClusterPresencePublisher(
            authority, "worker-a", "gateway", static _ => { }, authorityReachable: () => Volatile.Read(ref reachable));
        publisher.PublishLogin(new PresenceSnapshot(700, "Feed700", 70, 1, 0, 12, 0, 10, 1, 1, 0, 0, null, 0, false, false));

        Assert.False(await publisher.FlushAsync().WaitAsync(Safety));
        Assert.Equal(0, authority.Calls);
        Assert.Equal(1, publisher.PendingCount);
        Assert.Equal(0, publisher.RegisteredCount);

        Volatile.Write(ref reachable, true);
        Assert.True(await publisher.FlushAsync().WaitAsync(Safety));

        Assert.Equal(0, publisher.PendingCount);
        Assert.Equal(1, publisher.RegisteredCount);
        Assert.True(authority.Calls > 0);
    }

    [Fact]
    public async Task The_chat_pump_drains_nothing_and_throws_nothing_while_not_reachable()
    {
        var routed = new LeaseRoutedPresence(NewHolder(), static () => false, static () => null);
        await using var chat = new ClusterChatRouting(routed, routed, "worker-a", "gateway", static _ => 0, authorityReachable: () => routed.IsAuthorityReachable)
        {
            Deliver = static _ => { },
        };

        Assert.Equal(0, chat.PumpOnce()); // unguarded, the router throws InvalidOperationException here
    }

    [Fact]
    public async Task A_guild_request_is_refused_at_the_door_while_the_link_is_not_up_counted_and_logged_once_per_outage()
    {
        var transport = new GuildTransport();
        var logger = new CapturingLogger();
        await using var link = new PeerGuildHolderLink(transport, new GuildHolderService(new InMemoryRealmPresenceDirectory()), "worker-a", static () => false, logger);
        bool completed = false;

        Assert.False(link.Forward(InfoRequest(), _ => completed = true));
        Assert.False(link.Forward(InfoRequest(), null));
        Assert.False(completed);
        Assert.False(link.CanForward);
        Assert.Equal(2, link.DoorRefused);
        Assert.Equal(0, link.QueueDropped);
        Assert.Single(logger.AtLeast(LogLevel.Warning));

        transport.Reachable = true;
        Assert.True(link.CanForward);
        Assert.True(link.Forward(InfoRequest(), null));
        await link.FlushAsync().WaitAsync(Safety);

        transport.Reachable = false;
        Assert.False(link.Forward(InfoRequest(), null)); // a second outage logs again
        Assert.Equal(3, link.DoorRefused);
        Assert.Equal(2, logger.AtLeast(LogLevel.Warning).Length);
    }

    [Fact]
    public async Task A_petition_put_or_character_deleted_notice_that_cannot_be_sent_logs_a_warning_naming_what_was_lost()
    {
        var transport = new GuildTransport();
        var logger = new CapturingLogger();
        await using var link = new PeerGuildHolderLink(transport, new GuildHolderService(new InMemoryRealmPresenceDirectory()), "worker-a", static () => false, logger);

        Assert.False(link.Forward(new GuildRequest(GuildRequestKind.PetitionPut, ObjectGuid.Player(1)), null));
        Assert.False(link.Forward(new GuildRequest(GuildRequestKind.PetitionCharacterDeleted, ObjectGuid.Player(2)), null));

        (LogLevel Level, string Text)[] warnings = logger.AtLeast(LogLevel.Warning);
        Assert.Equal(3, warnings.Length); // one per lost request, plus the once-per-outage line
        Assert.Contains(warnings, static w => w.Text.Contains("PetitionPut") && w.Text.Contains("lost"));
        Assert.Contains(warnings, static w => w.Text.Contains("PetitionCharacterDeleted") && w.Text.Contains("lost"));
    }

    [Fact]
    public async Task A_guild_request_is_forwarded_while_linked_and_refused_at_the_door_after_the_link_drops()
    {
        var transport = new GuildTransport { Reachable = true };
        await using var link = new PeerGuildHolderLink(transport, new GuildHolderService(new InMemoryRealmPresenceDirectory()), "worker-a", static () => false);

        Assert.True(link.Forward(InfoRequest(), null));
        await link.FlushAsync().WaitAsync(Safety);
        Assert.Equal(1, transport.Forwarded);

        transport.Reachable = false;
        Assert.False(link.Forward(InfoRequest(), null));

        Assert.Equal(1, link.DoorRefused);
        Assert.Equal(0, link.SendFailed);
    }

    [Fact]
    public async Task The_guild_event_pump_drains_nothing_and_throws_nothing_while_not_reachable()
    {
        var transport = new GuildTransport();
        using var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var context = new SocialContext(world, new NoCharacters(), new NullSocialPersistence());
        await using var link = new PeerGuildHolderLink(transport, new GuildHolderService(new InMemoryRealmPresenceDirectory()), "worker-a", static () => false);
        link.Bind(world, context.Guilds, context.Petitions);

        Assert.Equal(0, link.PumpOnce());
        Assert.Equal(0, transport.Drains);

        transport.Reachable = true;
        link.PumpOnce();
        Assert.Equal(1, transport.Drains);
    }

    [Fact]
    public async Task A_guild_request_accepted_before_the_link_dropped_is_refused_by_the_sender_with_one_log_line_per_outage()
    {
        var transport = new GuildTransport { Reachable = true };
        var logger = new CapturingLogger();
        await using var link = new PeerGuildHolderLink(transport, new GuildHolderService(new InMemoryRealmPresenceDirectory()), "worker-a", static () => false, logger);
        transport.Block();
        Assert.True(link.Forward(InfoRequest(), null));
        await transport.Entered.WaitAsync(Safety); // the sender is inside the send of request 1
        Assert.True(link.Forward(InfoRequest(), null)); // accepted: still linked
        Assert.True(link.Forward(InfoRequest(), null));
        transport.Reachable = false;
        transport.Release();

        await link.FlushAsync().WaitAsync(Safety);

        Assert.Equal(1, transport.Forwarded); // the two behind it never reached the transport
        Assert.Equal(2, link.SendFailed);
        Assert.Equal(0, link.DoorRefused);
        (LogLevel Level, string Text)[] lines = logger.AtLeast(LogLevel.Information);
        Assert.Single(lines);
        Assert.Contains("refused", lines[0].Text);

        transport.Reachable = true;
        Assert.True(link.Forward(InfoRequest(), null));
        await link.FlushAsync().WaitAsync(Safety);

        Assert.Equal(2, transport.Forwarded);
        lines = logger.AtLeast(LogLevel.Information);
        Assert.Equal(2, lines.Length);
        Assert.Contains("resume", lines[1].Text);
        Assert.Empty(logger.AtLeast(LogLevel.Warning));
    }

    private sealed class NoCharacters : ICharacterLookup
    {
        public CharacterInfo? Find(uint characterId) => null;

        public CharacterInfo? FindByName(string name) => null;
    }

    private sealed class NullSocialPersistence : ISocialPersistence
    {
        public void SetSocial(int characterId, int otherId, SocialFlags flags)
        {
        }

        public void SaveGuild(GuildData guild)
        {
        }

        public void DeleteGuild(int guildId)
        {
        }
    }
}

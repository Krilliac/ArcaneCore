using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Custom channel moderation held at the presence holder (S2c): owner, moderators, bans and mutes live in the <see cref="InMemoryClusterSocialRouter"/> the holder serves, so a ban
/// or mute decided on one worker binds a character that joins or speaks from another. Two in-process <see cref="ClusterChatRouting"/> workers share one holder.
/// </summary>
public sealed class CrossWorkerChannelModerationTests : IAsyncDisposable
{
    private const string GatewayId = "gateway";
    private const string Channel = "X";

    private sealed class Worker : IAsyncDisposable
    {
        public Worker(string id, InMemoryRealmPresenceDirectory directory, InMemoryClusterSocialRouter router)
        {
            Id = id;
            Routing = new ClusterChatRouting(router, directory, id, GatewayId, guid => Generations.GetValueOrDefault(guid));
            Routing.Deliver = Received.Add;
        }

        public string Id { get; }

        public ClusterChatRouting Routing { get; }

        public Dictionary<ulong, long> Generations { get; } = [];

        public List<ClusterChatDelivery> Received { get; } = [];

        public ValueTask DisposeAsync() => Routing.DisposeAsync();
    }

    private readonly InMemoryRealmPresenceDirectory _directory = new();
    private readonly InMemoryClusterSocialRouter _router;
    private readonly Worker _a;
    private readonly Worker _b;
    private long _version;

    public CrossWorkerChannelModerationTests()
    {
        _router = new InMemoryClusterSocialRouter(_directory);
        _a = new Worker("worker-a", _directory, _router);
        _b = new Worker("worker-b", _directory, _router);
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private void Register(Worker worker, ulong guid, string name, long generation, byte security = 0)
    {
        var entry = new RealmPresenceEntry(
            guid, name, (int)guid + 1000, worker.Id, GatewayId, 0, 12, 0, 10, 1, 1, 0, 0, 0, ++_version, generation, DateTimeOffset.UtcNow, Security: security);
        Assert.Equal(PresenceMutationResult.Applied, _directory.TryUpsert(entry, out _));
        worker.Generations[guid] = generation;
    }

    private static ClusterChatSender Sender(ulong guid, string name) => new(guid, name, (int)guid + 1000);

    private static ClusterChatFrame Line(ulong speaker, string text) => new()
    {
        Kind = ClusterChatFrameKind.Channel,
        SenderGuid = speaker,
        Channel = Channel,
        Text = text,
        Packets = [new ClusterChatPacket(WorldOpcode.SmsgMessagechat, [1, 2, 3])],
    };

    private async Task JoinAsync(Worker worker, ulong guid, string name)
    {
        worker.Routing.JoinChannel(Sender(guid, name), Channel);
        await worker.Routing.FlushAsync();
    }

    private async Task SpeakAsync(Worker worker, ulong guid, string name, string text)
    {
        worker.Routing.SendToChannel(Sender(guid, name), Channel, Line(guid, text));
        await worker.Routing.FlushAsync();
    }

    [Fact]
    public async Task BannedOnWorkerA_JoinsOnWorkerB_IsRefused_AndTheirLinesNeverReachA()
    {
        Register(_a, 1, "Alice", 5);
        Register(_a, 3, "Mallory", 6);
        Register(_b, 2, "Bob", 7);
        await JoinAsync(_a, 1, "Alice");
        await JoinAsync(_a, 3, "Mallory");
        _a.Routing.ModerateChannel(Channel, ChannelModerationAction.ClaimOwner, 1, 1);
        _a.Routing.ModerateChannel(Channel, ChannelModerationAction.Ban, 1, 3);
        await _a.Routing.FlushAsync();

        ChannelModerationState state = Assert.IsType<ChannelModerationState>(_router.GetChannelModeration(Channel));
        Assert.Equal(1UL, state.Owner);
        Assert.Equal([3UL], state.Banned);

        // Mallory logs in again, now on worker B, and tries to join and speak.
        Register(_b, 3, "Mallory", 8);
        await JoinAsync(_b, 3, "Mallory");
        Assert.False(_router.JoinChannel(Channel, 3, 8), "the router refuses a subscribe of a banned character");
        await SpeakAsync(_b, 3, "Mallory", "spam");
        Assert.Equal(0, _a.Routing.PumpOnce());
        Assert.Empty(_a.Received);

        // Control: an unbanned member of worker B reaches worker A's member on the same channel.
        await JoinAsync(_b, 2, "Bob");
        await SpeakAsync(_b, 2, "Bob", "hello");
        Assert.Equal(1, _a.Routing.PumpOnce());
        ClusterChatDelivery heard = Assert.Single(_a.Received);
        Assert.Equal(1UL, heard.RecipientGuid);
        Assert.Equal(2UL, heard.Frame.SenderGuid);

        // Lifting the ban lets her in again.
        _a.Routing.ModerateChannel(Channel, ChannelModerationAction.Unban, 1, 3);
        await _a.Routing.FlushAsync();
        Assert.True(_router.JoinChannel(Channel, 3, 8));
    }

    [Fact]
    public async Task AMutedSpeaker_IsRefusedByTheHolder_AndALineRoutedBeforeTheMuteIsDroppedByTheReceiver()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);
        await JoinAsync(_a, 1, "Alice");
        await JoinAsync(_b, 2, "Bob");
        _a.Routing.ModerateChannel(Channel, ChannelModerationAction.ClaimOwner, 1, 1);
        await _a.Routing.FlushAsync();

        // Bob speaks, then Alice mutes him before worker A's pump runs: the queued line is dropped at the receiver.
        await SpeakAsync(_b, 2, "Bob", "first");
        _a.Routing.ModerateChannel(Channel, ChannelModerationAction.Mute, 1, 2);
        await _a.Routing.FlushAsync();
        Assert.Equal(0, _a.Routing.PumpOnce());
        Assert.Equal(1, _a.Routing.BarredDropped);
        Assert.True(_a.Routing.IsSpeakerBarred(Channel, 2));
        Assert.False(_a.Routing.IsSpeakerBarred(Channel, 1));
        Assert.Empty(_a.Received);

        // Spoken after the mute, the line is refused at the holder and never queued.
        await SpeakAsync(_b, 2, "Bob", "second");
        Assert.Empty(_router.DrainWorkerDeliveriesBatch("worker-a"));

        // Unmuted, he is heard again.
        _a.Routing.ModerateChannel(Channel, ChannelModerationAction.Unmute, 1, 2);
        await _a.Routing.FlushAsync();
        await SpeakAsync(_b, 2, "Bob", "third");
        Assert.Equal(1, _a.Routing.PumpOnce());
        Assert.False(_a.Routing.IsSpeakerBarred(Channel, 2));
        Assert.Equal("third", Assert.Single(_a.Received).Frame.Text);
    }

    [Fact]
    public async Task OnlyTheOwnerAModeratorOrStaffMayModerate_AndAClaimNeedsAnUnownedChannel()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);
        Register(_b, 5, "Carol", 9, security: 2);
        await JoinAsync(_a, 1, "Alice");
        await JoinAsync(_b, 2, "Bob");
        await JoinAsync(_b, 5, "Carol");

        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.ClaimOwner, 1, 1));
        Assert.False(_router.ModerateChannel(Channel, ChannelModerationAction.ClaimOwner, 2, 2), "already owned");
        Assert.False(_router.ModerateChannel(Channel, ChannelModerationAction.ClaimOwner, 2, 1), "a claim is for oneself");
        Assert.False(_router.ModerateChannel(Channel, ChannelModerationAction.Ban, 2, 1), "a plain member cannot ban");
        Assert.False(_router.ModerateChannel(Channel, ChannelModerationAction.Mute, 2, 5));
        Assert.Empty(_router.GetChannelModeration(Channel)!.Banned);

        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.AddModerator, 1, 2));
        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.Mute, 2, 5), "a moderator may mute");
        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.Unmute, 5, 5), "staff moderate any channel");
        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.SetOwner, 5, 2));
        Assert.Equal(2UL, _router.GetChannelModeration(Channel)!.Owner);

        // A banned character cannot come back and is no longer subscribed; the owner leaving clears ownership until someone claims it.
        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.Ban, 2, 1));
        Assert.False(_router.LeaveChannel(Channel, 1, 5), "the ban already unsubscribed her");
        _b.Routing.LeaveChannel(Sender(2, "Bob"), Channel);
        await _b.Routing.FlushAsync();
        ChannelModerationState afterLeave = Assert.IsType<ChannelModerationState>(_router.GetChannelModeration(Channel));
        Assert.Equal(0UL, afterLeave.Owner);
        Assert.Equal([1UL], afterLeave.Banned);
        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.ClaimOwner, 5, 5));
    }

    [Fact]
    public async Task ModerationEndsWithTheChannel_WhenItsLastMemberLeaves()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);
        await JoinAsync(_a, 1, "Alice");
        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.ClaimOwner, 1, 1));
        Assert.True(_router.ModerateChannel(Channel, ChannelModerationAction.Ban, 1, 2));

        _a.Routing.LeaveChannel(Sender(1, "Alice"), Channel);
        await _a.Routing.FlushAsync();

        Assert.Null(_router.GetChannelModeration(Channel));
        Assert.False(_router.ModerateChannel(Channel, ChannelModerationAction.Unban, 1, 2), "no channel left to moderate");
        await JoinAsync(_b, 2, "Bob");
        Assert.True(_router.JoinChannel(Channel, 2, 7), "the ban died with the empty channel, as at the workers");
    }
}

using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Chat between workers (S2c) with two in-process workers: two <see cref="ClusterChatRouting"/> instances share one holder (an <see cref="InMemoryRealmPresenceDirectory"/> and its
/// <see cref="InMemoryClusterSocialRouter"/>, as the peer link would reach), and the "world thread" side of each is a recording <see cref="ClusterChatRouting.Deliver"/>.
/// Also pins the bytes of <see cref="ClusterChatFrame"/>.
/// </summary>
public sealed class CrossWorkerChatTests : IAsyncDisposable
{
    private const string GatewayId = "gateway";

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

        /// <summary>The generation this worker's publisher holds per character.</summary>
        public Dictionary<ulong, long> Generations { get; } = [];

        public List<ClusterChatDelivery> Received { get; } = [];

        public ValueTask DisposeAsync() => Routing.DisposeAsync();
    }

    private readonly InMemoryRealmPresenceDirectory _directory = new();
    private readonly InMemoryClusterSocialRouter _router;
    private readonly Worker _a;
    private readonly Worker _b;
    private long _version;

    public CrossWorkerChatTests()
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

    private void Register(Worker worker, ulong guid, string name, long generation, int guildId = 0, int groupId = 0)
    {
        var entry = new RealmPresenceEntry(
            guid, name, (int)guid + 1000, worker.Id, GatewayId, 0, 12, 0, 10, 1, 1, 0, guildId, groupId, ++_version, generation, DateTimeOffset.UtcNow);
        Assert.Equal(PresenceMutationResult.Applied, _directory.TryUpsert(entry, out _));
        worker.Generations[guid] = generation;
    }

    private static ClusterChatSender Sender(ulong guid, string name) => new(guid, name, (int)guid + 1000);

    private static ClusterChatFrame Line(ClusterChatFrameKind kind, string text, string? channel = null) => new()
    {
        Kind = kind,
        SenderGuid = 1,
        Text = text,
        Channel = channel,
        Packets = [new ClusterChatPacket(WorldOpcode.SmsgMessagechat, [1, 2, 3])],
    };

    [Fact]
    public async Task Whisper_GoesFromWorkerAToWorkerB_AndAnUnknownNameIsNotFound()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);
        int notFound = 0;

        _a.Routing.SendWhisper(Sender(1, "Alice"), "bob", Line(ClusterChatFrameKind.Whisper, "hello"), () => notFound++);
        _a.Routing.SendWhisper(Sender(1, "Alice"), "Nobody", Line(ClusterChatFrameKind.Whisper, "hello"), () => notFound++);
        await _a.Routing.FlushAsync();

        Assert.Equal(1, notFound);
        Assert.Equal(1, _b.Routing.PumpOnce());
        ClusterChatDelivery delivery = Assert.Single(_b.Received);
        Assert.Equal(2UL, delivery.RecipientGuid);
        Assert.Equal(7, delivery.RecipientSessionGeneration);
        Assert.Equal(ClusterChatFrameKind.Whisper, delivery.Frame.Kind);
        Assert.Equal("hello", delivery.Frame.Text);
        Assert.Equal("worker-a", delivery.Frame.OriginWorkerId);
        Assert.True(delivery.DeliverySequence > 0);
        Assert.Equal(0, _a.Routing.PumpOnce());
    }

    [Fact]
    public async Task WhisperReply_TravelsBackToTheWhisperer()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);

        _b.Routing.SendReply(Sender(2, "Bob"), 1, new ClusterChatFrame
        {
            Kind = ClusterChatFrameKind.WhisperReply,
            SenderGuid = 2,
            NoteWhisperGuid = 2,
            Packets = [new ClusterChatPacket(WorldOpcode.SmsgMessagechat, [9]), new ClusterChatPacket(WorldOpcode.SmsgNotification, [8])],
        });
        await _b.Routing.FlushAsync();

        Assert.Equal(1, _a.Routing.PumpOnce());
        ClusterChatDelivery delivery = Assert.Single(_a.Received);
        Assert.Equal(1UL, delivery.RecipientGuid);
        Assert.Equal(ClusterChatFrameKind.WhisperReply, delivery.Frame.Kind);
        Assert.Equal(2UL, delivery.Frame.NoteWhisperGuid);
        Assert.Equal(2, delivery.Frame.Packets.Count);
    }

    [Fact]
    public async Task GuildChat_CrossesWorkers_AndTheSpeakersOwnWorkerIsNotAppliedTwice()
    {
        Register(_a, 1, "Alice", 5, guildId: 9);
        Register(_a, 3, "Carol", 6, guildId: 9);
        Register(_b, 2, "Bob", 7, guildId: 9);
        Register(_b, 4, "Dan", 8, guildId: 10);

        _a.Routing.SendToGuild(Sender(1, "Alice"), 9, Line(ClusterChatFrameKind.Guild, "gm"));
        await _a.Routing.FlushAsync();

        Assert.Equal(1, _b.Routing.PumpOnce());
        ClusterChatDelivery delivery = Assert.Single(_b.Received);
        Assert.Equal(2UL, delivery.RecipientGuid);
        Assert.Equal(ClusterChatFrameKind.Guild, delivery.Frame.Kind);
        Assert.Equal(0, _a.Routing.PumpOnce()); // Alice and Carol were served locally by worker A
        Assert.Empty(_a.Received);
    }

    [Fact]
    public async Task GroupChat_CrossesWorkers_OnlyForTheSpeakersGroup()
    {
        Register(_a, 1, "Alice", 5, groupId: 3);
        Register(_b, 2, "Bob", 7, groupId: 3);
        Register(_b, 4, "Dan", 8, groupId: 4);

        _a.Routing.SendToGroup(Sender(1, "Alice"), 3, Line(ClusterChatFrameKind.Group, "pull"));
        await _a.Routing.FlushAsync();

        Assert.Equal(1, _b.Routing.PumpOnce());
        Assert.Equal(2UL, Assert.Single(_b.Received).RecipientGuid);
    }

    [Fact]
    public async Task ChannelJoin_CrossesWorkers_AndLinesReachTheOtherWorkersMembers()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);
        Register(_b, 4, "Dan", 8);

        _a.Routing.JoinChannel(Sender(1, "Alice"), "Trade");
        _b.Routing.JoinChannel(Sender(2, "Bob"), "trade");
        await _a.Routing.FlushAsync();
        await _b.Routing.FlushAsync();
        _a.Routing.SendToChannel(Sender(1, "Alice"), "Trade", Line(ClusterChatFrameKind.Channel, "wts", "Trade"));
        await _a.Routing.FlushAsync();
        await _b.Routing.FlushAsync();

        Assert.Equal(1, _b.Routing.PumpOnce());
        ClusterChatDelivery delivery = Assert.Single(_b.Received);
        Assert.Equal(2UL, delivery.RecipientGuid); // Dan never joined
        Assert.Equal("Trade", delivery.Frame.Channel);

        // Leaving unsubscribes: the next line finds no other member.
        _b.Routing.LeaveChannel(Sender(2, "Bob"), "Trade");
        await _b.Routing.FlushAsync();
        _a.Routing.SendToChannel(Sender(1, "Alice"), "Trade", Line(ClusterChatFrameKind.Channel, "again", "Trade"));
        await _b.Routing.FlushAsync();
        await _a.Routing.FlushAsync();
        Assert.Equal(0, _b.Routing.PumpOnce());
    }

    [Fact]
    public async Task ChannelLine_FromANonMember_IsRefusedByTheRouter()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);
        _b.Routing.JoinChannel(Sender(2, "Bob"), "Trade");
        await _b.Routing.FlushAsync();

        _a.Routing.SendToChannel(Sender(1, "Alice"), "Trade", Line(ClusterChatFrameKind.Channel, "spam", "Trade"));
        await _a.Routing.FlushAsync();
        await _b.Routing.FlushAsync();

        Assert.Equal(0, _b.Routing.PumpOnce());
    }

    [Fact]
    public async Task ADeliveryToAReplacedSession_IsDropped()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);

        _a.Routing.SendWhisper(Sender(1, "Alice"), "Bob", Line(ClusterChatFrameKind.Whisper, "hi"), () => { });
        await _a.Routing.FlushAsync();

        // Bob logs in again on worker B (generation 8) before the pump runs: the queued delivery is stamped with generation 7.
        _b.Generations[2] = 8;

        Assert.Equal(0, _b.Routing.PumpOnce());
        Assert.Empty(_b.Received);
        Assert.Equal(1, _b.Routing.StaleDropped);
        Assert.False(_b.Routing.IsCurrent(2, 7));
        Assert.True(_b.Routing.IsCurrent(2, 8));
    }

    [Fact]
    public async Task ASenderWithoutARegistration_RoutesNothing()
    {
        Register(_b, 2, "Bob", 7);
        _a.Routing.SendWhisper(Sender(1, "Alice"), "Bob", Line(ClusterChatFrameKind.Whisper, "hi"), () => { });
        await _a.Routing.FlushAsync();

        Assert.Equal(0, _b.Routing.PumpOnce());
    }

    [Fact]
    public async Task Who_ReturnsEveryWorkersEntriesAndTheRealmTotal()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);
        IReadOnlyList<RealmPresenceEntry>? entries = null;
        int online = -1;

        _a.Routing.QueryWho(new RealmWhoFilter(1, 255, uint.MaxValue, uint.MaxValue, null, null, 50), (e, n) =>
        {
            entries = e;
            online = n;
        });
        await _a.Routing.FlushAsync();

        Assert.NotNull(entries);
        Assert.Equal(["Alice", "Bob"], entries.Select(e => e.CharacterName).Order().ToArray());
        Assert.Equal(2, online);
    }

    [Fact]
    public async Task Pump_LeavesDeliveriesQueuedWhileNoOneListens_AndHandsThemOverOnceSomeoneDoes()
    {
        Register(_a, 1, "Alice", 5);
        Register(_b, 2, "Bob", 7);
        _a.Routing.SendWhisper(Sender(1, "Alice"), "bob", Line(ClusterChatFrameKind.Whisper, "later"), () => { });
        await _a.Routing.FlushAsync();

        _b.Routing.Deliver = null;
        Assert.Equal(0, _b.Routing.PumpOnce());
        Assert.Empty(_b.Received);

        _b.Routing.Deliver = _b.Received.Add;
        Assert.Equal(1, _b.Routing.PumpOnce());
        Assert.Equal("later", Assert.Single(_b.Received).Frame.Text);
    }

    [Fact]
    public async Task Pump_LeavesNoWorkerDeliveryQueuedAtTheHolder_AndTheSharedGatewayQueueStaysBounded()
    {
        Register(_a, 1, "Alice", 5, guildId: 9);
        Register(_b, 2, "Bob", 7, guildId: 9);

        _a.Routing.SendToGuild(Sender(1, "Alice"), 9, Line(ClusterChatFrameKind.Guild, "gm"));
        await _a.Routing.FlushAsync();
        Assert.Equal(1, _b.Routing.PumpOnce());
        Assert.Equal(0, _a.Routing.PumpOnce()); // the speaker's own copy, if the router queued one, is dropped as served locally
        Assert.Empty(_router.DrainWorkerDeliveries("worker-b"));
        Assert.Empty(_router.DrainWorkerDeliveries("worker-a"));

        // Every worker shares one gateway id and nothing consumes its queue in a cluster realm: it must not grow with the realm's chat. More lines than the cap leave at most the cap.
        for (int i = 0; i < InMemoryClusterSocialRouter.MaxGatewayQueueDeliveries + 100; i++)
        {
            _a.Routing.SendToGuild(Sender(1, "Alice"), 9, Line(ClusterChatFrameKind.Guild, "line " + i));
        }

        await _a.Routing.FlushAsync();
        IReadOnlyList<RoutedSocialDelivery> gateway = _router.DrainGatewayDeliveries(GatewayId);
        Assert.InRange(gateway.Count, 1, InMemoryClusterSocialRouter.MaxGatewayQueueDeliveries);
        Assert.Equal(gateway.OrderBy(static d => d.DeliverySequence).ToArray(), gateway); // the newest survive, in order
        Assert.True(gateway.Sum(static d => PresenceWireCodec.DeliveryWireSize(d)) <= InMemoryClusterSocialRouter.MaxGatewayQueueBytes);
        Assert.Empty(_router.DrainGatewayDeliveries(GatewayId));
    }

    [Fact]
    public async Task Pump_TakesALargeBacklogInBoundedBatches_WithoutLosingOrReorderingAny()
    {
        Register(_a, 1, "Alice", 5, guildId: 9);
        Register(_b, 2, "Bob", 7, guildId: 9);
        string filler = new('x', 1500);
        const int Lines = 120;
        for (int i = 0; i < Lines; i++)
        {
            _a.Routing.SendToGuild(Sender(1, "Alice"), 9, Line(ClusterChatFrameKind.Guild, i.ToString("D3") + filler));
        }

        await _a.Routing.FlushAsync();

        Assert.True(_router.DrainWorkerDeliveriesBatch("worker-b").Count < Lines, "one batch is bounded");
        // That batch was dropped from the queue above; run the rest through the pump (it takes batches until the queue is empty).
        int handed = _b.Routing.PumpOnce();
        Assert.Equal(handed, _b.Received.Count);
        Assert.True(handed > 0);
        Assert.Equal(_b.Received.Select(static d => d.DeliverySequence).Order().ToArray(), _b.Received.Select(static d => d.DeliverySequence).ToArray());
        Assert.Empty(_router.DrainWorkerDeliveriesBatch("worker-b"));
    }

    // ---- golden bytes --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void FrameKinds_ArePinned()
    {
        Assert.Equal(1, (byte)ClusterChatFrameKind.Whisper);
        Assert.Equal(2, (byte)ClusterChatFrameKind.WhisperReply);
        Assert.Equal(3, (byte)ClusterChatFrameKind.Group);
        Assert.Equal(4, (byte)ClusterChatFrameKind.Guild);
        Assert.Equal(5, (byte)ClusterChatFrameKind.Officer);
        Assert.Equal(6, (byte)ClusterChatFrameKind.Channel);
    }

    [Fact]
    public void Frame_HasTheGoldenLayout()
    {
        var frame = new ClusterChatFrame
        {
            Kind = ClusterChatFrameKind.Channel,
            OriginWorkerId = "w",
            SenderGuid = 0x0102,
            SenderTeam = 1,
            SenderSecurity = 2,
            IgnoreExempt = true,
            SubGroup = 3,
            Channel = "Tr",
            MuteNotice = null,
            Text = "hi",
            ChatTag = 4,
            NoteWhisperGuid = 5,
            Packets = [new ClusterChatPacket(WorldOpcode.SmsgMessagechat, [0xAA, 0xBB])],
        };

        // version 1, kind 6, "w", guid, team 1, security 2, exempt, sub-group 3, channel "Tr", no mute notice, text "hi", tag 4, noted guid 5, one packet.
        string expected =
            "01" + "06" + "0177" + "0201000000000000" + "01" + "02" + "01" + "03" + "01025472" + "00" + "026869" + "04" + "0500000000000000" +
            "01000000" + ToHex((ushort)WorldOpcode.SmsgMessagechat) + "02000000" + "AABB";
        Assert.Equal(Convert.FromHexString(expected), frame.ToBytes());

        ClusterChatFrame decoded = ClusterChatFrame.FromBytes(frame.ToBytes());
        Assert.Equal(frame.Kind, decoded.Kind);
        Assert.Equal(frame.OriginWorkerId, decoded.OriginWorkerId);
        Assert.Equal(frame.SenderGuid, decoded.SenderGuid);
        Assert.Equal(frame.Channel, decoded.Channel);
        Assert.Null(decoded.MuteNotice);
        Assert.Equal(frame.Text, decoded.Text);
        Assert.Equal(frame.NoteWhisperGuid, decoded.NoteWhisperGuid);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, Assert.Single(decoded.Packets).Payload);
    }

    [Fact]
    public void Frame_RejectsBadVersionKindAndTruncation()
    {
        byte[] good = new ClusterChatFrame { Kind = ClusterChatFrameKind.Group, OriginWorkerId = "w" }.ToBytes();

        byte[] badVersion = [.. good];
        badVersion[0] = 2;
        Assert.Throws<InvalidDataException>(() => ClusterChatFrame.FromBytes(badVersion));

        byte[] badKind = [.. good];
        badKind[1] = 0;
        Assert.Throws<InvalidDataException>(() => ClusterChatFrame.FromBytes(badKind));

        Assert.Throws<EndOfStreamException>(() => ClusterChatFrame.FromBytes(good.AsSpan(0, good.Length - 3)));
    }

    private static string ToHex(ushort value) => Convert.ToHexString([(byte)(value & 0xFF), (byte)(value >> 8)]);
}

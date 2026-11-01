using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>Mail-arrived and auction notifications from the Economy holder reach the worker that hosts the recipient (M15 S2d).</summary>
public sealed class EconomyNotifyTests
{
    private const int SellerId = 11;
    private const int OldBidderId = 22;
    private const int NewBidderId = 33;

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static void Online(InMemoryRealmPresenceDirectory directory, int characterId, string worker, long generation = 1)
    {
        var entry = new RealmPresenceEntry(
            (uint)characterId, "Char" + characterId, characterId, worker, "gw-1", 0, 0, 0, 60, 1, 1, 0, 0, 0,
            PresenceVersion: 1, SessionGeneration: generation, UpdatedAtUtc: Now);
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(entry, out _));
    }

    private static (EconomyCommitNotifier Notifier, EconomyNotifyRouter Router, EconomyNotifyClient ClientOnB) Holder(InMemoryRealmPresenceDirectory directory)
    {
        var router = new EconomyNotifyRouter(directory);
        var client = new EconomyNotifyClient((request, _) => Task.FromResult(router.Handle(request)));
        return (new EconomyCommitNotifier(router), router, client);
    }

    private static AuctionRecord Auction(int bidder, uint bid) => new()
    {
        Id = 7, HouseId = 2, ItemEntry = 2589, ItemCount = 1, SellerId = SellerId, StartBid = 100, Buyout = 1000, BidderId = bidder, Bid = bid,
    };

    [Fact]
    public async Task MailSentOnHolderA_ToPlayerOnB_ProducesReceivedMailOnB()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Online(directory, NewBidderId, "worker-B");
        (EconomyCommitNotifier notifier, EconomyNotifyRouter router, EconomyNotifyClient clientOnB) = Holder(directory);

        Assert.Equal(EconomyNotifyStatus.Queued, notifier.MailCommitted(NewBidderId, Now.ToUnixTimeSeconds(), Now.ToUnixTimeSeconds()));

        Assert.Empty(await clientOnB.DrainAsync("worker-A"));
        IReadOnlyList<EconomyNotification> drained = await clientOnB.DrainAsync("worker-B");
        EconomyNotification n = Assert.Single(drained);
        Assert.Equal(WorldOpcode.SmsgReceivedMail, n.Opcode);
        Assert.Equal(EconomyPackets.ReceivedMail(), n.Payload);
        Assert.Equal((ulong)NewBidderId, n.RecipientGuid);

        var sent = new List<EconomyNotification>();
        var receiver = new EconomyNotifyReceiver(guid => guid == (ulong)NewBidderId ? 1 : null, sent.Add);
        Assert.Equal(1, receiver.Apply(drained));
        Assert.Equal(WorldOpcode.SmsgReceivedMail, Assert.Single(sent).Opcode);
        Assert.Equal(0, router.PendingCount("worker-B"));
    }

    [Fact]
    public async Task OutbidNotice_ReachesThePreviousBidderOnB()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Online(directory, OldBidderId, "worker-B");
        (EconomyCommitNotifier notifier, _, EconomyNotifyClient clientOnB) = Holder(directory);
        AuctionRecord beforeBid = Auction(OldBidderId, 300);

        Assert.Equal(EconomyNotifyStatus.Queued, notifier.Outbid(beforeBid));

        EconomyNotification n = Assert.Single(await clientOnB.DrainAsync("worker-B"));
        Assert.Equal(WorldOpcode.SmsgAuctionBidderNotification, n.Opcode);
        // vmangos sends the outbid mail before overwriting bidder and bid: the packet names the outbid player and the outbid player's own bid.
        Assert.Equal(EconomyPackets.BidderNotification(beforeBid, won: false), n.Payload);
        Assert.NotEqual(EconomyPackets.BidderNotification(Auction(NewBidderId, 500), won: false), n.Payload);
        Assert.Equal((ulong)OldBidderId, n.RecipientGuid);
    }

    [Fact]
    public async Task DelayedLetter_DoesNotNotifyUntilDue()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Online(directory, NewBidderId, "worker-B");
        (EconomyCommitNotifier notifier, EconomyNotifyRouter router, EconomyNotifyClient client) = Holder(directory);
        long now = Now.ToUnixTimeSeconds();

        Assert.Equal(EconomyNotifyStatus.Deferred, notifier.MailCommitted(NewBidderId, now + 3600, now));
        Assert.Equal(0, router.PendingCount("worker-B"));
        Assert.Empty(await client.DrainAsync("worker-B"));

        Assert.Equal(EconomyNotifyStatus.Queued, notifier.MailCommitted(NewBidderId, now, now));
        Assert.Equal(1, router.PendingCount("worker-B"));
        Assert.Equal(EconomyNotifyStatus.Queued, notifier.MailDelivered(NewBidderId));
        Assert.Equal(2, (await client.DrainAsync("worker-B")).Count);
    }

    [Fact]
    public async Task Drain_WithMaxSizePayloads_FitsOnePeerLinkFrame_AndKeepsTheRest()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        string worker = new('w', EconomyNotifyRouter.MaxWorkerIdLength);
        Online(directory, NewBidderId, worker);
        var router = new EconomyNotifyRouter(directory);
        byte[] big = new byte[EconomyNotifyWire.MaxPayloadLength];
        for (int i = 0; i < EconomyNotifyRouter.MaxQueuedPerWorker; i++)
        {
            Assert.Equal(EconomyNotifyStatus.Queued, router.Notify(NewBidderId, WorldOpcode.SmsgReceivedMail, big));
        }

        byte[] reply = router.Handle(EconomyNotifyWire.EncodeDrainRequest(worker));

        Assert.True(reply.Length <= PeerLinkWire.MaxPayloadBytes, $"reply of {reply.Length} B");
        _ = PeerLinkWire.EncodeEcho(RealmServiceFrameType.Pong, 1, 0, reply); // throws if it does not fit
        IReadOnlyList<EconomyNotification> items = EconomyNotifyWire.DecodeDrainReply(reply);
        Assert.NotEmpty(items);
        Assert.True(items.Count < EconomyNotifyRouter.MaxQueuedPerWorker);
        Assert.Equal(EconomyNotifyRouter.MaxQueuedPerWorker - items.Count, router.PendingCount(worker));
        var next = await new EconomyNotifyClient((r, _) => Task.FromResult(router.Handle(r))).DrainAsync(worker);
        Assert.Equal(items[^1].Sequence + 1, next[0].Sequence);
    }

    [Fact]
    public void OverlongWorkerId_IsNeitherQueuedNorDrained()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        string longId = new('w', EconomyNotifyRouter.MaxWorkerIdLength + 1);
        Online(directory, NewBidderId, longId);
        var router = new EconomyNotifyRouter(directory);

        Assert.Equal(EconomyNotifyStatus.RecipientOffline, router.Notify(NewBidderId, WorldOpcode.SmsgReceivedMail, [1]));
        Assert.Empty(router.DrainWorker(longId));
        Assert.Throws<ArgumentNullException>(() => router.PendingCount(null!));
    }

    [Fact]
    public void DrainRequest_HasTheGoldenLayout()
    {
        // op 64, worker id "w" (7-bit length prefix 1).
        Assert.Equal(Convert.FromHexString("400177"), EconomyNotifyWire.EncodeDrainRequest("w"));
        Assert.Equal(64, EconomyNotifyWire.OpDrainWorker);
    }

    [Fact]
    public void OkReplyWithOneItem_HasTheGoldenLayout()
    {
        // status 0, count 1, then opcode 0x0123 (u16), payload length 2 and bytes AA BB, guid 5, generation 6, worker "w", sequence 7.
        var item = new EconomyNotification((WorldOpcode)0x0123, [0xAA, 0xBB], 5, 6, "w", 7);
        byte[] expected = Convert.FromHexString(
            "00" + "01000000" + "2301" + "02000000" + "AABB" + "0500000000000000" + "0600000000000000" + "0177" + "0700000000000000");

        Assert.Equal(expected, EconomyNotifyWire.EncodeDrainReply([item]));
        EconomyNotification decoded = Assert.Single(EconomyNotifyWire.DecodeDrainReply(expected));
        Assert.Equal((WorldOpcode)0x0123, decoded.Opcode);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, decoded.Payload);
        Assert.Equal(5UL, decoded.RecipientGuid);
        Assert.Equal(6L, decoded.RecipientSessionGeneration);
        Assert.Equal("w", decoded.TargetWorkerId);
        Assert.Equal(7L, decoded.Sequence);
        Assert.Equal(expected.Length, EconomyNotifyWire.ReplyHeaderBytes + EconomyNotifyWire.EncodedItemBytes(item));
    }

    [Fact]
    public void ErrorFrame_HasTheGoldenLayout()
    {
        // status 1, message "no".
        Assert.Equal(Convert.FromHexString("01026E6F"), EconomyNotifyWire.EncodeError("no"));
    }

    [Fact]
    public async Task OwnerAndWonNotices_RouteToTheirOwnWorkers()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Online(directory, SellerId, "worker-A");
        Online(directory, NewBidderId, "worker-B");
        (EconomyCommitNotifier notifier, _, EconomyNotifyClient client) = Holder(directory);
        AuctionRecord sold = Auction(NewBidderId, 1000);

        notifier.AuctionOwnerResult(sold, sold: true);
        notifier.AuctionWon(sold);

        EconomyNotification owner = Assert.Single(await client.DrainAsync("worker-A"));
        Assert.Equal(WorldOpcode.SmsgAuctionOwnerNotification, owner.Opcode);
        Assert.Equal(EconomyPackets.OwnerNotification(sold, sold: true), owner.Payload);
        EconomyNotification won = Assert.Single(await client.DrainAsync("worker-B"));
        Assert.Equal(WorldOpcode.SmsgAuctionBidderNotification, won.Opcode);
        Assert.Equal(EconomyPackets.BidderNotification(sold, won: true), won.Payload);
    }

    [Fact]
    public async Task OfflineRecipient_IsNotQueued_AndStaleSessionIsDropped()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        (EconomyCommitNotifier notifier, _, EconomyNotifyClient client) = Holder(directory);
        Assert.Equal(EconomyNotifyStatus.RecipientOffline, notifier.MailDelivered(NewBidderId));
        Assert.Empty(await client.DrainAsync("worker-B"));

        Online(directory, NewBidderId, "worker-B", generation: 1);
        notifier.MailDelivered(NewBidderId);
        IReadOnlyList<EconomyNotification> drained = await client.DrainAsync("worker-B");
        var sent = new List<EconomyNotification>();
        var relogged = new EconomyNotifyReceiver(_ => 2, sent.Add);
        Assert.Equal(0, relogged.Apply(drained));
        Assert.Empty(sent);
        Assert.Equal(0, new EconomyNotifyReceiver(_ => null, sent.Add).Apply(drained));
    }

    [Fact]
    public async Task Drain_PreservesCommitOrder_AndBoundsTheQueue()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Online(directory, NewBidderId, "worker-B");
        (EconomyCommitNotifier notifier, EconomyNotifyRouter router, EconomyNotifyClient client) = Holder(directory);

        for (int i = 0; i < EconomyNotifyRouter.MaxQueuedPerWorker + 10; i++)
        {
            notifier.MailDelivered(NewBidderId);
        }

        Assert.Equal(EconomyNotifyRouter.MaxQueuedPerWorker, router.PendingCount("worker-B"));
        IReadOnlyList<EconomyNotification> first = await client.DrainAsync("worker-B");
        Assert.Equal(EconomyNotifyRouter.MaxDrainBatch, first.Count);
        Assert.Equal(11, first[0].Sequence); // the 10 oldest were dropped
        Assert.True(first.Zip(first.Skip(1), (a, b) => a.Sequence < b.Sequence).All(x => x));
    }

    [Fact]
    public void MalformedRequest_AnswersWithAnErrorFrame()
    {
        var router = new EconomyNotifyRouter(new InMemoryRealmPresenceDirectory());
        byte[] reply = router.Handle(new byte[] { 99 });
        Assert.Equal(1, reply[0]);
        Assert.Equal(Convert.FromHexString("011C"), reply[..2]); // status error, message of 28 bytes
        Assert.False(EconomyNotifyWire.IsEconomyNotifyRequest(new byte[] { 99 }));
        Assert.Throws<InvalidOperationException>(() => EconomyNotifyWire.DecodeDrainReply(reply));
    }
}

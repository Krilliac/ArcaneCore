using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Auxiliary;
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

        Assert.Equal(EconomyNotifyStatus.Queued, notifier.MailCommitted(NewBidderId));

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
        AuctionRecord afterBid = Auction(NewBidderId, 500);

        Assert.Equal(EconomyNotifyStatus.Queued, notifier.Outbid(afterBid, OldBidderId));

        EconomyNotification n = Assert.Single(await clientOnB.DrainAsync("worker-B"));
        Assert.Equal(WorldOpcode.SmsgAuctionBidderNotification, n.Opcode);
        Assert.Equal(EconomyPackets.BidderNotification(afterBid, won: false), n.Payload);
        Assert.Equal((ulong)OldBidderId, n.RecipientGuid);
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
        Assert.Equal(EconomyNotifyStatus.RecipientOffline, notifier.MailCommitted(NewBidderId));
        Assert.Empty(await client.DrainAsync("worker-B"));

        Online(directory, NewBidderId, "worker-B", generation: 1);
        notifier.MailCommitted(NewBidderId);
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
            notifier.MailCommitted(NewBidderId);
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
        Assert.False(EconomyNotifyWire.IsEconomyNotifyRequest(new byte[] { 99 }));
        Assert.Throws<InvalidOperationException>(() => EconomyNotifyWire.DecodeDrainReply(reply));
    }
}

using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Auxiliary;

/// <summary>
/// Mail and auction requests of a worker that does not hold the Economy lease are served by the holder (M15 S3d): the holder cache takes the list, bid and
/// buyout, a mail take commits through the holder, and only the holder runs expiry. Worker B and holder A share one SQLite database.
/// </summary>
public sealed class CrossWorkerEconomyTests
{
    private const int SellerId = 10;
    private const int FirstBidder = 77;
    private const int SecondBidder = 88;
    private const int MailReceiver = 5;
    private const uint House = 1;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ListFromB_IsAnsweredFromHolderACache()
    {
        await using var cluster = await Cluster.CreateAsync();

        EconomyForwardResult result = await cluster.ClientOnB.ListAuctionsAsync(House);

        Assert.Equal(EconomyForwardStatus.Ok, result.Status);
        ForwardedAuction listed = Assert.Single(result.Auctions);
        Assert.Equal(cluster.Auction, listed.Auction);
        Assert.Equal(cluster.Auction.ItemGuid, listed.Item.Guid);
        Assert.Empty((await cluster.ClientOnB.ListAuctionsAsync(House + 1)).Auctions);
        Assert.Equal(cluster.Auction, Assert.Single((await cluster.ClientOnB.ListAuctionsAsync(House, cluster.Auction.Id)).Auctions).Auction);
    }

    [Fact]
    public async Task BidFromB_LandsInHolderACacheAndStore_AndReplayIsIdempotent()
    {
        await using var cluster = await Cluster.CreateAsync();
        AuctionRecord bid = cluster.Auction with { BidderId = FirstBidder, Bid = 60 };
        var commit = new EconomyCommitRequest(Guid.NewGuid(), [], [new UpdateAuction(cluster.Auction, bid)]);

        Assert.Equal(EconomyForwardStatus.Ok, (await cluster.ClientOnB.WriteAuctionAsync(commit)).Status);

        Assert.Equal(bid, await cluster.A.OnWorld(() => cluster.A.Feature.Auctions.Single().Auction));
        await using (CharacterDbContext db = cluster.NewContext())
        {
            Assert.Equal(bid.Bid, (await db.Set<AuctionRow>().SingleAsync()).Bid);
        }

        // The same operation again (a retry after a lost answer) is answered Ok and changes nothing.
        Assert.Equal(EconomyForwardStatus.Ok, (await cluster.ClientOnB.WriteAuctionAsync(commit)).Status);
        Assert.Equal(bid, await cluster.A.OnWorld(() => cluster.A.Feature.Auctions.Single().Auction));

        // A bid that still expects the old row is refused by the holder cache and writes nothing.
        AuctionRecord stale = cluster.Auction with { BidderId = SecondBidder, Bid = 70 };
        var staleCommit = new EconomyCommitRequest(Guid.NewGuid(), [], [new UpdateAuction(cluster.Auction, stale)]);
        Assert.Equal(EconomyForwardStatus.Conflict, (await cluster.ClientOnB.WriteAuctionAsync(staleCommit)).Status);
        Assert.Equal(bid, await cluster.A.OnWorld(() => cluster.A.Feature.Auctions.Single().Auction));
    }

    [Fact]
    public async Task BuyoutFromB_RemovesTheAuctionFromHolderACache()
    {
        await using var cluster = await Cluster.CreateAsync();
        var commit = new EconomyCommitRequest(Guid.NewGuid(), [],
            [new DeleteAuction(cluster.Auction), new DeleteEscrowItem(cluster.Auction.ItemGuid)]);

        Assert.Equal(EconomyForwardStatus.Ok, (await cluster.ClientOnB.WriteAuctionAsync(commit)).Status);

        Assert.Empty(await cluster.A.OnWorld(() => cluster.A.Feature.Auctions.ToList()));
        Assert.Empty((await cluster.ClientOnB.ListAuctionsAsync(House)).Auctions);
        await using CharacterDbContext db = cluster.NewContext();
        Assert.Empty(await db.Set<AuctionRow>().ToListAsync());
    }

    [Fact]
    public async Task OutbidNotice_FromAForwardedBid_ReachesThePreviousBidderOnB()
    {
        await using var cluster = await Cluster.CreateAsync();
        var directory = new InMemoryRealmPresenceDirectory();
        var router = new EconomyNotifyRouter(directory);
        cluster.A.Feature.CommitNotifier = new EconomyCommitNotifier(router);
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(new RealmPresenceEntry(
            (uint)FirstBidder, "Char" + FirstBidder, FirstBidder, "worker-B", "gw-1", 0, 0, 0, 60, 1, 1, 0, 0, 0,
            PresenceVersion: 1, SessionGeneration: 1, UpdatedAtUtc: DateTimeOffset.UtcNow), out _));
        AuctionRecord first = cluster.Auction with { BidderId = FirstBidder, Bid = 60 };
        AuctionRecord second = first with { BidderId = SecondBidder, Bid = 80 };

        Assert.Equal(EconomyForwardStatus.Ok, (await cluster.ClientOnB.WriteAuctionAsync(
            new EconomyCommitRequest(Guid.NewGuid(), [], [new UpdateAuction(cluster.Auction, first)]))).Status);
        Assert.Equal(0, router.PendingCount("worker-B"));
        Assert.Equal(EconomyForwardStatus.Ok, (await cluster.ClientOnB.WriteAuctionAsync(
            new EconomyCommitRequest(Guid.NewGuid(), [], [new UpdateAuction(first, second)]))).Status);

        EconomyNotification notice = Assert.Single(router.DrainWorker("worker-B"));
        Assert.Equal(WorldOpcode.SmsgAuctionBidderNotification, notice.Opcode);
        Assert.Equal((ulong)FirstBidder, notice.RecipientGuid);
    }

    [Fact]
    public async Task MailTakeFromB_CommitsThroughTheHolder()
    {
        await using var cluster = await Cluster.CreateAsync();

        EconomyForwardResult list = await cluster.ClientOnB.ListMailAsync(MailReceiver);
        Assert.Equal(EconomyForwardStatus.Ok, list.Status);
        MailRecord mail = Assert.Single(list.Mails, m => m.Mail.Id == cluster.Mail.Id).Mail;
        Assert.Equal(70u, mail.Money);

        MailRecord taken = mail with { Money = 0 };
        var commit = new EconomyCommitRequest(Guid.NewGuid(), [], [new UpdateMail(mail, taken)]);
        Assert.Equal(EconomyForwardStatus.Ok, (await cluster.ClientOnB.WriteMailAsync(commit)).Status);

        Assert.Equal(0u, Assert.Single((await cluster.ClientOnB.ListMailAsync(MailReceiver)).Mails, m => m.Mail.Id == mail.Id).Mail.Money);
        // The letter changed under the worker: a second take that still expects the old money finds the stored row different and writes nothing.
        var again = new EconomyCommitRequest(Guid.NewGuid(), [], [new UpdateMail(mail, taken)]);
        Assert.NotEqual(EconomyForwardStatus.Ok, (await cluster.ClientOnB.WriteMailAsync(again)).Status);
    }

    [Fact]
    public async Task OnlyTheHolderRunsExpiry()
    {
        await using var cluster = await Cluster.CreateAsync();
        Assert.False(cluster.B.Feature.IsEconomyHolder);
        Assert.True(cluster.A.Feature.IsEconomyHolder);

        await cluster.B.OnWorld(() =>
        {
            cluster.B.Feature.RunExpirySweep();
            return true;
        });
        await Task.Delay(500);
        Assert.True(await cluster.ExpiredMailExistsAsync(), "worker B must not expire letters");

        await cluster.A.OnWorld(() =>
        {
            cluster.A.Feature.RunExpirySweep();
            return true;
        });
        using var timeout = new CancellationTokenSource(Budget);
        while (await cluster.ExpiredMailExistsAsync())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task WorkerBRefusesToServe_AndABadRequestIsInvalid()
    {
        await using var cluster = await Cluster.CreateAsync();

        var any = new EconomyForwardRequest(Guid.NewGuid(), "worker-C", EconomyForwardKind.AuctionList, HouseId: House);
        Assert.Equal(EconomyForwardStatus.NotHolder, (await cluster.B.Feature.ServeForwardedAsync(any)).Status);

        var auctionChange = new EconomyCommitRequest(Guid.NewGuid(), [], [new DeleteAuction(cluster.Auction)]);
        var wrongKind = new EconomyForwardRequest(Guid.NewGuid(), "worker-B", EconomyForwardKind.MailWrite, Commit: auctionChange);
        Assert.Equal(EconomyForwardStatus.Invalid, (await cluster.A.Feature.ServeForwardedAsync(wrongKind)).Status);
        var noCommit = new EconomyForwardRequest(Guid.NewGuid(), "worker-B", EconomyForwardKind.AuctionWrite);
        Assert.Equal(EconomyForwardStatus.Invalid, (await cluster.A.Feature.ServeForwardedAsync(noCommit)).Status);
        Assert.Single(await cluster.A.OnWorld(() => cluster.A.Feature.Auctions.ToList()));
    }

    [Fact]
    public async Task WriteOverAFailingLink_RetriesWithTheSameIdsThenReportsUnknown()
    {
        var seen = new List<EconomyForwardRequest>();
        var client = new EconomyForwardClient(new FailingLink(seen), "worker-B");
        var commit = new EconomyCommitRequest(Guid.NewGuid(), [], []);

        EconomyForwardResult result = await client.WriteAuctionAsync(commit);

        Assert.Equal(EconomyForwardStatus.Unknown, result.Status);
        Assert.Equal(EconomyForwardClient.WriteAttempts, seen.Count);
        Assert.Single(seen.Select(r => r.RequestId).Distinct());
        Assert.All(seen, r => Assert.Equal(commit.OperationId, r.Commit!.OperationId));
        Assert.Equal(EconomyForwardStatus.Failed, (await client.ListMailAsync(MailReceiver)).Status);
    }

    private sealed class FailingLink(List<EconomyForwardRequest> seen) : IEconomyHolderLink
    {
        public Task<EconomyForwardResult> SendAsync(EconomyForwardRequest request, CancellationToken cancellationToken)
        {
            seen.Add(request);
            throw new IOException("link down");
        }
    }

    /// <summary>One feature on its own world thread over a shared database.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CharacterSaveQueue _saves;

        public Node(DbContextOptions<CharacterDbContext> options)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => new CharacterDbContext(options));
            services.AddScoped<IEconomyStore>(provider => new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()));
            services.AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CharacterSaveQueue>.Instance));
            _services = services.BuildServiceProvider();
            _saves = _services.GetRequiredService<CharacterSaveQueue>();
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, _saves, NullLogger<WorldRuntime>.Instance);
            Feature = new EconomyFeature(_services, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EconomyFeature>.Instance);
            Feature.Options.ExpirySweepSeconds = 0;
            Feature.Attach(World);
            World.Start();
        }

        public WorldRuntime World { get; }
        public EconomyFeature Feature { get; }

        public Task<T> OnWorld<T>(Func<T> action) => World.InvokeAsync(action).WaitAsync(Budget);

        public async ValueTask DisposeAsync()
        {
            await Feature.DisposeAsync();
            World.Dispose();
            await _saves.StopAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly string _path;
        private readonly DbContextOptions<CharacterDbContext> _options;

        private Cluster(string path, DbContextOptions<CharacterDbContext> options, AuctionRecord auction, MailRecord mail)
        {
            _path = path;
            _options = options;
            Auction = auction;
            Mail = mail;
            A = new Node(options);
            B = new Node(options);
            B.Feature.HolderLink = new DirectEconomyHolderLink(A.Feature);
            ClientOnB = new EconomyForwardClient(B.Feature.HolderLink, "worker-B");
        }

        public AuctionRecord Auction { get; }
        public MailRecord Mail { get; }
        public Node A { get; }
        public Node B { get; }
        public EconomyForwardClient ClientOnB { get; }

        public CharacterDbContext NewContext() => new(_options);

        public async Task<bool> ExpiredMailExistsAsync()
        {
            await using CharacterDbContext db = NewContext();
            return await db.Set<MailRow>().AnyAsync(m => m.Id == 600);
        }

        public static async Task<Cluster> CreateAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), $"arcanecore-cross-worker-economy-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var auction = new AuctionRecord
            {
                Id = 1, HouseId = House, ItemGuid = 100, ItemEntry = 4000, ItemCount = 2,
                SellerId = SellerId, StartBid = 50, Buyout = 500, ExpireTime = now + 7200, Deposit = 1,
            };
            var mail = new MailRecord
            {
                Id = 500, MessageType = MailMessageType.Normal, SenderId = SellerId, ReceiverId = MailReceiver, Subject = "money", Money = 70,
                DeliverTime = now - 60, ExpireTime = now + 86_400,
            };
            // A money-only letter past its expiry: the holder sweep deletes it (vmangos ReturnOrDeleteOldMails).
            MailRecord expired = mail with { Id = 600, ReceiverId = 6, Money = 10, DeliverTime = now - 7200, ExpireTime = now - 10 };
            await using (var db = new CharacterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var item = new ItemInstanceRow { Guid = auction.ItemGuid };
                item.CopyFrom(0, new ItemInstanceData { Guid = auction.ItemGuid, Entry = auction.ItemEntry, Count = auction.ItemCount });
                db.Add(item);
                var auctionRow = new AuctionRow();
                auctionRow.CopyFrom(auction);
                db.Add(auctionRow);
                foreach (MailRecord letter in new[] { mail, expired })
                {
                    var row = new MailRow();
                    row.CopyFrom(letter);
                    db.Add(row);
                }

                await db.SaveChangesAsync();
            }

            return new Cluster(path, options, auction, mail);
        }

        public async ValueTask DisposeAsync()
        {
            await B.DisposeAsync();
            await A.DisposeAsync();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }
}

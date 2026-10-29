using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// CMSG_ITEM_TEXT_QUERY (vmangos ItemHandler.cpp HandleItemTextQuery): the text of a letter the player can see or a carried letter item.
/// A letter that is not yet delivered or has expired is not visible (the FindOwnMail filter), so its body is not sent; a text already read
/// is answered from a cache instead of another store read.
/// </summary>
public sealed class EconomyItemTextQueryTests
{
    private const uint Visible = 8101;
    private const uint Delayed = 8102;
    private const uint Expired = 8103;

    [Fact]
    public async Task ItemTextQuery_HidesUndeliveredAndExpiredLetters_AndCachesTheText()
    {
        var reads = new ReadCounter();
        await using Rig rig = await Rig.StartAsync(loginReceiver: true,
            services => services.AddScoped<IEconomyStore>(provider => new CountingStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), reads)));
        await SeedAsync(rig);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), rig.Token);
        await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgMailListResult, rig.Token);

        // The hidden letters are asked for first: had either been answered, its response would arrive before the visible one's.
        await QueryAsync(rig, Delayed);
        await QueryAsync(rig, Expired);
        await QueryAsync(rig, Visible);
        var reader = new PacketReader(await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgItemTextQueryResponse, rig.Token));
        Assert.Equal(Visible, reader.ReadUInt32());
        Assert.Equal("visible", reader.ReadCString());

        await QueryAsync(rig, Visible);
        await QueryAsync(rig, Visible);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(Visible, new PacketReader(await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgItemTextQueryResponse, rig.Token)).ReadUInt32());
        }

        Assert.Equal(1, reads.Of(Visible));
        Assert.Equal(0, reads.Of(Delayed) + reads.Of(Expired));
    }

    private static Task QueryAsync(Rig rig, uint textId)
        => rig.Receiver!.SendAsync(WorldOpcode.CmsgItemTextQuery, BitConverter.GetBytes(textId), rig.Token);

    private static async Task SeedAsync(Rig rig)
    {
        long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        MailRecord Letter(uint id, uint text, long deliver, long expire) => new()
        {
            Id = id, MessageType = MailMessageType.Normal, SenderId = checked((uint)rig.SenderGuid), ReceiverId = checked((int)rig.ReceiverGuid),
            Subject = $"letter {id}", ItemTextId = text, DeliverTime = deliver, ExpireTime = expire,
        };

        EconomyChange[] letters =
        [
            new InsertMail(Letter(6101, Visible, now - 10, now + 86400), "visible"),
            new InsertMail(Letter(6102, Delayed, now + 3600, now + 86400), "delayed"),
            new InsertMail(Letter(6103, Expired, now - 1000, now - 10), "expired"),
        ];
        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(new EconomyCommitRequest(Guid.NewGuid(), [], letters), rig.Token));
    }

    private sealed class ReadCounter
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, int> _reads = new();
        public void Add(uint id) => _reads.AddOrUpdate(id, 1, (_, n) => n + 1);
        public int Of(uint id) => _reads.GetValueOrDefault(id);
    }

    private sealed class CountingStore(IEconomyStore inner, ReadCounter reads) : IEconomyStore
    {
        public Task<string?> GetItemTextAsync(uint id, CancellationToken c = default)
        {
            reads.Add(id);
            return inner.GetItemTextAsync(id, c);
        }

        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int id, CancellationToken c = default) => inner.GetMailsAsync(id, c);
        public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest r, CancellationToken c = default) => inner.CommitAsync(r, c);
        public Task<bool> IsCommittedAsync(Guid id, CancellationToken c = default) => inner.IsCommittedAsync(id, c);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long n, int m, CancellationToken c = default) => inner.GetExpiredMailsAsync(n, m, c);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int id, CancellationToken c = default) => inner.GetMailsInvolvingAsync(id, c);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken c = default) => inner.GetAuctionsAsync(c);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> ids, CancellationToken c = default) => inner.GetEscrowItemsAsync(ids, c);
        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter f, CancellationToken c = default) => inner.GetAuctionSnapshotAsync(f, c);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken c = default) => inner.GetIdSeedAsync(c);
    }
}
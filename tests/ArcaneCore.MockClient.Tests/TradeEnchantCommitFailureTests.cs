using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Durable conflict coverage for the existing two-party trade settlement boundary.</summary>
public sealed class TradeEnchantCommitFailureTests
{
    [Fact]
    public async Task TwoParticipantConflict_DoesNotPublishEnchantReagentsOrOrdinaryTransfer()
    {
        var refusal = new ConflictStoreControl();
        await using Rig rig = await Rig.StartAsync(loginReceiver: true,
            configureServices: services => services.AddScoped<IEconomyStore>(provider =>
                new ConflictEconomyStore(new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), refusal)));

        const uint spellId = 991011;
        const uint enchantId = 991012;
        (byte bag, byte slot, ObjectGuid itemGuid, byte targetBag, byte targetSlot, ObjectGuid targetGuid) = await rig.Server.World.InvokeAsync(() =>
        {
            var sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var recipient = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            SpellFeature spells = rig.Server.Services.GetRequiredService<SpellFeature>();
            spells.System.Store = new SpellStore([.. spells.System.Store.All, new SpellInfo
            {
                Id = spellId,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.EnchantItem, MiscValue = (int)enchantId }],
                Reagents = [new SpellReagent((int)SyntheticArcaneServer.FixedRewardItem, 1)],
            }], [], []);
            spells.System.ItemEnchantments = new ItemEnchantmentCatalog([new ItemEnchantmentDefinition(enchantId, [])]);
            spells.Spellbook.LearnSpell(sender, spellId);
            Assert.Equal(InventoryResult.Ok, sender.Inventory.AddItem(SyntheticArcaneServer.FixedRewardItem, 2, out _));
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok,
                sender.Inventory.AddItem(SyntheticArcaneServer.UnchosenRewardItem, 1, out var item));
            Assert.Equal(InventoryResult.Ok, recipient.Inventory.AddItem(SyntheticArcaneServer.ChosenRewardItem, 1, out Item? target));
            return (item!.BagSlot, item.Slot, item.Guid, target!.BagSlot, target.Slot, target.Guid);
        }).WaitAsync(rig.Token);

        await rig.Sender.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid), rig.Token);
        await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, rig.Token);
        await rig.Receiver.SendAsync(WorldOpcode.CmsgBeginTrade, [], rig.Token);
        await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, rig.Token);
        await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, rig.Token);
        await rig.Sender.SendAsync(WorldOpcode.CmsgSetTradeItem, [0, bag, slot], rig.Token);
        await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
        await rig.Receiver.SendAsync(WorldOpcode.CmsgSetTradeItem, [6, targetBag, targetSlot], rig.Token);
        await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
        var cast = new PacketWriter();
        cast.WriteUInt32(spellId);
        new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = new ObjectGuid(6) }.Write(cast);
        await rig.Sender.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray(), rig.Token);
        byte[] pending = await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
        Assert.Equal(spellId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pending.AsSpan(13)));
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await StatusAsync(rig.Receiver, 4, rig.Token);
        await rig.Receiver.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await StatusAsync(rig.Sender, 3, rig.Token);

        Assert.True(refusal.CommitAttempted);
        Assert.Null(await rig.Server.World.InvokeAsync(() =>
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory.GetItemByGuid(itemGuid)));
        Assert.NotNull(await rig.Server.World.InvokeAsync(() =>
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!.Inventory.GetItemByGuid(itemGuid)));
        await rig.Server.World.InvokeAsync(() =>
        {
            var sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var recipient = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            Assert.Equal(2u, sender.Inventory.GetItemCount(SyntheticArcaneServer.FixedRewardItem));
            Assert.Equal(0u, recipient.Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        IItemStore items = scope.ServiceProvider.GetRequiredService<IItemStore>();
        IReadOnlyList<InventoryItemData> senderStored = await items.GetInventoryAsync((int)rig.SenderGuid, rig.Token);
        Assert.Contains(senderStored, row => row.Item.Guid == itemGuid.Low);
        Assert.Equal(2u, (uint)senderStored.Where(row => row.Item.Entry == SyntheticArcaneServer.FixedRewardItem).Sum(row => row.Item.Count));
        IReadOnlyList<InventoryItemData> recipientStored = await items.GetInventoryAsync((int)rig.ReceiverGuid, rig.Token);
        Assert.Equal(0u, recipientStored.Single(row => row.Item.Guid == targetGuid.Low).Item.Enchantments[0]);
    }

    private static async Task StatusAsync(ScenarioConnection connection, uint status, CancellationToken token)
    {
        while (true)
        {
            byte[] payload = await connection.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, token);
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload) == status) return;
        }
    }

    private sealed class ConflictStoreControl
    {
        public bool CommitAttempted;
    }

    private sealed class ConflictEconomyStore(IEconomyStore inner, ConflictStoreControl control) : IEconomyStore
    {
        public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            control.CommitAttempted = true;
            return Task.FromResult(EconomyCommitResult.Conflict);
        }

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default) => inner.IsCommittedAsync(operationId, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default) => inner.GetMailsAsync(receiverId, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default) => inner.GetExpiredMailsAsync(now, max, cancellationToken);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetMailsInvolvingAsync(characterId, cancellationToken);
        public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default) => inner.GetItemTextAsync(itemTextId, cancellationToken);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default) => inner.GetAuctionsAsync(cancellationToken);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default) => inner.GetEscrowItemsAsync(itemGuids, cancellationToken);
        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default) => inner.GetAuctionSnapshotAsync(filter, cancellationToken);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default) => inner.GetIdSeedAsync(cancellationToken);
    }
}

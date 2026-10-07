using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Economy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Deferred trade-enchant wire coverage: build-5875 TRADE_ITEM uses raw packed slot 6.</summary>
public sealed class TradeEnchantSettlementTests
{
    private const uint TradeEnchantSpell = 991001;
    private const uint TradeEnchantId = 991002;

    [Theory]
    [InlineData(false, 2u)]
    [InlineData(false, 1u)]
    [InlineData(true, 2u)]
    public async Task FinalAcceptance_CommitsEnchantAndReagentTogether_OrRefusesMissingReagent(bool removeReagent, uint reagentCount)
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true, configureServices: TradeEnchantCatalog.With(TradeEnchantCatalog.Plain(TradeEnchantId)));
        (byte bag, byte slot, ObjectGuid targetGuid) = await rig.Server.World.InvokeAsync(() =>
        {
            SpellFeature feature = rig.Server.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = TradeEnchantSpell,
                ManaCost = 5,
                RecoveryTime = 1000,
                StartRecoveryTime = 1500,
                StartRecoveryCategory = SpellConstants.GlobalCooldownCategory,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.EnchantItem, MiscValue = (int)TradeEnchantId }],
                Reagents = [new SpellReagent((int)SyntheticArcaneServer.FixedRewardItem, 1)],
            }], [], []);
            feature.System.ItemEnchantments = new ItemEnchantmentCatalog([new ItemEnchantmentDefinition(TradeEnchantId, [])]);
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            feature.Spellbook.LearnSpell(caster, TradeEnchantSpell);
            caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
            caster.SetUInt32(UpdateFields.UnitFieldPower1, 100);
            Assert.Equal(InventoryResult.Ok, caster.Inventory.AddItem(SyntheticArcaneServer.FixedRewardItem, reagentCount, out _));
            Assert.Equal(InventoryResult.Ok, receiver.Inventory.AddItem(SyntheticArcaneServer.ChosenRewardItem, 1, out Item? target));
            return (target!.BagSlot, target.Slot, target.Guid);
        }).WaitAsync(rig.Token);
        await OpenAsync(rig);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgSetTradeItem, [6, bag, slot], rig.Token);
        await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
        var cast = new PacketWriter();
        cast.WriteUInt32(TradeEnchantSpell);
        new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = new ObjectGuid(6) }.Write(cast);
        await rig.Sender.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray(), rig.Token);
        await ReadPendingAsync(rig.Sender, rig.Token);
        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var recipient = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            Assert.Equal(reagentCount, caster.Inventory.GetItemCount(SyntheticArcaneServer.FixedRewardItem));
            Assert.Equal(100u, caster.GetUInt32(UpdateFields.UnitFieldPower1));
            Assert.Equal(0u, recipient.Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            if (removeReagent) caster.Inventory.DestroyItemCount(SyntheticArcaneServer.FixedRewardItem, reagentCount);
            return true;
        }).WaitAsync(rig.Token);
        // Drain old BACK_TO_TRADE packets with a later extension before the acceptance phase.
        await rig.Sender.SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioWire.UInt32(1), rig.Token);
        await ReadPendingAsync(rig.Receiver, rig.Token, gold: 1);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Sender.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        await ReadStatusAsync(rig.Receiver, TradeStatus.TradeAccept, rig.Token);
        await rig.Receiver.SendAsync(WorldOpcode.CmsgAcceptTrade, [], rig.Token);
        if (!removeReagent)
        {
            byte[] go = await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgSpellGo, rig.Token);
            Assert.Equal(SpellPackets.BuildSpellGo(new ObjectGuid(rig.SenderGuid), new ObjectGuid(rig.SenderGuid),
                TradeEnchantSpell, SpellCastFlags.Unknown9, [], [],
                new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = targetGuid }), go);
        }
        await ReadStatusAsync(rig.Receiver, removeReagent ? TradeStatus.BackToTrade : TradeStatus.TradeComplete, rig.Token);
        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            var recipient = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            Assert.Equal(removeReagent ? 0u : reagentCount - 1, caster.Inventory.GetItemCount(SyntheticArcaneServer.FixedRewardItem));
            Assert.Equal(removeReagent ? 100u : 95u, caster.GetUInt32(UpdateFields.UnitFieldPower1));
            Assert.Equal(removeReagent ? 0u : TradeEnchantId, recipient.Inventory.GetItemByGuid(targetGuid)!.EnchantmentId(0));
            EconomyFeature economy = rig.Server.Services.GetRequiredService<EconomyFeature>();
            if (removeReagent)
            {
                Assert.False(economy.TradeOf(caster)!.Initiator.Accepted);
                Assert.False(economy.TradeOf(caster)!.Target.Accepted);
                Assert.Null(economy.TradeOf(caster)!.Initiator.PendingEnchantment);
            }
            else Assert.Null(economy.TradeOf(caster));
            return true;
        }).WaitAsync(rig.Token);
        if (!removeReagent)
        {
            await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
            IReadOnlyList<InventoryItemData> stored = await scope.ServiceProvider.GetRequiredService<IItemStore>()
                .GetInventoryAsync((int)rig.ReceiverGuid, rig.Token);
            Assert.Equal(TradeEnchantId, stored.Single(i => i.Item.Guid == targetGuid.Low).Item.Enchantments[0]);
            CharacterLife? life = await scope.ServiceProvider.GetRequiredService<ICharacterLifeStore>()
                .LoadAsync((int)rig.SenderGuid, rig.Token);
            Assert.Equal(95u, life!.Powers[0]);
        }
    }

    private static async Task OpenAsync(Rig rig)
    {
        await rig.Sender.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid), rig.Token);
        await ReadStatusAsync(rig.Receiver!, TradeStatus.BeginTrade, rig.Token);
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgBeginTrade, [], rig.Token);
        await ReadStatusAsync(rig.Sender, TradeStatus.OpenWindow, rig.Token);
        await ReadStatusAsync(rig.Receiver, TradeStatus.OpenWindow, rig.Token);
    }

    private static async Task ReadPendingAsync(ScenarioConnection connection, CancellationToken token, uint? gold = null)
    {
        while (true)
        {
            byte[] payload = await connection.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(13)) == TradeEnchantSpell
                && (gold is null || BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(9)) == gold)) return;
        }
    }

    private static async Task ReadStatusAsync(ScenarioConnection connection, TradeStatus status, CancellationToken token)
    {
        while (true)
        {
            byte[] payload = await connection.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == (uint)status) return;
        }
    }

    [Fact]
    public async Task TradeItemCast_IsDeferred_AndPublishesPendingSpellWithoutMutatingTheTarget()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true, configureServices: TradeEnchantCatalog.With(TradeEnchantCatalog.Stat(TradeEnchantId, 4, 10)));
        (byte bag, byte slot) = await rig.Server.World.InvokeAsync(() =>
        {
            SpellFeature feature = rig.Server.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = TradeEnchantSpell,
                Name = "Synthetic trade enchant",
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.EnchantItem, MiscValue = (int)TradeEnchantId }],
            }], [], []);
            feature.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog(
                [new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(TradeEnchantId,
                    [new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentEffect(5, 4, 10)])]);
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.True(feature.Spellbook.LearnSpell(caster, TradeEnchantSpell));
            var receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok,
                receiver.Inventory.AddItem(SyntheticArcaneServer.FixedRewardItem, 1, out var target));
            return (target!.BagSlot, target.Slot);
        }).WaitAsync(rig.Token);

        await rig.Sender.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid), rig.Token);
        await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, rig.Token);
        await rig.Receiver.SendAsync(WorldOpcode.CmsgBeginTrade, [], rig.Token);
        await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, rig.Token);
        await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, rig.Token);

        await rig.Receiver.SendAsync(WorldOpcode.CmsgSetTradeItem, [6, bag, slot], rig.Token);
        byte[] extended = await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(extended.AsSpan(13)));

        byte[] cast = new byte[7];
        BinaryPrimitives.WriteUInt32LittleEndian(cast, TradeEnchantSpell);
        BinaryPrimitives.WriteUInt16LittleEndian(cast.AsSpan(4), 0x1000);
        cast[6] = 1; // packed GUID byte count
        // The raw packed GUID value is TRADE_SLOT_NONTRADED (6), not the item GUID.
        await rig.Sender.SendAsync(WorldOpcode.CmsgCastSpell, [.. cast, (byte)6], rig.Token);

        byte[] pending = await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
        Assert.Equal(TradeEnchantSpell, BinaryPrimitives.ReadUInt32LittleEndian(pending.AsSpan(13)));
        Assert.Equal(0u, await rig.Server.World.InvokeAsync(() =>
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory
                .GetItem(bag, slot)!.EnchantmentId(0)).WaitAsync(rig.Token));

        await rig.Sender.SendAsync(WorldOpcode.CmsgUnacceptTrade, [], rig.Token);
        await rig.Sender.SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioWire.UInt32(2), rig.Token);
        await ReadPendingAsync(rig.Receiver, rig.Token, gold: 2);
        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Equal(TradeEnchantSpell, rig.Server.Services.GetRequiredService<EconomyFeature>()
                .TradeOf(caster)!.SideOf(caster).PendingEnchantment!.Value.SpellId);
            return true;
        }).WaitAsync(rig.Token);
        await rig.Receiver.SendAsync(WorldOpcode.CmsgClearTradeItem, [6], rig.Token);
        while (true)
        {
            byte[] cleared = await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatusExtended, rig.Token);
            if (cleared[0] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(cleared.AsSpan(13)) == 0) break;
        }
        await rig.Server.World.InvokeAsync(() =>
        {
            var caster = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            TradeSession trade = rig.Server.Services.GetRequiredService<EconomyFeature>().TradeOf(caster)!;
            Assert.Null(trade.Initiator.PendingEnchantment);
            Assert.Null(trade.Target.PendingEnchantment);
            Assert.Equal(0u, rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Inventory
                .GetItem(bag, slot)!.EnchantmentId(0));
            return true;
        }).WaitAsync(rig.Token);
    }
}

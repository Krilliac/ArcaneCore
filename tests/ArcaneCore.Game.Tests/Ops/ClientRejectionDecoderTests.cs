using ArcaneCore.Game.ClientErrors;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>The decoder reads the reason out of the payloads the real builders write (docs/ops/observability.md).</summary>
public sealed class ClientRejectionDecoderTests
{
    private static ClientRejection Decode(WorldOpcode opcode, byte[] payload)
    {
        Assert.True(ClientRejectionDecoder.IsCandidate(opcode));
        Assert.True(ClientRejectionDecoder.TryDecode(opcode, payload, out ClientRejection r));
        return r;
    }

    [Fact]
    public void CastResult_FailureCarriesSpellAndReason_SuccessIsNotARejection()
    {
        ClientRejection r = Decode(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(6478, SpellCastResult.NotFishable));
        Assert.Equal(ClientRejectionKind.CastResult, r.Kind);
        Assert.Equal(6478u, r.Entry);
        Assert.Equal("NotFishable", r.ReasonName);
        Assert.False(ClientRejectionDecoder.TryDecode(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(6478, SpellCastResult.CastOk), out _));
    }

    [Fact]
    public void InventoryLootBuySellQuestTradeAreDecoded()
    {
        ClientRejection inv = Decode(WorldOpcode.SmsgInventoryChangeFailure,
            Items.ItemPackets.InventoryChangeFailure(Items.InventoryResult.CantEquipLevelI, new ObjectGuid(0x4000000000000011), ObjectGuid.Empty, 0, 20));
        Assert.Equal("CantEquipLevelI", inv.ReasonName);
        Assert.Equal(0x4000000000000011ul, inv.Target);
        Assert.False(ClientRejectionDecoder.TryDecode(WorldOpcode.SmsgInventoryChangeFailure, Items.ItemPackets.InventoryChangeFailure(Items.InventoryResult.Ok, ObjectGuid.Empty, ObjectGuid.Empty), out _));

        ClientRejection loot = Decode(WorldOpcode.SmsgLootResponse, GroupLootPackets.LootErrorResponse(new ObjectGuid(0xF1300000000000AA), LootError.TooFar));
        Assert.Equal("TooFar", loot.ReasonName);
        Assert.Equal(0xF1300000000000AAul, loot.Target);

        ClientRejection buy = Decode(WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(new ObjectGuid(5), 118, BuyResult.NotEnoughMoney).AsSpan().ToArray());
        Assert.Equal(118u, buy.Entry);
        Assert.Equal("NotEnoughMoney", buy.ReasonName);

        ClientRejection sell = Decode(WorldOpcode.SmsgSellItem, NpcPackets.SellFailed(new ObjectGuid(5), new ObjectGuid(77), SellResult.CantSellItem).AsSpan().ToArray());
        Assert.Equal("CantSellItem", sell.ReasonName);
        Assert.Equal(77u, sell.Arg);

        ClientRejection qi = Decode(WorldOpcode.SmsgQuestgiverQuestInvalid, QuestPackets.QuestInvalid(QuestInvalidReason.NotEnoughMoney).AsSpan().ToArray());
        Assert.Equal("NotEnoughMoney", qi.ReasonName);
        ClientRejection qf = Decode(WorldOpcode.SmsgQuestgiverQuestFailed, QuestPackets.QuestFailed(4321, QuestInvalidReason.InventoryFull).AsSpan().ToArray());
        Assert.Equal(4321u, qf.Entry);

        ClientRejection trade = Decode(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.TargetTooFar));
        Assert.Equal("TargetTooFar", trade.ReasonName);
        Assert.False(ClientRejectionDecoder.TryDecode(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.TradeComplete), out _));
    }

    [Fact]
    public void ACloseWindowTradeStatus_IsARejectionOnlyWhenItCarriesAnInventoryError()
    {
        Assert.False(ClientRejectionDecoder.TryDecode(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.CloseWindow), out _));

        ClientRejection full = Decode(WorldOpcode.SmsgTradeStatus,
            EconomyPackets.TradeStatus(TradeStatus.CloseWindow, inventoryResult: ArcaneCore.Game.Items.InventoryResult.InventoryFull, targetError: true));
        Assert.Equal(ClientRejectionKind.TradeStatus, full.Kind);
        Assert.Equal("CloseWindow", full.ReasonName);
        Assert.Equal((uint)ArcaneCore.Game.Items.InventoryResult.InventoryFull, full.Arg);
    }

    [Fact]
    public void ARealLootWindowAndShortPayloadsAreNotRejections_AndOtherOpcodesAreNotCandidates()
    {
        byte[] window = new byte[14];
        window[8] = 1;
        Assert.False(ClientRejectionDecoder.TryDecode(WorldOpcode.SmsgLootResponse, window, out _));
        Assert.False(ClientRejectionDecoder.TryDecode(WorldOpcode.SmsgCastResult, [1, 2], out _));
        Assert.False(ClientRejectionDecoder.IsCandidate(WorldOpcode.SmsgPong));
        Assert.False(ClientRejectionDecoder.IsCandidate(WorldOpcode.SmsgUpdateObject));
    }
}

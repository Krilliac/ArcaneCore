using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// A bid the bidder cannot afford and a cancellation whose cut the seller cannot pay get no answer in vmangos
/// (D:\refs\vmangos\src\game\Handlers\AuctionHouseHandler.cpp:498-503 and :592-594). Packets of one session are answered
/// in order, so the next deterministic answer proves the earlier request produced none.
/// </summary>
public sealed class EconomyAuctionSilentRefusalTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unaffordable_bid_is_silent_unless_the_option_is_off(bool silent)
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        rig.Server.Services.GetRequiredService<EconomyFeature>().Options.AuctionSilentRefusals = silent;
        uint auction = await ListAsync(rig);
        await BidAsync(rig, auction, 100);                 // the bidder has no money
        await BidAsync(rig, auction, 1);                   // below the start bid: BID_INCREMENT, always answered
        (AuctionAction _, AuctionError first) = await ReadResultAsync(rig.Receiver!, rig.Token);
        Assert.Equal(silent ? AuctionError.BidIncrement : AuctionError.NotEnoughMoney, first);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cancel_the_seller_cannot_pay_the_cut_for_is_silent_unless_the_option_is_off(bool silent)
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        rig.Server.Services.GetRequiredService<EconomyFeature>().Options.AuctionSilentRefusals = silent;
        uint auction = await ListAsync(rig);
        await rig.Server.World.InvokeAsync(() =>
        {
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!.Money = 1000;
            return true;
        }).WaitAsync(rig.Token);
        await BidAsync(rig, auction, 100);
        Assert.Equal(AuctionError.Ok, (await ReadResultAsync(rig.Receiver!, rig.Token)).Error);
        await rig.Server.World.InvokeAsync(() =>
        {
            rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!.Money = 0;   // the 5% cut of 100 is 5
            return true;
        }).WaitAsync(rig.Token);
        await CancelAsync(rig, auction);
        await CancelAsync(rig, 999_999);                    // an unknown auction: REMOVED / DATABASE, always answered
        (AuctionAction action, AuctionError first) = await ReadResultAsync(rig.Sender, rig.Token);
        Assert.Equal(AuctionAction.Removed, action);
        Assert.Equal(silent ? AuctionError.Database : AuctionError.NotEnoughMoney, first);
    }

    private static async Task<uint> ListAsync(Rig rig)
    {
        ulong item = await rig.Server.World.InvokeAsync(() =>
        {
            Player player = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(SyntheticArcaneServer.FixedRewardItem, 1, out Item? added));
            return added!.Guid.Value;
        }).WaitAsync(rig.Token);
        var sell = new byte[28];
        BinaryPrimitives.WriteUInt64LittleEndian(sell, SyntheticArcaneServer.NpcGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(sell.AsSpan(8), item);
        BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(16), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(24), 120);
        await rig.Sender.SendAsync(WorldOpcode.CmsgAuctionSellItem, sell, rig.Token);
        byte[] started = await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgAuctionCommandResult, rig.Token);
        Assert.Equal((uint)AuctionError.Ok, BinaryPrimitives.ReadUInt32LittleEndian(started.AsSpan(8)));
        return BinaryPrimitives.ReadUInt32LittleEndian(started);
    }

    private static Task BidAsync(Rig rig, uint auction, uint price)
    {
        var bid = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bid, SyntheticArcaneServer.NpcGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(bid.AsSpan(8), auction);
        BinaryPrimitives.WriteUInt32LittleEndian(bid.AsSpan(12), price);
        return rig.Receiver!.SendAsync(WorldOpcode.CmsgAuctionPlaceBid, bid, rig.Token);
    }

    private static Task CancelAsync(Rig rig, uint auction)
    {
        var remove = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(remove, SyntheticArcaneServer.NpcGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(remove.AsSpan(8), auction);
        return rig.Sender.SendAsync(WorldOpcode.CmsgAuctionRemoveItem, remove, rig.Token);
    }

    private static async Task<(AuctionAction Action, AuctionError Error)> ReadResultAsync(ArcaneCore.MockClient.Scenarios.ScenarioConnection connection, CancellationToken token)
    {
        var reader = new PacketReader(await connection.ReadUntilAsync(WorldOpcode.SmsgAuctionCommandResult, token));
        reader.ReadUInt32();
        return ((AuctionAction)reader.ReadUInt32(), (AuctionError)reader.ReadUInt32());
    }
}

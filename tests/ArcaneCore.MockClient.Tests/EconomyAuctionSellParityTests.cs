using System.Buffers.Binary;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// CMSG_AUCTION_SELL_ITEM against vmangos HandleAuctionSellItem (D:\refs\vmangos\src\game\Handlers\AuctionHouseHandler.cpp:230-405):
/// the 2,000,000,000 client limit (:236), the per-account limit (:274-280), Rate.Auction.Time (:362) and the
/// INVENTORY / ITEM_NOT_FOUND answers (:314-346). Only SQLite is exercised; no store changed.
/// </summary>
public sealed class EconomyAuctionSellParityTests
{
    [Fact]
    public async Task A_price_above_the_client_limit_is_refused_with_not_enough_money()
    {
        await using Rig rig = await Rig.StartAsync();
        ulong item = await GiveItemAsync(rig, SyntheticArcaneServer.FixedRewardItem);
        await SellAsync(rig, item, bid: 2_000_000_001, buyout: 0, minutes: 120);
        Assert.Equal((AuctionError.NotEnoughMoney, InventoryResult.Ok), await ReadResultAsync(rig));
        await SellAsync(rig, item, bid: 2_000_000_000, buyout: 0, minutes: 120);    // exactly the limit is fine
        Assert.Equal((AuctionError.Ok, InventoryResult.Ok), await ReadResultAsync(rig));
    }

    [Fact]
    public async Task A_missing_item_answers_inventory_item_not_found_and_an_empty_guid_item_not_found()
    {
        await using Rig rig = await Rig.StartAsync();
        await SellAsync(rig, 0, bid: 10, buyout: 0, minutes: 120);
        Assert.Equal((AuctionError.ItemNotFound, InventoryResult.Ok), await ReadResultAsync(rig));
        await SellAsync(rig, ObjectGuid.Item(987654).Value, bid: 10, buyout: 0, minutes: 120);
        Assert.Equal((AuctionError.Inventory, InventoryResult.ItemNotFound), await ReadResultAsync(rig));
    }

    [Fact]
    public async Task The_account_limit_refuses_the_next_listing_with_a_message()
    {
        await using Rig rig = await Rig.StartAsync();
        rig.Server.Services.GetRequiredService<EconomyFeature>().Options.AuctionAccountConcurrentLimit = 1;
        ulong first = await GiveItemAsync(rig, SyntheticArcaneServer.FixedRewardItem);
        ulong second = await GiveItemAsync(rig, SyntheticArcaneServer.UnchosenRewardItem);
        await SellAsync(rig, first, 10, 0, 120);
        Assert.Equal(AuctionError.Ok, (await ReadResultAsync(rig)).Error);
        await SellAsync(rig, second, 10, 0, 120);
        var chat = new List<string>();
        Assert.Equal(AuctionError.Database, (await ReadResultAsync(rig, chat)).Error);
        // The system message precedes the command result on the wire.
        Assert.Contains(chat, text => text.Contains("You have reached the limit of active auctions on your account."));
    }

    [Fact]
    public async Task Rate_auction_time_scales_the_listing_duration()
    {
        await using Rig rig = await Rig.StartAsync();
        rig.Server.Services.GetRequiredService<EconomyFeature>().Options.AuctionRateTime = 0.5f;
        long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        ulong item = await GiveItemAsync(rig, SyntheticArcaneServer.FixedRewardItem);
        await SellAsync(rig, item, 10, 0, 1440);
        Assert.Equal(AuctionError.Ok, (await ReadResultAsync(rig)).Error);
        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        AuctionRecord auction = Assert.Single(await new EfEconomyStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).GetAuctionsAsync(rig.Token));
        Assert.Equal(now + (12 * 3600), auction.ExpireTime);
    }

    private static async Task<ulong> GiveItemAsync(Rig rig, uint entry)
        => await rig.Server.World.InvokeAsync(() =>
        {
            Player player = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? added));
            return added!.Guid.Value;
        }).WaitAsync(rig.Token);

    private static Task SellAsync(Rig rig, ulong item, uint bid, uint buyout, uint minutes)
    {
        var payload = new byte[28];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, SyntheticArcaneServer.NpcGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), item);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), bid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), buyout);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), minutes);
        return rig.Sender.SendAsync(WorldOpcode.CmsgAuctionSellItem, payload, rig.Token);
    }

    private static async Task<(AuctionError Error, InventoryResult Inventory)> ReadResultAsync(Rig rig, List<string>? chat = null)
    {
        byte[] payload;
        while (true)
        {
            MockClient.Protocol.WorldFrame frame = await rig.Sender.ReadAsync(rig.Token);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgMessagechat)
            {
                chat?.Add(System.Text.Encoding.UTF8.GetString(frame.Payload));
            }
            else if (frame.Opcode == (ushort)WorldOpcode.SmsgAuctionCommandResult)
            {
                payload = frame.Payload;
                break;
            }
        }

        var reader = new PacketReader(payload);
        reader.ReadUInt32();
        Assert.Equal((uint)AuctionAction.Started, reader.ReadUInt32());
        var error = (AuctionError)reader.ReadUInt32();
        return (error, error == AuctionError.Inventory ? (InventoryResult)reader.ReadByte() : InventoryResult.Ok);
    }
}

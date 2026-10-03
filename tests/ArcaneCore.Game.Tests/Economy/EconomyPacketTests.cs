using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>Byte layouts of the economy server packets (gtker/wow_messages 1.12 definitions).</summary>
public sealed class EconomyPacketTests
{
    private static readonly ItemTemplate Sword = new() { Entry = 25, Name = "Sword", DisplayId = 1542, MaxDurability = 20, LockId = 0 };

    private static uint U32(byte[] p, int at) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(at));

    private static ulong U64(byte[] p, int at) => BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(at));

    [Fact]
    public void SendMailResult_AppendsEquipErrorOrTakenItem()
    {
        Assert.Equal(12, EconomyPackets.SendMailResult(3, MailAction.Send, MailResult.Ok).Length);
        byte[] equip = EconomyPackets.SendMailResult(3, MailAction.Send, MailResult.EquipError, InventoryResult.InventoryFull);
        Assert.Equal((16, 3u, 0u, 1u, (uint)InventoryResult.InventoryFull), (equip.Length, U32(equip, 0), U32(equip, 4), U32(equip, 8), U32(equip, 12)));
        byte[] taken = EconomyPackets.SendMailResult(3, MailAction.ItemTaken, MailResult.Ok, itemGuid: 77, itemCount: 4);
        Assert.Equal((20, 2u, 77u, 4u), (taken.Length, U32(taken, 4), U32(taken, 12), U32(taken, 16)));
        // Any other result of an item take still carries the item fields (wowm SMSG_SEND_MAIL_RESULT).
        Assert.Equal(20, EconomyPackets.SendMailResult(3, MailAction.ItemTaken, MailResult.InternalError).Length);
        Assert.Equal(12, EconomyPackets.SendMailResult(3, MailAction.Deleted, MailResult.InternalError).Length);
    }

    [Fact]
    public void MailList_WritesSenderByTypeAndItemFields()
    {
        var item = new ItemInstanceData
        {
            Guid = 9, Entry = 25, Count = 3, Durability = 12, RandomPropertyId = 5,
            Charges = [-1, 0, 0, 0, 0], Enchantments = [7, .. new uint[20]],
        };
        var normal = new MailRecord
        {
            Id = 1, MessageType = MailMessageType.Normal, SenderId = 42, ReceiverId = 2, Subject = "hi", ItemTextId = 6,
            ItemGuid = 9, ItemEntry = 25, Money = 100, Cod = 50, Checked = MailCheckMask.Read, ExpireTime = 1000 + 86400,
        };
        var auction = new MailRecord { Id = 2, MessageType = MailMessageType.Auction, SenderId = 7, Subject = "", ExpireTime = 1000 };
        byte[] p = EconomyPackets.MailList([new MailView(normal, item), new MailView(auction, null)], 1000, e => e == 25 ? Sword : null);

        Assert.Equal(2, p[0]);
        int at = 1;
        Assert.Equal(1u, U32(p, at));
        Assert.Equal((byte)MailMessageType.Normal, p[at + 4]);
        Assert.Equal(42ul, U64(p, at + 5));
        at += 13;
        Assert.Equal("hi\0", Encoding.UTF8.GetString(p, at, 3));
        at += 3;
        Assert.Equal((6u, 0u, MailStationery.Default, 25u, 7u, 5u, 0u), (U32(p, at), U32(p, at + 4), U32(p, at + 8), U32(p, at + 12), U32(p, at + 16), U32(p, at + 20), U32(p, at + 24)));
        at += 28;
        Assert.Equal(3, p[at]);
        at += 1;
        Assert.Equal((uint.MaxValue, 20u, 12u, 100u, 50u, (uint)MailCheckMask.Read), (U32(p, at), U32(p, at + 4), U32(p, at + 8), U32(p, at + 12), U32(p, at + 16), U32(p, at + 20)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(at + 24)));
        Assert.Equal(0u, U32(p, at + 28));
        at += 32;

        // An auction letter carries the house id as a u32 sender.
        Assert.Equal((2u, (byte)MailMessageType.Auction, 7u), (U32(p, at), p[at + 4], U32(p, at + 5)));
        at += 9;
        Assert.Equal(0, p[at]);
        Assert.Equal(at + 1 + 28 + 1 + 32, p.Length);
    }

    [Fact]
    public void SmallMailPackets()
    {
        Assert.Equal([0, 0, 0, 0], EconomyPackets.ReceivedMail());
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(EconomyPackets.NextMailTime(true)));
        Assert.Equal(-86400f, BinaryPrimitives.ReadSingleLittleEndian(EconomyPackets.NextMailTime(false)));
        byte[] text = EconomyPackets.ItemTextResponse(5, "abc");
        Assert.Equal((5u, "abc\0"), (U32(text, 0), Encoding.UTF8.GetString(text, 4, 4)));
        byte[] hello = EconomyPackets.AuctionHello(0xF130000000000001, 7);
        Assert.Equal((12, 0xF130000000000001, 7u), (hello.Length, U64(hello, 0), U32(hello, 8)));
    }

    [Fact]
    public void AuctionCommandResult_VariesByActionAndError()
    {
        var auction = new AuctionRecord { Id = 4, BidderId = 9, Bid = 200 };
        Assert.Equal(12, EconomyPackets.AuctionCommandResult(4, AuctionAction.Started, AuctionError.Ok).Length);
        byte[] bid = EconomyPackets.AuctionCommandResult(4, AuctionAction.BidPlaced, AuctionError.Ok, auction: auction);
        Assert.Equal((16, 10u), (bid.Length, U32(bid, 12)));
        byte[] inventory = EconomyPackets.AuctionCommandResult(4, AuctionAction.Started, AuctionError.Inventory, InventoryResult.InventoryFull);
        Assert.Equal((13, (byte)InventoryResult.InventoryFull), (inventory.Length, inventory[12]));
        byte[] higher = EconomyPackets.AuctionCommandResult(4, AuctionAction.BidPlaced, AuctionError.HigherBid, auction: auction);
        Assert.Equal((28, 9ul, 200u, 10u), (higher.Length, U64(higher, 12), U32(higher, 20), U32(higher, 24)));
    }

    [Fact]
    public void AuctionList_WritesSixtyFourBytesPerAuctionAndTheTotal()
    {
        var auction = new AuctionRecord { Id = 3, ItemEntry = 25, SellerId = 5, StartBid = 10, Buyout = 90, ExpireTime = 1060, BidderId = 6, Bid = 40 };
        var item = new ItemInstanceData { Guid = 1, Entry = 25, Count = 2, Charges = [3, 0, 0, 0, 0], Enchantments = new uint[21] };
        byte[] p = EconomyPackets.AuctionList([new AuctionView(auction, item)], 77, 1000);
        Assert.Equal(4 + 64 + 4, p.Length);
        Assert.Equal((1u, 3u, 25u, 2u, 3u), (U32(p, 0), U32(p, 4), U32(p, 8), U32(p, 24), U32(p, 28)));
        Assert.Equal(5ul, U64(p, 32));
        Assert.Equal((10u, 1u, 90u, 60_000u), (U32(p, 40), U32(p, 44), U32(p, 48), U32(p, 52)));
        Assert.Equal((6ul, 40u, 77u), (U64(p, 56), U32(p, 64), U32(p, 68)));

        byte[] unbid = EconomyPackets.AuctionList([new AuctionView(auction with { BidderId = 0, Bid = 0, ExpireTime = 1 }, item)], 1, 1000);
        Assert.Equal((0u, 0u, 0ul), (U32(unbid, 44), U32(unbid, 52), U64(unbid, 56)));
    }

    [Fact]
    public void AuctionNotifications()
    {
        var auction = new AuctionRecord { Id = 3, HouseId = 7, ItemEntry = 25, BidderId = 6, Bid = 100 };
        byte[] won = EconomyPackets.BidderNotification(auction, won: true, randomPropertyId: 4);
        Assert.Equal((32, 7u, 3u, 6ul, 0u, 5u, 25u, 4u), (won.Length, U32(won, 0), U32(won, 4), U64(won, 8), U32(won, 16), U32(won, 20), U32(won, 24), U32(won, 28)));
        Assert.Equal(100u, U32(EconomyPackets.BidderNotification(auction, won: false), 16));
        byte[] owner = EconomyPackets.OwnerNotification(auction, sold: true);
        Assert.Equal((28, 3u, 100u, 0ul), (owner.Length, U32(owner, 0), U32(owner, 4), U64(owner, 12)));
        Assert.Equal(6ul, U64(EconomyPackets.OwnerNotification(auction, sold: false), 12));
        Assert.Equal([25, 0, 0, 0, 25, 0, 0, 0, 0, 0, 0, 0], EconomyPackets.RemovedNotification(25));
    }

    [Fact]
    public void TradeStatus_ExtraFieldsOnlyForBeginAndCloseWindow()
    {
        Assert.Equal(4, EconomyPackets.TradeStatus(TradeStatus.OpenWindow).Length);
        byte[] begin = EconomyPackets.TradeStatus(TradeStatus.BeginTrade, 99);
        Assert.Equal((12, 1u, 99ul), (begin.Length, U32(begin, 0), U64(begin, 4)));
        byte[] close = EconomyPackets.TradeStatus(TradeStatus.CloseWindow, inventoryResult: InventoryResult.InventoryFull, targetError: true);
        Assert.Equal((13, 12u, (uint)InventoryResult.InventoryFull, (byte)1, 0u), (close.Length, U32(close, 0), U32(close, 4), close[8], U32(close, 9)));
    }

    [Fact]
    public void TradeStatusExtended_SevenSlotsOfSixtyOneBytes()
    {
        var item = new ItemInstanceData
        {
            Guid = 1, Entry = 25, Count = 2, Creator = 8, GiftCreator = 0, Flags = (uint)ItemDynFlags.Wrapped, Durability = 9,
            Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21],
        };
        byte[] p = EconomyPackets.TradeStatusExtended(true, 500, [item, null, null, null, null, null, null], e => e == 25 ? Sword : null);
        Assert.Equal(17 + (7 * 61), p.Length);
        Assert.Equal((1, 7u, 7u, 500u, 0u), (p[0], U32(p, 1), U32(p, 5), U32(p, 9), U32(p, 13)));
        int at = 17;
        Assert.Equal(0, p[at]);
        Assert.Equal((25u, 1542u, 2u, 1u), (U32(p, at + 1), U32(p, at + 5), U32(p, at + 9), U32(p, at + 13)));
        Assert.Equal(8ul, U64(p, at + 1 + 16 + 8 + 4));
        Assert.Equal((20u, 9u), (U32(p, at + 53), U32(p, at + 57)));
        Assert.Equal(1, p[at + 61]);
        Assert.All(p.Skip(at + 62).Take(60), b => Assert.Equal(0, b));
        Assert.Equal(6, p[17 + (6 * 61)]);
    }
}

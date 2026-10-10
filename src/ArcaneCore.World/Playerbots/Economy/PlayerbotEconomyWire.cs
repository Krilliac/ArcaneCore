using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots;

/// <summary>One auction of SMSG_AUCTION_LIST_RESULT / SMSG_AUCTION_OWNER_LIST_RESULT with every field the bot reads.</summary>
/// <param name="Owner">The seller's player GUID (a seller-less auction house bot listing carries the zero seller).</param>
/// <param name="Buyout">0 = a bid-only auction.</param>
internal readonly record struct PlayerbotAuctionRow(uint Id, uint Entry, int RandomProperty, uint Count, ulong Owner, uint StartBid,
    uint MinOutbid, uint Buyout, uint MsLeft, ulong Bidder, uint Bid);

/// <summary>A decoded auction list: the rows of the page and the total the house matched.</summary>
internal sealed record PlayerbotAuctionPage(IReadOnlyList<PlayerbotAuctionRow> Rows, uint Total);

/// <summary>SMSG_AUCTION_COMMAND_RESULT up to the error code (the tail by error kind is not needed).</summary>
internal readonly record struct PlayerbotAuctionResult(uint AuctionId, AuctionAction Action, AuctionError Error);

/// <summary>One letter of SMSG_MAIL_LIST_RESULT.</summary>
/// <param name="Sender">The sender player GUID for a player's letter, else the auction house id / creature or object entry.</param>
internal sealed record PlayerbotMailLetter(uint Id, MailMessageType Type, ulong Sender, string Subject, uint ItemEntry, uint ItemCount,
    uint Money, uint Cod, MailCheckMask Checked)
{
    public bool IsRead => (Checked & MailCheckMask.Read) != 0;
}

/// <summary>SMSG_SEND_MAIL_RESULT up to the result code.</summary>
internal readonly record struct PlayerbotMailResult(uint MailId, MailAction Action, MailResult Result);

/// <summary>
/// The auction house and mailbox wire of the economy bot: client payloads in the layouts <c>EconomyHandlers</c> parses (gtker/wow_messages
/// 1.12 cmsg_auction_*, cmsg_get_mail_list, cmsg_mail_*) and decoders of the replies <c>EconomyPackets</c> writes. A reply that is shorter than
/// its layout throws <see cref="FormatException"/>, never another exception type.
/// </summary>
internal static class PlayerbotEconomyWire
{
    /// <summary>The "any" value of a CMSG_AUCTION_LIST_ITEMS filter (<see cref="AuctionSearch.Any"/>).</summary>
    internal const uint Any = AuctionSearch.Any;

    /// <summary>The most rows an auction list reply may carry (<see cref="AuctionHouseRules.PageSize"/>).</summary>
    internal const int MaxRows = AuctionHouseRules.PageSize;

    // --- client payloads -----------------------------------------------------------------------------------------------

    /// <summary>
    /// CMSG_AUCTION_LIST_ITEMS (EconomyHandlers.HandleListItems): u64 auctioneer, u32 list start, cstring name, u8 min level, u8 max level,
    /// u32 inventory type, u32 class, u32 subclass, u32 quality, u8 usable.
    /// </summary>
    internal static byte[] ListItems(ulong auctioneer, uint listFrom, string name, byte levelMin, byte levelMax, uint inventoryType,
        uint itemClass, uint itemSubClass, uint quality, bool usable)
    {
        var w = new PacketWriter(48 + name.Length);
        w.WriteUInt64(auctioneer);
        w.WriteUInt32(listFrom);
        w.WriteCString(name);
        w.WriteByte(levelMin);
        w.WriteByte(levelMax);
        w.WriteUInt32(inventoryType);
        w.WriteUInt32(itemClass);
        w.WriteUInt32(itemSubClass);
        w.WriteUInt32(quality);
        w.WriteByte(usable ? (byte)1 : (byte)0);
        return w.ToArray();
    }

    /// <summary>CMSG_AUCTION_LIST_OWNER_ITEMS: u64 auctioneer, u32 list start.</summary>
    internal static byte[] ListOwnerItems(ulong auctioneer, uint listFrom)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(auctioneer);
        w.WriteUInt32(listFrom);
        return w.ToArray();
    }

    /// <summary>CMSG_AUCTION_SELL_ITEM: u64 auctioneer, u64 item, u32 start bid, u32 buyout, u32 duration in minutes.</summary>
    internal static byte[] SellItem(ulong auctioneer, ulong item, uint startBid, uint buyout, uint minutes)
    {
        var w = new PacketWriter(28);
        w.WriteUInt64(auctioneer);
        w.WriteUInt64(item);
        w.WriteUInt32(startBid);
        w.WriteUInt32(buyout);
        w.WriteUInt32(minutes);
        return w.ToArray();
    }

    /// <summary>CMSG_AUCTION_PLACE_BID: u64 auctioneer, u32 auction id, u32 price (a price at or above the buyout buys it out).</summary>
    internal static byte[] PlaceBid(ulong auctioneer, uint auctionId, uint price)
    {
        var w = new PacketWriter(16);
        w.WriteUInt64(auctioneer);
        w.WriteUInt32(auctionId);
        w.WriteUInt32(price);
        return w.ToArray();
    }

    /// <summary>CMSG_GET_MAIL_LIST: u64 mailbox.</summary>
    internal static byte[] GetMailList(ulong mailbox)
    {
        var w = new PacketWriter(8);
        w.WriteUInt64(mailbox);
        return w.ToArray();
    }

    /// <summary>CMSG_MAIL_TAKE_ITEM / CMSG_MAIL_TAKE_MONEY / CMSG_MAIL_MARK_AS_READ / CMSG_MAIL_DELETE (EconomyHandlers.MailCommand): u64 mailbox, u32 mail id.</summary>
    internal static byte[] MailCommand(ulong mailbox, uint mailId)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(mailbox);
        w.WriteUInt32(mailId);
        return w.ToArray();
    }

    // --- server replies ------------------------------------------------------------------------------------------------

    /// <summary>
    /// SMSG_AUCTION_LIST_RESULT / SMSG_AUCTION_OWNER_LIST_RESULT (EconomyPackets.AuctionList): u32 count, per row u32 id, u32 entry, u32 enchant,
    /// u32 random property, u32 suffix factor, u32 count, u32 charges, u64 owner, u32 start bid, u32 minimum outbid, u32 buyout, u32 ms left,
    /// u64 bidder, u32 bid; then u32 total.
    /// </summary>
    internal static PlayerbotAuctionPage DecodeAuctionList(byte[] payload) => Decode(payload, nameof(DecodeAuctionList), static data =>
    {
        var r = new PacketReader(data);
        uint count = r.ReadUInt32();
        if (count > MaxRows) throw new FormatException("auction list row count");
        var rows = new List<PlayerbotAuctionRow>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint id = r.ReadUInt32();
            uint entry = r.ReadUInt32();
            _ = r.ReadUInt32();
            int property = r.ReadInt32();
            _ = r.ReadUInt32();
            uint stack = r.ReadUInt32();
            _ = r.ReadUInt32();
            ulong owner = r.ReadUInt64();
            uint startBid = r.ReadUInt32();
            uint minOutbid = r.ReadUInt32();
            uint buyout = r.ReadUInt32();
            uint msLeft = r.ReadUInt32();
            ulong bidder = r.ReadUInt64();
            uint bid = r.ReadUInt32();
            rows.Add(new PlayerbotAuctionRow(id, entry, property, stack, owner, startBid, minOutbid, buyout, msLeft, bidder, bid));
        }

        return new PlayerbotAuctionPage(rows, r.ReadUInt32());
    });

    /// <summary>SMSG_AUCTION_COMMAND_RESULT (EconomyPackets.AuctionCommandResult): u32 auction id, u32 action, u32 error, then a tail by error kind.</summary>
    internal static PlayerbotAuctionResult DecodeCommandResult(byte[] payload) => Decode(payload, nameof(DecodeCommandResult), static data =>
    {
        var r = new PacketReader(data);
        uint id = r.ReadUInt32();
        var action = (AuctionAction)r.ReadUInt32();
        return new PlayerbotAuctionResult(id, action, (AuctionError)r.ReadUInt32());
    });

    /// <summary>
    /// SMSG_MAIL_LIST_RESULT (EconomyPackets.MailList): u8 count, per letter u32 id, u8 type, the sender (u64 for a player's letter, u32 for a
    /// creature, object or auction house letter, nothing for other types), cstring subject, u32 text id, u32 0, u32 stationery, u32 item entry,
    /// u32 enchant, u32 random property, u32 0, u8 item count, u32 charges, u32 max durability, u32 durability, u32 money, u32 cod, u32 checked,
    /// float days left, u32 0.
    /// </summary>
    internal static IReadOnlyList<PlayerbotMailLetter> DecodeMailList(byte[] payload) => Decode(payload, nameof(DecodeMailList), static data =>
    {
        var r = new PacketReader(data);
        int count = r.ReadByte();
        var letters = new List<PlayerbotMailLetter>(count);
        for (int i = 0; i < count; i++)
        {
            uint id = r.ReadUInt32();
            var type = (MailMessageType)r.ReadByte();
            ulong sender = type switch
            {
                MailMessageType.Normal => r.ReadUInt64(),
                MailMessageType.Creature or MailMessageType.GameObject or MailMessageType.Auction => r.ReadUInt32(),
                _ => 0,
            };
            string subject = r.ReadCString();
            _ = r.ReadUInt32();
            _ = r.ReadUInt32();
            _ = r.ReadUInt32();
            uint itemEntry = r.ReadUInt32();
            _ = r.ReadUInt32();
            _ = r.ReadUInt32();
            _ = r.ReadUInt32();
            uint itemCount = r.ReadByte();
            _ = r.ReadUInt32();
            _ = r.ReadUInt32();
            _ = r.ReadUInt32();
            uint money = r.ReadUInt32();
            uint cod = r.ReadUInt32();
            var state = (MailCheckMask)r.ReadUInt32();
            _ = r.ReadSingle();
            _ = r.ReadUInt32();
            letters.Add(new PlayerbotMailLetter(id, type, sender, subject, itemEntry, itemCount, money, cod, state));
        }

        return (IReadOnlyList<PlayerbotMailLetter>)letters;
    });

    /// <summary>SMSG_SEND_MAIL_RESULT (EconomyPackets.SendMailResult): u32 mail id, u32 action, u32 result, then an equip error or the taken item.</summary>
    internal static PlayerbotMailResult DecodeMailResult(byte[] payload) => Decode(payload, nameof(DecodeMailResult), static data =>
    {
        var r = new PacketReader(data);
        uint id = r.ReadUInt32();
        var action = (MailAction)r.ReadUInt32();
        return new PlayerbotMailResult(id, action, (MailResult)r.ReadUInt32());
    });

    /// <summary>MSG_QUERY_NEXT_MAIL_TIME server form (EconomyPackets.NextMailTime): a float, 0 when an unread delivered letter waits, else -1 day.</summary>
    internal static bool DecodeHasUnreadMail(byte[] payload) => Decode(payload, nameof(DecodeHasUnreadMail), static data =>
        new PacketReader(data).ReadSingle() == 0f);

    private static T Decode<T>(byte[] payload, string what, Func<byte[], T> decode)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            return decode(payload);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new FormatException($"malformed {what} payload ({payload.Length} bytes)", e);
        }
    }
}

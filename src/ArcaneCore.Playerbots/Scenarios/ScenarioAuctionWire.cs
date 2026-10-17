using ArcaneCore.Game.Economy;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>SMSG_AUCTION_COMMAND_RESULT (EconomyPackets.AuctionCommandResult) up to the error code.</summary>
public sealed record AuctionCommandResultView(uint AuctionId, AuctionAction Action, AuctionError Error);

/// <summary>SMSG_AUCTION_BIDDER_NOTIFICATION (EconomyPackets.BidderNotification): a zero bid means "won", any other "outbid".</summary>
public sealed record AuctionBidderNotificationView(uint HouseId, uint AuctionId, ulong Bidder, uint Bid, uint OutBid, uint ItemEntry, int RandomPropertyId);

/// <summary>One row of SMSG_AUCTION_LIST_RESULT / OWNER_LIST_RESULT / BIDDER_LIST_RESULT (EconomyPackets.AuctionList).</summary>
public sealed record AuctionListRowView(uint AuctionId, uint ItemEntry, int RandomPropertyId, ulong Bidder, uint Bid);

/// <summary>The three auction list results: the rows of the page and the total.</summary>
public sealed record AuctionListView(IReadOnlyList<AuctionListRowView> Rows, uint Total);

/// <summary>
/// Auction-house wire of the scenario harness (kept apart from <see cref="ScenarioPackets"/> / <see cref="ScenarioDecoders"/>): client payloads in
/// the layouts EconomyHandlers parses (gtker/wow_messages 1.12 cmsg_auction_*) and decoders mirroring EconomyPackets.
/// </summary>
public static class ScenarioAuctionWire
{
    /// <summary>CMSG_AUCTION_SELL_ITEM: u64 auctioneer, u64 item, u32 start bid, u32 buyout, u32 duration in minutes.</summary>
    public static byte[] Sell(ulong auctioneer, ulong item, uint bid, uint buyout, uint minutes)
    {
        var w = new PacketWriter(28);
        w.WriteUInt64(auctioneer);
        w.WriteUInt64(item);
        w.WriteUInt32(bid);
        w.WriteUInt32(buyout);
        w.WriteUInt32(minutes);
        return w.ToArray();
    }

    /// <summary>CMSG_AUCTION_PLACE_BID: u64 auctioneer, u32 auction id, u32 price.</summary>
    public static byte[] PlaceBid(ulong auctioneer, uint auctionId, uint price)
    {
        var w = new PacketWriter(16);
        w.WriteUInt64(auctioneer);
        w.WriteUInt32(auctionId);
        w.WriteUInt32(price);
        return w.ToArray();
    }

    /// <summary>CMSG_AUCTION_REMOVE_ITEM: u64 auctioneer, u32 auction id.</summary>
    public static byte[] Remove(ulong auctioneer, uint auctionId)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(auctioneer);
        w.WriteUInt32(auctionId);
        return w.ToArray();
    }

    /// <summary>
    /// CMSG_AUCTION_LIST_BIDDER_ITEMS (wow_messages cmsg_auction_list_bidder_items.wowm): u64 auctioneer, u32 start, u32 count of outbid
    /// auction ids, then the ids (the client asks again for auctions it was outbid on).
    /// </summary>
    public static byte[] ListBidderItems(ulong auctioneer, uint listFrom, params uint[] outbidIds)
    {
        var w = new PacketWriter(16 + (outbidIds.Length * 4));
        w.WriteUInt64(auctioneer);
        w.WriteUInt32(listFrom);
        w.WriteUInt32((uint)outbidIds.Length);
        foreach (uint id in outbidIds)
        {
            w.WriteUInt32(id);
        }

        return w.ToArray();
    }

    public static AuctionCommandResultView CommandResult(byte[] payload) => Decode(payload, nameof(CommandResult), static data =>
    {
        var r = new PacketReader(data);
        uint id = r.ReadUInt32();
        var action = (AuctionAction)r.ReadUInt32();
        return new AuctionCommandResultView(id, action, (AuctionError)r.ReadUInt32());
    });

    public static AuctionBidderNotificationView BidderNotification(byte[] payload) => Decode(payload, nameof(BidderNotification), static data =>
    {
        var r = new PacketReader(data);
        uint house = r.ReadUInt32();
        uint id = r.ReadUInt32();
        ulong bidder = r.ReadUInt64();
        uint bid = r.ReadUInt32();
        uint outBid = r.ReadUInt32();
        uint entry = r.ReadUInt32();
        return new AuctionBidderNotificationView(house, id, bidder, bid, outBid, entry, unchecked((int)r.ReadUInt32()));
    });

    /// <summary>Count, then per row: id, entry, enchant, random property, suffix, count, charges, owner, start bid, outbid, buyout, ms left, bidder, bid; total.</summary>
    public static AuctionListView List(byte[] payload) => Decode(payload, nameof(List), static data =>
    {
        var r = new PacketReader(data);
        uint count = r.ReadUInt32();
        if (count > 50) throw new FormatException("auction list row count");
        var rows = new List<AuctionListRowView>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint id = r.ReadUInt32();
            uint entry = r.ReadUInt32();
            r.ReadUInt32();
            int property = unchecked((int)r.ReadUInt32());
            r.ReadUInt32();
            r.ReadUInt32();
            r.ReadUInt32();
            r.ReadUInt64();
            r.ReadUInt32();
            r.ReadUInt32();
            r.ReadUInt32();
            r.ReadUInt32();
            ulong bidder = r.ReadUInt64();
            rows.Add(new AuctionListRowView(id, entry, property, bidder, r.ReadUInt32()));
        }

        return new AuctionListView(rows, r.ReadUInt32());
    });

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

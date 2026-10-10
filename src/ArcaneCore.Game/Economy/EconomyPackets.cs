using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Economy;

/// <summary>A letter with the escrowed item it carries, as the mailbox shows it.</summary>
public sealed record MailView(MailRecord Mail, ItemInstanceData? Item)
{
    /// <summary>The body text a new letter creates (only set while it is being sent).</summary>
    public string? Body { get; init; }
}

/// <summary>A listed auction with its escrowed item.</summary>
public sealed record AuctionView(AuctionRecord Auction, ItemInstanceData Item);

/// <summary>
/// Server packets of mail, auction house and trade, byte-for-byte per the 1.12.1 layouts
/// described by gtker/wow_messages (MIT): world/mail, world/auction, world/trade.
/// </summary>
public static class EconomyPackets
{
    /// <summary>SMSG_SEND_MAIL_RESULT: id, action, result, then the equip error or the taken item (wow_messages 1.12).</summary>
    public static byte[] SendMailResult(uint mailId, MailAction action, MailResult result,
        InventoryResult equipError = InventoryResult.Ok, uint itemGuid = 0, uint itemCount = 0)
    {
        var w = new PacketWriter(24);
        w.WriteUInt32(mailId);
        w.WriteUInt32((uint)action);
        w.WriteUInt32((uint)result);
        if (result == MailResult.EquipError)
        {
            w.WriteUInt32((uint)equipError);
        }
        else if (action == MailAction.ItemTaken)
        {
            w.WriteUInt32(itemGuid);
            w.WriteUInt32(itemCount);
        }

        return w.ToArray();
    }

    /// <summary>Most letters sent in one list (vmangos MailHandler.cpp:758-766: 254, the count is one byte).</summary>
    public const int MaxMailListEntries = 254;

    /// <summary>SMSG_MAIL_LIST_RESULT: u8 count, then each letter (at most <see cref="MaxMailListEntries"/>; the client shows 50).</summary>
    public static byte[] MailList(IReadOnlyList<MailView> mails, long now, Func<uint, ItemTemplate?> templates)
    {
        var w = new PacketWriter(16 + (mails.Count * 96));
        int count = Math.Min(mails.Count, MaxMailListEntries);
        w.WriteByte((byte)count);
        foreach (MailView view in mails.Take(count))
        {
            MailRecord mail = view.Mail;
            w.WriteUInt32(mail.Id);
            w.WriteByte((byte)mail.MessageType);
            switch (mail.MessageType)
            {
                case MailMessageType.Normal:
                    w.WriteUInt64(ObjectGuid.Player(mail.SenderId).Value);
                    break;
                case MailMessageType.Creature or MailMessageType.GameObject or MailMessageType.Auction:
                    w.WriteUInt32(mail.SenderId);
                    break;
            }

            ItemInstanceData? item = view.Item;
            ItemTemplate? template = item is null ? null : templates(item.Entry);
            w.WriteCString(mail.Subject);
            w.WriteUInt32(mail.ItemTextId);
            w.WriteUInt32(0);
            w.WriteUInt32(mail.Stationery);
            w.WriteUInt32(item?.Entry ?? 0);
            w.WriteUInt32(item is { Enchantments.Count: > 0 } ? item.Enchantments[0] : 0);
            w.WriteUInt32(item is null ? 0 : unchecked((uint)item.RandomPropertyId));
            w.WriteUInt32(0);
            w.WriteByte((byte)Math.Min(item?.Count ?? 0, byte.MaxValue));
            w.WriteUInt32(item is { Charges.Count: > 0 } ? unchecked((uint)item.Charges[0]) : 0);
            w.WriteUInt32(template?.MaxDurability ?? 0);
            w.WriteUInt32(item?.Durability ?? 0);
            w.WriteUInt32(mail.Money);
            w.WriteUInt32(mail.Cod);
            w.WriteUInt32((uint)mail.Checked);
            w.WriteSingle(MailRules.DaysLeft(mail, now));
            w.WriteUInt32(0);
        }

        return w.ToArray();
    }

    /// <summary>SMSG_RECEIVED_MAIL: u32 0 (wow_messages: "unknown", vmangos sends 0).</summary>
    public static byte[] ReceivedMail() => [0, 0, 0, 0];

    /// <summary>MSG_QUERY_NEXT_MAIL_TIME server form: 0 when unread mail waits, else -1 day (vmangos).</summary>
    public static byte[] NextMailTime(bool hasUnread)
    {
        var w = new PacketWriter(4);
        w.WriteSingle(hasUnread ? 0f : -(float)MailRules.SecondsPerDay);
        return w.ToArray();
    }

    /// <summary>SMSG_ITEM_TEXT_QUERY_RESPONSE: text id, text.</summary>
    public static byte[] ItemTextResponse(uint textId, string text)
    {
        var w = new PacketWriter(8 + text.Length);
        w.WriteUInt32(textId);
        w.WriteCString(text);
        return w.ToArray();
    }

    /// <summary>MSG_AUCTION_HELLO server form: auctioneer GUID and house id.</summary>
    public static byte[] AuctionHello(ulong auctioneer, uint houseId)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(auctioneer);
        w.WriteUInt32(houseId);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_AUCTION_COMMAND_RESULT (wow_messages 1.12). The inventory result is one byte as
    /// wow_messages types it (InventoryResult : u8).
    /// </summary>
    public static byte[] AuctionCommandResult(uint auctionId, AuctionAction action, AuctionError error,
        InventoryResult inventoryResult = InventoryResult.Ok, AuctionRecord? auction = null)
    {
        var w = new PacketWriter(32);
        w.WriteUInt32(auctionId);
        w.WriteUInt32((uint)action);
        w.WriteUInt32((uint)error);
        switch (error)
        {
            case AuctionError.Ok when action == AuctionAction.BidPlaced:
                w.WriteUInt32(auction is null ? 0 : AuctionHouseRules.OutBid(auction.Bid));
                break;
            case AuctionError.Inventory:
                w.WriteByte((byte)inventoryResult);
                break;
            case AuctionError.HigherBid:
                w.WriteUInt64(auction is { BidderId: > 0 } ? ObjectGuid.Player((uint)auction.BidderId).Value : 0);
                w.WriteUInt32(auction?.Bid ?? 0);
                w.WriteUInt32(auction is null ? 0 : AuctionHouseRules.OutBid(auction.Bid));
                break;
        }

        return w.ToArray();
    }

    /// <summary>SMSG_AUCTION_LIST_RESULT / OWNER_LIST_RESULT / BIDDER_LIST_RESULT: count, items, total.</summary>
    public static byte[] AuctionList(IReadOnlyList<AuctionView> page, int total, long now)
    {
        var w = new PacketWriter(12 + (page.Count * 64));
        w.WriteUInt32((uint)page.Count);
        foreach (AuctionView view in page)
        {
            AuctionRecord a = view.Auction;
            ItemInstanceData item = view.Item;
            w.WriteUInt32(a.Id);
            w.WriteUInt32(a.ItemEntry);
            w.WriteUInt32(item.Enchantments.Count > 0 ? item.Enchantments[0] : 0);
            w.WriteUInt32(unchecked((uint)item.RandomPropertyId));
            w.WriteUInt32(0);
            w.WriteUInt32(item.Count);
            w.WriteUInt32(item.Charges.Count > 0 ? unchecked((uint)item.Charges[0]) : 0);
            w.WriteUInt64(ObjectGuid.Player((uint)a.SellerId).Value);
            w.WriteUInt32(a.StartBid);
            w.WriteUInt32(a.BidderId != 0 || a.Bid > 0 ? AuctionHouseRules.OutBid(a.Bid) : 0);
            w.WriteUInt32(a.Buyout);
            w.WriteUInt32((uint)Math.Clamp((a.ExpireTime - now) * 1000, 0, uint.MaxValue));
            w.WriteUInt64(a.BidderId != 0 ? ObjectGuid.Player((uint)a.BidderId).Value : 0);
            w.WriteUInt32(a.Bid);
        }

        w.WriteUInt32((uint)total);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_AUCTION_BIDDER_NOTIFICATION (1.12): the client shows "won" when the bid field is 0,
    /// else "outbid" (vmangos SendAuctionBidderNotification semantics).
    /// </summary>
    public static byte[] BidderNotification(AuctionRecord auction, bool won, int randomPropertyId = 0)
    {
        var w = new PacketWriter(36);
        w.WriteUInt32(auction.HouseId);
        w.WriteUInt32(auction.Id);
        w.WriteUInt64(auction.BidderId != 0 ? ObjectGuid.Player((uint)auction.BidderId).Value : 0);
        w.WriteUInt32(won ? 0 : auction.Bid);
        w.WriteUInt32(AuctionHouseRules.OutBid(auction.Bid));
        w.WriteUInt32(auction.ItemEntry);
        w.WriteUInt32(unchecked((uint)randomPropertyId));
        return w.ToArray();
    }

    /// <summary>SMSG_AUCTION_OWNER_NOTIFICATION (1.12): a sold auction carries no bidder GUID ("your auction sold").</summary>
    public static byte[] OwnerNotification(AuctionRecord auction, bool sold, int randomPropertyId = 0)
    {
        var w = new PacketWriter(32);
        w.WriteUInt32(auction.Id);
        w.WriteUInt32(auction.Bid);
        w.WriteUInt32(AuctionHouseRules.OutBid(auction.Bid));
        w.WriteUInt64(!sold && auction.BidderId != 0 ? ObjectGuid.Player((uint)auction.BidderId).Value : 0);
        w.WriteUInt32(auction.ItemEntry);
        w.WriteUInt32(unchecked((uint)randomPropertyId));
        return w.ToArray();
    }

    /// <summary>SMSG_AUCTION_REMOVED_NOTIFICATION: item entry twice and the random property.</summary>
    public static byte[] RemovedNotification(uint itemEntry, int randomPropertyId = 0)
    {
        var w = new PacketWriter(12);
        w.WriteUInt32(itemEntry);
        w.WriteUInt32(itemEntry);
        w.WriteUInt32(unchecked((uint)randomPropertyId));
        return w.ToArray();
    }

    /// <summary>SMSG_TRADE_STATUS. BEGIN_TRADE carries a GUID; CLOSE_WINDOW an inventory result, target flag and 0.</summary>
    public static byte[] TradeStatus(TradeStatus status, ulong guid = 0, InventoryResult inventoryResult = InventoryResult.Ok, bool targetError = false)
    {
        var w = new PacketWriter(16);
        w.WriteUInt32((uint)status);
        if (status == Economy.TradeStatus.BeginTrade)
        {
            w.WriteUInt64(guid);
        }
        else if (status == Economy.TradeStatus.CloseWindow)
        {
            w.WriteUInt32((uint)inventoryResult);
            w.WriteByte(targetError ? (byte)1 : (byte)0);
            w.WriteUInt32(0);
        }

        return w.ToArray();
    }

    /// <summary>
    /// SMSG_TRADE_STATUS_EXTENDED: whose window, slot counts (7, 7), gold, spell, then 7 slots
    /// of u8 index + 60 bytes (wow_messages TradeSlot 1.12; empty slots are 15 zero u32s).
    /// </summary>
    public static byte[] TradeStatusExtended(bool traderWindow, uint gold, IReadOnlyList<ItemInstanceData?> slots, Func<uint, ItemTemplate?> templates, uint spellId = 0)
    {
        var w = new PacketWriter(17 + (TradeRules.SlotCount * 61));
        w.WriteByte(traderWindow ? (byte)1 : (byte)0);
        w.WriteUInt32(TradeRules.SlotCount);
        w.WriteUInt32(TradeRules.SlotCount);
        w.WriteUInt32(gold);
        w.WriteUInt32(spellId);
        for (int i = 0; i < TradeRules.SlotCount; i++)
        {
            w.WriteByte((byte)i);
            ItemInstanceData? item = i < slots.Count ? slots[i] : null;
            if (item is null)
            {
                for (int j = 0; j < 15; j++)
                {
                    w.WriteUInt32(0);
                }

                continue;
            }

            ItemTemplate? template = templates(item.Entry);
            w.WriteUInt32(item.Entry);
            w.WriteUInt32(template?.DisplayId ?? 0);
            w.WriteUInt32(item.Count);
            w.WriteUInt32((item.Flags & (uint)ItemDynFlags.Wrapped) != 0 ? 1u : 0u);
            w.WriteUInt64(item.GiftCreator);
            w.WriteUInt32(item.Enchantments.Count > 0 ? item.Enchantments[0] : 0);
            w.WriteUInt64(item.Creator);
            w.WriteUInt32(item.Charges.Count > 0 ? unchecked((uint)item.Charges[0]) : 0);
            w.WriteUInt32(0);
            w.WriteUInt32(unchecked((uint)item.RandomPropertyId));
            w.WriteUInt32(template?.LockId ?? 0);
            w.WriteUInt32(template?.MaxDurability ?? 0);
            w.WriteUInt32(item.Durability);
        }

        return w.ToArray();
    }
}

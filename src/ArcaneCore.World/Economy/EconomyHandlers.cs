using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Economy;

/// <summary>
/// Mail, auction house and trade opcodes. Client payload layouts follow gtker/wow_messages
/// 1.12 (MIT): world/mail, world/auction, world/trade and queries/cmsg_item_text_query.
/// Every handler runs on the world thread; the feature does the rest.
/// </summary>
public sealed class EconomyHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.MsgQueryNextMailTime, (s, p, _) => Feature(s).QueryNextMailTime(s, p));
        table.OnWorld(WorldOpcode.CmsgGetMailList, (s, p, d) => Feature(s).GetMailList(s, p, Guid(d)));
        table.OnWorld(WorldOpcode.CmsgSendMail, HandleSendMail);
        table.OnWorld(WorldOpcode.CmsgMailTakeMoney, (s, p, d) => MailCommand(d, (box, id) => Feature(s).TakeMoney(s, p, box, id)));
        table.OnWorld(WorldOpcode.CmsgMailTakeItem, (s, p, d) => MailCommand(d, (box, id) => Feature(s).TakeItem(s, p, box, id)));
        table.OnWorld(WorldOpcode.CmsgMailMarkAsRead, (s, p, d) => MailCommand(d, (box, id) => Feature(s).MarkAsRead(s, p, box, id)));
        table.OnWorld(WorldOpcode.CmsgMailReturnToSender, (s, p, d) => MailCommand(d, (box, id) => Feature(s).ReturnToSender(s, p, box, id)));
        table.OnWorld(WorldOpcode.CmsgMailDelete, (s, p, d) => MailCommand(d, (box, id) => Feature(s).DeleteMail(s, p, box, id)));
        table.OnWorld(WorldOpcode.CmsgMailCreateTextItem, (s, p, d) => MailCommand(d, (box, id) => Feature(s).CreateTextItem(s, p, box, id)));
        table.OnWorld(WorldOpcode.CmsgItemTextQuery, (s, p, d) => Feature(s).QueryItemText(s, p, new PacketReader(d).ReadUInt32()));

        table.OnWorld(WorldOpcode.MsgAuctionHello, (s, p, d) => Feature(s).AuctionHello(s, p, Guid(d)));
        table.OnWorld(WorldOpcode.CmsgAuctionSellItem, HandleSellItem);
        table.OnWorld(WorldOpcode.CmsgAuctionListItems, HandleListItems);
        table.OnWorld(WorldOpcode.CmsgAuctionListOwnerItems, (s, p, d) =>
        {
            var r = new PacketReader(d);
            ObjectGuid auctioneer = new(r.ReadUInt64());
            Feature(s).ListOwnerAuctions(s, p, auctioneer, r.ReadUInt32());
        });
        table.OnWorld(WorldOpcode.CmsgAuctionListBidderItems, (s, p, d) =>
        {
            // guid, u32 start, u32 count, u32[count] outbid auction ids (vmangos AuctionListBidderItem::Read, Packets/AuctionHouse.cpp:11-23;
            // wow_messages cmsg_auction_list_bidder_items). A body without the list (older tools) lists no outbid ids; a count larger than
            // the body is cut to the ids present.
            var r = new PacketReader(d);
            ObjectGuid auctioneer = new(r.ReadUInt64());
            uint listFrom = r.ReadUInt32();
            uint[] outbid = [];
            if (r.TryReadUInt32(out uint count))
            {
                outbid = new uint[Math.Min(count, (uint)(r.Remaining / 4))];
                for (int i = 0; i < outbid.Length; i++)
                {
                    outbid[i] = r.ReadUInt32();
                }
            }

            Feature(s).ListBidderAuctions(s, p, auctioneer, listFrom, outbid);
        });
        table.OnWorld(WorldOpcode.CmsgAuctionPlaceBid, (s, p, d) =>
        {
            var r = new PacketReader(d);
            ObjectGuid auctioneer = new(r.ReadUInt64());
            uint auctionId = r.ReadUInt32();
            Feature(s).PlaceBid(s, p, auctioneer, auctionId, r.ReadUInt32());
        });
        table.OnWorld(WorldOpcode.CmsgAuctionRemoveItem, (s, p, d) =>
        {
            var r = new PacketReader(d);
            ObjectGuid auctioneer = new(r.ReadUInt64());
            Feature(s).CancelAuction(s, p, auctioneer, r.ReadUInt32());
        });

        table.OnWorld(WorldOpcode.CmsgInitiateTrade, (s, p, d) => Feature(s).InitiateTrade(s, p, Guid(d)));
        table.OnWorld(WorldOpcode.CmsgBeginTrade, (s, p, _) => Feature(s).BeginTrade(p));
        table.OnWorld(WorldOpcode.CmsgBusyTrade, (s, p, _) => Feature(s).DeclineTrade(p, TradeStatus.Busy));
        table.OnWorld(WorldOpcode.CmsgIgnoreTrade, (s, p, _) => Feature(s).DeclineTrade(p, TradeStatus.IgnoreYou));
        table.OnWorld(WorldOpcode.CmsgAcceptTrade, (s, p, _) => Feature(s).AcceptTrade(s, p));
        table.OnWorld(WorldOpcode.CmsgUnacceptTrade, (s, p, _) => Feature(s).UnacceptTrade(p));
        table.OnWorld(WorldOpcode.CmsgCancelTrade, (s, p, _) => Feature(s).CancelTradeRequest(p));
        table.OnWorld(WorldOpcode.CmsgSetTradeItem, (s, p, d) =>
        {
            var r = new PacketReader(d);
            byte tradeSlot = r.ReadByte();
            byte bag = r.ReadByte();
            Feature(s).SetTradeItem(p, tradeSlot, bag, r.ReadByte());
        });
        table.OnWorld(WorldOpcode.CmsgClearTradeItem, (s, p, d) => Feature(s).ClearTradeItem(p, new PacketReader(d).ReadByte()));
        table.OnWorld(WorldOpcode.CmsgSetTradeGold, (s, p, d) => Feature(s).SetTradeGold(p, new PacketReader(d).ReadUInt32()));
    }

    private static EconomyFeature Feature(WorldSession session) => session.Services.GetRequiredService<EconomyFeature>();

    private static ObjectGuid Guid(byte[] payload) => new(new PacketReader(payload).ReadUInt64());

    /// <summary>guid mailbox, u32 mail id (take money/item, mark read, return, delete, create text item).</summary>
    private static void MailCommand(byte[] payload, Action<ObjectGuid, uint> action)
    {
        var r = new PacketReader(payload);
        ObjectGuid mailbox = new(r.ReadUInt64());
        action(mailbox, r.ReadUInt32());
    }

    /// <summary>CMSG_SEND_MAIL: guid mailbox, cstr receiver, cstr subject, cstr body, u32, u32, guid item, u32 money, u32 cod, u32, u32.</summary>
    private static void HandleSendMail(WorldSession session, Player player, byte[] payload)
    {
        var r = new PacketReader(payload);
        ObjectGuid mailbox = new(r.ReadUInt64());
        string receiver = r.ReadCString();
        string subject = r.ReadCString();
        string body = r.ReadCString();
        r.Skip(8);
        ObjectGuid item = new(r.ReadUInt64());
        uint money = r.ReadUInt32();
        uint cod = r.ReadUInt32();
        Feature(session).SendMail(session, player, new SendMailRequest(mailbox, receiver, subject, body, item, money, cod));
    }

    /// <summary>CMSG_AUCTION_SELL_ITEM: guid auctioneer, guid item, u32 bid, u32 buyout, u32 duration in minutes.</summary>
    private static void HandleSellItem(WorldSession session, Player player, byte[] payload)
    {
        var r = new PacketReader(payload);
        ObjectGuid auctioneer = new(r.ReadUInt64());
        ObjectGuid item = new(r.ReadUInt64());
        uint bid = r.ReadUInt32();
        uint buyout = r.ReadUInt32();
        uint minutes = r.ReadUInt32();
        Feature(session).SellItem(session, player, auctioneer, item, bid, buyout, minutes);
    }

    /// <summary>CMSG_AUCTION_LIST_ITEMS: guid, u32 list start, cstr name, u8 min level, u8 max level, u32 slot, class, subclass, quality, u8 usable.</summary>
    private static void HandleListItems(WorldSession session, Player player, byte[] payload)
    {
        var r = new PacketReader(payload);
        ObjectGuid auctioneer = new(r.ReadUInt64());
        uint listFrom = r.ReadUInt32();
        string name = r.ReadCString();
        byte levelMin = r.ReadByte();
        byte levelMax = r.ReadByte();
        uint inventoryType = r.ReadUInt32();
        uint itemClass = r.ReadUInt32();
        uint itemSubClass = r.ReadUInt32();
        uint quality = r.ReadUInt32();
        bool usable = r.ReadByte() != 0;
        Feature(session).ListAuctions(session, player, auctioneer,
            new AuctionQuery(listFrom, name, levelMin, levelMax, inventoryType, itemClass, itemSubClass, quality, usable));
    }
}

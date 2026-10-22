using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Economy;

/// <summary>The economy bot's decoders against the bytes <see cref="EconomyPackets"/> writes, and its payloads against the layouts the handlers read.</summary>
public sealed class PlayerbotEconomyWireTests
{
    private const long Now = 1_800_000_000;

    private static AuctionView View(uint id, int seller, uint entry = 4000, uint count = 1, uint startBid = 80, uint buyout = 100, int bidder = 0, uint bid = 0,
        long expire = Now + 3600, int property = 0)
        => new(new AuctionRecord
        {
            Id = id, HouseId = AuctionHouseRules.AllianceHouse, ItemGuid = 900 + id, ItemEntry = entry, ItemCount = count, SellerId = seller, StartBid = startBid,
            Buyout = buyout, ExpireTime = expire, BidderId = bidder, Bid = bid, Deposit = 7,
        }, new ItemInstanceData { Guid = 900 + id, Entry = entry, Count = count, RandomPropertyId = property });

    // --- auction lists ---------------------------------------------------------------------------------------------

    [Fact]
    public void DecodeAuctionList_ReadsEveryFieldOfEveryRow_AndTheTotal()
    {
        AuctionView[] page =
        [
            View(11, seller: 42, entry: 4000, count: 3, startBid: 80, buyout: 120, property: -7),
            View(12, seller: 0, entry: 4001, buyout: 0, startBid: 5),                                             // an AH bot listing, bid only
            View(13, seller: 99, entry: 4002, buyout: 500, bidder: 17, bid: 300, expire: Now + 90),
        ];
        PlayerbotAuctionPage decoded = PlayerbotEconomyWire.DecodeAuctionList(EconomyPackets.AuctionList(page, 137, Now));
        Assert.Equal(137u, decoded.Total);
        Assert.Equal(3, decoded.Rows.Count);

        PlayerbotAuctionRow first = decoded.Rows[0];
        Assert.Equal((11u, 4000u, -7, 3u), (first.Id, first.Entry, first.RandomProperty, first.Count));
        Assert.Equal(ObjectGuid.Player(42).Value, first.Owner);
        Assert.Equal((80u, 0u, 120u), (first.StartBid, first.MinOutbid, first.Buyout));
        Assert.Equal(3_600_000u, first.MsLeft);
        Assert.Equal((0ul, 0u), (first.Bidder, first.Bid));

        PlayerbotAuctionRow sellerless = decoded.Rows[1];
        Assert.Equal(12u, sellerless.Id);
        Assert.Equal(0ul, sellerless.Owner);
        Assert.Equal(0u, sellerless.Buyout);

        PlayerbotAuctionRow bid = decoded.Rows[2];
        Assert.Equal(ObjectGuid.Player(17).Value, bid.Bidder);
        Assert.Equal(300u, bid.Bid);
        Assert.Equal(AuctionHouseRules.OutBid(300), bid.MinOutbid);
        Assert.Equal(90_000u, bid.MsLeft);
    }

    [Fact]
    public void DecodeAuctionList_ReadsAnEmptyPage()
    {
        PlayerbotAuctionPage decoded = PlayerbotEconomyWire.DecodeAuctionList(EconomyPackets.AuctionList([], 0, Now));
        Assert.Empty(decoded.Rows);
        Assert.Equal(0u, decoded.Total);
    }

    [Fact]
    public void DecodeAuctionList_ReadsAFullPage_AndRefusesMoreRowsThanAPageHolds()
    {
        AuctionView[] full = [.. Enumerable.Range(1, PlayerbotEconomyWire.MaxRows).Select(i => View((uint)i, seller: i))];
        PlayerbotAuctionPage decoded = PlayerbotEconomyWire.DecodeAuctionList(EconomyPackets.AuctionList(full, 200, Now));
        Assert.Equal(PlayerbotEconomyWire.MaxRows, decoded.Rows.Count);
        Assert.Equal(50u, decoded.Rows[^1].Id);

        var w = new PacketWriter(8);
        w.WriteUInt32((uint)PlayerbotEconomyWire.MaxRows + 1);
        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeAuctionList(w.ToArray()));
    }

    [Fact]
    public void DecodeAuctionList_ThrowsFormatException_ForEveryTruncation()
    {
        byte[] whole = EconomyPackets.AuctionList([View(1, 5), View(2, 6)], 2, Now);
        Assert.Equal(2, PlayerbotEconomyWire.DecodeAuctionList(whole).Rows.Count);
        for (int length = 0; length < whole.Length; length++)
        {
            byte[] cut = whole[..length];
            Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeAuctionList(cut));
        }

        Assert.Throws<ArgumentNullException>(() => PlayerbotEconomyWire.DecodeAuctionList(null!));
    }

    // --- command results -------------------------------------------------------------------------------------------

    [Fact]
    public void DecodeCommandResult_ReadsTheIdActionAndError_WhateverTheTailOfTheError()
    {
        AuctionRecord outbid = View(5, 1, bidder: 9, bid: 400).Auction;
        (byte[] Packet, AuctionAction Action, AuctionError Error)[] cases =
        [
            (EconomyPackets.AuctionCommandResult(5, AuctionAction.Started, AuctionError.Ok), AuctionAction.Started, AuctionError.Ok),
            (EconomyPackets.AuctionCommandResult(5, AuctionAction.BidPlaced, AuctionError.Ok, auction: outbid), AuctionAction.BidPlaced, AuctionError.Ok),
            (EconomyPackets.AuctionCommandResult(5, AuctionAction.BidPlaced, AuctionError.HigherBid, auction: outbid), AuctionAction.BidPlaced, AuctionError.HigherBid),
            (EconomyPackets.AuctionCommandResult(5, AuctionAction.Started, AuctionError.Inventory, InventoryResult.ItemNotFound), AuctionAction.Started, AuctionError.Inventory),
            (EconomyPackets.AuctionCommandResult(5, AuctionAction.BidPlaced, AuctionError.ItemNotFound), AuctionAction.BidPlaced, AuctionError.ItemNotFound),
            (EconomyPackets.AuctionCommandResult(5, AuctionAction.Removed, AuctionError.NotEnoughMoney), AuctionAction.Removed, AuctionError.NotEnoughMoney),
            (EconomyPackets.AuctionCommandResult(5, AuctionAction.BidPlaced, AuctionError.BidOwn), AuctionAction.BidPlaced, AuctionError.BidOwn),
        ];
        foreach ((byte[] packet, AuctionAction action, AuctionError error) in cases)
        {
            PlayerbotAuctionResult decoded = PlayerbotEconomyWire.DecodeCommandResult(packet);
            Assert.Equal((5u, action, error), (decoded.AuctionId, decoded.Action, decoded.Error));
        }

        for (int length = 0; length < 12; length++)
        {
            byte[] cut = cases[0].Packet[..length];
            Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeCommandResult(cut));
        }
    }

    // --- mail ------------------------------------------------------------------------------------------------------

    private static MailView Letter(uint id, MailMessageType type, uint sender, string subject, uint money = 0, uint cod = 0, ItemInstanceData? item = null,
        MailCheckMask state = MailCheckMask.None)
        => new(new MailRecord
        {
            Id = id, MessageType = type, SenderId = sender, ReceiverId = 7, Subject = subject, Money = money, Cod = cod, Checked = state,
            ItemGuid = item?.Guid ?? 0, ItemEntry = item?.Entry ?? 0, DeliverTime = Now, ExpireTime = Now + 86_400 * 30,
        }, item);

    [Fact]
    public void DecodeMailList_ReadsEveryKindOfLetter_WithItsSenderMoneyCodAndState()
    {
        ItemInstanceData won = new() { Guid = 777, Entry = 4000, Count = 1 };
        ItemInstanceData stack = new() { Guid = 778, Entry = 4001, Count = 12 };
        MailView[] mails =
        [
            Letter(1, MailMessageType.Auction, sender: 2, MailRules.AuctionSubject(4000, AuctionMailAction.Won), item: won),
            Letter(2, MailMessageType.Auction, sender: 2, MailRules.AuctionSubject(4001, AuctionMailAction.Successful), money: 1234),
            Letter(3, MailMessageType.Normal, sender: 55, "from a player", money: 10, cod: 99, item: stack, state: MailCheckMask.CodPayment | MailCheckMask.HasBody),
            Letter(4, MailMessageType.Creature, sender: 1234, "from a creature", money: 5, state: MailCheckMask.Read),
            Letter(5, MailMessageType.GameObject, sender: 8765, "from an object"),
            Letter(6, MailMessageType.Item, sender: 0, "from an item"),
        ];
        IReadOnlyList<PlayerbotMailLetter> decoded = PlayerbotEconomyWire.DecodeMailList(EconomyPackets.MailList(mails, Now, _ => null));
        Assert.Equal(6, decoded.Count);

        PlayerbotMailLetter wonLetter = decoded[0];
        Assert.Equal((1u, MailMessageType.Auction, 2ul), (wonLetter.Id, wonLetter.Type, wonLetter.Sender));
        Assert.Equal(MailRules.AuctionSubject(4000, AuctionMailAction.Won), wonLetter.Subject);
        Assert.Equal((4000u, 1u, 0u, 0u), (wonLetter.ItemEntry, wonLetter.ItemCount, wonLetter.Money, wonLetter.Cod));
        Assert.False(wonLetter.IsRead);

        PlayerbotMailLetter sale = decoded[1];
        Assert.Equal((MailMessageType.Auction, 1234u, 0u, 0u), (sale.Type, sale.Money, sale.ItemEntry, sale.Cod));

        PlayerbotMailLetter player = decoded[2];
        Assert.Equal((MailMessageType.Normal, ObjectGuid.Player(55).Value), (player.Type, player.Sender));
        Assert.Equal("from a player", player.Subject);
        Assert.Equal((4001u, 12u, 10u, 99u), (player.ItemEntry, player.ItemCount, player.Money, player.Cod));
        Assert.Equal(MailCheckMask.CodPayment | MailCheckMask.HasBody, player.Checked);
        Assert.False(player.IsRead);

        PlayerbotMailLetter creature = decoded[3];
        Assert.Equal((MailMessageType.Creature, 1234ul, 5u), (creature.Type, creature.Sender, creature.Money));
        Assert.True(creature.IsRead);

        Assert.Equal((MailMessageType.GameObject, 8765ul), (decoded[4].Type, decoded[4].Sender));
        Assert.Equal(("from an item", 0ul), (decoded[5].Subject, decoded[5].Sender));
    }

    [Fact]
    public void DecodeMailList_ReadsALetterBuiltByTheHousesOwnFactory()
    {
        MailRecord won = MailRules.AuctionLetter(31, AuctionHouseRules.AllianceHouse, 7, MailRules.AuctionSubject(4000, AuctionMailAction.Expired), Now,
            new EconomyOptions(), money: 0, itemGuid: 777, itemEntry: 4000);
        IReadOnlyList<PlayerbotMailLetter> decoded = PlayerbotEconomyWire.DecodeMailList(
            EconomyPackets.MailList([new MailView(won, new ItemInstanceData { Guid = 777, Entry = 4000, Count = 1 })], Now, _ => null));
        PlayerbotMailLetter letter = Assert.Single(decoded);
        Assert.Equal((31u, MailMessageType.Auction, 0u), (letter.Id, letter.Type, letter.Cod));
        Assert.True(PlayerbotEconomyPlanner.TryParseAuctionSubject(letter.Subject, out uint entry, out AuctionMailAction action));
        Assert.Equal((4000u, AuctionMailAction.Expired), (entry, action));
        Assert.Equal(4000u, letter.ItemEntry);
    }

    [Fact]
    public void DecodeMailList_ReadsAnEmptyBox_AndThrowsFormatException_ForEveryTruncation()
    {
        Assert.Empty(PlayerbotEconomyWire.DecodeMailList(EconomyPackets.MailList([], Now, _ => null)));

        byte[] whole = EconomyPackets.MailList(
        [
            Letter(1, MailMessageType.Auction, 2, "1:0:2", money: 5),
            Letter(2, MailMessageType.Normal, 3, "hello", cod: 4, item: new ItemInstanceData { Guid = 5, Entry = 4000, Count = 2 }),
        ], Now, _ => null);
        Assert.Equal(2, PlayerbotEconomyWire.DecodeMailList(whole).Count);
        for (int length = 0; length < whole.Length; length++)
        {
            byte[] cut = whole[..length];
            Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeMailList(cut));
        }
    }

    [Fact]
    public void DecodeMailResult_ReadsTheIdActionAndResult_WhateverTheTail()
    {
        (byte[] Packet, MailAction Action, MailResult Result)[] cases =
        [
            (EconomyPackets.SendMailResult(8, MailAction.MoneyTaken, MailResult.Ok), MailAction.MoneyTaken, MailResult.Ok),
            (EconomyPackets.SendMailResult(8, MailAction.ItemTaken, MailResult.Ok, itemGuid: 777, itemCount: 3), MailAction.ItemTaken, MailResult.Ok),
            (EconomyPackets.SendMailResult(8, MailAction.ItemTaken, MailResult.EquipError, InventoryResult.InventoryFull), MailAction.ItemTaken, MailResult.EquipError),
            (EconomyPackets.SendMailResult(8, MailAction.Deleted, MailResult.Ok), MailAction.Deleted, MailResult.Ok),
            (EconomyPackets.SendMailResult(8, MailAction.MoneyTaken, MailResult.InternalError), MailAction.MoneyTaken, MailResult.InternalError),
        ];
        foreach ((byte[] packet, MailAction action, MailResult result) in cases)
        {
            PlayerbotMailResult decoded = PlayerbotEconomyWire.DecodeMailResult(packet);
            Assert.Equal((8u, action, result), (decoded.MailId, decoded.Action, decoded.Result));
        }

        for (int length = 0; length < 12; length++)
        {
            byte[] cut = cases[0].Packet[..length];
            Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeMailResult(cut));
        }
    }

    [Fact]
    public void DecodeHasUnreadMail_IsTrueOnlyWhenTheHouseSaysAnUnreadLetterWaits()
    {
        Assert.True(PlayerbotEconomyWire.DecodeHasUnreadMail(EconomyPackets.NextMailTime(true)));
        Assert.False(PlayerbotEconomyWire.DecodeHasUnreadMail(EconomyPackets.NextMailTime(false)));
        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeHasUnreadMail([]));
        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeHasUnreadMail([0, 0, 0]));
    }

    // --- client payloads -------------------------------------------------------------------------------------------

    [Fact]
    public void ClientPayloads_HaveTheLayoutsTheHandlersRead()
    {
        var sell = new PacketReader(PlayerbotEconomyWire.SellItem(0xAA, 0xBB, 80, 100, 480));
        Assert.Equal((0xAAul, 0xBBul, 80u, 100u, 480u), (sell.ReadUInt64(), sell.ReadUInt64(), sell.ReadUInt32(), sell.ReadUInt32(), sell.ReadUInt32()));

        var bid = new PacketReader(PlayerbotEconomyWire.PlaceBid(0xAA, 17, 999));
        Assert.Equal((0xAAul, 17u, 999u), (bid.ReadUInt64(), bid.ReadUInt32(), bid.ReadUInt32()));

        var owner = new PacketReader(PlayerbotEconomyWire.ListOwnerItems(0xAA, 50));
        Assert.Equal((0xAAul, 50u), (owner.ReadUInt64(), owner.ReadUInt32()));

        var list = new PacketReader(PlayerbotEconomyWire.ListItems(0xAA, 0, "Linen Cloth", 1, 60, PlayerbotEconomyWire.Any, 7, PlayerbotEconomyWire.Any, 2, usable: true));
        Assert.Equal((0xAAul, 0u, "Linen Cloth"), (list.ReadUInt64(), list.ReadUInt32(), list.ReadCString()));
        Assert.Equal(((byte)1, (byte)60), (list.ReadByte(), list.ReadByte()));
        Assert.Equal((PlayerbotEconomyWire.Any, 7u, PlayerbotEconomyWire.Any, 2u), (list.ReadUInt32(), list.ReadUInt32(), list.ReadUInt32(), list.ReadUInt32()));
        Assert.Equal((byte)1, list.ReadByte());

        var mailList = new PacketReader(PlayerbotEconomyWire.GetMailList(0xCC));
        Assert.Equal(0xCCul, mailList.ReadUInt64());
        var command = new PacketReader(PlayerbotEconomyWire.MailCommand(0xCC, 31));
        Assert.Equal((0xCCul, 31u), (command.ReadUInt64(), command.ReadUInt32()));
    }

    [Fact]
    public void Decode_RecordsFaultAndAnomaly_WhenPayloadIsMalformed()
    {
        static long SiteCount() => FaultLedger.Shared.Snapshot()
            .Where(s => s.Site == "playerbot.economy-wire")
            .Sum(s => s.Count);

        long before = SiteCount();
        byte[] truncated = new byte[7];
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "bot-economy/wire-decode-failed" && n.Subject == nameof(PlayerbotEconomyWire.DecodeCommandResult)
                && n.Detail.Contains("(7 bytes)", StringComparison.Ordinal));

        Assert.Throws<FormatException>(() => PlayerbotEconomyWire.DecodeCommandResult(truncated));
        Assert.True(SiteCount() > before);
        Assert.Equal(1, capture.Count("bot-economy/wire-decode-failed"));
    }
}

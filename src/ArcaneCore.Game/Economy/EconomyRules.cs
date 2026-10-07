using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Economy;

/// <summary>How a mail opcode proves the player is at a mailbox (<see cref="EconomyOptions.MailboxAccess"/>).</summary>
public enum MailboxAccessMode
{
    /// <summary>
    /// vmangos WorldSession::CheckMailBox: the addressed GAMEOBJECT_TYPE_MAILBOX must exist in the
    /// player's map, be spawned and within interaction distance of an alive, in-world player. Default.
    /// </summary>
    Retail,

    /// <summary>
    /// Any game object GUID is accepted. The only deviation from retail: for synthetic test hosts and
    /// servers without game object content. It lets any player read, send and collect mail from anywhere.
    /// </summary>
    Permissive,
}

/// <summary>One auction house (AuctionHouse.dbc: id, deposit percent, cut percent).</summary>
public sealed record AuctionHouseEntry(uint Id, uint DepositPercent, uint CutPercent);

/// <summary>
/// Economy configuration (section <c>Economy</c>). Defaults follow vmangos/cMaNGOS behavior; the
/// auction-house percentages are AuctionHouse.dbc-style values and should be checked against the
/// extracted 1.12.1 DBC before acceptance (docs/integration/economy.md).
/// </summary>
public sealed class EconomyOptions
{
    /// <summary>vmangos/cMaNGOS MAX_MONEY_AMOUNT.</summary>
    public const uint MaxMoney = 0x7FFFFFFF - 1;

    /// <summary>Mailbox proof for every mail opcode; <see cref="MailboxAccessMode.Retail"/> (default) or <see cref="MailboxAccessMode.Permissive"/> (config key <c>Economy:MailboxAccess</c>).</summary>
    public MailboxAccessMode MailboxAccess { get; set; } = MailboxAccessMode.Retail;

    /// <summary>Postage per letter in copper (vmangos HandleSendMail: 30).</summary>
    public uint MailPostage { get; set; } = 30;

    /// <summary>Days before an ordinary letter expires (vmangos: 30).</summary>
    public uint MailExpireDays { get; set; } = 30;

    /// <summary>Days before a cash-on-delivery letter expires (vmangos: 3).</summary>
    public uint CodExpireDays { get; set; } = 3;

    /// <summary>A recipient already holding MORE than this many letters is refused (vmangos MailHandler.cpp:258: count &gt; 100).</summary>
    public int MaxMailboxSize { get; set; } = 100;

    /// <summary>Whether players of opposite teams may mail each other (vmangos CONFIG_BOOL_ALLOW_TWO_SIDE_INTERACTION_MAIL).</summary>
    public bool AllowCrossTeamMail { get; set; }

    /// <summary>Whether players of opposite teams may trade (vmangos CONFIG_BOOL_ALLOW_TWO_SIDE_TRADE).</summary>
    public bool AllowCrossTeamTrade { get; set; }

    /// <summary>The Plain Letter item created by CMSG_MAIL_CREATE_TEXT_ITEM (vmangos MAIL_BODY_ITEM_TEMPLATE 8383).</summary>
    public uint LetterItemEntry { get; set; } = 8383;

    /// <summary>Minimum deposit in copper (vmangos CONFIG_UINT32_AUCTION_DEPOSIT_MIN, default 0).</summary>
    public uint AuctionDepositMin { get; set; }

    /// <summary>Rate.Auction.Deposit multiplier (vmangos World.cpp:535, default 1.0).</summary>
    public float AuctionRateDeposit { get; set; } = 1.0f;

    /// <summary>Rate.Auction.Cut multiplier (vmangos World.cpp:536, default 1.0).</summary>
    public float AuctionRateCut { get; set; } = 1.0f;

    /// <summary>Delivery delay of letters carrying an item or money, seconds (vmangos MailDeliveryDelay, World.cpp:691: 1 hour).</summary>
    public uint MailDeliveryDelaySeconds { get; set; } = 3600;

    /// <summary>Reading a letter shortens its remaining life to this many days when more remains (vmangos MailHandler.cpp:454-458: 3; 0 disables).</summary>
    public uint MailReadExpiryDays { get; set; } = 3;

    /// <summary>Largest cash-on-delivery amount accepted, copper (vmangos MailHandler.cpp:162: 100,000,000).</summary>
    public uint MailMaxCodCopper { get; set; } = 100_000_000;

    /// <summary>Longest subject in bytes; longer ones are dropped (vmangos MailHandler.cpp:155: 64).</summary>
    public int MailSubjectMaxLength { get; set; } = 64;

    /// <summary>Longest body in bytes; longer ones are dropped (vmangos MailHandler.cpp:158: 500).</summary>
    public int MailBodyMaxLength { get; set; } = 500;

    /// <summary>Answer oversize or over-COD letters with an internal error instead of dropping them silently like vmangos (default false).</summary>
    public bool MailOversizeAnswersError { get; set; }

    /// <summary>
    /// Also return an expired money-only player letter to its sender (default true, the pre-lane behaviour; no retail source
    /// shows otherwise). false matches the emulators: vmangos and mangos-classic delete it
    /// (ObjectMgr.cpp:6995-7000: only letters with items are returned).
    /// </summary>
    public bool ReturnExpiredMoneyOnlyMail { get; set; } = true;

    /// <summary>
    /// Letters with an item or money may be deleted by the receiver, destroying the attachment, as vmangos does
    /// (MailHandler.cpp:469-491 refuses only cash on delivery). Default false: only emptied letters can be deleted, the
    /// pre-lane behaviour, since no retail source shows attachments being destroyable.
    /// </summary>
    public bool AllowDeleteWithAttachments { get; set; }

    /// <summary>Scam-prevention delay after a trade modification before an accept counts, ms (vmangos TradeHandler.cpp:657-658: 200; 0 = off).</summary>
    public uint TradeScamPreventionMs { get; set; } = 200;

    /// <summary>
    /// Measure the delay in whole seconds like vmangos (time(nullptr): effectively "not within the same second"); false
    /// uses real milliseconds (a true 200 ms). Default true.
    /// </summary>
    public bool TradeScamPreventionWholeSeconds { get; set; } = true;

    /// <summary>
    /// Report not enough gold / bag space with notifications 801-803 and keep the window open, as vmangos does
    /// (TradeHandler.cpp:274-290, 420-455); false closes the trade with the inventory error. Default true.
    /// </summary>
    public bool TradeSpaceNotifications { get; set; } = true;

    /// <summary>Rate.Auction.Time multiplier of a listing's duration (vmangos World.cpp:534, AuctionHouseHandler.cpp:362; default 1.0).</summary>
    public float AuctionRateTime { get; set; } = 1.0f;

    /// <summary>
    /// Active auctions one account may hold per auction house (vmangos Auction.AccountConcurrentLimit, World.cpp:538,
    /// AuctionHouseHandler.cpp:274-280); 0 = unlimited (default).
    /// </summary>
    public uint AuctionAccountConcurrentLimit { get; set; }

    /// <summary>
    /// A bid the bidder cannot afford, or a cancellation whose cut the seller cannot pay, is answered with nothing, as
    /// vmangos does (AuctionHouseHandler.cpp:498-503, 592-594); false answers NOT_ENOUGH_MONEY. Default true.
    /// </summary>
    public bool AuctionSilentRefusals { get; set; } = true;

    /// <summary>Seconds between expiry sweeps of mail and auctions.</summary>
    public uint ExpirySweepSeconds { get; set; } = 60;

    /// <summary>The three houses: Alliance 2, Horde 6, neutral (Blackwater/goblin) 7.</summary>
    public List<AuctionHouseEntry> AuctionHouses { get; set; } =
    [
        new(AuctionHouseRules.AllianceHouse, 15, 5),
        new(AuctionHouseRules.HordeHouse, 15, 5),
        new(AuctionHouseRules.NeutralHouse, 75, 15),
    ];

    /// <summary>
    /// Faction.dbc ids of auctioneers that serve the neutral house (Booty Bay 21, Gadgetzan 369,
    /// Everlook 577: the factions of vmangos's goblin templates 120/474/855).
    /// </summary>
    public List<uint> NeutralAuctioneerFactions { get; set; } = [21, 369, 577];
}

/// <summary>Auction pricing and house selection (vmangos AuctionHouseMgr; re-implemented, GPL code not copied).</summary>
public static class AuctionHouseRules
{
    public const uint AllianceHouse = 2;
    public const uint HordeHouse = 6;
    public const uint NeutralHouse = 7;

    /// <summary>vmangos MIN_AUCTION_TIME: the unit of the deposit (2 hours, in seconds).</summary>
    public const uint MinAuctionSeconds = 2 * 60 * 60;

    /// <summary>Listing durations the 1.12 client offers, in minutes (2, 8 and 24 hours).</summary>
    public static readonly IReadOnlyList<uint> DurationsMinutes = [120, 480, 1440];

    /// <summary>The client limit of a start bid or buyout (vmangos AuctionHouseHandler.cpp:236: 2,000,000,000 copper).</summary>
    public const uint MaxPrice = 2_000_000_000;

    /// <summary>Maximum auctions per list result page (vmangos: 50).</summary>
    public const int PageSize = 50;

    /// <summary>The house of an auctioneer: neutral for goblin factions, otherwise the player's own team.</summary>
    public static AuctionHouseEntry? HouseFor(EconomyOptions options, uint auctioneerFactionId, Team playerTeam)
    {
        uint id = options.NeutralAuctioneerFactions.Contains(auctioneerFactionId) ? NeutralHouse
            : playerTeam == Team.Alliance ? AllianceHouse : HordeHouse;
        return options.AuctionHouses.FirstOrDefault(h => h.Id == id);
    }

    /// <summary>
    /// vmangos GetAuctionDeposit (AuctionHouseMgr.cpp:98-110): single-precision arithmetic in vmangos order,
    /// float(sell * count * units) * depositPercent / 100.0f, raised to the minimum, then multiplied by
    /// Rate.Auction.Deposit and truncated. The integer product is taken in 64 bits (vmangos wraps it in uint32;
    /// real item data never reaches that: max SellPrice*stack*12 = 24,000,000).
    /// </summary>
    public static uint Deposit(AuctionHouseEntry house, uint sellPrice, uint count, uint durationMinutes, uint minimum = 0, float rate = 1f)
    {
        ulong units = (ulong)durationMinutes * 60 / MinAuctionSeconds;
        float deposit = (float)((ulong)sellPrice * count * units);
        deposit = deposit * house.DepositPercent;
        deposit /= 100.0f;
        float min = minimum;
        if (deposit < min)
        {
            deposit = min;
        }

        float scaled = deposit * rate;
        return scaled >= uint.MaxValue ? uint.MaxValue : scaled <= 0f ? 0u : (uint)scaled;
    }

    /// <summary>
    /// vmangos GetAuctionCut (AuctionHouseMgr.cpp:845-848): cutPercent * bid * Rate.Auction.Cut / 100.0f in single
    /// precision. ArcaneCore keeps the cutPercent*bid product in 64 bits on purpose (vmangos wraps it in uint32).
    /// </summary>
    public static uint Cut(AuctionHouseEntry house, uint bid, float rate = 1f)
    {
        float cut = (float)((ulong)house.CutPercent * bid) * rate / 100.0f;
        return cut >= uint.MaxValue ? uint.MaxValue : cut <= 0f ? 0u : (uint)cut;
    }

    /// <summary>
    /// The successful-sale letter's money: bid + deposit − cut (vmangos AuctionHouseMgr.cpp:226), capped at the money limit. A cut
    /// above bid + deposit (a house row over 100%, or a large Rate.Auction.Cut) pays nothing; vmangos' uint32 subtraction wraps there.
    /// </summary>
    public static uint Proceeds(uint bid, uint deposit, uint cut)
        => (uint)Math.Clamp((long)bid + deposit - cut, 0, EconomyOptions.MaxMoney);

    /// <summary>vmangos GetAuctionOutBid: 5% of the current bid in whole percents, at least 1 copper.</summary>
    public static uint OutBid(uint bid) => Math.Max(bid / 100 * 5, 1);

    /// <summary>The smallest bid accepted now: the start bid without a bidder, otherwise current + outbid.</summary>
    public static uint MinimumBid(AuctionRecord auction)
        => auction.BidderId == 0 ? auction.StartBid : (uint)Math.Min(uint.MaxValue, (ulong)auction.Bid + OutBid(auction.Bid));
}

/// <summary>vmangos AuctionAction (SMSG_AUCTION_COMMAND_RESULT action).</summary>
public enum AuctionAction : uint
{
    Started = 0,
    Removed = 1,
    BidPlaced = 2,
}

/// <summary>vmangos AuctionError.</summary>
public enum AuctionError : uint
{
    Ok = 0,
    Inventory = 1,
    Database = 2,
    NotEnoughMoney = 3,
    ItemNotFound = 4,
    HigherBid = 5,
    BidIncrement = 7,
    BidOwn = 10,
    RestrictedAccount = 13,
}

/// <summary>Auction mail subjects "entry:0:action" (vmangos AuctionMessageAction).</summary>
public enum AuctionMailAction
{
    Outbidded = 0,
    Won = 1,
    Successful = 2,
    Expired = 3,
    CancelledToBidder = 4,
    Canceled = 5,
}

/// <summary>vmangos MailResponseType (SMSG_SEND_MAIL_RESULT action).</summary>
public enum MailAction : uint
{
    Send = 0,
    MoneyTaken = 1,
    ItemTaken = 2,
    ReturnedToSender = 3,
    Deleted = 4,
    MadePermanent = 5,
}

/// <summary>vmangos MailResponseResult.</summary>
public enum MailResult : uint
{
    Ok = 0,
    EquipError = 1,
    CannotSendToSelf = 2,
    NotEnoughMoney = 3,
    RecipientNotFound = 4,
    NotYourTeam = 5,
    InternalError = 6,
    DisabledForTrialAccount = 14,
    RecipientCapReached = 15,
    CantSendWrappedCod = 16,
    MailAndChatSuspended = 17,
    TooManyAttachments = 18,
    MailAttachmentInvalid = 19,
}

/// <summary>Search filters of CMSG_AUCTION_LIST_ITEMS (1.12 layout, wow_messages).</summary>
public sealed record AuctionQuery(
    uint ListFrom,
    string Name,
    byte LevelMin,
    byte LevelMax,
    uint InventoryType,
    uint ItemClass,
    uint ItemSubClass,
    uint Quality,
    bool Usable);

/// <summary>Auction search (vmangos AuctionHouseObject::BuildListAuctionItems semantics; 0xFFFFFFFF means any).</summary>
public static class AuctionSearch
{
    public const uint Any = 0xFFFFFFFF;

    private const uint InvTypeChest = 5;
    private const uint InvTypeRobe = 20;

    /// <summary>The page of matching auctions (at most <see cref="AuctionHouseRules.PageSize"/>) and the total match count.</summary>
    public static (IReadOnlyList<AuctionRecord> Page, int Total) Run(IEnumerable<AuctionRecord> auctions, AuctionQuery query,
        Func<uint, ItemTemplate?> templates, Func<ItemTemplate, bool>? usable = null)
    {
        // vmangos keeps OrderedAuctionMap keyed by buyout price (AuctionHouseMgr.cpp:71-75, 711-790), insertion order
        // (the auction id) within a key, so which rows a page shows follows the buyout price.
        List<AuctionRecord> matches = auctions.OrderBy(a => a.Buyout).ThenBy(a => a.Id)
            .Where(a => templates(a.ItemEntry) is { } t && Matches(t, query, usable)).ToList();
        int from = (int)Math.Min(query.ListFrom, (uint)matches.Count);
        return (matches.Skip(from).Take(AuctionHouseRules.PageSize).ToList(), matches.Count);
    }

    public static bool Matches(ItemTemplate template, AuctionQuery query, Func<ItemTemplate, bool>? usable = null)
    {
        if (query.ItemClass != Any && template.Class != query.ItemClass)
        {
            return false;
        }

        if (query.ItemSubClass != Any && template.SubClass != query.ItemSubClass)
        {
            return false;
        }

        // vmangos AuctionHouseMgr.cpp:760: the chest slot (INVTYPE_CHEST 5) also lists robes (INVTYPE_ROBE 20).
        if (query.InventoryType != Any && template.InventoryType != query.InventoryType
            && !(query.InventoryType == InvTypeChest && template.InventoryType == InvTypeRobe))
        {
            return false;
        }

        if (query.Quality != Any && template.Quality < query.Quality)
        {
            return false;
        }

        // vmangos AuctionHouseMgr.cpp:765: the maximum level only applies together with a minimum level.
        if (query.LevelMin != 0 && (template.RequiredLevel < query.LevelMin
            || (query.LevelMax != 0 && template.RequiredLevel > query.LevelMax)))
        {
            return false;
        }

        if (query.Usable && usable is not null && !usable(template))
        {
            return false;
        }

        return query.Name.Length == 0 || template.Name.Contains(query.Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Letter construction helpers shared by player mail, auction results and returns.</summary>
public static class MailRules
{
    public const long SecondsPerDay = 24 * 60 * 60;

    /// <summary>The "entry:0:action" subject of an auction letter (vmangos AuctionHouseMgr).</summary>
    public static string AuctionSubject(uint itemEntry, AuctionMailAction action) => $"{itemEntry}:0:{(int)action}";

    /// <summary>A system letter from the auction house (MAIL_AUCTION, stationery 62, copied text).</summary>
    public static MailRecord AuctionLetter(uint id, uint houseId, int receiverId, string subject, long now, EconomyOptions options,
        uint money = 0, uint itemGuid = 0, uint itemEntry = 0) => new()
    {
        Id = id,
        MessageType = MailMessageType.Auction,
        Stationery = MailStationery.Auction,
        SenderId = houseId,
        ReceiverId = receiverId,
        Subject = subject,
        ItemGuid = itemGuid,
        ItemEntry = itemEntry,
        Money = money,
        Checked = MailCheckMask.Copied,
        DeliverTime = now,
        // vmangos Mail.cpp:318-321: an auction note without item and money lives one hour.
        ExpireTime = now + (money == 0 && itemGuid == 0 ? 3600 : options.MailExpireDays * SecondsPerDay),
    };

    /// <summary>
    /// Delivery and expiry of a new letter (vmangos MailDraft::SendMailTo, Mail.cpp:312-326): letters with an item or money
    /// arrive after the configured delay, text-only letters at once, and expiry (3 days COD, else 30) counts from delivery.
    /// </summary>
    public static (long Deliver, long Expire) SendTiming(long now, bool hasItemOrMoney, uint cod, EconomyOptions options)
    {
        long deliver = now + (hasItemOrMoney ? options.MailDeliveryDelaySeconds : 0);
        return (deliver, deliver + ((cod > 0 ? options.CodExpireDays : options.MailExpireDays) * SecondsPerDay));
    }

    /// <summary>MailHandler.cpp:454-458: reading clamps the remaining life to <paramref name="days"/> days when more remains (0 = off).</summary>
    public static long ExpireAfterRead(long expireTime, long now, uint days)
        => days > 0 && expireTime - now > days * SecondsPerDay ? now + (days * SecondsPerDay) : expireTime;

    /// <summary>The returned copy of a player's letter (vmangos MailDraft::SendReturnToSender): sender and receiver swap, COD cleared.</summary>
    public static MailRecord Returned(MailRecord mail, uint newId, long now, EconomyOptions options, uint deliverDelaySeconds = 0) => mail with
    {
        Id = newId,
        SenderId = checked((uint)mail.ReceiverId),
        ReceiverId = checked((int)mail.SenderId),
        Cod = 0,
        Checked = (mail.Checked & (MailCheckMask.HasBody | MailCheckMask.Copied)) | MailCheckMask.Returned,
        DeliverTime = now + deliverDelaySeconds,
        ExpireTime = now + deliverDelaySeconds + (options.MailExpireDays * SecondsPerDay),
    };

    /// <summary>Whether the letter can be returned to a player (a player's unreturned letter).</summary>
    public static bool CanReturn(MailRecord mail) => mail.MessageType == MailMessageType.Normal
        && (mail.Checked & MailCheckMask.Returned) == 0 && mail.SenderId != 0;

    /// <summary>
    /// Whether an expired letter goes back to its sender rather than being deleted. Only player letters that are not
    /// already returned and not COD payments qualify (vmangos ReturnOrDeleteOldMails, ObjectMgr.cpp:6995-7002, 7029-7044).
    /// vmangos returns only letters with an item; by default a money-only letter is returned too
    /// (<see cref="EconomyOptions.ReturnExpiredMoneyOnlyMail"/> false restores the vmangos deletion).
    /// </summary>
    public static bool ReturnsOnExpiry(MailRecord mail, EconomyOptions options)
        => mail.MessageType == MailMessageType.Normal && mail.SenderId != 0
            && (mail.Checked & (MailCheckMask.CodPayment | MailCheckMask.Returned)) == 0
            && (mail.HasItem || (options.ReturnExpiredMoneyOnlyMail && mail.Money > 0));

    /// <summary>Remaining days as the client shows them (SMSG_MAIL_LIST_RESULT expiration_time).</summary>
    public static float DaysLeft(MailRecord mail, long now) => (mail.ExpireTime - now) / (float)SecondsPerDay;
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Economy;

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

    /// <summary>Postage per letter in copper (vmangos HandleSendMail: 30).</summary>
    public uint MailPostage { get; set; } = 30;

    /// <summary>Days before an ordinary letter expires (vmangos: 30).</summary>
    public uint MailExpireDays { get; set; } = 30;

    /// <summary>Days before a cash-on-delivery letter expires (vmangos: 3).</summary>
    public uint CodExpireDays { get; set; } = 3;

    /// <summary>Most letters a mailbox may hold (vmangos MAX_INBOX_CLIENT_CAPACITY 100 is the client view).</summary>
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

    /// <summary>The page of matching auctions (at most <see cref="AuctionHouseRules.PageSize"/>) and the total match count.</summary>
    public static (IReadOnlyList<AuctionRecord> Page, int Total) Run(IEnumerable<AuctionRecord> auctions, AuctionQuery query,
        Func<uint, ItemTemplate?> templates, Func<ItemTemplate, bool>? usable = null)
    {
        List<AuctionRecord> matches = auctions.OrderBy(a => a.Id).Where(a => templates(a.ItemEntry) is { } t && Matches(t, query, usable)).ToList();
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

        if (query.InventoryType != Any && template.InventoryType != query.InventoryType)
        {
            return false;
        }

        if (query.Quality != Any && template.Quality < query.Quality)
        {
            return false;
        }

        if ((query.LevelMin != 0 && template.RequiredLevel < query.LevelMin)
            || (query.LevelMax != 0 && template.RequiredLevel > query.LevelMax))
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
        ExpireTime = now + (options.MailExpireDays * SecondsPerDay),
    };

    /// <summary>The returned copy of a player's letter (vmangos MailDraft::SendReturnToSender): sender and receiver swap, COD cleared.</summary>
    public static MailRecord Returned(MailRecord mail, uint newId, long now, EconomyOptions options) => mail with
    {
        Id = newId,
        SenderId = checked((uint)mail.ReceiverId),
        ReceiverId = checked((int)mail.SenderId),
        Cod = 0,
        Checked = (mail.Checked & (MailCheckMask.HasBody | MailCheckMask.Copied)) | MailCheckMask.Returned,
        DeliverTime = now,
        ExpireTime = now + (options.MailExpireDays * SecondsPerDay),
    };

    /// <summary>Whether the letter can be returned to a player (a player's unreturned letter).</summary>
    public static bool CanReturn(MailRecord mail) => mail.MessageType == MailMessageType.Normal
        && (mail.Checked & MailCheckMask.Returned) == 0 && mail.SenderId != 0;

    /// <summary>Remaining days as the client shows them (SMSG_MAIL_LIST_RESULT expiration_time).</summary>
    public static float DaysLeft(MailRecord mail, long now) => (mail.ExpireTime - now) / (float)SecondsPerDay;
}

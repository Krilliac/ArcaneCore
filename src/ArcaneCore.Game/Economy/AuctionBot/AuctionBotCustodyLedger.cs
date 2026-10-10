using System.Security.Cryptography;
using System.Text;

namespace ArcaneCore.Game.Economy.AuctionBot;

/// <summary>What a custody row holds (MaNGOS Zero CustodyLedger.h CustodyKind).</summary>
public enum AuctionBotCustodyKind : byte
{
    Gold = 0,
    Item = 1,
}

/// <summary>Why the bot holds it (MaNGOS Zero CustodyRole, reduced to the bot's two movements).</summary>
public enum AuctionBotCustodyRole : byte
{
    /// <summary>An item the bot creates into escrow for its own listing.</summary>
    Listing = 0,

    /// <summary>Copper the bot pays for a player's auction it buys out.</summary>
    Buyout = 1,
}

/// <summary>Lifecycle of a custody row (MaNGOS Zero CustodyState).</summary>
public enum AuctionBotCustodyState : byte
{
    /// <summary>Started, outcome not known yet: it counts against the budgets and blocks a second attempt on the same auction.</summary>
    Reserved = 0,

    /// <summary>The transaction committed.</summary>
    TerminalOk = 1,

    /// <summary>The transaction did not commit; nothing moved.</summary>
    TerminalBack = 2,
}

/// <summary>One custody row (MaNGOS Zero CustodyRow). Times are Unix seconds.</summary>
public sealed record AuctionBotCustodyRow(
    string IdemKey,
    AuctionBotCustodyKind Kind,
    AuctionBotCustodyRole Role,
    AuctionBotCustodyState State,
    uint HouseId,
    uint AuctionId,
    uint ItemGuid,
    uint ItemEntry,
    uint ItemCount,
    uint Amount,
    long CreatedTime,
    long ResolvedTime = 0)
{
    /// <summary>The economy operation id of this row's transaction, so a replay is AlreadyCommitted instead of a second copy.</summary>
    public Guid OperationId => AuctionBotCustodyLedger.OperationIdFor(IdemKey);
}

/// <summary>Totals of the current UTC day and of the open reservations.</summary>
public readonly record struct AuctionBotCustodyTotals(uint ItemsToday, ulong CopperToday, int ReservedItems, ulong ReservedCopper, int Rows);

/// <summary>
/// The bot's custody ledger, after MaNGOS Zero CustodyLedger/CustodyService/CustodyReconciler (src/game/AuctionHouseBot/Custody*). Every
/// item the bot creates and every copper it pays goes through a row with an idempotency key BEFORE the economy transaction starts:
/// <list type="bullet">
/// <item>a key is used once, and its economy operation id is derived from it, so a retried listing or buyout is AlreadyCommitted by the
/// store and can never mint a second item or pay twice;</item>
/// <item>an item GUID is held by at most one live listing row, and an auction by at most one live buyout row (GetSingleLiveBidRow);</item>
/// <item>a row whose outcome is unknown stays Reserved (fail closed): it keeps counting against the daily budgets and blocks the
/// auction until <see cref="Reconcile"/> learns the durable outcome;</item>
/// <item>the daily item and copper budgets bound what the bot can add to the economy even if every other guard failed.</item>
/// </list>
/// In memory: after a restart the durable auction rows and the economy operation ledger are the truth, and the World side reconciles
/// the rows it still holds against them. World thread only.
/// </summary>
public sealed class AuctionBotCustodyLedger(AuctionBotOptions options)
{
    public const long SecondsPerDay = 86_400;

    private readonly Dictionary<string, AuctionBotCustodyRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, int> _buyoutAttempts = [];

    public static string ListingKey(uint auctionId) => $"ahbot:list:{auctionId}";

    public static string BuyoutKey(uint auctionId, int attempt) => $"ahbot:buy:{auctionId}:{attempt}";

    /// <summary>A stable operation id for a key (the first 16 bytes of its SHA-256, marked as an RFC 4122 version 8 GUID).</summary>
    public static Guid OperationIdFor(string idemKey)
    {
        ArgumentNullException.ThrowIfNull(idemKey);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(idemKey), hash);
        Span<byte> bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public IReadOnlyCollection<AuctionBotCustodyRow> Rows => _rows.Values;

    public IEnumerable<AuctionBotCustodyRow> Pending => _rows.Values.Where(r => r.State == AuctionBotCustodyState.Reserved);

    public AuctionBotCustodyRow? Find(string idemKey) => _rows.GetValueOrDefault(idemKey);

    /// <summary>
    /// Reserve a new listing: the item does not exist yet. Refused (null) when the key was used, the GUID is held by another live
    /// listing, or the day's item budget is spent.
    /// </summary>
    public AuctionBotCustodyRow? TryReserveListing(uint houseId, uint auctionId, uint itemGuid, uint itemEntry, uint itemCount, long now)
    {
        if (auctionId == 0 || itemGuid == 0 || itemEntry == 0 || itemCount == 0)
        {
            return null;
        }

        string key = ListingKey(auctionId);
        if (_rows.ContainsKey(key)
            || _rows.Values.Any(r => r.Kind == AuctionBotCustodyKind.Item && r.ItemGuid == itemGuid && r.State != AuctionBotCustodyState.TerminalBack))
        {
            return null;
        }

        AuctionBotCustodyTotals totals = Totals(now);
        if ((ulong)totals.ItemsToday + 1 > options.DailyItemBudget)
        {
            return null;
        }

        var row = new AuctionBotCustodyRow(key, AuctionBotCustodyKind.Item, AuctionBotCustodyRole.Listing, AuctionBotCustodyState.Reserved,
            houseId, auctionId, itemGuid, itemEntry, itemCount, 0, now);
        _rows[key] = row;
        return row;
    }

    /// <summary>
    /// Reserve copper for buying out an auction. Refused (null) when the auction already has a live buyout row or was bought by the
    /// bot, the price is 0, or the day's copper budget cannot cover it.
    /// </summary>
    public AuctionBotCustodyRow? TryReserveBuyout(uint houseId, uint auctionId, uint itemGuid, uint itemEntry, uint itemCount, uint price, long now)
    {
        if (auctionId == 0 || price == 0)
        {
            return null;
        }

        if (_rows.Values.Any(r => r.Role == AuctionBotCustodyRole.Buyout && r.AuctionId == auctionId && r.State != AuctionBotCustodyState.TerminalBack))
        {
            return null;
        }

        AuctionBotCustodyTotals totals = Totals(now);
        if (totals.CopperToday + price > options.DailyBuyBudgetCopper)
        {
            return null;
        }

        int attempt = _buyoutAttempts.GetValueOrDefault(auctionId) + 1;
        _buyoutAttempts[auctionId] = attempt;
        string key = BuyoutKey(auctionId, attempt);
        var row = new AuctionBotCustodyRow(key, AuctionBotCustodyKind.Gold, AuctionBotCustodyRole.Buyout, AuctionBotCustodyState.Reserved,
            houseId, auctionId, itemGuid, itemEntry, itemCount, price, now);
        _rows[key] = row;
        return row;
    }

    /// <summary>
    /// Record a known outcome. Only a Reserved row moves; resolving a terminal row again does nothing and returns false, so a duplicate
    /// completion cannot count an item or a payment twice.
    /// </summary>
    public bool Resolve(string idemKey, bool committed, long now)
    {
        if (!_rows.TryGetValue(idemKey, out AuctionBotCustodyRow? row) || row.State != AuctionBotCustodyState.Reserved)
        {
            return false;
        }

        _rows[idemKey] = row with
        {
            State = committed ? AuctionBotCustodyState.TerminalOk : AuctionBotCustodyState.TerminalBack,
            ResolvedTime = now,
        };
        return true;
    }

    /// <summary>
    /// MaNGOS Zero CustodyReconciler: settle every Reserved row whose durable outcome is now known (<paramref name="outcome"/> returns
    /// true when the row's operation committed, false when it did not, null when still unknown). Returns how many rows moved.
    /// </summary>
    public int Reconcile(Func<AuctionBotCustodyRow, bool?> outcome, long now)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        int moved = 0;
        foreach (AuctionBotCustodyRow row in Pending.ToList())
        {
            if (outcome(row) is { } committed && Resolve(row.IdemKey, committed, now))
            {
                moved++;
            }
        }

        return moved;
    }

    /// <summary>Today's spent budgets (Reserved and committed rows created this UTC day) and the open reservations.</summary>
    public AuctionBotCustodyTotals Totals(long now)
    {
        long day = Day(now);
        uint items = 0;
        ulong copper = 0;
        int reservedItems = 0;
        ulong reservedCopper = 0;
        foreach (AuctionBotCustodyRow row in _rows.Values)
        {
            if (row.State == AuctionBotCustodyState.Reserved)
            {
                if (row.Kind == AuctionBotCustodyKind.Item)
                {
                    reservedItems++;
                }
                else
                {
                    reservedCopper += row.Amount;
                }
            }

            if (row.State == AuctionBotCustodyState.TerminalBack || Day(row.CreatedTime) != day)
            {
                continue;
            }

            if (row.Kind == AuctionBotCustodyKind.Item)
            {
                items++;
            }
            else
            {
                copper += row.Amount;
            }
        }

        return new AuctionBotCustodyTotals(items, copper, reservedItems, reservedCopper, _rows.Count);
    }

    /// <summary>
    /// The invariants the guards above keep; an empty list when they hold (MaNGOS Zero reconcile checks). A non-empty list means a
    /// defect, and the World side stops the bot.
    /// </summary>
    public IReadOnlyList<string> Audit()
    {
        var problems = new List<string>();
        foreach (var group in _rows.Values.Where(r => r.Kind == AuctionBotCustodyKind.Item && r.State != AuctionBotCustodyState.TerminalBack)
            .GroupBy(r => r.ItemGuid).Where(g => g.Count() > 1))
        {
            problems.Add($"item {group.Key} is held by {group.Count()} listings ({string.Join(", ", group.Select(r => r.IdemKey))})");
        }

        foreach (var group in _rows.Values.Where(r => r.Role == AuctionBotCustodyRole.Buyout && r.State != AuctionBotCustodyState.TerminalBack)
            .GroupBy(r => r.AuctionId).Where(g => g.Count() > 1))
        {
            problems.Add($"auction {group.Key} has {group.Count()} live or committed buyouts");
        }

        foreach (var day in _rows.Values.Where(r => r.State != AuctionBotCustodyState.TerminalBack).GroupBy(r => Day(r.CreatedTime)))
        {
            ulong copper = (ulong)day.Where(r => r.Kind == AuctionBotCustodyKind.Gold).Sum(r => (long)r.Amount);
            int items = day.Count(r => r.Kind == AuctionBotCustodyKind.Item);
            if (copper > options.DailyBuyBudgetCopper)
            {
                problems.Add($"day {day.Key}: {copper} copper paid, over the {options.DailyBuyBudgetCopper} budget");
            }

            if ((ulong)items > options.DailyItemBudget)
            {
                problems.Add($"day {day.Key}: {items} items created, over the {options.DailyItemBudget} budget");
            }
        }

        return problems;
    }

    /// <summary>MaNGOS Zero DeleteTerminalOlderThan: forget terminal rows resolved before <paramref name="cutoff"/>. Reserved rows always stay.</summary>
    public int PruneTerminal(long cutoff)
    {
        List<string> old = [.. _rows.Values.Where(r => r.State != AuctionBotCustodyState.Reserved && r.ResolvedTime < cutoff
            && Day(r.CreatedTime) < Day(cutoff)).Select(r => r.IdemKey)];
        foreach (string key in old)
        {
            _rows.Remove(key);
        }

        return old.Count;
    }

    private static long Day(long time) => time / SecondsPerDay;
}

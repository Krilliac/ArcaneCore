using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Kernel.Economy;

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

    /// <summary>
    /// Copper the bot bids on a player's auction (cMaNGOS AuctionBotBuyer bid). TerminalOk while the bid stands or after it won;
    /// <see cref="AuctionBotCustodyLedger.ReleaseBid"/> moves it to TerminalBack when a player outbids it, since no copper left.
    /// </summary>
    Bid = 2,
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

    public AuctionBotCustodyRecord ToRecord()
        => new(IdemKey, (byte)Kind, (byte)Role, (byte)State, HouseId, AuctionId, ItemGuid, ItemEntry, ItemCount, Amount, CreatedTime, ResolvedTime);

    /// <summary>The row of a persisted record, or null when its kind, role or state is not one this build knows.</summary>
    public static AuctionBotCustodyRow? FromRecord(AuctionBotCustodyRecord r)
        => r is null || r.Kind > 1 || r.Role > 2 || r.State > 2 || string.IsNullOrEmpty(r.IdemKey) ? null
            : new(r.IdemKey, (AuctionBotCustodyKind)r.Kind, (AuctionBotCustodyRole)r.Role, (AuctionBotCustodyState)r.State, r.HouseId,
                r.AuctionId, r.ItemGuid, r.ItemEntry, r.ItemCount, r.Amount, r.CreatedTime, r.ResolvedTime);
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
/// Every change is also queued for the durable copy (<see cref="TakeChanges"/>, characters <c>ahbot_custody</c>); after a restart
/// <see cref="Load"/> brings the rows back and the World side rechecks every Reserved one against the economy operation ledger.
/// World thread only.
/// </summary>
public sealed class AuctionBotCustodyLedger(AuctionBotOptions options)
{
    public const long SecondsPerDay = 86_400;

    private readonly Dictionary<string, AuctionBotCustodyRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, int> _buyoutAttempts = [];
    private readonly Dictionary<uint, int> _bidAttempts = [];
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settledBids = new(StringComparer.Ordinal);

    public static string ListingKey(uint auctionId) => $"ahbot:list:{auctionId}";

    public static string BuyoutKey(uint auctionId, int attempt) => $"ahbot:buy:{auctionId}:{attempt}";

    public static string BidKey(uint auctionId, int attempt) => $"ahbot:bid:{auctionId}:{attempt}";

    /// <summary>The highest auction id any row names (the auction id allocator must stay above it, so a listing key is never reused).</summary>
    public uint MaxAuctionId => _rows.Count == 0 ? 0 : _rows.Values.Max(r => r.AuctionId);

    /// <summary>
    /// Replace the in-memory rows with persisted ones (after a restart). Unknown records are skipped and counted. The attempt counters
    /// resume above the loaded keys so a new attempt never reuses one.
    /// </summary>
    public int Load(IEnumerable<AuctionBotCustodyRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _rows.Clear();
        _buyoutAttempts.Clear();
        _bidAttempts.Clear();
        _changed.Clear();
        _deleted.Clear();
        int skipped = 0;
        foreach (AuctionBotCustodyRecord record in records)
        {
            if (AuctionBotCustodyRow.FromRecord(record) is not { } row)
            {
                skipped++;
                continue;
            }

            _rows[row.IdemKey] = row;
            Dictionary<uint, int>? attempts = row.Role switch
            {
                AuctionBotCustodyRole.Buyout => _buyoutAttempts,
                AuctionBotCustodyRole.Bid => _bidAttempts,
                _ => null,
            };
            if (attempts is not null && int.TryParse(row.IdemKey.AsSpan(row.IdemKey.LastIndexOf(':') + 1), out int attempt))
            {
                attempts[row.AuctionId] = Math.Max(attempts.GetValueOrDefault(row.AuctionId), attempt);
            }
        }

        return skipped;
    }

    /// <summary>Changed rows and deleted keys since the last call, for the durable copy. Taking them clears the queue.</summary>
    public (IReadOnlyList<AuctionBotCustodyRecord> Rows, IReadOnlyList<string> Deleted) TakeChanges()
    {
        List<AuctionBotCustodyRecord> rows = [.. _changed.Where(_rows.ContainsKey).Select(k => _rows[k].ToRecord())];
        List<string> deleted = [.. _deleted];
        _changed.Clear();
        _deleted.Clear();
        return (rows, deleted);
    }

    /// <summary>Put changes that failed to persist back on the queue (a newer change of the same key wins).</summary>
    public void Requeue(IEnumerable<AuctionBotCustodyRecord> rows, IEnumerable<string> deleted)
    {
        foreach (AuctionBotCustodyRecord row in rows)
        {
            if (_rows.ContainsKey(row.IdemKey))
            {
                _changed.Add(row.IdemKey);
            }
        }

        foreach (string key in deleted)
        {
            if (!_rows.ContainsKey(key))
            {
                _deleted.Add(key);
            }
        }
    }

    /// <summary>The bot won the auction its standing bid named (or it is gone): the bid row is final and may be pruned.</summary>
    public bool SettleBid(string idemKey)
        => _rows.TryGetValue(idemKey, out AuctionBotCustodyRow? row) && row.Role == AuctionBotCustodyRole.Bid
            && row.State == AuctionBotCustodyState.TerminalOk && _settledBids.Add(idemKey);

    public bool HasChanges => _changed.Count > 0 || _deleted.Count > 0;

    private void Put(AuctionBotCustodyRow row)
    {
        _rows[row.IdemKey] = row;
        _changed.Add(row.IdemKey);
        _deleted.Remove(row.IdemKey);
    }

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
        Put(row);
        return row;
    }

    /// <summary>
    /// Reserve copper for buying out an auction. Refused (null) when the auction already has a live buyout or bid row or was bought
    /// by the bot, the price is 0, or the day's copper budget cannot cover it.
    /// </summary>
    public AuctionBotCustodyRow? TryReserveBuyout(uint houseId, uint auctionId, uint itemGuid, uint itemEntry, uint itemCount, uint price, long now)
    {
        if (auctionId == 0 || price == 0)
        {
            return null;
        }

        if (_rows.Values.Any(r => r.Role != AuctionBotCustodyRole.Listing && r.AuctionId == auctionId && r.State != AuctionBotCustodyState.TerminalBack))
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
        Put(row);
        return row;
    }

    /// <summary>
    /// Reserve copper for a bid on an auction. Refused (null) when the auction already has a live bid or buyout row (the bot never
    /// bids against itself), the price is 0, or the day's copper budget cannot cover it.
    /// </summary>
    public AuctionBotCustodyRow? TryReserveBid(uint houseId, uint auctionId, uint itemGuid, uint itemEntry, uint itemCount, uint price, long now)
    {
        if (auctionId == 0 || price == 0)
        {
            return null;
        }

        if (_rows.Values.Any(r => r.Role != AuctionBotCustodyRole.Listing && r.AuctionId == auctionId && r.State != AuctionBotCustodyState.TerminalBack))
        {
            return null;
        }

        if (Totals(now).CopperToday + price > options.DailyBuyBudgetCopper)
        {
            return null;
        }

        int attempt = _bidAttempts.GetValueOrDefault(auctionId) + 1;
        _bidAttempts[auctionId] = attempt;
        var row = new AuctionBotCustodyRow(BidKey(auctionId, attempt), AuctionBotCustodyKind.Gold, AuctionBotCustodyRole.Bid,
            AuctionBotCustodyState.Reserved, houseId, auctionId, itemGuid, itemEntry, itemCount, price, now);
        Put(row);
        return row;
    }

    /// <summary>The standing (committed) bid row on <paramref name="auctionId"/>, or null.</summary>
    public AuctionBotCustodyRow? StandingBid(uint auctionId)
        => _rows.Values.FirstOrDefault(r => r.Role == AuctionBotCustodyRole.Bid && r.AuctionId == auctionId && r.State == AuctionBotCustodyState.TerminalOk
            && r.ResolvedTime >= 0);

    /// <summary>
    /// A player outbid (or the seller cancelled under) a standing bot bid: no copper left the bot, so the row moves to TerminalBack and
    /// frees its budget. Only a committed bid row moves; false otherwise.
    /// </summary>
    public bool ReleaseBid(string idemKey, long now)
    {
        if (!_rows.TryGetValue(idemKey, out AuctionBotCustodyRow? row) || row.Role != AuctionBotCustodyRole.Bid || row.State != AuctionBotCustodyState.TerminalOk)
        {
            return false;
        }

        Put(row with { State = AuctionBotCustodyState.TerminalBack, ResolvedTime = now });
        return true;
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

        Put(row with
        {
            State = committed ? AuctionBotCustodyState.TerminalOk : AuctionBotCustodyState.TerminalBack,
            ResolvedTime = now,
        });
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

        foreach (var group in _rows.Values.Where(r => r.Role == AuctionBotCustodyRole.Bid && r.State != AuctionBotCustodyState.TerminalBack)
            .GroupBy(r => r.AuctionId).Where(g => g.Count() > 1))
        {
            problems.Add($"auction {group.Key} has {group.Count()} live or standing bot bids");
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
        // A standing bid (TerminalOk bid row) still holds its auction; it is kept until the bid is released or the auction is gone.
        List<string> old = [.. _rows.Values.Where(r => r.State != AuctionBotCustodyState.Reserved && r.ResolvedTime < cutoff
            && Day(r.CreatedTime) < Day(cutoff) && !(r.Role == AuctionBotCustodyRole.Bid && r.State == AuctionBotCustodyState.TerminalOk && !_settledBids.Contains(r.IdemKey)))
            .Select(r => r.IdemKey)];
        foreach (string key in old)
        {
            _rows.Remove(key);
            _changed.Remove(key);
            _settledBids.Remove(key);
            _deleted.Add(key);
        }

        return old.Count;
    }

    private static long Day(long time) => time / SecondsPerDay;
}

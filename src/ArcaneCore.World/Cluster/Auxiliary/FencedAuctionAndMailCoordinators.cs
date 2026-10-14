using System.Collections.Concurrent;
using ArcaneCore.Kernel.Cluster.Auxiliary;

namespace ArcaneCore.World.Cluster.Auxiliary;

/// <summary>
/// Single fenced realm auction-house-bot intent processor and custody ledger (<c>docs/CLUSTERING_DESIGN.md</c> §8 Auction house bot, §10 M15.6).
/// Revalidates every remote planner proposal against the active <c>OwnerFencingEpoch</c>, price bounds, blacklist,
/// one-live-buy-per-auction rule, and daily budgets, while recording <see cref="AuctionBotIntentId"/> durably with
/// the custody row so replayed intents return <see cref="AuctionBotIntentOutcome.Duplicate"/> without double-minting
/// items or double-spending copper.
/// </summary>
public sealed class FencedAuctionBotIntentProcessor : IAuctionBotIntentChannel
{
    public sealed record CustodyLedgerEntry(
        long LedgerKey,
        AuctionBotIntentId IntentId,
        uint HouseId,
        bool IsListing,
        uint ItemEntryOrAuctionId,
        uint ItemCount,
        uint CopperCommitted,
        long OwnerFencingEpoch);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AuctionBotIntentResult> _durableIntentResults = new(StringComparer.Ordinal);
    private readonly Dictionary<long, CustodyLedgerEntry> _custodyLedger = [];
    private readonly Dictionary<(uint HouseId, uint AuctionId), AuctionMarketListingSnapshot> _openAuctions = [];
    private readonly HashSet<(uint HouseId, uint AuctionId)> _activeBuyCustodyLocks = [];
    private readonly HashSet<uint> _blacklistedItems;

    private long _activeOwnerFencingEpoch;
    private long _nextLedgerKey = 1;
    private uint _nextAuctionId = 10_000;
    private long _marketVersion = 1;
    private readonly ulong _dailyBuyBudgetCopper;
    private ulong _dailyBuySpentCopper;
    private readonly int _dailyListingBudget;
    private int _dailyListingsCreated;
    private readonly int _maxPendingQueueDepth;
    private int _currentPendingDepth;

    public FencedAuctionBotIntentProcessor(
        long initialOwnerFencingEpoch,
        ulong dailyBuyBudgetCopper = 1_000_000,
        int dailyListingBudget = 500,
        IEnumerable<uint>? blacklistedItems = null,
        int maxPendingQueueDepth = 64)
    {
        if (initialOwnerFencingEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialOwnerFencingEpoch), "initialOwnerFencingEpoch must be positive");
        }

        if (maxPendingQueueDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingQueueDepth), "maxPendingQueueDepth must be positive");
        }

        _activeOwnerFencingEpoch = initialOwnerFencingEpoch;
        _dailyBuyBudgetCopper = dailyBuyBudgetCopper;
        _dailyListingBudget = dailyListingBudget;
        _blacklistedItems = blacklistedItems is not null ? [.. blacklistedItems] : [];
        _maxPendingQueueDepth = maxPendingQueueDepth;
    }

    public long ActiveOwnerFencingEpoch
    {
        get
        {
            lock (_gate)
            {
                return _activeOwnerFencingEpoch;
            }
        }
    }

    public ulong TotalCopperSpent
    {
        get
        {
            lock (_gate)
            {
                return _dailyBuySpentCopper;
            }
        }
    }

    public int TotalListingsCreated
    {
        get
        {
            lock (_gate)
            {
                return _dailyListingsCreated;
            }
        }
    }

    public IReadOnlyCollection<CustodyLedgerEntry> CustodyEntries
    {
        get
        {
            lock (_gate)
            {
                return [.. _custodyLedger.Values];
            }
        }
    }

    public void AdvanceOwnerFencingEpoch(long newEpoch)
    {
        lock (_gate)
        {
            if (newEpoch <= _activeOwnerFencingEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(newEpoch), $"newEpoch ({newEpoch}) must exceed current epoch ({_activeOwnerFencingEpoch})");
            }

            _activeOwnerFencingEpoch = newEpoch;
        }
    }

    public void SetPendingQueueDepth(int depth)
    {
        if (depth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "depth cannot be negative");
        }

        lock (_gate)
        {
            _currentPendingDepth = depth;
        }
    }

    /// <summary>Seeds an open player auction into the market snapshot so the remote planner can propose buys against it.</summary>
    public void SeedOpenAuction(uint houseId, AuctionMarketListingSnapshot listing)
    {
        lock (_gate)
        {
            _openAuctions[(houseId, listing.AuctionId)] = listing;
            _marketVersion++;
        }
    }

    public ValueTask<AuctionBotIntentResult> ProposeListingAsync(
        AuctionBotListingIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            string key = intent.IntentId.ToKey();
            if (_durableIntentResults.TryGetValue(key, out AuctionBotIntentResult? prior))
            {
                return ValueTask.FromResult(prior with { Outcome = AuctionBotIntentOutcome.Duplicate });
            }

            if (_currentPendingDepth >= _maxPendingQueueDepth)
            {
                return ValueTask.FromResult(new AuctionBotIntentResult(
                    intent.IntentId,
                    AuctionBotIntentOutcome.QueueFull,
                    "intent queue is full",
                    LedgerKey: 0));
            }

            if (intent.OwnerFencingEpoch != _activeOwnerFencingEpoch)
            {
                var fencedOut = new AuctionBotIntentResult(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    $"stale owner fencing epoch {intent.OwnerFencingEpoch} != active {_activeOwnerFencingEpoch}",
                    LedgerKey: 0);
                return ValueTask.FromResult(fencedOut);
            }

            if (intent.ItemEntry == 0 || intent.Count == 0 || intent.Hours == 0)
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    "invalid item entry, count, or duration",
                    ledgerKey: 0));
            }

            if (_blacklistedItems.Contains(intent.ItemEntry))
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    $"item {intent.ItemEntry} is blacklisted",
                    ledgerKey: 0));
            }

            if (intent.StartBid == 0 || (intent.Buyout != 0 && intent.Buyout < intent.StartBid))
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    "invalid listing price bounds (start_bid must be > 0 and buyout >= start_bid)",
                    ledgerKey: 0));
            }

            if (_dailyListingsCreated >= _dailyListingBudget)
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    "daily listing budget exhausted",
                    ledgerKey: 0));
            }

            long ledgerKey = _nextLedgerKey++;
            uint auctionId = _nextAuctionId++;
            _dailyListingsCreated++;

            var custody = new CustodyLedgerEntry(
                LedgerKey: ledgerKey,
                IntentId: intent.IntentId,
                HouseId: intent.HouseId,
                IsListing: true,
                ItemEntryOrAuctionId: intent.ItemEntry,
                ItemCount: intent.Count,
                CopperCommitted: 0,
                OwnerFencingEpoch: _activeOwnerFencingEpoch);
            _custodyLedger[ledgerKey] = custody;

            _openAuctions[(intent.HouseId, auctionId)] = new AuctionMarketListingSnapshot(
                AuctionId: auctionId,
                ItemEntry: intent.ItemEntry,
                Count: intent.Count,
                CurrentBid: intent.StartBid,
                Buyout: intent.Buyout);
            _marketVersion++;

            return ValueTask.FromResult(RecordTerminalResultLocked(
                intent.IntentId,
                AuctionBotIntentOutcome.Ok,
                reason: null,
                ledgerKey: ledgerKey));
        }
    }

    public ValueTask<AuctionBotIntentResult> ProposeBuyAsync(
        AuctionBotBuyIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            string key = intent.IntentId.ToKey();
            if (_durableIntentResults.TryGetValue(key, out AuctionBotIntentResult? prior))
            {
                return ValueTask.FromResult(prior with { Outcome = AuctionBotIntentOutcome.Duplicate });
            }

            if (_currentPendingDepth >= _maxPendingQueueDepth)
            {
                return ValueTask.FromResult(new AuctionBotIntentResult(
                    intent.IntentId,
                    AuctionBotIntentOutcome.QueueFull,
                    "intent queue is full",
                    LedgerKey: 0));
            }

            if (intent.OwnerFencingEpoch != _activeOwnerFencingEpoch)
            {
                return ValueTask.FromResult(new AuctionBotIntentResult(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    $"stale owner fencing epoch {intent.OwnerFencingEpoch} != active {_activeOwnerFencingEpoch}",
                    LedgerKey: 0));
            }

            if (!_openAuctions.TryGetValue((intent.HouseId, intent.AuctionId), out AuctionMarketListingSnapshot listing))
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    $"auction {intent.AuctionId} on house {intent.HouseId} is no longer open",
                    ledgerKey: 0));
            }

            // One-live-row rule per auction + observed bid optimistic concurrency check (CLUSTERING_DESIGN.md:508).
            if (_activeBuyCustodyLocks.Contains((intent.HouseId, intent.AuctionId)))
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    $"auction {intent.AuctionId} already has a live bot custody row",
                    ledgerKey: 0));
            }

            if (listing.CurrentBid != intent.ObservedBid)
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    $"observed bid {intent.ObservedBid} != current bid {listing.CurrentBid}",
                    ledgerKey: 0));
            }

            if (intent.Price == 0 || (intent.IsBuyout && (listing.Buyout == 0 || intent.Price < listing.Buyout)))
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    "invalid buy price for auction",
                    ledgerKey: 0));
            }

            if (_dailyBuySpentCopper + intent.Price > _dailyBuyBudgetCopper)
            {
                return ValueTask.FromResult(RecordTerminalResultLocked(
                    intent.IntentId,
                    AuctionBotIntentOutcome.Rejected,
                    "daily buy copper budget exhausted",
                    ledgerKey: 0));
            }

            long ledgerKey = _nextLedgerKey++;
            _dailyBuySpentCopper += intent.Price;
            _activeBuyCustodyLocks.Add((intent.HouseId, intent.AuctionId));

            var custody = new CustodyLedgerEntry(
                LedgerKey: ledgerKey,
                IntentId: intent.IntentId,
                HouseId: intent.HouseId,
                IsListing: false,
                ItemEntryOrAuctionId: intent.AuctionId,
                ItemCount: listing.Count,
                CopperCommitted: intent.Price,
                OwnerFencingEpoch: _activeOwnerFencingEpoch);
            _custodyLedger[ledgerKey] = custody;

            if (intent.IsBuyout)
            {
                _openAuctions.Remove((intent.HouseId, intent.AuctionId));
            }
            else
            {
                _openAuctions[(intent.HouseId, intent.AuctionId)] = listing with { CurrentBid = intent.Price };
            }

            _marketVersion++;

            return ValueTask.FromResult(RecordTerminalResultLocked(
                intent.IntentId,
                AuctionBotIntentOutcome.Ok,
                reason: null,
                ledgerKey: ledgerKey));
        }
    }

    public ValueTask<IReadOnlyDictionary<AuctionBotIntentId, AuctionBotIntentResult>> QueryOutstandingIntentsAsync(
        IReadOnlyList<AuctionBotIntentId> intentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intentIds);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var found = new Dictionary<AuctionBotIntentId, AuctionBotIntentResult>(intentIds.Count);
            foreach (AuctionBotIntentId id in intentIds)
            {
                if (_durableIntentResults.TryGetValue(id.ToKey(), out AuctionBotIntentResult? res))
                {
                    found[id] = res;
                }
            }

            return ValueTask.FromResult<IReadOnlyDictionary<AuctionBotIntentId, AuctionBotIntentResult>>(found);
        }
    }

    public AuctionMarketSnapshot GetMarketSnapshot(uint houseId)
    {
        lock (_gate)
        {
            List<AuctionMarketListingSnapshot> rows = [];
            foreach (KeyValuePair<(uint HouseId, uint AuctionId), AuctionMarketListingSnapshot> kvp in _openAuctions)
            {
                if (kvp.Key.HouseId == houseId)
                {
                    rows.Add(kvp.Value);
                }
            }

            return new AuctionMarketSnapshot(houseId, _marketVersion, rows);
        }
    }

    /// <summary>
    /// Verifies that the custody ledger has no duplicate <see cref="AuctionBotIntentId"/>s, that total committed
    /// copper equals <see cref="TotalCopperSpent"/>, and that total listing rows equal <see cref="TotalListingsCreated"/>.
    /// </summary>
    public bool AuditCustody(out string? auditFailure)
    {
        lock (_gate)
        {
            var seenIntents = new HashSet<string>(StringComparer.Ordinal);
            ulong copperSum = 0;
            int listingCount = 0;

            foreach (CustodyLedgerEntry entry in _custodyLedger.Values)
            {
                if (!seenIntents.Add(entry.IntentId.ToKey()))
                {
                    auditFailure = $"duplicate intent_id '{entry.IntentId}' in custody ledger";
                    return false;
                }

                if (entry.IsListing)
                {
                    listingCount++;
                }
                else
                {
                    copperSum += entry.CopperCommitted;
                }
            }

            if (copperSum != _dailyBuySpentCopper)
            {
                auditFailure = $"custody copper sum ({copperSum}) != spent ledger ({_dailyBuySpentCopper})";
                return false;
            }

            if (listingCount != _dailyListingsCreated)
            {
                auditFailure = $"custody listing count ({listingCount}) != created count ({_dailyListingsCreated})";
                return false;
            }

            auditFailure = null;
            return true;
        }
    }

    private AuctionBotIntentResult RecordTerminalResultLocked(
        AuctionBotIntentId intentId,
        AuctionBotIntentOutcome outcome,
        string? reason,
        long ledgerKey)
    {
        var result = new AuctionBotIntentResult(intentId, outcome, reason, ledgerKey);
        _durableIntentResults[intentId.ToKey()] = result;
        return result;
    }
}

/// <summary>
/// Coordinates deterministic server-mail operation IDs across retries and worker relogs (<c>docs/CLUSTERING_DESIGN.md</c> §8 Server mail, lines 543–551).
/// Ensures that a <c>(RealmId, CharacterId, TemplateId)</c> reward can never be committed or delivered twice even if a player
/// relogs onto another map worker before <c>mail_server_character</c> settles.
/// </summary>
public sealed class ClusterServerMailCoordinator
{
    private enum DeliveryState : byte
    {
        InFlight = 1,
        Committed = 2,
    }

    private readonly int _realmId;
    private readonly ConcurrentDictionary<string, DeliveryState> _operations = new(StringComparer.Ordinal);

    public ClusterServerMailCoordinator(int realmId = 1)
    {
        if (realmId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId), "realmId must be positive");
        }

        _realmId = realmId;
    }

    /// <summary>
    /// Attempts to begin a server-mail reward delivery for <c>(characterId, templateId)</c>, returning the deterministic
    /// <see cref="ServerMailIdempotencyKey"/> when admitted, or <see langword="false"/> if the operation is already
    /// in flight or committed.
    /// </summary>
    public bool TryBeginDelivery(int characterId, uint templateId, out ServerMailIdempotencyKey idempotencyKey)
    {
        idempotencyKey = ServerMailIdempotencyKey.Derive(_realmId, characterId, templateId);
        return _operations.TryAdd(idempotencyKey.OperationId, DeliveryState.InFlight);
    }

    /// <summary>Marks an in-flight server-mail operation as durably committed.</summary>
    public void CompleteDelivery(ServerMailIdempotencyKey idempotencyKey)
    {
        _operations[idempotencyKey.OperationId] = DeliveryState.Committed;
    }

    /// <summary>Releases an in-flight reservation when the underlying database transaction rolled back without committing.</summary>
    public void AbortDelivery(ServerMailIdempotencyKey idempotencyKey)
    {
        _operations.TryRemove(
            new KeyValuePair<string, DeliveryState>(idempotencyKey.OperationId, DeliveryState.InFlight));
    }

    public bool IsCommitted(int characterId, uint templateId)
    {
        var key = ServerMailIdempotencyKey.Derive(_realmId, characterId, templateId);
        return _operations.TryGetValue(key.OperationId, out DeliveryState state) && state == DeliveryState.Committed;
    }
}

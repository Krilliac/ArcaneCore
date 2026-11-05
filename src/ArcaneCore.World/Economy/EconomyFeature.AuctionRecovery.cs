using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Economy;

public sealed partial class EconomyFeature
{
    /// <summary>Period of the recovery timer; a failed read is retried on the next tick for the first few failures.</summary>
    private static readonly TimeSpan RecoveryTick = TimeSpan.FromSeconds(1);

    private const int ImmediateRetries = 3;
    private const long MaxRetryMs = 300_000;
    private const long MismatchBaseMs = 30_000;

    private readonly Dictionary<uint, AuctionRecovery> _auctionRecoveries = [];
    private Timer? _auctionRecoveryTimer;

    /// <summary>World thread: an auction remains reserved until fresh storage reads repair its cache.</summary>
    public bool IsAuctionQuarantined(uint auctionId) => _auctionRecoveries.ContainsKey(auctionId);

    /// <summary>World-thread test probe for the recovery deadline after a completed read.</summary>
    internal long? AuctionRecoveryNotBeforeMs(uint auctionId)
        => _auctionRecoveries.TryGetValue(auctionId, out AuctionRecovery? recovery) ? recovery.NotBeforeMs : null;

    // True from Attach on a clustered node that did not hold the Economy lease then: the auction cache was read at Attach and the holder has written since,
    // so it is replaced by a fresh snapshot the first time this node finds the lease held (world thread).
    private bool _auctionCacheStale;
    private bool _auctionRefreshing;

    /// <summary>
    /// World thread: whether the auction cache is current for this node's holding of the Economy lease. Always true on a node that held the lease at attach (or runs no keeper).
    /// On a node that took the lease over later it is false until a fresh snapshot of the auctions has replaced the cache: the first call that finds the lease held starts the read
    /// (off the world thread), and the expiry sweep, the recovery sweep and the auction bot act only once this is true. A failed read is tried again by the next call.
    /// </summary>
    public bool EnsureAuctionCacheCurrent()
    {
        if (!_auctionCacheStale)
        {
            return true;
        }

        if (HoldsEconomyLease && Enabled && !_stopping)
        {
            BeginAuctionRefresh();
        }

        return false;
    }

    private void BeginAuctionRefresh()
    {
        if (_auctionRefreshing)
        {
            return;
        }

        _auctionRefreshing = true;
        Read(async (store, ct) =>
        {
            // Rows and escrow from one snapshot, the id seed read after it (as Attach does), so every row seen is at or below the seed.
            AuctionSnapshot snapshot = await store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter(), ct).ConfigureAwait(false);
            EconomyIdSeed seed = await store.GetIdSeedAsync(ct).ConfigureAwait(false);
            return (Snapshot: snapshot, Seed: seed);
        }, result =>
        {
            _auctionRefreshing = false;
            if (_stopping || !_auctionCacheStale)
            {
                return;
            }

            ApplyAuctionSnapshot(result.Snapshot, result.Seed);
            _auctionCacheStale = false;
            _logger.LogInformation("Auction cache reloaded after taking over the economy lease: {Auctions} auctions ({Reserved} reserved)", _auctions.Count, _auctionRecoveries.Count);
        }, () => _auctionRefreshing = false);
    }

    /// <summary>World thread: replace the auction cache with <paramref name="snapshot"/> (rows the cache holds and the snapshot lacks are gone; a row whose escrow item does not match is reserved, as at Attach) and raise the id allocators.</summary>
    private void ApplyAuctionSnapshot(AuctionSnapshot snapshot, EconomyIdSeed seed)
    {
        RaiseTo(ref _lastMailId, seed.MaxMailId);
        RaiseTo(ref _lastAuctionId, seed.MaxAuctionId);
        RaiseTo(ref _lastTextId, seed.MaxItemTextId);
        HashSet<uint> present = [.. snapshot.Auctions.Select(a => a.Id)];
        foreach (uint gone in _auctions.Keys.Where(id => !present.Contains(id)).ToList())
        {
            _auctions.Remove(gone);
            _expiryBackoff.Remove(gone);
            _auctionRecoveries.Remove(gone);
            _busyAuctions.Remove(gone);
        }

        foreach (AuctionRecord auction in snapshot.Auctions)
        {
            if (snapshot.Escrow.TryGetValue(auction.ItemGuid, out ItemInstanceData? item) && EscrowMatches(auction, item))
            {
                if (!_auctions.TryGetValue(auction.Id, out AuctionView? previous) || previous.Auction != auction)
                {
                    _expiryBackoff.Remove(auction.Id);
                }

                _auctions[auction.Id] = new AuctionView(auction, item);
                if (_auctionRecoveries.Remove(auction.Id))
                {
                    _busyAuctions.Remove(auction.Id);
                }
            }
            else
            {
                _auctions.Remove(auction.Id);
                ReserveMismatchedAuction(auction);
            }
        }
    }

    private long _recoveryReads;

    /// <summary>Recovery reads this node has started (tests: a node without the economy lease starts none).</summary>
    internal long RecoveryReadsStarted => Interlocked.Read(ref _recoveryReads);

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private void QuarantineAuction(uint auctionId, IReadOnlyList<EconomyChange> changes)
    {
        AuctionRecord[] affected = changes.SelectMany(change => change switch
        {
            InsertAuction insert => new[] { insert.Auction },
            UpdateAuction update => new[] { update.Expected, update.Updated },
            DeleteAuction delete => new[] { delete.Expected },
            _ => Array.Empty<AuctionRecord>(),
        }).Where(row => row.Id == auctionId).ToArray();
        _auctionRecoveries.TryAdd(auctionId, new AuctionRecovery(affected));
        if (_stopping)
        {
            return;
        }

        // Recovery remains available even when the optional expiry sweep is disabled.
        EnsureRecoveryTimer();
        RecoverAuctions();
    }

    /// <summary>
    /// World thread: reserve an auction whose row is readable but whose escrow item does not match
    /// (startup load, deletion resync). It is neither listed nor expired until a later authoritative
    /// read finds a matching escrow item. The first read waits one mismatch backoff because the
    /// caller has just observed the mismatch itself.
    /// </summary>
    private void ReserveMismatchedAuction(AuctionRecord auction)
    {
        _auctionRecoveries[auction.Id] = new AuctionRecovery([auction])
        {
            Mismatch = true,
            Mismatches = 1,
            NotBeforeMs = NowMs + MismatchBaseMs,
        };
        _busyAuctions.Add(auction.Id);
        EnsureRecoveryTimer();
    }

    private void EnsureRecoveryTimer()
    {
        if (_stopping)
        {
            return;
        }

        _auctionRecoveryTimer ??= new Timer(_ => _world?.Post(RecoverAuctions), null, RecoveryTick, RecoveryTick);
    }

    private static bool EscrowMatches(AuctionRecord auction, ItemInstanceData item)
        => item.Guid == auction.ItemGuid && item.Entry == auction.ItemEntry && item.Count == auction.ItemCount;

    internal void RecoverAuctions()
    {
        // The recovery timer is not the sweep: a node that is not the economy lease holder never settles a reserved auction either.
        if (_stopping || !HoldsEconomyLease || !EnsureAuctionCacheCurrent())
        {
            return;
        }

        long nowMs = NowMs;
        foreach ((uint id, AuctionRecovery recovery) in _auctionRecoveries
            .Where(p => !p.Value.InFlight && p.Value.NotBeforeMs <= nowMs).Take(ExpiryBatch).ToArray())
        {
            recovery.InFlight = true;
            Interlocked.Increment(ref _recoveryReads);
            Read(async (store, ct) =>
            {
                // One transaction for row and escrow: a concurrent writer cannot be seen half-applied.
                AuctionSnapshot snapshot = await store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter(AuctionId: id), ct).ConfigureAwait(false);
                AuctionRecord? row = snapshot.Auctions.SingleOrDefault(a => a.Id == id);
                if (row is null)
                {
                    return new RecoveredAuction(null, null);
                }

                return snapshot.Escrow.TryGetValue(row.ItemGuid, out ItemInstanceData? item) && EscrowMatches(row, item)
                    ? new RecoveredAuction(row, item)
                    : new RecoveredAuction(row, null);
            }, result =>
            {
                if (_stopping || !_auctionRecoveries.TryGetValue(id, out AuctionRecovery? current) || !ReferenceEquals(current, recovery))
                {
                    return;
                }

                // Patch only the reserved ID. Other active auctions may have changed while
                // these reads were in flight and must never be replaced by this snapshot.
                if (result.Row is { } row && result.Item is { } item)
                {
                    if (!_auctions.TryGetValue(id, out AuctionView? previous) || previous.Auction != row)
                    {
                        _expiryBackoff.Remove(id);
                    }

                    _auctions[id] = new AuctionView(row, item);
                }
                else if (result.Row is { } mismatched)
                {
                    // Stays reserved (never listed, bid on, cancelled or expired) until the escrow is repaired.
                    recovery.InFlight = false;
                    recovery.Mismatches++;
                    recovery.NotBeforeMs = NowMs + Math.Min(MismatchBaseMs << Math.Min(recovery.Mismatches - 1, 4), MaxRetryMs);
                    if (!recovery.Mismatch)
                    {
                        recovery.Mismatch = true;
                        _logger.LogError("auction {Auction} has no matching authoritative escrow item {Item}; it stays reserved and unlisted until the item is repaired",
                            id, mismatched.ItemGuid);
                    }

                    return;
                }
                else
                {
                    _auctions.Remove(id);
                    _expiryBackoff.Remove(id);
                }

                _auctionRecoveries.Remove(id);
                _busyAuctions.Remove(id);
            }, () =>
            {
                if (_auctionRecoveries.TryGetValue(id, out AuctionRecovery? current) && ReferenceEquals(current, recovery))
                {
                    recovery.InFlight = false;
                    recovery.ReadFailures++;
                    // The recovery timer already retries every second; only a persistent outage backs off.
                    recovery.NotBeforeMs = recovery.ReadFailures <= ImmediateRetries
                        ? 0
                        : NowMs + Math.Min(1000L << Math.Min(recovery.ReadFailures - ImmediateRetries, 9), MaxRetryMs);
                }
            });
        }
    }

    private void InvalidateDeletedAuctionRecoveries(int characterId, IReadOnlyDictionary<uint, AuctionRecord> stillSold)
    {
        foreach ((uint id, AuctionRecovery recovery) in _auctionRecoveries.Where(pair =>
            pair.Value.Affected.Any(row => row.SellerId == characterId || row.BidderId == characterId)
            || (_auctions.TryGetValue(pair.Key, out AuctionView? cached)
                && (cached.Auction.SellerId == characterId || cached.Auction.BidderId == characterId))).ToArray())
        {
            if (recovery.Affected.Any(row => row.SellerId == characterId) && !stillSold.ContainsKey(id))
            {
                // Deletion's fresh authoritative row set proves the auction is gone.
                _auctionRecoveries.Remove(id);
                _busyAuctions.Remove(id);
                _auctions.Remove(id);
                _expiryBackoff.Remove(id);
            }
            else
            {
                // Already queued reads retain the old identity and cannot publish. A
                // retained/neutralized auction stays reserved until the new read finishes.
                // The fresh entry carries no backoff or mismatch state: its read is immediate.
                _auctionRecoveries[id] = new AuctionRecovery(recovery.Affected);
            }
        }
    }

    private sealed class AuctionRecovery(IReadOnlyList<AuctionRecord> affected)
    {
        public IReadOnlyList<AuctionRecord> Affected { get; } = affected;
        public bool InFlight { get; set; }

        /// <summary>Feature-clock milliseconds before which no read is started (0 = none).</summary>
        public long NotBeforeMs { get; set; }

        public int ReadFailures { get; set; }
        public int Mismatches { get; set; }

        /// <summary>The row is readable but its escrow item does not match.</summary>
        public bool Mismatch { get; set; }
    }

    /// <summary>Row null: gone. Row with null item: escrow missing or different. Both set: valid.</summary>
    private sealed record RecoveredAuction(AuctionRecord? Row, ItemInstanceData? Item);
}

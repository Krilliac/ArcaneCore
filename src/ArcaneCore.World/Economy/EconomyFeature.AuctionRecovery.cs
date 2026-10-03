using System.Runtime.CompilerServices;
using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("ArcaneCore.World.Tests")]

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

    private void RecoverAuctions()
    {
        if (_stopping)
        {
            return;
        }

        long nowMs = NowMs;
        foreach ((uint id, AuctionRecovery recovery) in _auctionRecoveries
            .Where(p => !p.Value.InFlight && p.Value.NotBeforeMs <= nowMs).Take(ExpiryBatch).ToArray())
        {
            recovery.InFlight = true;
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

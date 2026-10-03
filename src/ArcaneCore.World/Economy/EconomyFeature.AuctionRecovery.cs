using System.Runtime.CompilerServices;
using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;

[assembly: InternalsVisibleTo("ArcaneCore.World.Tests")]

namespace ArcaneCore.World.Economy;

public sealed partial class EconomyFeature
{
    private readonly Dictionary<uint, AuctionRecovery> _auctionRecoveries = [];
    private Timer? _auctionRecoveryTimer;

    /// <summary>World thread: an auction remains reserved until fresh storage reads repair its cache.</summary>
    public bool IsAuctionQuarantined(uint auctionId) => _auctionRecoveries.ContainsKey(auctionId);

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
        _auctionRecoveryTimer ??= new Timer(_ => _world?.Post(RecoverAuctions), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        RecoverAuctions();
    }

    private void RecoverAuctions()
    {
        if (_stopping)
        {
            return;
        }

        foreach ((uint id, AuctionRecovery recovery) in _auctionRecoveries.Where(p => !p.Value.InFlight).Take(ExpiryBatch).ToArray())
        {
            recovery.InFlight = true;
            Read(async (store, ct) =>
            {
                AuctionRecord? row = (await store.GetAuctionsAsync(ct).ConfigureAwait(false)).SingleOrDefault(a => a.Id == id);
                if (row is null)
                {
                    return new RecoveredAuction(null, null);
                }

                IReadOnlyDictionary<uint, ItemInstanceData> escrow = await store.GetEscrowItemsAsync([row.ItemGuid], ct).ConfigureAwait(false);
                if (!escrow.TryGetValue(row.ItemGuid, out ItemInstanceData? item)
                    || item.Guid != row.ItemGuid || item.Entry != row.ItemEntry || item.Count != row.ItemCount)
                {
                    throw new InvalidOperationException($"auction {id} does not have a matching authoritative escrow item");
                }

                return new RecoveredAuction(row, item);
            }, result =>
            {
                if (_stopping || !_auctionRecoveries.TryGetValue(id, out AuctionRecovery? current) || !ReferenceEquals(current, recovery))
                {
                    return;
                }

                // Patch only the reserved ID. Other active auctions may have changed while
                // these reads were in flight and must never be replaced by this snapshot.
                if (result.Row is { } row)
                {
                    _auctions[id] = new AuctionView(row, result.Item!);
                }
                else
                {
                    _auctions.Remove(id);
                }

                _auctionRecoveries.Remove(id);
                _busyAuctions.Remove(id);
            }, () =>
            {
                if (_auctionRecoveries.TryGetValue(id, out AuctionRecovery? current) && ReferenceEquals(current, recovery))
                {
                    recovery.InFlight = false;
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
            }
            else
            {
                // Already queued reads retain the old identity and cannot publish. A
                // retained/neutralized auction stays reserved until the new read finishes.
                _auctionRecoveries[id] = new AuctionRecovery(recovery.Affected);
            }
        }
    }

    private sealed class AuctionRecovery(IReadOnlyList<AuctionRecord> affected)
    {
        public IReadOnlyList<AuctionRecord> Affected { get; } = affected;
        public bool InFlight { get; set; }
    }

    private sealed record RecoveredAuction(AuctionRecord? Row, ItemInstanceData? Item);
}

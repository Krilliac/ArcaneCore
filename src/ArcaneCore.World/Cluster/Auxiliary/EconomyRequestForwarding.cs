using ArcaneCore.Kernel.Economy;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Economy;

namespace ArcaneCore.World.Cluster.Auxiliary;

/// <summary>
/// Transport from a worker to the node holding the Economy lease (M15 S3d). A peer link implementation sends the request to the holder and returns its
/// <see cref="EconomyFeature.ServeForwardedAsync"/> answer; a lost link, timeout or refusal surfaces as <see cref="IOException"/>, <see cref="TimeoutException"/> or
/// <see cref="InvalidOperationException"/>, like <see cref="EconomyNotifyClient"/>. Implementations must be thread-safe.
/// </summary>
public interface IEconomyHolderLink
{
    Task<EconomyForwardResult> SendAsync(EconomyForwardRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A link that calls the holder's feature in this process: the node that holds the lease and serves its own players, and tests. Thread-safe: no state of its own.
/// </summary>
public sealed class DirectEconomyHolderLink(EconomyFeature holder) : IEconomyHolderLink
{
    public Task<EconomyForwardResult> SendAsync(EconomyForwardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return holder.ServeForwardedAsync(request, cancellationToken);
    }
}

/// <summary>
/// Worker side of request forwarding: reads of mail and auctions and every write go to the Economy holder, which owns the only cache and the fenced store writes
/// (<c>docs/CLUSTERING_DESIGN.md</c> section 8). A write carries the commit the worker built for its own participants (saved first, as a local economy operation
/// does); a link failure while sending a write is retried with the same request id and operation id, so a lost answer is never a second write, and ends as
/// <see cref="EconomyForwardStatus.Unknown"/> when the link stays down (the caller reconciles by sending the same commit again later). Reads that cannot reach
/// the holder answer <see cref="EconomyForwardStatus.Failed"/>.
/// Thread-safe: no mutable state; each call is independent.
/// </summary>
public sealed class EconomyForwardClient
{
    /// <summary>Attempts a write makes over a failing link before it reports <see cref="EconomyForwardStatus.Unknown"/>.</summary>
    public const int WriteAttempts = 3;

    private readonly IEconomyHolderLink _link;
    private readonly string _workerId;

    public EconomyForwardClient(IEconomyHolderLink link, string workerId)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > EconomyNotifyRouter.MaxWorkerIdLength)
        {
            throw new ArgumentException($"worker id must be 1 to {EconomyNotifyRouter.MaxWorkerIdLength} characters", nameof(workerId));
        }

        _link = link;
        _workerId = workerId;
    }

    /// <summary>The letters of <paramref name="characterId"/> as the holder holds them (its cache when the character is online there, else the store).</summary>
    public Task<EconomyForwardResult> ListMailAsync(int characterId, CancellationToken cancellationToken = default)
        => ReadAsync(new EconomyForwardRequest(Guid.NewGuid(), _workerId, EconomyForwardKind.MailList, CharacterId: characterId), cancellationToken);

    /// <summary>The live auctions of a house from the holder's cache; <paramref name="auctionId"/> not 0 returns that auction only.</summary>
    public Task<EconomyForwardResult> ListAuctionsAsync(uint houseId, uint auctionId = 0, CancellationToken cancellationToken = default)
        => ReadAsync(new EconomyForwardRequest(Guid.NewGuid(), _workerId, EconomyForwardKind.AuctionList, HouseId: houseId, AuctionId: auctionId), cancellationToken);

    /// <summary>Send a mail write (send, return, take money or item, COD payment, delete, mark read) for the holder to validate, commit and cache.</summary>
    public Task<EconomyForwardResult> WriteMailAsync(EconomyCommitRequest commit, CancellationToken cancellationToken = default)
        => WriteAsync(EconomyForwardKind.MailWrite, commit, cancellationToken);

    /// <summary>Send an auction write (list, bid, buyout, cancel) for the holder to validate against its cache, commit and apply.</summary>
    public Task<EconomyForwardResult> WriteAuctionAsync(EconomyCommitRequest commit, CancellationToken cancellationToken = default)
        => WriteAsync(EconomyForwardKind.AuctionWrite, commit, cancellationToken);

    private async Task<EconomyForwardResult> ReadAsync(EconomyForwardRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _link.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLinkFailure(ex))
        {
            return EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.Failed);
        }
    }

    private async Task<EconomyForwardResult> WriteAsync(EconomyForwardKind kind, EconomyCommitRequest commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.OperationId == Guid.Empty)
        {
            throw new ArgumentException("a forwarded write needs a stable operation id", nameof(commit));
        }

        var request = new EconomyForwardRequest(Guid.NewGuid(), _workerId, kind, Commit: commit);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _link.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                if (attempt >= WriteAttempts)
                {
                    return EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.Unknown);
                }
            }
        }
    }

    private static bool IsLinkFailure(Exception ex) => ex is IOException or TimeoutException or InvalidOperationException;
}

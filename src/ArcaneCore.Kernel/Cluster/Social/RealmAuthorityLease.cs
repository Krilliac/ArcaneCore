namespace ArcaneCore.Kernel.Cluster.Social;

/// <summary>Well-known realm-singleton lease scopes for M15.2 (<c>docs/CLUSTERING_DESIGN.md</c> §149, §525-528, §538-542, §565).</summary>
public static class RealmAuthorityScopes
{
    /// <summary>Single realm-wide authority for auction house, mail expiration, and AH bot custody scheduling.</summary>
    public const string Economy = "economy";

    /// <summary>Single realm-wide authority for online presence, whisper/group/guild/channel routing, and AutoBroadcast.</summary>
    public const string Social = "social";

    /// <summary>Single realm-wide authority for durable instance ID allocation and group instance bindings.</summary>
    public const string Instances = "instances";
}

/// <summary>
/// Fenced lease for a realm-singleton subsystem (economy, social, instances).
/// <see cref="FencingToken"/> increases strictly monotonically on every acquisition or takeover so storage
/// and downstream handlers reject mutations carrying an older token even if a partitioned former owner's clock lags.
/// </summary>
public sealed record RealmAuthorityLease(
    string Scope,
    string OwnerNodeId,
    Guid OwnerIncarnation,
    long FencingToken,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsActiveAt(DateTimeOffset nowUtc) => nowUtc < ExpiresAtUtc;
}

/// <summary>Outcome of a lease acquisition, renewal, or release operation.</summary>
public enum AuthorityLeaseOutcome
{
    Granted = 0,
    Renewed = 1,
    RejectedHeldByOther = 2,
    RejectedStaleToken = 3,
    Released = 4,
}

/// <summary>Result of a lease store operation, carrying the current active lease when applicable.</summary>
public sealed record AuthorityLeaseResult(AuthorityLeaseOutcome Outcome, RealmAuthorityLease? Lease);

/// <summary>
/// Store for realm-singleton authority leases (M15.2). Guarantees at most one active lease holder per
/// <c>scope</c> at any instant and issues strictly monotonic fencing tokens across takeovers.
/// </summary>
public interface IRealmAuthorityLeaseStore
{
    /// <summary>
    /// Acquire the lease for <paramref name="scope"/> if unheld or expired at <paramref name="nowUtc"/>
    /// (incrementing the fencing token), or renew it in place if already held by the same
    /// <paramref name="ownerNodeId"/> and <paramref name="ownerIncarnation"/>.
    /// </summary>
    AuthorityLeaseResult TryAcquire(
        string scope,
        string ownerNodeId,
        Guid ownerIncarnation,
        TimeSpan ttl,
        DateTimeOffset nowUtc);

    /// <summary>
    /// Renew an active lease held by <paramref name="ownerNodeId"/> / <paramref name="ownerIncarnation"/>
    /// at the exact <paramref name="fencingToken"/>. Fails with <see cref="AuthorityLeaseOutcome.RejectedStaleToken"/>
    /// if the lease expired or was taken over by a newer fencing token.
    /// </summary>
    AuthorityLeaseResult TryRenew(
        string scope,
        string ownerNodeId,
        Guid ownerIncarnation,
        long fencingToken,
        TimeSpan ttl,
        DateTimeOffset nowUtc);

    /// <summary>
    /// Release the lease for <paramref name="scope"/> if and only if it is currently held by
    /// <paramref name="ownerNodeId"/> / <paramref name="ownerIncarnation"/> at <paramref name="fencingToken"/>.
    /// The high-water fencing token is preserved so subsequent acquisitions continue incrementing.
    /// </summary>
    AuthorityLeaseResult TryRelease(
        string scope,
        string ownerNodeId,
        Guid ownerIncarnation,
        long fencingToken);

    /// <summary>
    /// Validate that <paramref name="ownerNodeId"/> currently holds an unexpired lease for <paramref name="scope"/>
    /// at <paramref name="nowUtc"/> with the latest <paramref name="fencingToken"/>. Every authority mutation
    /// (such as auction settlement, mail expiry, or AH bot custody reservation) checks this before committing.
    /// </summary>
    bool ValidateFencingToken(string scope, string ownerNodeId, long fencingToken, DateTimeOffset nowUtc);

    /// <summary>
    /// As <see cref="ValidateFencingToken(string, string, long, DateTimeOffset)"/>, and the lease must also be held by <paramref name="ownerIncarnation"/>:
    /// two processes configured with the same node id cannot both pass.
    /// </summary>
    bool ValidateFencingToken(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken, DateTimeOffset nowUtc);

    /// <summary>Return the currently active lease for <paramref name="scope"/> at <paramref name="nowUtc"/>, or <c>null</c> if unheld or expired.</summary>
    RealmAuthorityLease? GetCurrent(string scope, DateTimeOffset nowUtc);

    /// <summary>Return the highest fencing token ever issued for <paramref name="scope"/> (<c>0</c> if never acquired).</summary>
    long GetHighWaterToken(string scope);
}

/// <summary>Thread-safe in-memory <see cref="IRealmAuthorityLeaseStore"/> for single-host and multi-worker test topologies.</summary>
public sealed class InMemoryRealmAuthorityLeaseStore : IRealmAuthorityLeaseStore
{
    private sealed class ScopeSlot
    {
        public long HighWaterToken;
        public RealmAuthorityLease? Current;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScopeSlot> _scopes = new(StringComparer.Ordinal);

    public AuthorityLeaseResult TryAcquire(
        string scope,
        string ownerNodeId,
        Guid ownerIncarnation,
        TimeSpan ttl,
        DateTimeOffset nowUtc)
    {
        ValidateScopeAndOwner(scope, ownerNodeId);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Lease TTL must be positive.");
        }

        lock (_gate)
        {
            if (!_scopes.TryGetValue(scope, out ScopeSlot? slot))
            {
                slot = new ScopeSlot();
                _scopes[scope] = slot;
            }

            if (slot.Current is { } active && active.IsActiveAt(nowUtc))
            {
                if (string.Equals(active.OwnerNodeId, ownerNodeId, StringComparison.Ordinal)
                    && active.OwnerIncarnation == ownerIncarnation)
                {
                    RealmAuthorityLease renewed = active with { ExpiresAtUtc = nowUtc + ttl };
                    slot.Current = renewed;
                    return new AuthorityLeaseResult(AuthorityLeaseOutcome.Renewed, renewed);
                }

                return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedHeldByOther, active);
            }

            long token = checked(slot.HighWaterToken + 1);
            slot.HighWaterToken = token;
            var lease = new RealmAuthorityLease(scope, ownerNodeId, ownerIncarnation, token, nowUtc, nowUtc + ttl);
            slot.Current = lease;
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.Granted, lease);
        }
    }

    public AuthorityLeaseResult TryRenew(
        string scope,
        string ownerNodeId,
        Guid ownerIncarnation,
        long fencingToken,
        TimeSpan ttl,
        DateTimeOffset nowUtc)
    {
        ValidateScopeAndOwner(scope, ownerNodeId);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Lease TTL must be positive.");
        }

        lock (_gate)
        {
            if (!_scopes.TryGetValue(scope, out ScopeSlot? slot) || slot.Current is not { } current)
            {
                return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, null);
            }

            if (fencingToken != current.FencingToken || fencingToken < slot.HighWaterToken)
            {
                return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, current.IsActiveAt(nowUtc) ? current : null);
            }

            if (!current.IsActiveAt(nowUtc))
            {
                return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, null);
            }

            if (!string.Equals(current.OwnerNodeId, ownerNodeId, StringComparison.Ordinal)
                || current.OwnerIncarnation != ownerIncarnation)
            {
                return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedHeldByOther, current);
            }

            RealmAuthorityLease renewed = current with { ExpiresAtUtc = nowUtc + ttl };
            slot.Current = renewed;
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.Renewed, renewed);
        }
    }

    public AuthorityLeaseResult TryRelease(
        string scope,
        string ownerNodeId,
        Guid ownerIncarnation,
        long fencingToken)
    {
        ValidateScopeAndOwner(scope, ownerNodeId);

        lock (_gate)
        {
            if (!_scopes.TryGetValue(scope, out ScopeSlot? slot) || slot.Current is not { } current)
            {
                return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, null);
            }

            if (current.FencingToken != fencingToken
                || !string.Equals(current.OwnerNodeId, ownerNodeId, StringComparison.Ordinal)
                || current.OwnerIncarnation != ownerIncarnation)
            {
                return new AuthorityLeaseResult(AuthorityLeaseOutcome.RejectedStaleToken, current);
            }

            slot.Current = null;
            return new AuthorityLeaseResult(AuthorityLeaseOutcome.Released, null);
        }
    }

    public bool ValidateFencingToken(string scope, string ownerNodeId, long fencingToken, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(ownerNodeId) || fencingToken <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _scopes.TryGetValue(scope, out ScopeSlot? slot)
                && slot.Current is { } current
                && current.FencingToken == fencingToken
                && slot.HighWaterToken == fencingToken
                && string.Equals(current.OwnerNodeId, ownerNodeId, StringComparison.Ordinal)
                && current.IsActiveAt(nowUtc);
        }
    }

    public bool ValidateFencingToken(string scope, string ownerNodeId, Guid ownerIncarnation, long fencingToken, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(ownerNodeId) || fencingToken <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _scopes.TryGetValue(scope, out ScopeSlot? slot)
                && slot.Current is { } current
                && current.FencingToken == fencingToken
                && slot.HighWaterToken == fencingToken
                && string.Equals(current.OwnerNodeId, ownerNodeId, StringComparison.Ordinal)
                && current.OwnerIncarnation == ownerIncarnation
                && current.IsActiveAt(nowUtc);
        }
    }

    public RealmAuthorityLease? GetCurrent(string scope, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        lock (_gate)
        {
            return _scopes.TryGetValue(scope, out ScopeSlot? slot)
                && slot.Current is { } current
                && current.IsActiveAt(nowUtc)
                ? current
                : null;
        }
    }

    public long GetHighWaterToken(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return 0;
        }

        lock (_gate)
        {
            return _scopes.TryGetValue(scope, out ScopeSlot? slot) ? slot.HighWaterToken : 0;
        }
    }

    private static void ValidateScopeAndOwner(string scope, string ownerNodeId)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope must be non-empty.", nameof(scope));
        }

        if (string.IsNullOrWhiteSpace(ownerNodeId))
        {
            throw new ArgumentException("OwnerNodeId must be non-empty.", nameof(ownerNodeId));
        }
    }
}

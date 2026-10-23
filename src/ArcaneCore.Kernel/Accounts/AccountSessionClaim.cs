using System.Collections.Concurrent;

namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// Monotonic per-account session generation claim (M15.0 item A4 / M15.1 prerequisite).
/// Every world-session registration for an account atomically increments <see cref="Generation"/>
/// (<c>1, 2, ...</c>) and stamps the claiming gateway's <see cref="NodeIncarnation"/>. A stale
/// session releasing or acting with an older <see cref="Generation"/> (or a different
/// <see cref="NodeIncarnation"/>) cannot overwrite or outlive a newer session for the same account.
/// </summary>
public sealed record AccountSessionClaim(int AccountId, long Generation, Guid NodeIncarnation);

/// <summary>
/// Authoritative store for per-account session generation claims. Single-node deployments use
/// <see cref="InMemoryAccountSessionClaimStore"/>; multi-gateway cluster deployments share one store
/// so a reconnect on gateway B atomically supersedes and revokes the stale session on gateway A.
/// </summary>
public interface IAccountSessionClaimStore
{
    /// <summary>
    /// Atomically advance the generation for <paramref name="accountId"/> and record
    /// <paramref name="nodeIncarnation"/> as the active holder. Any prior active holder is
    /// superseded and revocation subscribers are notified with the new claim.
    /// </summary>
    AccountSessionClaim Claim(int accountId, Guid nodeIncarnation);

    /// <summary>
    /// Release <paramref name="claim"/> if and only if it is still the active claim for
    /// <see cref="AccountSessionClaim.AccountId"/> (matching both <see cref="AccountSessionClaim.Generation"/>
    /// and <see cref="AccountSessionClaim.NodeIncarnation"/>). The monotonic generation floor for the account
    /// is preserved so subsequent logins continue incrementing from the highest generation ever issued.
    /// </summary>
    bool TryRelease(AccountSessionClaim claim);

    /// <summary>
    /// Return the currently active claim for <paramref name="accountId"/>, or <c>null</c> if no session
    /// holds the account (never claimed, cleanly released, or invalidated).
    /// </summary>
    AccountSessionClaim? GetActive(int accountId);

    /// <summary>
    /// Return the highest generation ever issued or invalidated for <paramref name="accountId"/>
    /// (<c>0</c> if the account has never been claimed).
    /// </summary>
    long GetGeneration(int accountId);

    /// <summary>
    /// Atomically advance the generation floor for <paramref name="accountId"/>, clear any active claim,
    /// and notify revocation subscribers so any gateway still holding an older generation kicks its session.
    /// Returns the new generation floor.
    /// </summary>
    long Invalidate(int accountId);

    /// <summary>
    /// Subscribe to claim supersession and invalidation notifications. The callback receives the new
    /// <see cref="AccountSessionClaim"/> (with <see cref="Guid.Empty"/> as <see cref="AccountSessionClaim.NodeIncarnation"/>
    /// when triggered by <see cref="Invalidate"/>).
    /// </summary>
    IDisposable SubscribeRevocations(Action<AccountSessionClaim> onSupersededOrInvalidated);
}

/// <summary>
/// Thread-safe in-memory <see cref="IAccountSessionClaimStore"/>. Preserves per-account monotonic generation
/// counters across releases and supports cross-gateway replacement when shared between <c>SessionRegistry</c> instances.
/// </summary>
public sealed class InMemoryAccountSessionClaimStore : IAccountSessionClaimStore
{
    private sealed class AccountSlot
    {
        public readonly object Gate = new();
        public long Generation;
        public AccountSessionClaim? Active;
    }

    private readonly ConcurrentDictionary<int, AccountSlot> _slots = new();
    private readonly Lock _subscribersLock = new();
    private Action<AccountSessionClaim>[] _subscribers = [];

    public AccountSessionClaim Claim(int accountId, Guid nodeIncarnation)
    {
        if (accountId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accountId), "AccountId must be non-zero.");
        }

        AccountSlot slot = _slots.GetOrAdd(accountId, static _ => new AccountSlot());
        AccountSessionClaim claim;
        bool notify;
        lock (slot.Gate)
        {
            notify = slot.Active is not null;
            long nextGeneration = checked(slot.Generation + 1);
            slot.Generation = nextGeneration;
            claim = new AccountSessionClaim(accountId, nextGeneration, nodeIncarnation);
            slot.Active = claim;
        }

        if (notify)
        {
            NotifySubscribers(claim);
        }

        return claim;
    }

    public bool TryRelease(AccountSessionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_slots.TryGetValue(claim.AccountId, out AccountSlot? slot))
        {
            return false;
        }

        lock (slot.Gate)
        {
            if (slot.Active != claim)
            {
                return false;
            }

            slot.Active = null;
            return true;
        }
    }

    public AccountSessionClaim? GetActive(int accountId)
    {
        if (!_slots.TryGetValue(accountId, out AccountSlot? slot))
        {
            return null;
        }

        lock (slot.Gate)
        {
            return slot.Active;
        }
    }

    public long GetGeneration(int accountId)
    {
        if (!_slots.TryGetValue(accountId, out AccountSlot? slot))
        {
            return 0;
        }

        lock (slot.Gate)
        {
            return slot.Generation;
        }
    }

    public long Invalidate(int accountId)
    {
        if (accountId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accountId), "AccountId must be non-zero.");
        }

        AccountSlot slot = _slots.GetOrAdd(accountId, static _ => new AccountSlot());
        AccountSessionClaim tombstone;
        lock (slot.Gate)
        {
            long nextGeneration = checked(slot.Generation + 1);
            slot.Generation = nextGeneration;
            slot.Active = null;
            tombstone = new AccountSessionClaim(accountId, nextGeneration, Guid.Empty);
        }

        NotifySubscribers(tombstone);
        return tombstone.Generation;
    }

    public IDisposable SubscribeRevocations(Action<AccountSessionClaim> onSupersededOrInvalidated)
    {
        ArgumentNullException.ThrowIfNull(onSupersededOrInvalidated);
        lock (_subscribersLock)
        {
            _subscribers = [.. _subscribers, onSupersededOrInvalidated];
        }

        return new Subscription(this, onSupersededOrInvalidated);
    }

    private void NotifySubscribers(AccountSessionClaim newer)
    {
        Action<AccountSessionClaim>[] snapshot = Volatile.Read(ref _subscribers);
        foreach (Action<AccountSessionClaim> subscriber in snapshot)
        {
            try
            {
                subscriber(newer);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One faulting subscriber must not abort the already-committed Claim/Invalidate or starve the rest.
                Diagnostics.FaultLedger.Shared.Record("accountclaims.subscriber", ex);
            }
        }
    }

    private void Unsubscribe(Action<AccountSessionClaim> callback)
    {
        lock (_subscribersLock)
        {
            int index = Array.IndexOf(_subscribers, callback);
            if (index < 0)
            {
                return;
            }

            if (_subscribers.Length == 1)
            {
                _subscribers = [];
                return;
            }

            var updated = new Action<AccountSessionClaim>[_subscribers.Length - 1];
            if (index > 0)
            {
                Array.Copy(_subscribers, 0, updated, 0, index);
            }

            if (index < _subscribers.Length - 1)
            {
                Array.Copy(_subscribers, index + 1, updated, index, _subscribers.Length - index - 1);
            }

            _subscribers = updated;
        }
    }

    private sealed class Subscription(InMemoryAccountSessionClaimStore owner, Action<AccountSessionClaim> callback) : IDisposable
    {
        private InMemoryAccountSessionClaimStore? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(callback);
        }
    }
}

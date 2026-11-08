using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Net;

/// <summary>
/// Per-account session generation and replacement seam (M15.0 item A4 / M15.1 prerequisite).
/// Assigns a strictly monotonic generation per account on registration, tracks the active
/// <see cref="AccountSessionClaim"/>, and ensures a stale session's teardown or late packets
/// cannot overwrite or outlive a newer session on this or another gateway.
/// </summary>
public interface ISessionClaims
{
    /// <summary>The identity of this gateway/registry instance inside <see cref="AccountSessionClaim.NodeIncarnation"/>.</summary>
    Guid NodeIncarnation { get; }

    /// <summary>The underlying account-claim store (in-memory single-node by default; shared across gateways in cluster mode).</summary>
    IAccountSessionClaimStore Store { get; }

    /// <summary>Look up the active claim for <paramref name="accountId"/>, or <c>null</c> if no session currently holds it.</summary>
    AccountSessionClaim? GetClaim(int accountId);

    /// <summary>Return the highest generation ever issued or invalidated for <paramref name="accountId"/> (<c>0</c> if never claimed).</summary>
    long GetGeneration(int accountId);

    /// <summary>True when <paramref name="session"/> is still the current active session for its account.</summary>
    bool IsCurrent(WorldSession session);

    /// <summary>True when <paramref name="accountId"/> is still actively held at <paramref name="generation"/> on this node.</summary>
    bool IsCurrent(int accountId, long generation);

    /// <summary>
    /// Advance the generation for <paramref name="accountId"/>, clear its active claim, and kick any local or remote
    /// session still holding an older generation for that account.
    /// </summary>
    long Invalidate(int accountId);
}

/// <summary>
/// Authenticated sessions by account. A new session for an account claims a monotonic
/// <see cref="AccountSessionClaim.Generation"/> and replaces and disconnects the previous one
/// (vmangos World::AddSession_ kicks the old session), so a client that reconnects after a crash
/// is not locked out by its own stale connection and a late teardown of the old connection cannot
/// clear the new one.
/// </summary>
public sealed class SessionRegistry : ISessionClaims, IDisposable
{
    private readonly ConcurrentDictionary<int, WorldSession> _byAccount = new();
    private readonly IDisposable _revocationSubscription;
    private readonly AccountPlayHistory _playHistory = new();

    public SessionRegistry()
        : this(null, null)
    {
    }

    public SessionRegistry(IAccountSessionClaimStore? claims, Guid? nodeIncarnation = null)
    {
        Store = claims ?? new InMemoryAccountSessionClaimStore();
        NodeIncarnation = nodeIncarnation ?? Guid.NewGuid();
        _revocationSubscription = Store.SubscribeRevocations(OnClaimSuperseded);
    }

    public Guid NodeIncarnation { get; }

    public IAccountSessionClaimStore Store { get; }

    public int Count => _byAccount.Count;

    /// <summary>
    /// A session was registered (authenticated). Raised on the registering thread after the registry holds it; a faulting
    /// subscriber is the subscriber's problem (the account address recorder queues and returns).
    /// </summary>
    public event Action<WorldSession>? Registered;

    public void Register(WorldSession session)
    {
        // Keyed by account: the session must have taken its account id from the authenticated row first,
        // or every unauthenticated session would share key 0 and kick each other.
        Invariant.Assert(session.AccountId != 0, "a session is registered only after authentication assigned its account id");

        AccountSessionClaim claim = Store.Claim(session.AccountId, NodeIncarnation);
        session.BindClaim(claim);

        WorldSession? previous = null;
        bool supersededSelf = false;
        _byAccount.AddOrUpdate(
            session.AccountId,
            session,
            (_, existing) =>
            {
                if (!ReferenceEquals(existing, session) && existing.SessionGeneration > claim.Generation)
                {
                    supersededSelf = true;
                    return existing;
                }

                previous = existing;
                return session;
            });

        if (supersededSelf)
        {
            session.Kick("superseded by a newer session of the account");
            return;
        }

        // Consecutive play time carries over (vmangos World::AddSession_, World.cpp:285-300): from the session this one replaces (a double login must not
        // reset it), else from the account's last logout when that was under five hours ago.
        WorldSession? replaced = previous is not null && !ReferenceEquals(previous, session) ? previous : null;
        session.InheritPlayTime(replaced, _playHistory);

        if (replaced is not null)
        {
            replaced.Kick("superseded by a newer login of the account");
        }

        Registered?.Invoke(session);
    }

    public void Unregister(WorldSession session)
    {
        if (_byAccount.TryRemove(new KeyValuePair<int, WorldSession>(session.AccountId, session)))
        {
            // Only the session that still held the account records its play time (vmangos World.cpp:2836, :3106); a replaced one passed it on.
            session.RecordPlayTime(_playHistory);
        }

        if (session.SessionClaim is { } claim)
        {
            Store.TryRelease(claim);
        }
    }

    public WorldSession? Find(int accountId) => _byAccount.GetValueOrDefault(accountId);

    public AccountSessionClaim? GetClaim(int accountId) => Store.GetActive(accountId);

    public long GetGeneration(int accountId) => Store.GetGeneration(accountId);

    public bool IsCurrent(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SessionClaim is not { } claim || claim.NodeIncarnation != NodeIncarnation)
        {
            return false;
        }

        return _byAccount.TryGetValue(claim.AccountId, out WorldSession? active)
            && ReferenceEquals(active, session)
            && Store.GetActive(claim.AccountId) == claim;
    }

    public bool IsCurrent(int accountId, long generation)
    {
        if (accountId == 0 || generation <= 0)
        {
            return false;
        }

        return _byAccount.TryGetValue(accountId, out WorldSession? active)
            && active.SessionGeneration == generation
            && Store.GetActive(accountId) is { } claim
            && claim.Generation == generation
            && claim.NodeIncarnation == NodeIncarnation;
    }

    public long Invalidate(int accountId) => Store.Invalidate(accountId);

    /// <summary>
    /// The realm presence authority saw a newer login of <paramref name="accountId"/> on another worker (<see cref="Cluster.Social.PresenceKick"/>): disconnect the session
    /// this node holds for it. True when a session was found and kicked. Thread-safe (the kick only cancels the session's token).
    /// </summary>
    public bool KickSuperseded(int accountId)
    {
        if (_byAccount.TryGetValue(accountId, out WorldSession? existing))
        {
            existing.Kick("the account logged in on another world worker");
            return true;
        }

        return false;
    }

    /// <summary>A point-in-time snapshot of the registered sessions (live ban enforcement and the periodic re-check).</summary>
    public IReadOnlyList<WorldSession> Sessions => [.. _byAccount.Values];

    public void Dispose() => _revocationSubscription.Dispose();

    private void OnClaimSuperseded(AccountSessionClaim newer)
    {
        if (newer.NodeIncarnation == NodeIncarnation)
        {
            return;
        }

        while (_byAccount.TryGetValue(newer.AccountId, out WorldSession? existing))
        {
            if (existing.SessionGeneration >= newer.Generation)
            {
                return;
            }

            if (_byAccount.TryRemove(new KeyValuePair<int, WorldSession>(newer.AccountId, existing)))
            {
                // Invalidate() publishes a tombstone claim with NodeIncarnation Guid.Empty (IAccountSessionClaimStore.SubscribeRevocations);
                // every other claim here is a newer login on another node (a same-node login returned above).
                existing.Kick(newer.NodeIncarnation == Guid.Empty
                    ? "the account's claim was invalidated"
                    : "the account logged in on another world worker");
                return;
            }
        }
    }
}

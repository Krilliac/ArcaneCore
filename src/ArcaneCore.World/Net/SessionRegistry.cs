using System.Collections.Concurrent;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Net;

/// <summary>
/// Authenticated sessions by account. A new session for an account replaces and disconnects
/// the previous one (vmangos World::AddSession_ kicks the old session), so a client that
/// reconnects after a crash is not locked out by its own stale connection.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<int, WorldSession> _byAccount = new();

    public int Count => _byAccount.Count;

    public void Register(WorldSession session)
    {
        // Keyed by account: the session must have taken its account id from the authenticated row first,
        // or every unauthenticated session would share key 0 and kick each other.
        Invariant.Assert(session.AccountId != 0, "a session is registered only after authentication assigned its account id");
        WorldSession? previous = null;
        _byAccount.AddOrUpdate(
            session.AccountId,
            session,
            (_, existing) =>
            {
                previous = existing;
                return session;
            });

        if (previous is not null && !ReferenceEquals(previous, session))
        {
            previous.Kick();
        }
    }

    public void Unregister(WorldSession session)
        => _byAccount.TryRemove(new KeyValuePair<int, WorldSession>(session.AccountId, session));

    public WorldSession? Find(int accountId) => _byAccount.GetValueOrDefault(accountId);

    /// <summary>A point-in-time snapshot of the registered sessions (live ban enforcement and the periodic re-check).</summary>
    public IReadOnlyList<WorldSession> Sessions => [.. _byAccount.Values];
}

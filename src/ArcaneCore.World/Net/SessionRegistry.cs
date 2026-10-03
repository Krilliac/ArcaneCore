using System.Collections.Concurrent;

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

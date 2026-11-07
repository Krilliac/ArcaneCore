using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Net;

/// <summary>
/// The per-account consecutive play time that outlives a session, a re-expression of vmangos' <c>m_accountsPlayHistory</c> (World.cpp:294-300 reads it
/// when an account logs in with no session of it live; World.cpp:2836 and :3106 write it when a session ends): the seconds an account has played, and
/// when it last left. A login within <see cref="PlayTimeLimit.FullSeconds"/> of the last logout continues the old total; a later one starts again at
/// zero, as an account that rested five hours has. One per <see cref="SessionRegistry"/>. Thread-safe (sessions register and unregister on connection threads).
/// Entries are small and kept per account that ever played while <c>World:LimitPlayTime</c> was on, as vmangos keeps them.
/// </summary>
public sealed class AccountPlayHistory
{
    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    private readonly record struct Entry(uint PlayedSeconds, long LogoutMs);

    /// <summary>
    /// The seconds a session starting at <paramref name="nowMs"/> (non-wrapping game milliseconds) inherits: the account's total when it logged out less than
    /// five hours before, else 0 (and the stale total is forgotten).
    /// </summary>
    public uint TakeCarry(int accountId, long nowMs)
    {
        if (!_entries.TryGetValue(accountId, out Entry entry))
        {
            return 0;
        }

        if ((nowMs - entry.LogoutMs) / 1000 < PlayTimeLimit.FullSeconds)
        {
            return entry.PlayedSeconds;
        }

        _entries.TryRemove(new KeyValuePair<int, Entry>(accountId, entry));
        return 0;
    }

    /// <summary>The account's session ended at <paramref name="nowMs"/> with <paramref name="playedSeconds"/> of consecutive play.</summary>
    public void Record(int accountId, uint playedSeconds, long nowMs) => _entries[accountId] = new Entry(playedSeconds, nowMs);
}

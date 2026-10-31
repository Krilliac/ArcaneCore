using System.Collections.Concurrent;

namespace ArcaneCore.Kernel.Cluster.Social;

/// <summary>
/// Versioned realm-wide presence record for an online character (M15.2).
/// <para>
/// <see cref="PresenceVersion"/> is strictly monotonic per character across logins, map transfers,
/// and status updates; <see cref="SessionGeneration"/> is the monotonic per-account generation
/// from the gateway session claim. Together they guarantee that a late logout or stale update from
/// a prior worker or superseded session cannot overwrite or delete a newer presence entry.
/// </para>
/// </summary>
public sealed record RealmPresenceEntry(
    ulong CharacterGuid,
    string CharacterName,
    int AccountId,
    string WorkerId,
    string GatewayId,
    uint MapId,
    uint ZoneId,
    uint AreaId,
    byte Level,
    byte Class,
    byte Race,
    byte Gender,
    int GuildId,
    int GroupId,
    long PresenceVersion,
    long SessionGeneration,
    DateTimeOffset UpdatedAtUtc,
    string? GuildName = null,
    bool IsAfk = false,
    bool IsDnd = false,
    string? StatusMessage = null);

/// <summary>Filter criteria for realm-wide <c>/who</c> queries against <see cref="IRealmPresenceDirectory"/>.</summary>
public sealed record RealmWhoFilter(
    byte MinLevel = 1,
    byte MaxLevel = 100,
    uint? ClassMask = null,
    uint? RaceMask = null,
    IReadOnlyList<uint>? ZoneIds = null,
    IReadOnlyList<string>? SearchTerms = null,
    int MaxResults = 49);

/// <summary>Outcome of a fenced presence upsert or removal.</summary>
public enum PresenceMutationResult
{
    Applied = 0,
    RejectedStaleVersion = 1,
    RejectedStaleSessionGeneration = 2,
    NotFound = 3,
}

/// <summary>
/// Realm-wide online presence and character directory authority (M15.2, <c>docs/CLUSTERING_DESIGN.md</c> §149, §212, §565).
/// Owns the authoritative online character projection across all map workers and gateways in a realm.
/// </summary>
public interface IRealmPresenceDirectory
{
    /// <summary>Number of currently online characters in the realm directory.</summary>
    int Count { get; }

    /// <summary>
    /// Atomically insert or update a character's presence entry if its <see cref="RealmPresenceEntry.SessionGeneration"/>
    /// is at least the highest seen for that character and its <see cref="RealmPresenceEntry.PresenceVersion"/> is strictly
    /// greater than the highest version ever applied or removed for that character.
    /// </summary>
    PresenceMutationResult TryUpsert(RealmPresenceEntry entry, out RealmPresenceEntry? current);

    /// <summary>
    /// Conditionally remove a character's online presence only when the active entry's version and session generation
    /// are not newer than <paramref name="expectedPresenceVersion"/> and <paramref name="expectedSessionGeneration"/>
    /// (and, when <paramref name="expectedWorkerId"/> is provided, the active entry is still owned by that worker).
    /// A late logout from a source worker after a transfer or reconnect on a destination worker is rejected without
    /// disturbing the destination worker's presence registration.
    /// </summary>
    PresenceMutationResult TryRemove(
        ulong characterGuid,
        long expectedPresenceVersion,
        long expectedSessionGeneration,
        string? expectedWorkerId = null);

    /// <summary>Look up an online character by GUID.</summary>
    RealmPresenceEntry? FindByGuid(ulong characterGuid);

    /// <summary>Look up an online character by case-insensitive character name.</summary>
    RealmPresenceEntry? FindByName(string characterName);

    /// <summary>Return all online characters belonging to <paramref name="accountId"/>.</summary>
    IReadOnlyList<RealmPresenceEntry> FindByAccount(int accountId);

    /// <summary>Return all online members of <paramref name="guildId"/> (ordered by character GUID).</summary>
    IReadOnlyList<RealmPresenceEntry> FindByGuild(int guildId);

    /// <summary>Return all online members of <paramref name="groupId"/> (ordered by character GUID).</summary>
    IReadOnlyList<RealmPresenceEntry> FindByGroup(int groupId);

    /// <summary>Evaluate a realm-wide <c>/who</c> query across all workers.</summary>
    IReadOnlyList<RealmPresenceEntry> QueryWho(RealmWhoFilter filter);

    /// <summary>Point-in-time snapshot of all online entries ordered by <see cref="RealmPresenceEntry.CharacterGuid"/>.</summary>
    IReadOnlyList<RealmPresenceEntry> Snapshot();
}

/// <summary>
/// Thread-safe in-memory <see cref="IRealmPresenceDirectory"/> with per-character monotonic version and
/// session-generation high-water marks.
/// </summary>
public sealed class InMemoryRealmPresenceDirectory : IRealmPresenceDirectory
{
    private sealed class CharacterPresenceSlot
    {
        public long HighWaterPresenceVersion;
        public long HighWaterSessionGeneration;
        public RealmPresenceEntry? Active;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, CharacterPresenceSlot> _byGuid = new();
    private readonly Dictionary<string, ulong> _guidByName = new(StringComparer.OrdinalIgnoreCase);
    private int _activeCount;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _activeCount;
            }
        }
    }

    public PresenceMutationResult TryUpsert(RealmPresenceEntry entry, out RealmPresenceEntry? current)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.CharacterGuid == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "CharacterGuid must be non-zero.");
        }

        if (string.IsNullOrWhiteSpace(entry.CharacterName))
        {
            throw new ArgumentException("CharacterName must be non-empty.", nameof(entry));
        }

        if (entry.PresenceVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "PresenceVersion must be positive.");
        }

        if (entry.SessionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "SessionGeneration must be positive.");
        }

        lock (_gate)
        {
            if (!_byGuid.TryGetValue(entry.CharacterGuid, out CharacterPresenceSlot? slot))
            {
                slot = new CharacterPresenceSlot();
                _byGuid[entry.CharacterGuid] = slot;
            }

            if (entry.SessionGeneration < slot.HighWaterSessionGeneration)
            {
                current = slot.Active;
                return PresenceMutationResult.RejectedStaleSessionGeneration;
            }

            if (entry.PresenceVersion <= slot.HighWaterPresenceVersion)
            {
                current = slot.Active;
                return PresenceMutationResult.RejectedStaleVersion;
            }

            if (slot.Active is { } previous)
            {
                if (!string.Equals(previous.CharacterName, entry.CharacterName, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveNameIfOwnedBy(previous.CharacterName, entry.CharacterGuid);
                }
            }
            else
            {
                _activeCount++;
            }

            slot.HighWaterSessionGeneration = entry.SessionGeneration;
            slot.HighWaterPresenceVersion = entry.PresenceVersion;
            slot.Active = entry;
            _guidByName[entry.CharacterName] = entry.CharacterGuid;
            current = entry;
            return PresenceMutationResult.Applied;
        }
    }

    public PresenceMutationResult TryRemove(
        ulong characterGuid,
        long expectedPresenceVersion,
        long expectedSessionGeneration,
        string? expectedWorkerId = null)
    {
        lock (_gate)
        {
            if (!_byGuid.TryGetValue(characterGuid, out CharacterPresenceSlot? slot) || slot.Active is not { } active)
            {
                return PresenceMutationResult.NotFound;
            }

            if (expectedSessionGeneration < active.SessionGeneration || expectedSessionGeneration < slot.HighWaterSessionGeneration)
            {
                return PresenceMutationResult.RejectedStaleSessionGeneration;
            }

            if (expectedPresenceVersion < active.PresenceVersion || expectedPresenceVersion < slot.HighWaterPresenceVersion)
            {
                return PresenceMutationResult.RejectedStaleVersion;
            }

            if (expectedWorkerId is not null && !string.Equals(expectedWorkerId, active.WorkerId, StringComparison.Ordinal))
            {
                return PresenceMutationResult.RejectedStaleVersion;
            }

            slot.HighWaterSessionGeneration = Math.Max(slot.HighWaterSessionGeneration, expectedSessionGeneration);
            slot.HighWaterPresenceVersion = Math.Max(slot.HighWaterPresenceVersion, expectedPresenceVersion);
            slot.Active = null;
            RemoveNameIfOwnedBy(active.CharacterName, characterGuid);
            _activeCount--;
            return PresenceMutationResult.Applied;
        }
    }

    /// <summary>Under the gate: drop a name from the index only while it still points at <paramref name="guid"/>; a later holder of the same name keeps its entry.</summary>
    private void RemoveNameIfOwnedBy(string name, ulong guid)
    {
        if (_guidByName.TryGetValue(name, out ulong owner) && owner == guid)
        {
            _guidByName.Remove(name);
        }
    }

    public RealmPresenceEntry? FindByGuid(ulong characterGuid)
    {
        lock (_gate)
        {
            return _byGuid.TryGetValue(characterGuid, out CharacterPresenceSlot? slot) ? slot.Active : null;
        }
    }

    public RealmPresenceEntry? FindByName(string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return null;
        }

        lock (_gate)
        {
            return _guidByName.TryGetValue(characterName, out ulong guid)
                && _byGuid.TryGetValue(guid, out CharacterPresenceSlot? slot)
                ? slot.Active
                : null;
        }
    }

    public IReadOnlyList<RealmPresenceEntry> FindByAccount(int accountId)
    {
        if (accountId == 0)
        {
            return [];
        }

        lock (_gate)
        {
            var results = new List<RealmPresenceEntry>();
            foreach (CharacterPresenceSlot slot in _byGuid.Values)
            {
                if (slot.Active is { } active && active.AccountId == accountId)
                {
                    results.Add(active);
                }
            }

            results.Sort(static (a, b) => a.CharacterGuid.CompareTo(b.CharacterGuid));
            return results;
        }
    }

    public IReadOnlyList<RealmPresenceEntry> FindByGuild(int guildId)
    {
        if (guildId <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            var results = new List<RealmPresenceEntry>();
            foreach (CharacterPresenceSlot slot in _byGuid.Values)
            {
                if (slot.Active is { } active && active.GuildId == guildId)
                {
                    results.Add(active);
                }
            }

            results.Sort(static (a, b) => a.CharacterGuid.CompareTo(b.CharacterGuid));
            return results;
        }
    }

    public IReadOnlyList<RealmPresenceEntry> FindByGroup(int groupId)
    {
        if (groupId <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            var results = new List<RealmPresenceEntry>();
            foreach (CharacterPresenceSlot slot in _byGuid.Values)
            {
                if (slot.Active is { } active && active.GroupId == groupId)
                {
                    results.Add(active);
                }
            }

            results.Sort(static (a, b) => a.CharacterGuid.CompareTo(b.CharacterGuid));
            return results;
        }
    }

    public IReadOnlyList<RealmPresenceEntry> QueryWho(RealmWhoFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        int limit = Math.Max(0, filter.MaxResults);
        if (limit == 0)
        {
            return [];
        }

        lock (_gate)
        {
            var matches = new List<RealmPresenceEntry>();
            foreach (CharacterPresenceSlot slot in _byGuid.Values)
            {
                if (slot.Active is not { } active)
                {
                    continue;
                }

                if (active.Level < filter.MinLevel || active.Level > filter.MaxLevel)
                {
                    continue;
                }

                if (filter.ClassMask is { } classMask && (classMask & (1u << active.Class)) == 0)
                {
                    continue;
                }

                if (filter.RaceMask is { } raceMask && (raceMask & (1u << active.Race)) == 0)
                {
                    continue;
                }

                if (filter.ZoneIds is { Count: > 0 } zones && !zones.Contains(active.ZoneId))
                {
                    continue;
                }

                if (filter.SearchTerms is { Count: > 0 } terms && !MatchesAllTerms(active, terms))
                {
                    continue;
                }

                matches.Add(active);
            }

            matches.Sort(static (a, b) => a.CharacterGuid.CompareTo(b.CharacterGuid));
            if (matches.Count > limit)
            {
                matches.RemoveRange(limit, matches.Count - limit);
            }

            return matches;
        }
    }

    public IReadOnlyList<RealmPresenceEntry> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new List<RealmPresenceEntry>(_activeCount);
            foreach (CharacterPresenceSlot slot in _byGuid.Values)
            {
                if (slot.Active is { } active)
                {
                    snapshot.Add(active);
                }
            }

            snapshot.Sort(static (a, b) => a.CharacterGuid.CompareTo(b.CharacterGuid));
            return snapshot;
        }
    }

    private static bool MatchesAllTerms(RealmPresenceEntry entry, IReadOnlyList<string> terms)
    {
        foreach (string term in terms)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            bool inName = entry.CharacterName.Contains(term, StringComparison.OrdinalIgnoreCase);
            bool inGuild = entry.GuildName is not null && entry.GuildName.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!inName && !inGuild)
            {
                return false;
            }
        }

        return true;
    }
}

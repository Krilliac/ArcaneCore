using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Characters;

/// <summary>
/// The identity (name, race, gender, class) of every character on the realm, so name queries
/// for offline characters need no database round trip — vmangos keeps the same cache
/// (ObjectMgr::LoadPlayerCacheData at startup, GetPlayerDataByGUID for name queries).
/// Loaded at startup and kept current by this process's own character creation and deletion. It is per process and nothing syncs it across a cluster, so a character another worker created is missing here, and a
/// character another worker deleted stays, until <see cref="RefreshAsync"/>: a worker takes that read before it trusts the directory enough to repair stored rows at run time. Thread-safe.
/// </summary>
public sealed partial class CharacterDirectory
{
    private readonly ConcurrentDictionary<int, CharacterIdentity> _byId = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    // When each id was last changed by this process (a counter, one long per character changed since start): a refresh leaves alone what changed while it read.
    private readonly Dictionary<int, long> _changedAt = [];
    private long _changes;

    public int Count => _byId.Count;

    /// <summary>Replace the contents with <paramref name="identities"/> (startup).</summary>
    public void Load(IEnumerable<CharacterIdentity> identities)
    {
        _byId.Clear();
        foreach (CharacterIdentity identity in identities)
        {
            _byId[identity.Id] = identity;
        }
    }

    public void Add(CharacterIdentity identity)
    {
        lock (_gate)
        {
            _byId[identity.Id] = identity;
            _changedAt[identity.Id] = ++_changes;
        }
    }

    public void Remove(int characterId)
    {
        lock (_gate)
        {
            _byId.TryRemove(characterId, out _);
            _changedAt[characterId] = ++_changes;
        }
    }

    /// <summary>
    /// Bring the directory up to what <paramref name="store"/> holds now, including characters created, renamed or deleted on other workers. Never empties it, so a lookup on another thread sees the old entry or the new one,
    /// and what this process added or removed while the read ran stays as this process left it. One refresh runs at a time. Call off the world thread. A caller about to repair stored rows reads those rows first: a stored
    /// row can only name a character that was stored before it, so this read then knows every character the rows name that still exists.
    /// </summary>
    public async Task RefreshAsync(ICharacterStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long readFrom;
            lock (_gate)
            {
                readFrom = _changes;
            }

            IReadOnlyList<CharacterIdentity> stored = await store.GetAllIdentitiesAsync(cancellationToken).ConfigureAwait(false);
            var storedIds = new HashSet<int>(stored.Count);
            foreach (CharacterIdentity identity in stored)
            {
                storedIds.Add(identity.Id);
                lock (_gate)
                {
                    if (!ChangedSince(identity.Id, readFrom))
                    {
                        _byId[identity.Id] = identity;
                    }
                }
            }

            foreach (int id in _byId.Keys)
            {
                if (storedIds.Contains(id))
                {
                    continue;
                }

                lock (_gate)
                {
                    if (!ChangedSince(id, readFrom))
                    {
                        _byId.TryRemove(id, out _);
                    }
                }
            }
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private bool ChangedSince(int id, long mark) => _changedAt.TryGetValue(id, out long at) && at > mark;

    public CharacterIdentity? Find(int characterId) => _byId.GetValueOrDefault(characterId);

    /// <summary>The distinct, ascending account ids owning a character whose name starts with <paramref name="prefix"/> (case-insensitive).</summary>
    public int[] AccountIdsByNamePrefix(string prefix)
        => [.. _byId.Values.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(c => c.AccountId).Distinct().Order()];

    /// <summary>
    /// The character called <paramref name="name"/> (case-insensitive), online or not, like
    /// vmangos ObjectMgr::GetPlayerGuidByName over its player cache. A linear scan: name lookups
    /// come from player commands (friend/guild requests), not per-tick work.
    /// </summary>
    public CharacterIdentity? FindByName(string name)
        => _byId.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

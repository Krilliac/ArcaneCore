using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Characters;

/// <summary>
/// The identity (name, race, gender, class) of every character on the realm, so name queries
/// for offline characters need no database round trip — vmangos keeps the same cache
/// (ObjectMgr::LoadPlayerCacheData at startup, GetPlayerDataByGUID for name queries).
/// Loaded once at startup and kept current by character creation and deletion. Thread-safe.
/// </summary>
public sealed class CharacterDirectory
{
    private readonly ConcurrentDictionary<int, CharacterIdentity> _byId = new();

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

    public void Add(CharacterIdentity identity) => _byId[identity.Id] = identity;

    public void Remove(int characterId) => _byId.TryRemove(characterId, out _);

    public CharacterIdentity? Find(int characterId) => _byId.GetValueOrDefault(characterId);

    /// <summary>
    /// The character called <paramref name="name"/> (case-insensitive), online or not, like
    /// vmangos ObjectMgr::GetPlayerGuidByName over its player cache. A linear scan: name lookups
    /// come from player commands (friend/guild requests), not per-tick work.
    /// </summary>
    public CharacterIdentity? FindByName(string name)
        => _byId.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

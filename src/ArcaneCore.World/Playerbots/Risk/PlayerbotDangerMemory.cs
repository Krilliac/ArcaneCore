using System.Numerics;
using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// What a bot remembers after a retreat or a death, for <see cref="PlayerbotRiskOptions.DangerMemorySeconds"/> of world time: the
/// creatures it fled from or died to (it does not pull them again), their kinds and the places it happened (each one near a
/// candidate raises that fight's risk). Bounded; the oldest goes first. World thread.
/// </summary>
internal sealed class PlayerbotDangerMemory
{
    /// <summary>A remembered place covers this radius (yards).</summary>
    internal const float SpotYards = 30f;

    private const int Max = 32;
    private readonly List<Spot> _spots = [];
    private readonly Dictionary<ObjectGuid, uint> _creatures = [];
    private readonly Dictionary<uint, (int Count, uint UntilMs)> _entries = [];

    /// <summary>Danger events remembered so far (deaths and retreats).</summary>
    internal int Count { get; private set; }

    internal void Remember(uint mapId, Vector3 where, IEnumerable<(ObjectGuid Guid, uint Entry)> creatures, uint nowMs, int seconds)
    {
        if (seconds <= 0) return;
        uint until = unchecked(nowMs + ((uint)seconds * 1000u));
        Expire(nowMs);
        if (_spots.Count >= Max) _spots.RemoveAt(0);
        _spots.Add(new Spot(mapId, where, until));
        foreach ((ObjectGuid guid, uint entry) in creatures)
        {
            if (_creatures.Count >= Max && !_creatures.ContainsKey(guid)) _creatures.Remove(_creatures.OrderBy(c => c.Value).First().Key);
            _creatures[guid] = until;
            if (entry == 0) continue;
            int count = _entries.TryGetValue(entry, out var known) ? known.Count : 0;
            if (_entries.Count >= Max && count == 0) _entries.Remove(_entries.OrderBy(e => e.Value.UntilMs).First().Key);
            _entries[entry] = (count + 1, until);
        }

        Count++;
    }

    /// <summary>The bot fled from or died to this very creature, and still remembers it.</summary>
    internal bool IsRemembered(ObjectGuid creature, uint nowMs)
        => _creatures.TryGetValue(creature, out uint until) && unchecked((int)(until - nowMs)) > 0;

    /// <summary>Remembered events near <paramref name="where"/> and against <paramref name="entry"/>.</summary>
    internal int Hits(uint mapId, Vector3 where, uint entry, uint nowMs)
    {
        Expire(nowMs);
        int hits = _spots.Count(spot => spot.MapId == mapId && Vector3.Distance(spot.Where, where) <= SpotYards);
        if (_entries.TryGetValue(entry, out var known)) hits += known.Count;
        return hits;
    }

    internal void Clear()
    {
        _spots.Clear();
        _creatures.Clear();
        _entries.Clear();
    }

    private void Expire(uint nowMs)
    {
        _spots.RemoveAll(spot => unchecked((int)(spot.UntilMs - nowMs)) <= 0);
        foreach (ObjectGuid guid in _creatures.Where(c => unchecked((int)(c.Value - nowMs)) <= 0).Select(c => c.Key).ToArray()) _creatures.Remove(guid);
        foreach (uint entry in _entries.Where(e => unchecked((int)(e.Value.UntilMs - nowMs)) <= 0).Select(e => e.Key).ToArray()) _entries.Remove(entry);
    }

    private sealed record Spot(uint MapId, Vector3 Where, uint UntilMs);
}

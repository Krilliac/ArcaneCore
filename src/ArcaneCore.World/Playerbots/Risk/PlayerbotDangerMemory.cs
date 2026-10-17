using System.Numerics;
using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// What a bot remembers after a retreat or a death, for <see cref="PlayerbotRiskOptions.DangerMemorySeconds"/> of world time: the
/// creatures it fled from or died to (it does not pull them again), their kinds and the places it happened (each one near a
/// candidate raises that fight's risk), and the kinds that beat it one against one (<see cref="LostAlone"/>). Bounded; the oldest
/// goes first. World thread.
/// </summary>
internal sealed class PlayerbotDangerMemory
{
    /// <summary>A remembered place covers this radius (yards).</summary>
    internal const float SpotYards = 30f;

    private const int Max = 32;
    private readonly List<Spot> _spots = [];
    private readonly Dictionary<ObjectGuid, (uint SinceMs, uint UntilMs)> _creatures = [];
    private readonly Dictionary<uint, (int Count, uint UntilMs)> _entries = [];
    private readonly Dictionary<uint, uint> _lostAlone = [];

    /// <summary>Danger events remembered so far (deaths and retreats).</summary>
    internal int Count { get; private set; }

    /// <param name="alone">
    /// The fight was against one creature only (<paramref name="creatures"/> holds one): its kind beat the bot one against one, and a
    /// lone pull of that kind is not taken again while this is remembered (<see cref="LostAlone"/>).
    /// </param>
    internal void Remember(uint mapId, Vector3 where, IEnumerable<(ObjectGuid Guid, uint Entry)> creatures, uint nowMs, int seconds,
        bool alone = false)
    {
        if (seconds <= 0) return;
        uint until = unchecked(nowMs + ((uint)seconds * 1000u));
        Expire(nowMs);
        if (_spots.Count >= Max) _spots.RemoveAt(0);
        _spots.Add(new Spot(mapId, where, until));
        foreach ((ObjectGuid guid, uint entry) in creatures)
        {
            if (_creatures.Count >= Max && !_creatures.ContainsKey(guid)) _creatures.Remove(_creatures.OrderBy(c => c.Value.UntilMs).First().Key);
            // A creature still held keeps the time it was first remembered: the knowledge is that old (CheckKnownDanger reads it).
            uint since = _creatures.TryGetValue(guid, out var held) && unchecked((int)(held.UntilMs - nowMs)) > 0 ? held.SinceMs : nowMs;
            _creatures[guid] = (since, until);
            if (entry == 0) continue;
            int count = _entries.TryGetValue(entry, out var known) ? known.Count : 0;
            if (_entries.Count >= Max && count == 0) _entries.Remove(_entries.OrderBy(e => e.Value.UntilMs).First().Key);
            _entries[entry] = (count + 1, until);
            if (alone)
            {
                if (_lostAlone.Count >= Max && !_lostAlone.ContainsKey(entry)) _lostAlone.Remove(_lostAlone.OrderBy(e => e.Value).First().Key);
                _lostAlone[entry] = until;
            }
        }

        Count++;
    }

    /// <summary>The bot fled from or died to this very creature, and still remembers it.</summary>
    internal bool IsRemembered(ObjectGuid creature, uint nowMs)
        => _creatures.TryGetValue(creature, out var held) && unchecked((int)(held.UntilMs - nowMs)) > 0;

    /// <summary>
    /// The bot still remembers this creature, and already did before <paramref name="sinceMs"/> (a memory written at that time or later,
    /// by a retreat of the fight that began then, is not older knowledge). 0 for <paramref name="sinceMs"/>: any time.
    /// </summary>
    internal bool RememberedBefore(ObjectGuid creature, uint sinceMs, uint nowMs)
        => _creatures.TryGetValue(creature, out var held) && unchecked((int)(held.UntilMs - nowMs)) > 0
            && (sinceMs == 0 || unchecked((int)(sinceMs - held.SinceMs)) > 0);

    /// <summary>A creature of this kind alone beat the bot (it retreated from it, one against one), and it still remembers.</summary>
    internal bool LostAlone(uint entry, uint nowMs)
        => entry != 0 && _lostAlone.TryGetValue(entry, out uint until) && unchecked((int)(until - nowMs)) > 0;

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
        _lostAlone.Clear();
    }

    private void Expire(uint nowMs)
    {
        _spots.RemoveAll(spot => unchecked((int)(spot.UntilMs - nowMs)) <= 0);
        foreach (ObjectGuid guid in _creatures.Where(c => unchecked((int)(c.Value.UntilMs - nowMs)) <= 0).Select(c => c.Key).ToArray()) _creatures.Remove(guid);
        foreach (uint entry in _entries.Where(e => unchecked((int)(e.Value.UntilMs - nowMs)) <= 0).Select(e => e.Key).ToArray()) _entries.Remove(entry);
        foreach (uint entry in _lostAlone.Where(e => unchecked((int)(e.Value - nowMs)) <= 0).Select(e => e.Key).ToArray()) _lostAlone.Remove(entry);
    }

    private sealed record Spot(uint MapId, Vector3 Where, uint UntilMs);
}

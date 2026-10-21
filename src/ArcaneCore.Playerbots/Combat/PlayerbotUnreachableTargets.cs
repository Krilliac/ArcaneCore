using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// The targets a bot gave up as unreachable, each until a time (the world's millisecond clock, <c>uint</c>, which wraps): it skips them
/// meanwhile. Bounded: when full, marking a new target drops the entry that expires soonest. Owned by one bot; one thread (its think).
/// </summary>
internal sealed class PlayerbotUnreachableTargets
{
    /// <summary>The most targets remembered at once.</summary>
    public const int Capacity = 64;

    private readonly Dictionary<ObjectGuid, uint> _until = new(Capacity);

    /// <summary>The number of entries held, expired ones included until a mark or a lookup drops them.</summary>
    public int Count => _until.Count;

    /// <summary>Remembers <paramref name="guid"/> as unreachable from <paramref name="nowMs"/> for <paramref name="forMs"/>; marking it again moves its end.</summary>
    public void Mark(ObjectGuid guid, uint nowMs, uint forMs)
    {
        if (!_until.ContainsKey(guid) && _until.Count >= Capacity) EvictSoonest(nowMs);
        _until[guid] = unchecked(nowMs + forMs);
    }

    /// <summary>Whether <paramref name="guid"/> is still marked at <paramref name="nowMs"/>. An expired entry is dropped.</summary>
    public bool Has(ObjectGuid guid, uint nowMs)
    {
        if (!_until.TryGetValue(guid, out uint until)) return false;
        if (unchecked((int)(until - nowMs)) > 0) return true; // wrap-safe: the time left as a signed distance
        _until.Remove(guid);
        return false;
    }

    /// <summary>Forgets every target.</summary>
    public void Clear() => _until.Clear();

    private void EvictSoonest(uint nowMs)
    {
        ObjectGuid soonest = default;
        int least = int.MaxValue;
        foreach ((ObjectGuid guid, uint until) in _until)
        {
            int left = unchecked((int)(until - nowMs));
            if (left < least)
            {
                least = left;
                soonest = guid;
            }
        }

        _until.Remove(soonest);
    }
}

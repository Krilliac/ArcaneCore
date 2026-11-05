using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The world-wide brake on capital city trips: at most <see cref="PlayerbotCityOptions.MaxTripsPerCapital"/> trips under way to one capital and
/// at most <see cref="PlayerbotCityOptions.MaxStartsPerMinute"/> starting for it in any minute, so a restart, a patch of levels or a shared
/// trigger does not send every bot to the same auctioneer at once. One instance per world (a weak side table), world thread only.
/// <para>
/// A trip holds a slot from its start until its controller releases it; a slot whose holder never did (a bot that logged out mid-trip) expires
/// after <see cref="TripCapMs"/>, the longest a trip can last. Cost: a scan of the trips under way (a handful) per start.
/// </para>
/// </summary>
internal sealed class PlayerbotCityLimiter
{
    /// <summary>The longest a trip can last (ms): a slot older than this is taken to belong to a bot that never released it.</summary>
    internal const uint TripCapMs = 1_500_000;

    private const uint WindowMs = 60_000;
    private static readonly ConditionalWeakTable<WorldRuntime, PlayerbotCityLimiter> Attached = new();

    private readonly Dictionary<object, (uint Zone, uint At)> _active = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<uint, Queue<uint>> _starts = [];
    private readonly List<object> _expired = [];

    internal static PlayerbotCityLimiter Of(WorldRuntime world) => Attached.GetValue(world, _ => new PlayerbotCityLimiter());

    /// <summary>The trips under way to a capital.</summary>
    internal int ActiveTrips(uint zoneId) => _active.Values.Count(v => v.Zone == zoneId);

    /// <summary>Take a slot for <paramref name="holder"/> at a capital; false (nothing taken) when the city is at either cap.</summary>
    internal bool TryAcquire(object holder, uint zoneId, uint now, PlayerbotCityOptions city)
    {
        _expired.Clear();
        foreach ((object key, (uint _, uint at)) in _active)
            if (unchecked(now - at) > TripCapMs) _expired.Add(key);
        foreach (object key in _expired) _active.Remove(key);

        // A holder asking again for the capital it already holds keeps its slot: no second slot, no start consumed, expiry unchanged.
        if (_active.TryGetValue(holder, out (uint Zone, uint At) held) && held.Zone == zoneId) return true;
        // State changes only once both caps pass, so a refused move to another capital leaves the slot held in the first one.
        if (ActiveTrips(zoneId) >= city.MaxTripsPerCapital) return false;
        if (!_starts.TryGetValue(zoneId, out Queue<uint>? starts)) _starts[zoneId] = starts = new Queue<uint>();
        while (starts.Count > 0 && unchecked(now - starts.Peek()) >= WindowMs) starts.Dequeue();
        if (starts.Count >= city.MaxStartsPerMinute) return false;
        starts.Enqueue(now);
        _active[holder] = (zoneId, now);
        return true;
    }

    /// <summary>Give the slot back (a trip ended, whatever the reason).</summary>
    internal void Release(object holder) => _active.Remove(holder);
}

namespace ArcaneCore.Game.Instances.Entry;

/// <summary>
/// The per-account "instances entered in the last hour" limit (vmangos
/// <c>AccountMgr::CheckInstanceCount</c> / <c>AddInstanceEnterTime</c>, AccountMgr.cpp:441-472;
/// in memory only, so a restart clears it). One entry per instance id; re-entering an id
/// refreshes its time; entries are purged lazily, only when a new instance is asked for at the cap.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class InstanceEnterLimiter
{
    private const long WindowSeconds = 3600;

    private readonly Dictionary<int, Dictionary<uint, long>> _enterTimes = [];

    /// <summary>
    /// Whether the account may enter <paramref name="instanceId"/> (0 = a not yet created instance)
    /// now. At the cap, one entry older than an hour is dropped to make room (vmangos erases the
    /// first one its unordered map yields; the oldest is dropped here).
    /// </summary>
    public bool CanEnter(int accountId, uint instanceId, int maxCount, long now)
    {
        if (!_enterTimes.TryGetValue(accountId, out Dictionary<uint, long>? times) || times.ContainsKey(instanceId) || times.Count < maxCount)
        {
            return true;
        }

        foreach ((uint id, long time) in times.OrderBy(t => t.Value))
        {
            if (time + WindowSeconds < now)
            {
                times.Remove(id);
                return true;
            }
        }

        return false;
    }

    /// <summary>Record an entry (vmangos <c>DungeonMap::Add</c> -> <c>AddInstanceEnterTime</c>, Map.cpp:2188).</summary>
    public void Record(int accountId, uint instanceId, long now)
    {
        if (!_enterTimes.TryGetValue(accountId, out Dictionary<uint, long>? times))
        {
            _enterTimes[accountId] = times = [];
        }

        times[instanceId] = now;
    }

    /// <summary>Number of instance ids recorded for the account (including ones past the hour not yet purged).</summary>
    public int Count(int accountId) => _enterTimes.TryGetValue(accountId, out Dictionary<uint, long>? times) ? times.Count : 0;
}

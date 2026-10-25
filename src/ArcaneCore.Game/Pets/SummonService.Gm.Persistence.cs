namespace ArcaneCore.Game.Pets;

/// <summary>Read-only view of the pet write-behind state for the persistence queue probe (<c>.arcane queues</c>).</summary>
public sealed partial class SummonService
{
    /// <summary>Pet saves still in flight (a character's newest queued write has not completed).</summary>
    public int PendingPersistenceWrites
    {
        get
        {
            lock (_cacheGate)
            {
                int pending = 0;
                foreach (Task tail in _saveTails.Values)
                {
                    if (!tail.IsCompleted) pending++;
                }
                return pending;
            }
        }
    }

    /// <summary>Ids of the characters whose failed pet snapshot is kept and retried by the next flush, ascending.</summary>
    public IReadOnlyList<int> RetainedPersistenceCharacters
    {
        get
        {
            lock (_cacheGate)
            {
                if (_retained.Count == 0) return [];
                int[] ids = [.. _retained.Keys];
                Array.Sort(ids);
                return ids;
            }
        }
    }
}

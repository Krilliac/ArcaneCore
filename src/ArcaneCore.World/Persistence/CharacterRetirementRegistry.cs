using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// The characters this process no longer owns: a whole-World handoff passed its point of no return and the new owner holds them now, so every write this
/// world could still attempt for them would be refused by the characters fence. Each character write path (the save queue, the keyed and feature write
/// queues) asks <see cref="IsRetired"/> before it enqueues and again before it executes; a retired character's write is dropped silently and counted per
/// queue (<see cref="TryDrop"/>), and the queue logs one informational line with its count at stop (<see cref="LogStopSummary"/>). A character that was
/// never retired is never affected. Retirement has no way back. One instance per process (a singleton in the world's services); thread-safe, any thread.
/// </summary>
public sealed class CharacterRetirementRegistry
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _retired = [];
    private readonly Dictionary<string, long> _dropped = new(StringComparer.Ordinal);

    /// <summary>Mark these characters as owned by another world from now on. Idempotent. Returns how many were newly retired.</summary>
    public int Retire(IEnumerable<int> characterIds)
    {
        ArgumentNullException.ThrowIfNull(characterIds);
        int added = 0;
        lock (_gate)
        {
            foreach (int id in characterIds)
            {
                if (_retired.Add(id))
                {
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>True when the process no longer owns the character.</summary>
    public bool IsRetired(int characterId)
    {
        lock (_gate)
        {
            return _retired.Count > 0 && _retired.Contains(characterId);
        }
    }

    /// <summary>When the character is retired, counts one dropped write against <paramref name="queue"/> and returns true (the caller drops the write silently); false for a character still owned.</summary>
    public bool TryDrop(string queue, int characterId)
    {
        lock (_gate)
        {
            if (_retired.Count == 0 || !_retired.Contains(characterId))
            {
                return false;
            }

            _dropped[queue] = _dropped.GetValueOrDefault(queue) + 1;
            return true;
        }
    }

    /// <summary>The writes dropped so far for <paramref name="queue"/>.</summary>
    public long DroppedCount(string queue)
    {
        lock (_gate)
        {
            return _dropped.GetValueOrDefault(queue);
        }
    }

    /// <summary>At stop: one informational line with the queue's dropped count (nothing when it dropped none).</summary>
    public void LogStopSummary(ILogger logger, string queue)
    {
        ArgumentNullException.ThrowIfNull(logger);
        long dropped = DroppedCount(queue);
        if (dropped > 0)
        {
            logger.LogInformation(
                "{Queue} dropped {Dropped} write(s) for characters that now belong to another world after the handoff",
                queue, dropped);
        }
    }

    /// <summary>Null-tolerant <see cref="TryDrop"/>: false when there is no registry.</summary>
    public static bool Drops([NotNullWhen(true)] CharacterRetirementRegistry? registry, string queue, int characterId)
        => registry is not null && registry.TryDrop(queue, characterId);

    /// <summary>Null-tolerant <see cref="IsRetired"/>: false when there is no registry.</summary>
    public static bool Retired([NotNullWhen(true)] CharacterRetirementRegistry? registry, int characterId)
        => registry is not null && registry.IsRetired(characterId);
}

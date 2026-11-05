using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// The characters this process no longer owns: a whole-World handoff passed its point of no return and the new owner holds them now, so every write this
/// world could still attempt for them would be refused by the characters fence. Each character write path (the save queue, the keyed and feature write
/// queues) asks <see cref="IsRetired"/> before it enqueues and again before it executes; a retired character's write is dropped silently and counted per
/// queue (<see cref="TryDrop"/>), and the queue logs one informational line with its count at stop (<see cref="LogStopSummary"/>). A character that was
/// never retired is never affected. Retirement has no way back. A handoff retires the whole process (<see cref="RetireAll"/>): the takeover moved the one ownership scope every write of
/// this process runs under, so no character is left that it could still write, named or not. Between its flush and that step the source is <see cref="Seal">sealed</see>: the takeover can
/// land at any moment, so a fence refusal is the expected end of a write, not a lost ownership. A write that belongs to no one character (an instance row, a guild) is covered by the
/// whole-process steps only (<see cref="TryDropShared"/>, <see cref="TryDropSharedRefused"/>): retiring named characters leaves it a write of this process. One instance per process (a singleton
/// in the world's services); thread-safe, any thread.
/// </summary>
public sealed class CharacterRetirementRegistry
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _retired = [];
    private readonly Dictionary<string, long> _dropped = new(StringComparer.Ordinal);
    private bool _allRetired;
    private bool _sealed;

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

    /// <summary>The process owns no character any more, whichever it is: every later write of any character is dropped. Idempotent; no way back.</summary>
    public void RetireAll()
    {
        lock (_gate)
        {
            _allRetired = true;
        }
    }

    /// <summary>
    /// From now on, until <see cref="Unseal"/> or <see cref="RetireAll"/> decides, a fence refusal of any character is dropped and counted instead of stopping the process
    /// (<see cref="TryDropRefused"/>). Writes are still attempted: before the takeover they land. A refusal by the characters fence (A's epoch token is stale after the takeover) could never have
    /// been written, so dropping it loses nothing. A refusal by the realm lease (RealmLeaseFenceLostException, or the lease not held) is also dropped, including the shared drop
    /// (<see cref="TryDropSharedRefused"/>), and that write is not proven unwritable: the drop is accepted because the flush left nothing open, so what a sealed write carries is a
    /// write that came after it. On the abort path (A lost the ownership some other way) the next write after <see cref="Unseal"/> is refused and goes terminal as ever. Idempotent.
    /// </summary>
    public void Seal()
    {
        lock (_gate)
        {
            _sealed = true;
        }
    }

    /// <summary>The handoff aborted and A resumed: a fence refusal means a lost ownership again. Idempotent.</summary>
    public void Unseal()
    {
        lock (_gate)
        {
            _sealed = false;
        }
    }

    /// <summary>True between <see cref="Seal"/> and <see cref="Unseal"/>: a handoff flushed and its takeover is undecided.</summary>
    public bool IsSealed
    {
        get
        {
            lock (_gate)
            {
                return _sealed;
            }
        }
    }

    /// <summary>True when the process no longer owns the character.</summary>
    public bool IsRetired(int characterId)
    {
        lock (_gate)
        {
            return IsRetiredLocked(characterId);
        }
    }

    /// <summary>When the character is retired, counts one dropped write against <paramref name="queue"/> and returns true (the caller drops the write silently); false for a character still owned.</summary>
    public bool TryDrop(string queue, int characterId)
    {
        lock (_gate)
        {
            if (!IsRetiredLocked(characterId))
            {
                return false;
            }

            _dropped[queue] = _dropped.GetValueOrDefault(queue) + 1;
            return true;
        }
    }

    /// <summary>For a write the fence just refused: as <see cref="TryDrop"/>, and also while the process is sealed. False is a real loss of ownership.</summary>
    public bool TryDropRefused(string queue, int characterId)
    {
        lock (_gate)
        {
            if (!_sealed && !IsRetiredLocked(characterId))
            {
                return false;
            }

            _dropped[queue] = _dropped.GetValueOrDefault(queue) + 1;
            return true;
        }
    }

    /// <summary>For a write of no one character: true, counted, once the whole process is retired (<see cref="RetireAll"/>). Retiring named characters does not touch it.</summary>
    public bool TryDropShared(string queue)
    {
        lock (_gate)
        {
            if (!_allRetired)
            {
                return false;
            }

            _dropped[queue] = _dropped.GetValueOrDefault(queue) + 1;
            return true;
        }
    }

    /// <summary>For a write of no one character that the fence just refused: as <see cref="TryDropShared"/>, and also while the process is sealed. False is a real loss of ownership.</summary>
    public bool TryDropSharedRefused(string queue)
    {
        lock (_gate)
        {
            if (!_sealed && !_allRetired)
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

    /// <summary>Null-tolerant <see cref="TryDropRefused"/>: false when there is no registry.</summary>
    public static bool DropsRefused([NotNullWhen(true)] CharacterRetirementRegistry? registry, string queue, int characterId)
        => registry is not null && registry.TryDropRefused(queue, characterId);

    /// <summary>Null-tolerant <see cref="TryDropShared"/>: false when there is no registry.</summary>
    public static bool DropsShared([NotNullWhen(true)] CharacterRetirementRegistry? registry, string queue)
        => registry is not null && registry.TryDropShared(queue);

    /// <summary>Null-tolerant <see cref="TryDropSharedRefused"/>: false when there is no registry.</summary>
    public static bool DropsSharedRefused([NotNullWhen(true)] CharacterRetirementRegistry? registry, string queue)
        => registry is not null && registry.TryDropSharedRefused(queue);

    /// <summary>Null-tolerant <see cref="IsRetired"/>: false when there is no registry.</summary>
    public static bool Retired([NotNullWhen(true)] CharacterRetirementRegistry? registry, int characterId)
        => registry is not null && registry.IsRetired(characterId);

    private bool IsRetiredLocked(int characterId) => _allRetired || _retired.Contains(characterId);
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Characters;

/// <summary>
/// Process-wide bookkeeping for finalizing character deletions that are pending in the deletion
/// ledger (<see cref="Kernel.Characters.ICharacterDeletionStore"/>): an in-flight guard so two
/// sessions of one account never finalize the same operation at once, and a per-session bound so a
/// finalizer that keeps failing is retried by the enumeration sweep at most once per session and
/// operation instead of on every character-list request (docs/integration/character-delete.md).
/// </summary>
public sealed class CharacterDeletionReconciler
{
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly ConditionalWeakTable<WorldSession, SessionSweeps> _sweeps = [];

    /// <summary>Claim an operation for finalization. False when another finalization of it is running.</summary>
    internal bool TryBegin(Guid operationId) => _inFlight.TryAdd(operationId, 0);

    internal void End(Guid operationId) => _inFlight.TryRemove(operationId, out _);

    /// <summary>True the first time this session sweeps <paramref name="operationId"/>; false for every repeat.</summary>
    internal bool TrySweep(WorldSession session, Guid operationId)
    {
        SessionSweeps sweeps = _sweeps.GetOrCreateValue(session);
        lock (sweeps)
        {
            return sweeps.Attempted.Add(operationId);
        }
    }

    private sealed class SessionSweeps
    {
        public HashSet<Guid> Attempted { get; } = [];
    }
}

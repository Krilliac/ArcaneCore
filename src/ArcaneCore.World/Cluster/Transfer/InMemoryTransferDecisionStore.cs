using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// Thread-safe transactional implementation of <see cref="ITransferDecisionStore"/> that enforces
/// atomic single-owner epoch handoff at <see cref="PlayerTransferStage.CommitDecision"/> and forbids
/// post-commit aborts.
/// </summary>
public sealed class InMemoryTransferDecisionStore : ITransferDecisionStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, TransferDecisionRecord> _decisions = [];
    private readonly Dictionary<ulong, CharacterOwnershipToken> _activeOwnership = [];
    private readonly Func<long> _clockUtcMs;

    public InMemoryTransferDecisionStore(Func<long>? clockUtcMs = null)
    {
        _clockUtcMs = clockUtcMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <inheritdoc />
    public void SeedCharacterOwnership(ulong characterGuid, CharacterOwnershipToken ownershipToken)
    {
        if (characterGuid == 0)
            throw new ArgumentOutOfRangeException(nameof(characterGuid), "CharacterGuid must be non-zero.");
        ArgumentNullException.ThrowIfNull(ownershipToken);

        lock (_sync)
        {
            _activeOwnership[characterGuid] = ownershipToken;
        }
    }

    /// <inheritdoc />
    public CharacterOwnershipToken? GetActiveOwnership(ulong characterGuid)
    {
        lock (_sync)
        {
            return _activeOwnership.TryGetValue(characterGuid, out CharacterOwnershipToken? token) ? token : null;
        }
    }

    /// <summary>
    /// Verifies whether <paramref name="candidate"/> matches the currently active durable ownership token
    /// for <paramref name="characterGuid"/>. Throws <see cref="CharacterOwnershipRejectedException"/> when stale.
    /// </summary>
    public void EnsureOwnershipActive(ulong characterGuid, CharacterOwnershipToken candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        lock (_sync)
        {
            if (!_activeOwnership.TryGetValue(characterGuid, out CharacterOwnershipToken? active) ||
                active != candidate)
            {
                throw new CharacterOwnershipRejectedException(candidate.Scope, candidate.Epoch);
            }
        }
    }

    /// <inheritdoc />
    public Task<TransferDecisionRecord> CreateReservationAsync(
        Guid transferId,
        ulong characterGuid,
        int accountId,
        long sessionGeneration,
        WorkerNodeId sourceWorkerId,
        WorkerNodeId targetWorkerId,
        uint sourceMapId,
        uint sourceInstanceId,
        uint targetMapId,
        uint targetInstanceId,
        CharacterOwnershipToken sourceOwnershipToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (transferId == Guid.Empty)
            throw new ArgumentException("TransferId must be non-empty.", nameof(transferId));
        if (characterGuid == 0)
            throw new ArgumentOutOfRangeException(nameof(characterGuid));
        ArgumentNullException.ThrowIfNull(sourceOwnershipToken);

        lock (_sync)
        {
            if (_decisions.ContainsKey(transferId))
                throw new InvalidOperationException($"Transfer '{transferId}' already exists.");

            // Check that no other unresolved transfer is already in flight for this character.
            bool hasInFlight = _decisions.Values.Any(d =>
                d.CharacterGuid == characterGuid &&
                d.DecisionState is PlayerTransferDecisionState.PreCommit or PlayerTransferDecisionState.Committed);
            if (hasInFlight)
            {
                throw new InvalidOperationException(
                    $"Character {characterGuid} already has an unresolved transfer in flight.");
            }

            if (_activeOwnership.TryGetValue(characterGuid, out CharacterOwnershipToken? currentOwner))
            {
                if (currentOwner != sourceOwnershipToken)
                {
                    throw new CharacterOwnershipRejectedException(
                        sourceOwnershipToken.Scope,
                        sourceOwnershipToken.Epoch);
                }
            }
            else
            {
                _activeOwnership[characterGuid] = sourceOwnershipToken;
            }

            var record = new TransferDecisionRecord(
                TransferId: transferId,
                CharacterGuid: characterGuid,
                AccountId: accountId,
                SessionGeneration: sessionGeneration,
                SourceWorkerId: sourceWorkerId,
                TargetWorkerId: targetWorkerId,
                SourceMapId: sourceMapId,
                SourceInstanceId: sourceInstanceId,
                TargetMapId: targetMapId,
                TargetInstanceId: targetInstanceId,
                CurrentStage: PlayerTransferStage.Reserve,
                DecisionState: PlayerTransferDecisionState.PreCommit,
                SourceOwnershipToken: sourceOwnershipToken,
                TargetOwnershipToken: null,
                CapturedBundle: null,
                AbortReason: null,
                UpdatedAtUtcMs: _clockUtcMs());

            _decisions[transferId] = record;
            return Task.FromResult(record);
        }
    }

    /// <inheritdoc />
    public Task<TransferDecisionRecord> AdvancePreCommitStageAsync(
        Guid transferId,
        PlayerTransferStage nextStage,
        PlayerTransferBundle? capturedBundle = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (nextStage is not (PlayerTransferStage.SuspendAndCapture
            or PlayerTransferStage.InstallPassive
            or PlayerTransferStage.StageWorldPort))
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextStage),
                $"AdvancePreCommitStageAsync only accepts stages 2..4; got {nextStage}.");
        }

        lock (_sync)
        {
            TransferDecisionRecord current = GetRequiredLocked(transferId);
            if (current.DecisionState != PlayerTransferDecisionState.PreCommit)
            {
                throw new InvalidOperationException(
                    $"Cannot advance pre-commit stage for transfer '{transferId}' in state {current.DecisionState}.");
            }

            PlayerTransferStage expectedPrevious = nextStage switch
            {
                PlayerTransferStage.SuspendAndCapture => PlayerTransferStage.Reserve,
                PlayerTransferStage.InstallPassive => PlayerTransferStage.SuspendAndCapture,
                PlayerTransferStage.StageWorldPort => PlayerTransferStage.InstallPassive,
                _ => PlayerTransferStage.None,
            };

            if (current.CurrentStage != expectedPrevious)
            {
                throw new InvalidOperationException(
                    $"Invalid stage transition for transfer '{transferId}': {current.CurrentStage} -> {nextStage} (expected {expectedPrevious}).");
            }

            EnsureOwnershipMatchesLocked(current.CharacterGuid, current.SourceOwnershipToken);

            PlayerTransferBundle? bundleToStore = capturedBundle ?? current.CapturedBundle;
            if (nextStage == PlayerTransferStage.SuspendAndCapture && bundleToStore is null)
            {
                throw new InvalidOperationException(
                    $"Stage {PlayerTransferStage.SuspendAndCapture} requires a non-null PlayerTransferBundle.");
            }

            TransferDecisionRecord updated = current with
            {
                CurrentStage = nextStage,
                CapturedBundle = bundleToStore,
                UpdatedAtUtcMs = _clockUtcMs(),
            };

            _decisions[transferId] = updated;
            return Task.FromResult(updated);
        }
    }

    /// <inheritdoc />
    public Task<TransferDecisionRecord> CommitDecisionAsync(
        Guid transferId,
        Guid targetIncarnation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetIncarnation == Guid.Empty)
            throw new ArgumentException("Target incarnation must be non-empty.", nameof(targetIncarnation));

        lock (_sync)
        {
            TransferDecisionRecord current = GetRequiredLocked(transferId);

            // Idempotent return if the same transfer was already committed with the same target incarnation.
            if (current.DecisionState is PlayerTransferDecisionState.Committed or PlayerTransferDecisionState.Completed &&
                current.TargetOwnershipToken is not null &&
                current.TargetOwnershipToken.Incarnation == targetIncarnation)
            {
                return Task.FromResult(current);
            }

            if (current.DecisionState != PlayerTransferDecisionState.PreCommit ||
                current.CurrentStage != PlayerTransferStage.StageWorldPort)
            {
                throw new InvalidOperationException(
                    $"Transfer '{transferId}' cannot commit from stage {current.CurrentStage} (state={current.DecisionState}); expected StageWorldPort.");
            }

            if (current.CapturedBundle is null)
            {
                throw new InvalidOperationException(
                    $"Transfer '{transferId}' cannot commit without a captured PlayerTransferBundle.");
            }

            // Fence check: source ownership token must still be the active authority at the commit instant.
            EnsureOwnershipMatchesLocked(current.CharacterGuid, current.SourceOwnershipToken);

            var targetToken = new CharacterOwnershipToken(
                Scope: current.SourceOwnershipToken.Scope,
                Epoch: checked(current.SourceOwnershipToken.Epoch + 1),
                Incarnation: targetIncarnation);

            // Atomic ownership handoff: source epoch N is superseded by target epoch N + 1 in the same critical section.
            _activeOwnership[current.CharacterGuid] = targetToken;

            TransferDecisionRecord committed = current with
            {
                CurrentStage = PlayerTransferStage.CommitDecision,
                DecisionState = PlayerTransferDecisionState.Committed,
                TargetOwnershipToken = targetToken,
                UpdatedAtUtcMs = _clockUtcMs(),
            };

            _decisions[transferId] = committed;
            return Task.FromResult(committed);
        }
    }

    /// <inheritdoc />
    public Task<TransferDecisionRecord> MarkCompletedAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            TransferDecisionRecord current = GetRequiredLocked(transferId);
            if (current.DecisionState == PlayerTransferDecisionState.Completed)
                return Task.FromResult(current);

            if (current.DecisionState != PlayerTransferDecisionState.Committed ||
                current.TargetOwnershipToken is null)
            {
                throw new InvalidOperationException(
                    $"Transfer '{transferId}' cannot complete from state {current.DecisionState} (stage={current.CurrentStage}); CommitDecision must succeed first.");
            }

            EnsureOwnershipMatchesLocked(current.CharacterGuid, current.TargetOwnershipToken);

            TransferDecisionRecord completed = current with
            {
                CurrentStage = PlayerTransferStage.ActivateAndRetire,
                DecisionState = PlayerTransferDecisionState.Completed,
                UpdatedAtUtcMs = _clockUtcMs(),
            };

            _decisions[transferId] = completed;
            return Task.FromResult(completed);
        }
    }

    /// <inheritdoc />
    public Task<TransferDecisionRecord> AbortPreCommitAsync(
        Guid transferId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reason))
            reason = "aborted";

        lock (_sync)
        {
            TransferDecisionRecord current = GetRequiredLocked(transferId);

            if (current.DecisionState == PlayerTransferDecisionState.Aborted)
                return Task.FromResult(current);

            if (current.IsCommittedOrBeyond)
            {
                throw new InvalidOperationException(
                    $"Transfer '{transferId}' has already reached {current.CurrentStage} ({current.DecisionState}); post-commit abort is forbidden and must roll forward.");
            }

            // Preserve source ownership token unchanged.
            _activeOwnership[current.CharacterGuid] = current.SourceOwnershipToken;

            TransferDecisionRecord aborted = current with
            {
                CurrentStage = PlayerTransferStage.Aborted,
                DecisionState = PlayerTransferDecisionState.Aborted,
                AbortReason = reason,
                UpdatedAtUtcMs = _clockUtcMs(),
            };

            _decisions[transferId] = aborted;
            return Task.FromResult(aborted);
        }
    }

    /// <inheritdoc />
    public Task<TransferDecisionRecord?> GetDecisionAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            return Task.FromResult(_decisions.TryGetValue(transferId, out TransferDecisionRecord? record) ? record : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TransferDecisionRecord>> GetUnresolvedForCharacterAsync(
        ulong characterGuid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            IReadOnlyList<TransferDecisionRecord> list = _decisions.Values
                .Where(d =>
                    d.CharacterGuid == characterGuid &&
                    d.DecisionState is PlayerTransferDecisionState.PreCommit or PlayerTransferDecisionState.Committed)
                .OrderBy(d => d.UpdatedAtUtcMs)
                .ToArray();

            return Task.FromResult(list);
        }
    }

    /// <inheritdoc />
    public Task<int> CountUnresolvedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            return Task.FromResult(_decisions.Values.Count(d =>
                d.DecisionState is PlayerTransferDecisionState.PreCommit or PlayerTransferDecisionState.Committed));
        }
    }

    private TransferDecisionRecord GetRequiredLocked(Guid transferId)
    {
        if (!_decisions.TryGetValue(transferId, out TransferDecisionRecord? record))
            throw new KeyNotFoundException($"Transfer decision '{transferId}' was not found.");

        return record;
    }

    private void EnsureOwnershipMatchesLocked(ulong characterGuid, CharacterOwnershipToken expected)
    {
        if (!_activeOwnership.TryGetValue(characterGuid, out CharacterOwnershipToken? active) ||
            active != expected)
        {
            throw new CharacterOwnershipRejectedException(expected.Scope, expected.Epoch);
        }
    }
}

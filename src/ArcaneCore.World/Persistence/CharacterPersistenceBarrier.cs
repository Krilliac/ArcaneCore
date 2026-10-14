using System.Diagnostics;
using ArcaneCore.Game.Pets;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// Additional per-character persistence participant that <see cref="CharacterPersistenceBarrier"/>
/// flushes during a character login, logout, or cluster transfer handoff barrier.
/// </summary>
public interface ICharacterPersistenceParticipant
{
    /// <summary>Diagnostic name of the persistence queue or subsystem.</summary>
    string Name { get; }

    /// <summary>Drain and retry any pending or retained writes for <paramref name="characterId"/>; fault if not durable.</summary>
    Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>A single queue or settlement failure observed during <see cref="CharacterPersistenceBarrier"/>.</summary>
public sealed record CharacterPersistenceFailure(string QueueName, Exception Exception);

/// <summary>Outcome of a unified per-character persistence flush across all world queues.</summary>
public sealed record CharacterPersistenceFlushResult(
    int CharacterId,
    IReadOnlyList<CharacterPersistenceFailure> Failures)
{
    /// <summary>True when every settlement barrier and per-character write queue drained durably with zero failures.</summary>
    public bool IsDurable => Failures.Count == 0;

    /// <summary>Names of all queues that failed to flush durably.</summary>
    public IReadOnlyList<string> FailedQueues => [.. Failures.Select(f => f.QueueName)];
}

/// <summary>
/// Unified per-character persistence barrier (M15.0 B1): coordinates waiting on in-flight settlements
/// and flushing every per-character persistence queue for a given character ID, reporting durable
/// success or detailed per-queue failures so login, logout, and cluster handoff can gate on one barrier.
/// </summary>
public sealed class CharacterPersistenceBarrier
{
    /// <summary>Default maximum time allowed for a unified character persistence barrier flush.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly IServiceProvider _services;
    private readonly ILogger<CharacterPersistenceBarrier>? _logger;

    public CharacterPersistenceBarrier(IServiceProvider services, ILogger<CharacterPersistenceBarrier>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger;
    }

    /// <summary>
    /// Attempt to flush all settlement barriers and per-character persistence queues for <paramref name="characterId"/>.
    /// Every registered queue is attempted even when an earlier queue reports a failure, so the caller receives the
    /// complete list of dirty or retained queues.
    /// </summary>
    public async Task<CharacterPersistenceFlushResult> TryFlushCharacterAsync(
        int characterId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveTimeout, TimeSpan.Zero, nameof(timeout));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(effectiveTimeout);
        var stopwatch = Stopwatch.StartNew();
        var failures = new List<CharacterPersistenceFailure>();

        // 1. Wait for any in-flight detached settlements before draining write queues.
        if (_services.GetService<QuestNpcFeature>() is { } questFeature)
        {
            await RunStepAsync(
                "QuestNpcSettlement",
                ct => questFeature.WaitForSettlementAsync(characterId, ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        foreach (ICharacterSettlementBarrier barrier in _services.GetServices<ICharacterSettlementBarrier>())
        {
            string barrierName = barrier.GetType().Name;
            await RunStepAsync(
                barrierName,
                ct => barrier.WaitForSettlementAsync(characterId, ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        // 2. Drain the primary character snapshot queue.
        if (_services.GetService<CharacterSaveQueue>() is { } saves)
        {
            await RunStepAsync(
                nameof(CharacterSaveQueue),
                ct => saves.FlushCharacterAsync(characterId, ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        // 3. Drain all per-character feature persistence queues.
        if ((_services.GetService<QuestNpcPersistence>() ?? _services.GetService<QuestNpcFeature>()?.Persistence) is { } questPersistence)
        {
            await RunStepAsync(
                nameof(QuestNpcPersistence),
                ct => questPersistence.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<TaxiFlightWriteQueue>() ?? _services.GetService<NpcServicesFeature>()?.RouteWrites) is { } taxiWrites)
        {
            await RunStepAsync(
                nameof(TaxiFlightWriteQueue),
                ct => taxiWrites.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<SpellbookCache>() ?? _services.GetService<SpellFeature>()?.Spellbook) is { } spellbook)
        {
            await RunStepAsync(
                nameof(SpellbookCache),
                ct => spellbook.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<SpellStatePersistence>() ?? _services.GetService<SpellFeature>()?.State) is { } spellState)
        {
            await RunStepAsync(
                nameof(SpellStatePersistence),
                ct => spellState.FlushCharacterDurableAsync(characterId, ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<TalentPersistence>() ?? _services.GetService<TalentFeature>()?.Persistence) is { } talentPersistence)
        {
            await RunStepAsync(
                nameof(TalentPersistence),
                ct => talentPersistence.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<SkillSaveCoordinator>() ?? _services.GetService<SkillsFeature>()?.Saves) is { } skillSaves)
        {
            await RunStepAsync(
                nameof(SkillSaveCoordinator),
                ct => skillSaves.FlushCharacterAsync(characterId, RemainingBudget(stopwatch, effectiveTimeout)).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if (_services.GetService<ReputationWriteQueue>() is { } reputationWrites)
        {
            await RunStepAsync(
                nameof(ReputationWriteQueue),
                ct => reputationWrites.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }
        else if (_services.GetService<ReputationFeature>() is { } reputationFeature)
        {
            await RunStepAsync(
                nameof(ReputationWriteQueue),
                ct => reputationFeature.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<SocialWriteQueue>() ?? _services.GetService<SocialFeature>()?.Writes) is { } socialWrites)
        {
            await RunStepAsync(
                nameof(SocialWriteQueue),
                ct => socialWrites.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<HonorWriteQueue>() ?? _services.GetService<HonorFeature>()?.Writes) is { } honorWrites)
        {
            await RunStepAsync(
                nameof(HonorWriteQueue),
                ct => honorWrites.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<RestWriteQueue>() ?? _services.GetService<RestFeature>()?.Writes) is { } restWrites)
        {
            await RunStepAsync(
                nameof(RestWriteQueue),
                ct => restWrites.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if (_services.GetService<ExploredZonesWriteQueue>() is { } exploredWrites)
        {
            await RunStepAsync(
                nameof(ExploredZonesWriteQueue),
                ct => exploredWrites.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }
        else if (_services.GetService<ExploredZonesPersistence>() is { } exploredPersistence)
        {
            await RunStepAsync(
                nameof(ExploredZonesWriteQueue),
                ct => exploredPersistence.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<InstanceWriteQueue>() ?? _services.GetService<InstanceFeature>()?.Writes) is { } instanceWrites)
        {
            await RunStepAsync(
                nameof(InstanceWriteQueue),
                ct => instanceWrites.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        if ((_services.GetService<SummonService>() ?? _services.GetService<PetsFeature>()?.Service) is { } petService)
        {
            await RunStepAsync(
                nameof(SummonService),
                ct => petService.FlushCharacterAsync(characterId).WaitAsync(ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        foreach (ICharacterPersistenceParticipant participant in _services.GetServices<ICharacterPersistenceParticipant>())
        {
            await RunStepAsync(
                participant.Name,
                ct => participant.FlushCharacterAsync(characterId, ct),
                failures,
                characterId,
                budget.Token).ConfigureAwait(false);
        }

        return new CharacterPersistenceFlushResult(characterId, failures);
    }

    /// <summary>
    /// Flush all settlement barriers and per-character persistence queues for <paramref name="characterId"/>,
    /// throwing <see cref="InvalidOperationException"/> when any queue remains dirty, retained, or faulted.
    /// </summary>
    public async Task FlushCharacterAsync(
        int characterId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CharacterPersistenceFlushResult result = await TryFlushCharacterAsync(characterId, timeout, cancellationToken).ConfigureAwait(false);
        if (result.IsDurable)
        {
            return;
        }

        Exception inner = result.Failures.Count == 1
            ? result.Failures[0].Exception
            : new AggregateException(result.Failures.Select(f => f.Exception));

        throw new InvalidOperationException(
            $"Character {characterId} persistence barrier failed for queue(s): {string.Join(", ", result.FailedQueues)}.",
            inner);
    }

    private async Task RunStepAsync(
        string queueName,
        Func<CancellationToken, Task> action,
        List<CharacterPersistenceFailure> failures,
        int characterId,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "Character {CharacterId} persistence barrier step {QueueName} failed", characterId, queueName);
            failures.Add(new CharacterPersistenceFailure(queueName, ex));
        }
    }

    private static TimeSpan RemainingBudget(Stopwatch stopwatch, TimeSpan totalBudget)
    {
        TimeSpan remaining = totalBudget - stopwatch.Elapsed;
        return remaining > TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50);
    }
}

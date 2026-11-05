using System.Diagnostics;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Diagnostics;
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

    /// <summary>Threshold above which a unified character persistence barrier flush reports a <c>persist/save-slow</c> anomaly (zero disables).</summary>
    public TimeSpan SlowFlushThreshold { get; init; } = TimeSpan.FromSeconds(2);

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

        TimeSpan elapsed = stopwatch.Elapsed;
        if (SlowFlushThreshold > TimeSpan.Zero && elapsed >= SlowFlushThreshold)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "save-slow",
                $"character {characterId}",
                $"barrier=CharacterPersistenceBarrier character={characterId} elapsedMs={(long)elapsed.TotalMilliseconds} thresholdMs={(long)SlowFlushThreshold.TotalMilliseconds} failures={failures.Count}",
                AnomalySeverity.Info);
        }

        return new CharacterPersistenceFlushResult(characterId, failures);
    }

    /// <summary>
    /// The characters the keyed feature queues that this barrier knows still have a write open for, whoever they are (a queue not registered with the barrier is not asked): a handoff flushes these too, or an offline character's retained write would never be named and would
    /// miss the commit. Each queue first runs its ordered barrier (which never retries), so a write still queued has landed or is retained by the time it is read; a queue with no such barrier
    /// reports what it has waiting as well. The save queue is not included (<see cref="CharacterSaveQueue.UnsettledCharacterIds"/>). Faults, under <see cref="DefaultTimeout"/>, when a queue does not
    /// answer. Ascending.
    /// </summary>
    public async Task<IReadOnlyList<int>> OpenCharacterIdsAsync(CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DefaultTimeout);
        var open = new SortedSet<int>();

        async Task AddAsync(Func<Task> drain, Func<IEnumerable<int>> retained)
        {
            await drain().WaitAsync(budget.Token).ConfigureAwait(false);
            open.UnionWith(retained());
        }

        if ((_services.GetService<QuestNpcPersistence>() ?? _services.GetService<QuestNpcFeature>()?.Persistence) is { } questPersistence)
            open.UnionWith(questPersistence.OpenCharacters);
        if ((_services.GetService<TaxiFlightWriteQueue>() ?? _services.GetService<NpcServicesFeature>()?.RouteWrites) is { } taxiWrites)
            await AddAsync(taxiWrites.FlushAsync, () => taxiWrites.RetainedCharacters).ConfigureAwait(false);
        if ((_services.GetService<SpellbookCache>() ?? _services.GetService<SpellFeature>()?.Spellbook) is { } spellbook)
            await AddAsync(spellbook.FlushAsync, () => spellbook.FailedCharacters).ConfigureAwait(false);
        if ((_services.GetService<SpellStatePersistence>() ?? _services.GetService<SpellFeature>()?.State) is { } spellState)
            await AddAsync(spellState.FlushAsync, () => spellState.RetainedCharacters).ConfigureAwait(false);
        if ((_services.GetService<TalentPersistence>() ?? _services.GetService<TalentFeature>()?.Persistence) is { } talentPersistence)
            await AddAsync(talentPersistence.FlushAsync, () => talentPersistence.FailedCharacters).ConfigureAwait(false);
        if ((_services.GetService<SkillSaveCoordinator>() ?? _services.GetService<SkillsFeature>()?.Saves) is { } skillSaves)
            open.UnionWith(skillSaves.PendingCharacters);
        if ((_services.GetService<ReputationWriteQueue>() ?? _services.GetService<ReputationFeature>()?.Writes) is { } reputationWrites)
            await AddAsync(reputationWrites.FlushAsync, () => reputationWrites.RetainedCharacters).ConfigureAwait(false);
        if ((_services.GetService<SocialWriteQueue>() ?? _services.GetService<SocialFeature>()?.Writes) is { } socialWrites)
            await AddAsync(socialWrites.FlushAsync, () => socialWrites.RetainedCharacters).ConfigureAwait(false);
        if ((_services.GetService<HonorWriteQueue>() ?? _services.GetService<HonorFeature>()?.Writes) is { } honorWrites)
            await AddAsync(honorWrites.FlushAsync, () => honorWrites.RetainedCharacters).ConfigureAwait(false);
        if ((_services.GetService<RestWriteQueue>() ?? _services.GetService<RestFeature>()?.Writes) is { } restWrites)
            await AddAsync(restWrites.FlushAsync, () => restWrites.RetainedCharacters).ConfigureAwait(false);
        if ((_services.GetService<ExploredZonesWriteQueue>() ?? _services.GetService<ExploredZonesPersistence>()?.Writes) is { } exploredWrites)
            await AddAsync(exploredWrites.FlushAsync, () => exploredWrites.RetainedCharacters).ConfigureAwait(false);
        if ((_services.GetService<InstanceWriteQueue>() ?? _services.GetService<InstanceFeature>()?.Writes) is { } instanceWrites)
            await AddAsync(instanceWrites.FlushAsync, () => instanceWrites.RetainedCharacters).ConfigureAwait(false);
        if ((_services.GetService<SummonService>() ?? _services.GetService<PetsFeature>()?.Service) is { } petService)
            open.UnionWith(petService.OpenPersistenceCharacters);
        return [.. open];
    }

    /// <summary>
    /// The writes a handoff must have durable that belong to no one character, so <see cref="OpenCharacterIdsAsync"/> cannot name them: instance rows, script data, deletions and raid reset times,
    /// guilds, petitions and character purges, then the group, GM audit, account data and account address queues, both respawn queues and the world-state save queues (Silithyst, Fishing
    /// Extravaganza, Elemental Invasion, Nightmare Dragons). Each queue retries what it retains and faults while one is still not durable. Every queue is attempted even when an earlier one
    /// faults, so a refused handoff has written all it could: one failure is thrown as it is, several as one <see cref="InvalidOperationException"/> whose inner exception is the
    /// <see cref="AggregateException"/> of them. All under one <see cref="DefaultTimeout"/> (a timeout or a cancellation stops the rest). The last time this process can write them: B reads the
    /// characters database, and the fence refuses A after the takeover. A lease refusal (<see cref="ArcaneCore.Data.Characters.Cluster.RealmLeaseNotHeldException"/>) of a keyed queue's write is not checked here: for a group the holder has the data, but a GM ticket taken on a non-holder is not durable, is not on B, and is only named at stop. Pet saves are flushed per character (<see cref="FlushCharacterAsync(int, TimeSpan?, CancellationToken)"/>), not here. The war effort (phase start, Colossus deaths) and Scourge invasion (start/stop, city defeats, Necropolis deaths) writers are covered through their <c>FlushStateWritesAsync</c>: the freeze's Park copies their in-memory queues on the world thread, and the flush waits for the store job in flight and writes the copy the way their StopAsync does, with no world thread (only a job's apply step needs it). A write that fails fails the flush. Not covered: the running game-event set, which a separate sink writes to the characters database. A state-save queue drops a value a store refused for the realm lease, and a failed value that a newer one of its key replaces, so the flush does not check those.
    /// </summary>
    public async Task FlushSharedWritesAsync(CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DefaultTimeout);
        var failures = new List<Exception>();

        async Task RunAsync(Task flush)
        {
            try
            {
                await flush.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("persist.barrier", ex);
                _logger?.LogWarning(ex, "Handoff shared write flush failed");
                failures.Add(ex);
            }
        }

        if ((_services.GetService<InstanceWriteQueue>() ?? _services.GetService<InstanceFeature>()?.Writes) is { } instanceWrites)
            await RunAsync(instanceWrites.FlushSharedAsync()).ConfigureAwait(false);
        if ((_services.GetService<SocialWriteQueue>() ?? _services.GetService<SocialFeature>()?.Writes) is { } socialWrites)
            await RunAsync(socialWrites.FlushSharedAsync()).ConfigureAwait(false);
        if (_services.GetService<SocialGroupPersistenceFeature>()?.Writes is { } groupWrites)
            await RunAsync(groupWrites.RetryRetainedAsync()).ConfigureAwait(false);
        if (_services.GetService<Gm.Audit.GmAuditFeature>()?.Writes is { } auditWrites)
            await RunAsync(auditWrites.RetryRetainedAsync()).ConfigureAwait(false);
        if (_services.GetService<Handlers.AccountDataFeature>()?.Writes is { } accountDataWrites)
            await RunAsync(accountDataWrites.RetryRetainedAsync()).ConfigureAwait(false);
        if (_services.GetService<Bans.AccountAddressFeature>()?.Writes is { } accountAddressWrites)
            await RunAsync(accountAddressWrites.RetryRetainedAsync()).ConfigureAwait(false);
        if (_services.GetService<Creatures.CreatureRespawnFeature>()?.Persistence is Creatures.CreatureRespawnQueue creatureRespawns)
            await RunAsync(creatureRespawns.RetryRetainedAsync()).ConfigureAwait(false);
        if (_services.GetService<GameObjects.GameObjectRespawnFeature>()?.Persistence is GameObjects.GameObjectRespawnQueue objectRespawns)
            await RunAsync(objectRespawns.RetryRetainedAsync()).ConfigureAwait(false);
        if (_services.GetService<OutdoorPvP.OutdoorPvPFeature>() is { } outdoorPvP)
            await RunAsync(outdoorPvP.FlushStateWritesAsync()).ConfigureAwait(false);
        if (_services.GetService<FishingExtravaganzaFeature>() is { } fishing)
            await RunAsync(fishing.FlushStateWritesAsync()).ConfigureAwait(false);
        if (_services.GetService<HolidayObjectsFeature>() is { } holiday)
            await RunAsync(holiday.FlushStateWritesAsync()).ConfigureAwait(false);
        if (_services.GetService<NightmareDragonsFeature>() is { } dragons)
            await RunAsync(dragons.FlushStateWritesAsync()).ConfigureAwait(false);
        if (_services.GetService<WarEffortFeature>() is { } warEffort)
            await RunAsync(warEffort.FlushStateWritesAsync()).ConfigureAwait(false);
        if (_services.GetService<ScourgeInvasionFeature>() is { } scourge)
            await RunAsync(scourge.FlushStateWritesAsync()).ConfigureAwait(false);

        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new InvalidOperationException($"{failures.Count} shared write queues are not durable", new AggregateException(failures));
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
            FaultLedger.Shared.Record("persist.barrier", ex);
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

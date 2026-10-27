using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Progression;

/// <summary>
/// Persists rested-experience states queued on the world thread, off it and in order (one consumer; the pattern of
/// <see cref="WorldState.ExploredZonesWriteQueue"/>: one value per character). The latest state of a character is an in-memory
/// overlay; a queued write persists the overlay, and one that fails all <c>MaxAttempts</c> attempts is RETAINED, never
/// dropped: the next change, <see cref="CharacterKeyedWriteQueue{TValue}.FlushCharacterAsync"/> (the login barrier, which faults while the character is still
/// not durable), <see cref="CharacterKeyedWriteQueue{TValue}.RequestRetry"/> (logout) and <see cref="CharacterKeyedWriteQueue{TValue}.StopAsync"/> (a final retry that throws if storage is still
/// failing) all carry it. <see cref="CharacterKeyedWriteQueue{TValue}.FlushAsync"/> is a pure ordered barrier that never retries and never throws.
/// Retention is in process only.
/// </summary>
public sealed class RestWriteQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null) : CharacterKeyedWriteQueue<CharacterRestState>(scopes, logger, drain)
{
    private protected override string QueueName => "RestWriteQueue";

    private protected override string Description => "rest write queue";

    private protected override string WritesNoun => "rested-state";

    private protected override string SitePrefix => "writequeue.rest";

    private protected override string ConsumeFaultSite => "persist.rest-write-queue";

    private protected override bool SkipsBusyAttemptFault => true;

    private protected override string SweepInvariantMessage => "rested-state retry recorded failures without a first exception";

    /// <summary>Record the character's current state and queue a write unless one is waiting.</summary>
    public void Save(int characterId, CharacterRestState state) => SaveCore(characterId, state);

    private protected override bool IsEmpty(CharacterRestState value) => false;

    private protected override Task SaveToStoreAsync(IServiceProvider services, int characterId, CharacterRestState value)
        => services.GetService<ICharacterRestStore>() is { } store ? store.SaveAsync(characterId, value) : Task.CompletedTask;

    private protected override void LogDroppedAfterStop(ILogger logger, int characterId)
        => logger.LogError("rested state of character {Character} arrived after the queue stopped; not persisted", characterId);

    private protected override void LogTerminalFailure(ILogger logger, Exception exception)
        => logger.LogCritical(exception, "rest write queue entered terminal failure after character ownership rejection");

    private protected override void LogConsumeFault(ILogger logger, Exception exception, object kind, int characterId)
        => logger.LogFault("writequeue.rest-consume", exception, "rest queue item {Kind} for character {Character} failed unexpectedly", kind, characterId);

    private protected override void LogRetryExhausted(ILogger logger, Exception failure, int characterId, int attempts)
        => logger.LogFault("writequeue.rest", failure, "rested-state writes for character {Character} are retained after {Max} failed attempts", characterId, attempts);

    private protected override void LogAttemptFailed(ILogger logger, Exception exception, int characterId, int attempt, int attempts)
        => logger.LogWarning(exception, "rested-state write for character {Character} failed (attempt {Attempt} of {Max})", characterId, attempt, attempts);

    private protected override void LogSweepSummary(ILogger logger, int failed, int total, string locked, string reason)
        => logger.LogWarning("rested-state retry: {Failed} of {Total} retained characters still failing{Locked}; first: {Reason}", failed, total, locked, reason);
}

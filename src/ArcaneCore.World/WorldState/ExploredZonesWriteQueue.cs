using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// Persists explored-zones words queued on the world thread, off it and in order (one consumer; the
/// pattern of <see cref="Reputation.ReputationWriteQueue"/>, reduced to a single value per
/// character). The latest words of a character are an in-memory overlay; a queued write persists
/// the overlay, and one that fails all <c>MaxAttempts</c> attempts is RETAINED, never dropped: the
/// next change, <see cref="CharacterKeyedWriteQueue{TValue}.FlushCharacterAsync"/> (the login barrier, which faults while the
/// character is still not durable), <see cref="CharacterKeyedWriteQueue{TValue}.RequestRetry"/> (logout) and <see cref="CharacterKeyedWriteQueue{TValue}.StopAsync"/>
/// (a final retry that throws if storage is still failing) all carry it. <see cref="CharacterKeyedWriteQueue{TValue}.FlushAsync"/>
/// is a pure ordered barrier that never retries and never throws. Retention is in process only.
/// </summary>
public sealed class ExploredZonesWriteQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null) : CharacterKeyedWriteQueue<uint[]>(scopes, logger, drain)
{
    private protected override string QueueName => "ExploredZonesWriteQueue";

    private protected override string Description => "explored-zones write queue";

    private protected override string WritesNoun => "explored-zones";

    private protected override string SitePrefix => "writequeue.exploredzones";

    private protected override string ConsumeFaultSite => "persist.explored-zones";

    private protected override bool SkipsBusyAttemptFault => false;

    private protected override string SweepInvariantMessage => "explored-zones retry recorded failures without a first exception";

    /// <summary>Record the character's current words (copied) and queue a write unless one is waiting.</summary>
    public void Save(int characterId, ReadOnlySpan<uint> words) => SaveCore(characterId, words.ToArray());

    private protected override bool IsEmpty(uint[]? value) => value is not { Length: > 0 };

    private protected override Task SaveToStoreAsync(IServiceProvider services, int characterId, uint[]? value)
        => services.GetService<IExploredZonesStore>() is { } store ? store.SaveAsync(characterId, value!) : Task.CompletedTask;

    private protected override void LogDroppedAfterStop(ILogger logger, int characterId)
        => logger.LogError("explored zones of character {Character} arrived after the queue stopped; not persisted", characterId);

    private protected override void LogTerminalFailure(ILogger logger, Exception exception)
        => logger.LogCritical(exception, "explored-zones write queue entered terminal failure after character ownership rejection");

    private protected override void LogConsumeFault(ILogger logger, Exception exception, object kind, int characterId)
        => logger.LogFault("writequeue.exploredzones-consume", exception, "explored-zones queue item {Kind} for character {Character} failed unexpectedly", kind, characterId);

    private protected override void LogRetryExhausted(ILogger logger, Exception failure, int characterId, int attempts)
        => logger.LogFault("writequeue.exploredzones", failure, "explored-zones writes for character {Character} are retained after {Max} failed attempts", characterId, attempts);

    private protected override void LogAttemptFailed(ILogger logger, Exception exception, int characterId, int attempt, int attempts)
        => logger.LogWarning(exception, "explored-zones write for character {Character} failed (attempt {Attempt} of {Max})", characterId, attempt, attempts);

    private protected override void LogSweepSummary(ILogger logger, int failed, int total, string locked, string reason)
        => logger.LogWarning("explored-zones retry: {Failed} of {Total} retained characters still failing{Locked}; first: {Reason}", failed, total, locked, reason);
}

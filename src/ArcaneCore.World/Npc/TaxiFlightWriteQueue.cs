using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Persists taxi-route saves and deletes requested on the world thread, off it and in order (one consumer, as
/// <see cref="Reputation.ReputationWriteQueue"/>), and keeps them until they are durable.
/// <para>
/// Each character has at most one outstanding operation, a save of a route or a delete; a newer request replaces an older
/// one (both are idempotent, so the last request wins and the older is never applied after it). The operation is removed
/// after a write only if no newer request replaced it meanwhile. A write that fails all <c>MaxAttempts</c> attempts is
/// <b>retained</b>, never dropped, and carried by the next trigger for the character: a newer request,
/// <see cref="CharacterKeyedWriteQueue{TValue}.FlushCharacterAsync"/> (the login barrier, which throws while the character is still unrecovered),
/// <see cref="CharacterKeyedWriteQueue{TValue}.RequestRetry"/> (logout) and <see cref="CharacterKeyedWriteQueue{TValue}.StopAsync"/> (a final retry that throws, naming the characters).
/// </para>
/// <para>
/// A save may carry an <c>abandon</c> fallback (<see cref="Save(int, TaxiFlightRoute, Action?)"/>). When such a save
/// fails every attempt and no newer request replaced it, the route is given up instead of retained: the fallback runs
/// (the owner puts the character somewhere it can stand) and the operation becomes a delete of the row, which is itself
/// retained until durable. This keeps the character row and the route row consistent on disk, the guarantee the
/// synchronous save had, while the write still leaves the world thread.
/// </para>
/// <para>
/// <see cref="CharacterKeyedWriteQueue{TValue}.FlushAsync"/> stays a pure ordered barrier: it never retries and never throws. Retention is in process
/// only: a crash, or a store that is still down at exit, loses what is retained (loudly).
/// </para>
/// </summary>
public sealed class TaxiFlightWriteQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null)
    : CharacterKeyedWriteQueue<TaxiFlightWriteOperation>(scopes, logger, drain)
{
    private protected override string QueueName => "TaxiFlightWriteQueue";

    private protected override string Description => "taxi route write queue";

    private protected override string WritesNoun => "taxi route";

    private protected override string SitePrefix => "writequeue.taxi";

    private protected override string ConsumeFaultSite => "writequeue.taxi-consume";

    private protected override bool SkipsBusyAttemptFault => false;

    private protected override string SweepInvariantMessage => "taxi route retry recorded failures without a first exception";

    private protected override string? SweepFaultSite => "writequeue.taxi-sweep";

    /// <summary>Queue saving the route of a character that logged out mid-flight; replaces any older request.</summary>
    public void Save(int characterId, TaxiFlightRoute route) => Save(characterId, route, abandon: null);

    /// <summary>
    /// Queue saving the route of a character that logged out mid-flight; replaces any older request. When the save
    /// fails every attempt (and was not replaced meanwhile), <paramref name="abandon"/> runs off the world thread and
    /// the route is deleted instead of retained; a null <paramref name="abandon"/> retains the failed save.
    /// </summary>
    public void Save(int characterId, TaxiFlightRoute route, Action? abandon)
    {
        ArgumentNullException.ThrowIfNull(route);
        SaveCore(characterId, new TaxiFlightWriteOperation(route, abandon));
    }

    /// <summary>Queue removing the character's saved route; replaces any older request.</summary>
    public void Delete(int characterId) => SaveCore(characterId, new TaxiFlightWriteOperation(null, null));

    private protected override bool IsEmpty(TaxiFlightWriteOperation? value) => value is null;

    private protected override TimeSpan BackoffAfter(int attempt) => TimeSpan.FromMilliseconds(200 * attempt);

    private protected override async Task SaveToStoreAsync(IServiceProvider services, int characterId, TaxiFlightWriteOperation? value)
    {
        if (value is null || services.GetService<ICharacterTaxiFlightStore>() is not { } store)
        {
            return; // no store: nothing to land, hosts without persistence keep nothing
        }

        // Both calls are idempotent, so a retry after a commit whose acknowledgement was lost is safe.
        if (value.Route is { } route)
        {
            await store.SaveAsync(characterId, route).ConfigureAwait(false);
        }
        else
        {
            await store.DeleteAsync(characterId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Give the route up rather than keep it: the owner relocates the character and the row goes. Only on a failure a real store attempt returned
    /// and that says something about the route: not when the database is merely locked by another writer, not when the stop drain refused the
    /// attempt (nothing was tried; abandoning then would write a departure-node snapshot over the logout snapshot), and not on an ownership rejection.
    /// The engine never abandons in a retry sweep.
    /// </summary>
    private protected override bool TryAbandon(TaxiFlightWriteOperation? value, Exception failure, out TaxiFlightWriteOperation? replacement)
    {
        if (value?.Abandon is not null && !StopDrainBudget.IsNoVerdict(failure))
        {
            replacement = new TaxiFlightWriteOperation(null, null);
            return true;
        }

        replacement = null;
        return false;
    }

    private protected override void OnAbandoned(int characterId, TaxiFlightWriteOperation? abandoned, Exception failure, int attempts)
    {
        AnomalyLedger.Shared.Report(
            "persist",
            "write-retry-exhausted",
            $"character {characterId}",
            $"queue=TaxiFlightWriteQueue character={characterId} action=save-abandoned attempts={attempts} error={failure.GetType().Name}",
            AnomalySeverity.Error);
        Logger.LogFault("writequeue.taxi", failure, "taxi route save for character {Character} failed; the route is abandoned and its row deleted", characterId);
        if (abandoned?.Abandon is not { } abandon)
        {
            return;
        }

        try
        {
            abandon();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.LogFault("writequeue.taxi-abandon", ex, "taxi route fallback for character {Character} failed; the route row is still deleted", characterId);
        }
    }

    private protected override void LogDroppedAfterStop(ILogger logger, int characterId)
        => logger.LogError("taxi route change for character {Character} arrived after the queue stopped; not persisted", characterId);

    private protected override void LogTerminalFailure(ILogger logger, Exception exception)
        => logger.LogCritical(exception, "taxi route write queue entered terminal failure after character ownership rejection");

    // The engine records ConsumeFaultSite itself, so this only logs (a LogFault here would count the fault twice).
    private protected override void LogConsumeFault(ILogger logger, Exception exception, object kind, int characterId)
        => logger.LogError(exception, "taxi route write queue item {Kind} for character {Character} failed unexpectedly", kind, characterId);

    private protected override void LogRetryExhausted(ILogger logger, Exception failure, int characterId, int attempts)
        => logger.LogFault("writequeue.taxi", failure, "taxi route write for character {Character} failed after {Attempts} attempts; retained for retry", characterId, attempts);

    private protected override void LogAttemptFailed(ILogger logger, Exception exception, int characterId, int attempt, int attempts)
        => logger.LogWarning(exception, "taxi route write for character {Character} failed (attempt {Attempt} of {Max}); retrying", characterId, attempt, attempts);

    private protected override void LogSweepSummary(ILogger logger, int failed, int total, string locked, string reason)
        => logger.LogWarning("taxi route retry: {Failed} of {Total} retained characters still failing{Locked}; first: {Reason}", failed, total, locked, reason);
}

/// <summary>
/// One outstanding <see cref="TaxiFlightWriteQueue"/> request: a null <paramref name="Route"/> is a delete; <paramref name="Abandon"/> is the
/// save's fallback (see the queue's summary). The engine's per-character version stands for the request generation.
/// </summary>
/// <param name="Route">The route to save, or null to delete the saved route.</param>
/// <param name="Abandon">Runs off the world thread when the save is given up, or null to retain a failed save.</param>
public sealed record TaxiFlightWriteOperation(TaxiFlightRoute? Route, Action? Abandon);

using ArcaneCore.Data;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World;

/// <summary>
/// Holds the single-writer lock (SQLite sidecar file, or a MariaDB/MySQL/PostgreSQL advisory lock) of the characters database (<see cref="CharacterDatabaseWriterLock"/>) for the life of the world process.
/// A container singleton: <see cref="WorldStartup"/> acquires it before any schema initializer opens the database, <see cref="WorldHost"/> re-checks it (idempotent; it gives back
/// only a lock it took itself), and the host container disposes it after every hosted service (the SQLite checkpointer's final WAL checkpoint included) has stopped, or the process ends.
/// A standby world is built with <c>deferUntilPromotion</c>: <see cref="Acquire"/> takes nothing and <see cref="AcquireForPromotion"/> does.
/// Thread-safe; <see cref="Acquire"/> and <see cref="Dispose"/> may be called from any thread.
/// <para>
/// A MariaDB/MySQL/PostgreSQL lock can be lost while the world runs (the session died, the server dropped it). The loss is logged Critical, <see cref="LockPath"/> is null while it lasts, and the
/// lock tries to take itself back on every health check (logged as a warning when it works). If the retake finds the lock held by a session other than this process's own earlier one, another
/// world writes the database: the guard logs Critical, records a fault, sets the failure exit code, asks the host for an orderly stop (<see cref="IHostApplicationLifetime.StopApplication"/>, as the
/// realm authority lease does) and suppresses the shutdown drain lanes (<see cref="StopDrainBudget.Suppress"/>), so the shutdown saves are NOT written into the other world's database; they stay
/// retained and are lost with the process. Residual window: until that first retake (the second health tick after the loss) this world keeps writing, and its save queues are not paused while
/// merely lost; the stopping window before the host reaches the queues' stop is likewise unguarded. New logins are not gated separately.
/// </para>
/// </summary>
public sealed class WorldWriterGuard(IOptions<DatabaseOptions>? databaseOptions = null, ILogger<WorldWriterGuard>? logger = null, bool deferUntilPromotion = false,
    IHostApplicationLifetime? lifetime = null, TimeSpan? lockHealthInterval = null, Action<int>? setExitCode = null, ShutdownDrain? drain = null) : IDisposable
{
    private readonly object _gate = new();
    private CharacterDatabaseWriterLock? _lock;
    private int _stopRequested;

    /// <summary>The held lock file's path (SQLite) or advisory lock identity (MariaDB, MySQL, PostgreSQL); null when no lock is held (not acquired, a database that has none, or a server lock that is currently lost).</summary>
    public string? LockPath
    {
        get { lock (_gate) return _lock is { IsLost: false } held ? held.Path : null; }
    }

    /// <summary>
    /// Takes the lock if this database has one and it is not held yet. Returns true only when this call took it (false: already held, or this database has no lock).
    /// Throws <see cref="CharacterDatabaseAlreadyOwnedException"/> when another process holds it.
    /// </summary>
    public bool Acquire() => !deferUntilPromotion && AcquireCore();

    /// <summary>
    /// A standby's promotion takes the lock it deferred (a guard that is not deferred behaves as <see cref="Acquire"/>). Returns true when this call took it.
    /// Throws <see cref="CharacterDatabaseAlreadyOwnedException"/> while the old world still holds it; nothing is held then and the standby stays a standby.
    /// </summary>
    public bool AcquireForPromotion() => AcquireCore();

    private bool AcquireCore()
    {
        lock (_gate)
        {
            if (_lock is not null || databaseOptions?.Value is not { } options) return false;
            _lock = CharacterDatabaseWriterLock.TryAcquire(options.Resolve(DatabaseComponent.Characters), OnLockLost, lockHealthInterval, OnLockRecovered, OnLockConflict, OnLockRetakeBlocked);
            if (_lock is null) return false;
            logger?.LogInformation("Holding the characters database writer lock {LockPath}", _lock.Path);
            return true;
        }
    }

    /// <summary>A held server lock was found lost. Logged only: the lock keeps trying to take itself back, and a take-over is <see cref="OnLockConflict"/>.</summary>
    internal void OnLockLost(string reason)
        => logger?.LogCritical("The characters database writer lock was LOST: {Reason}. This world no longer excludes a second world writer on the same database; it tries to take the lock back on every health check and stops itself if another session has taken it.", DatabaseStartup.Scrub(reason, null));

    /// <summary>A lost server lock was taken again.</summary>
    internal void OnLockRecovered(string description)
        => logger?.LogWarning("The characters database writer lock was RE-TAKEN: {LockPath}. This world excludes a second world writer again; writes during the gap were not guarded.", DatabaseStartup.Scrub(description, null));

    /// <summary>A retake of the lost lock could not complete (server unreachable, or this process's own dead session still holds it). Throttled by the lock.</summary>
    internal void OnLockRetakeBlocked(string reason)
        => logger?.LogWarning("The characters database writer lock is still lost: {Reason}. Retrying on the next health check.", DatabaseStartup.Scrub(reason, null));

    /// <summary>A lost server lock is held by another session (not this process's own earlier one): another world writes this database. Stops this world, once, without its shutdown saves.</summary>
    internal void OnLockConflict(string description)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
        string message = $"the characters database writer lock was lost and is now held by another session ({DatabaseStartup.Scrub(description, null)}); another world is writing this database, so this world is stopping";
        logger?.LogCritical("{Message}", message);
        FaultLedger.Shared.Record("world.writer-lock", new InvalidOperationException(message));
        if (drain is not null)
        {
            const string Why = "another world holds the characters database writer lock";
            drain.Saves.Suppress(Why);
            drain.Features.Suppress(Why);
            logger?.LogCritical("The shutdown saves of this world are SKIPPED ({Why}): unsaved character and feature changes since the last completed save are lost, so they cannot overwrite the other world's data.", Why);
        }

        (setExitCode ?? (code => ExitCodes.Current = code))(ExitCodes.Failure);
        lifetime?.StopApplication();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }
}

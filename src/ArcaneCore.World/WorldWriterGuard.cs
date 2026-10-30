using ArcaneCore.Data;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
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
/// lock tries to take itself back on every health check (logged as a warning when it works). If another session holds it for longer than a dead session of this process could, the guard logs
/// Critical, records a fault, sets the failure exit code and asks the host for an orderly stop (<see cref="IHostApplicationLifetime.StopApplication"/>, as the realm authority lease does): the
/// shutdown saves still run, so the stop does not make the second writer's data worse than leaving this world up would. New logins are not gated separately.
/// </para>
/// </summary>
public sealed class WorldWriterGuard(IOptions<DatabaseOptions>? databaseOptions = null, ILogger<WorldWriterGuard>? logger = null, bool deferUntilPromotion = false,
    IHostApplicationLifetime? lifetime = null, TimeSpan? lockHealthInterval = null, Action<int>? setExitCode = null) : IDisposable
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
            _lock = CharacterDatabaseWriterLock.TryAcquire(options.Resolve(DatabaseComponent.Characters), OnLockLost, lockHealthInterval, OnLockRecovered, OnLockConflict);
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

    /// <summary>A lost server lock is held by another session beyond the stale-session window: another world writes this database. Stops this world, once.</summary>
    internal void OnLockConflict(string description)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
        string message = $"the characters database writer lock was lost and is now held by another session ({DatabaseStartup.Scrub(description, null)}); another world is writing this database, so this world is stopping";
        logger?.LogCritical("{Message}", message);
        FaultLedger.Shared.Record("world.writer-lock", new InvalidOperationException(message));
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

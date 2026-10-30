using ArcaneCore.Data;
using ArcaneCore.Data.Characters.Ownership;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World;

/// <summary>
/// Holds the single-writer lock (SQLite sidecar file, or a MariaDB/MySQL/PostgreSQL advisory lock) of the characters database (<see cref="CharacterDatabaseWriterLock"/>) for the life of the world process.
/// A container singleton: <see cref="WorldStartup"/> acquires it before any schema initializer opens the database, <see cref="WorldHost"/> re-checks it (idempotent; it gives back
/// only a lock it took itself), and the host container disposes it after every hosted service (the SQLite checkpointer's final WAL checkpoint included) has stopped, or the process ends.
/// A standby world is built with <c>deferUntilPromotion</c>: <see cref="Acquire"/> takes nothing and <see cref="AcquireForPromotion"/> does.
/// Thread-safe; <see cref="Acquire"/> and <see cref="Dispose"/> may be called from any thread.
/// </summary>
public sealed class WorldWriterGuard(IOptions<DatabaseOptions>? databaseOptions = null, ILogger<WorldWriterGuard>? logger = null, bool deferUntilPromotion = false) : IDisposable
{
    private readonly object _gate = new();
    private CharacterDatabaseWriterLock? _lock;

    /// <summary>The held lock file's path (SQLite) or advisory lock identity (MariaDB, MySQL, PostgreSQL); null when no lock is held (not acquired, or a database that has none).</summary>
    public string? LockPath
    {
        get { lock (_gate) return _lock?.Path; }
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
            _lock = CharacterDatabaseWriterLock.TryAcquire(options.Resolve(DatabaseComponent.Characters),
                reason => logger?.LogCritical("The characters database writer lock was LOST: {Reason}. This world no longer excludes a second world writer on the same database; stop it or restart this world.", reason));
            if (_lock is null) return false;
            logger?.LogInformation("Holding the characters database writer lock {LockPath}", _lock.Path);
            return true;
        }
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

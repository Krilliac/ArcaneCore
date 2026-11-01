using ArcaneCore.Data;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Clustering;
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
/// With <c>Cluster:Fencing:Scope=Worker</c> the guard takes the lock of this worker's shard (<see cref="CharacterDatabaseWriterLock.TryAcquireShard"/>) instead of the realm lock, so several workers
/// of one database coexist and the same shard twice is refused. A worker never holds the realm lock: <see cref="ProbeRealmFree"/> is how it checks, after its durable claim, that no Combined world holds it
/// (design _wf/writer-lock-DESIGN.md section 1). Holding the shard lock is also the liveness proof for reclaiming the shard's row after a crash (<see cref="HoldsLock"/>).
/// </para>
/// <para>
/// A MariaDB/MySQL/PostgreSQL lock can be lost while the world runs (the session died, the server dropped it). The loss is logged Critical, <see cref="LockPath"/> is null while it lasts, and the
/// lock tries to take itself back on every health check (logged as a warning when it works). If the retake finds the lock held by a session other than this process's own earlier one, another
/// world writes the database: the guard trips the <see cref="CharacterDatabaseWriterFence"/> first, then logs Critical, records a fault, sets the failure exit code, asks the host for an orderly stop
/// (<see cref="IHostApplicationLifetime.StopApplication"/>, as the realm authority lease does) and suppresses the drain lanes (<see cref="StopDrainBudget.Suppress"/>). The fence is the backstop: every further
/// connection, transaction, statement and commit of this process on the characters database is refused at the data layer, whichever feature issues it, including the writers that have no lane. The lanes are the
/// polite path: a suppressed lane refuses every attempt of every save and feature queue from then on, stopping or not (the host's <c>SaveAll</c> runs while the queues are still live), so the shutdown
/// saves are skipped and the snapshots stay retained, lost with the process. Residual window: while the lock is merely lost, and until the first retake that finds the rival (the second health tick after the loss),
/// this world keeps writing and its queues are not paused; those saves land, and so does a write already on the wire at the instant of the trip. New logins are not gated separately.
/// </para>
/// </summary>
public sealed class WorldWriterGuard(IOptions<DatabaseOptions>? databaseOptions = null, ILogger<WorldWriterGuard>? logger = null, bool deferUntilPromotion = false,
    IHostApplicationLifetime? lifetime = null, TimeSpan? lockHealthInterval = null, Action<int>? setExitCode = null, ShutdownDrain? drain = null, CharacterDatabaseWriterFence? fence = null,
    IOptions<CharacterFencingOptions>? fencingOptions = null, IOptions<Cluster.Gateway.GatewayWorkerOptions>? workerOptions = null) : IDisposable
{
    private readonly object _gate = new();
    private volatile CharacterDatabaseWriterLock? _lock; // written under the gate, read lock-free by LockPath
    private int _stopRequested;

    /// <summary>The held lock file's path (SQLite) or advisory lock identity (MariaDB, MySQL, PostgreSQL); null when no lock is held (not acquired, a database that has none, or a server lock that is currently lost).</summary>
    public string? LockPath
    {
        get => _lock is { IsLost: false } held ? held.Path : null; // no gate: Acquire holds it across the connection to the server
    }

    /// <summary>True when this guard locks a worker shard (<c>Cluster:Fencing:Scope=Worker</c>) rather than the realm.</summary>
    public bool IsWorkerScope => fencingOptions?.Value.Scope == CharacterFencingScope.Worker;

    /// <summary>The worker shard this guard locks; null in Realm scope. Throws <see cref="WorkerScopeConfigurationException"/> for a shard id that is not valid.</summary>
    public string? Shard
    {
        get
        {
            if (fencingOptions?.Value is not { Scope: CharacterFencingScope.Worker } fencing) return null;
            return fencing.ResolveScope(workerOptions?.Value.NodeId ?? string.Empty)[Data.Characters.Ownership.CharacterOwnershipScopes.WorkerPrefix.Length..];
        }
    }

    /// <summary>True while the lock is held and not lost: the proof, for a shard, that no other process of that shard is alive.</summary>
    public bool HoldsLock => _lock is { IsLost: false };

    /// <summary>
    /// Whether the realm lock is free right now, without keeping it (a Worker-scope claim is followed by this probe: a holder is a Combined world, and the worker backs out).
    /// True when there are no database options or the database has no lock. An unreachable server propagates the driver's <see cref="System.Data.Common.DbException"/>.
    /// </summary>
    public bool ProbeRealmFree()
        => databaseOptions?.Value is not { } options || CharacterDatabaseWriterLock.ProbeRealmFree(options.Resolve(DatabaseComponent.Characters));

    /// <summary>
    /// Whether the advisory lock of the worker scope <paramref name="workerScope"/> (<c>worker:&lt;shard&gt;</c>) is free right now, without keeping it: the login-time reap's liveness proof
    /// (design _wf/writer-lock-DESIGN.md section 5.4; <see cref="CharacterClaimService"/>). False for a scope that is not a valid worker scope, so it is never reaped. True when there are no database
    /// options or the database has no lock. An unreachable server propagates the driver's <see cref="System.Data.Common.DbException"/>. Synchronous, one short round trip.
    /// </summary>
    public bool ProbeShardFree(string workerScope)
    {
        ArgumentNullException.ThrowIfNull(workerScope);
        if (!CharacterOwnershipScopes.IsWorker(workerScope))
            return false;
        string shard = workerScope[CharacterOwnershipScopes.WorkerPrefix.Length..];
        if (!CharacterDatabaseWriterLock.IsValidShardId(shard))
            return false;
        return databaseOptions?.Value is not { } options || CharacterDatabaseWriterLock.ProbeShardFree(options.Resolve(DatabaseComponent.Characters), shard);
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
            DatabaseConnectionOptions characters = options.Resolve(DatabaseComponent.Characters);
            if (Shard is { } shard)
            {
                if (characters.Provider == DatabaseProvider.Sqlite && fencingOptions?.Value.AllowSqliteWorkerScope != true)
                    throw new WorkerScopeConfigurationException("Cluster:Fencing:Scope=Worker needs a PostgreSQL or MariaDB characters database; SQLite is allowed only with Cluster:Fencing:AllowSqliteWorkerScope (in-process tests)");
                _lock = CharacterDatabaseWriterLock.TryAcquireShard(characters, shard, OnLockLost, lockHealthInterval, OnLockRecovered, OnLockConflict, OnLockRetakeBlocked);
            }
            else
            {
                _lock = CharacterDatabaseWriterLock.TryAcquire(characters, OnLockLost, lockHealthInterval, OnLockRecovered, OnLockConflict, OnLockRetakeBlocked);
            }

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
        const string Why = "another world holds the characters database writer lock";
        fence?.Trip(Why); // first: before the lanes and the stop, so no writer, laned or not, gets another statement in
        string message = $"the characters database writer lock was lost and is now held by another session ({DatabaseStartup.Scrub(description, null)}); another world is writing this database, so this world is stopping{(fence is null ? string.Empty : "; its characters database access is fenced off first")}";
        logger?.LogCritical("{Message}", message);
        FaultLedger.Shared.Record("world.writer-lock", new InvalidOperationException(message));
        if (drain is not null)
        {
            drain.Saves.Suppress(Why);
            drain.Features.Suppress(Why);
            logger?.LogCritical("The characters database is FENCED and the shutdown saves of this world are SKIPPED ({Why}): every further connection, transaction, statement and commit on it is refused, and unsaved character and feature changes since the last completed save are lost, so they cannot overwrite the other world's data.", Why);
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

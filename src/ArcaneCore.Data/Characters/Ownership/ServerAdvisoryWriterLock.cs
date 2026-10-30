using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Data.Content.Import;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Characters.Ownership;

/// <summary>
/// The single-writer guard of a MariaDB, MySQL or PostgreSQL characters database: a session-level advisory lock held on one dedicated, unpooled connection for the life of the
/// world process. The server releases it when that session ends (the process exits or crashes, the connection is closed or the server notices it is dead), so there is no lease.
/// <para>
/// Identity: both engines key the lock on the characters <b>database name</b> from the connection string. The MariaDB/MySQL lock name is <see cref="NamePrefix"/> + the lower-cased name
/// (database-name case is folded by the server on case-insensitive file systems, <c>lower_case_table_names</c>, so <c>Chars</c> and <c>chars</c> may be one database), replaced by the
/// prefix + 32 hex characters of SHA-256 of that name when it would exceed 64 characters. The PostgreSQL key is the first 8 bytes (little-endian) of SHA-256(<see cref="NamePrefix"/> + name) as a
/// signed 64-bit integer. Different realms' databases do not collide; two worlds configured with the same database name on the same server do. The connection string is otherwise used as
/// configured (host, credentials, TLS).
/// </para>
/// <para>
/// MariaDB/MySQL: <c>SELECT GET_LOCK(name, 0)</c> returns 1 when taken, 0 when another session holds it (timeout 0 = do not wait) and NULL on an error; the lock is released by
/// <c>RELEASE_LOCK(name)</c> or when the session ends, and a name is limited to 64 characters. https://mariadb.com/kb/en/get_lock/ and
/// https://dev.mysql.com/doc/refman/8.4/en/locking-functions.html. <c>IS_USED_LOCK(name)</c> returns the connection id of the holder (NULL when free), compared with <c>CONNECTION_ID()</c>
/// by the health check. GET_LOCK needs no database, so the connection is opened without one (the schema bootstrapper may not have created the characters database yet), which also means there is
/// no fallback database that two worlds could disagree on. Pooling is off: a pooled connection would be reset and reused instead of ended. <c>wait_timeout</c> is set for the session to
/// <see cref="StaleSessionTimeoutFor"/> (about four health intervals): the health check keeps a live session from ever being that idle, and the server ends the session of a host that died
/// without a TCP reset (and with it the lock) instead of holding it for the default 8 hours.
/// </para>
/// <para>
/// PostgreSQL: <c>SELECT pg_try_advisory_lock(key)</c> (bigint) returns true when taken and false when another session holds it, without waiting; session level, so it is released by
/// <c>pg_advisory_unlock(key)</c> or at session end. https://www.postgresql.org/docs/16/functions-admin.html#FUNCTIONS-ADVISORY-LOCKS. Advisory locks are scoped to the database the session is
/// connected to, so every world connects to the same fixed maintenance database (<see cref="DatabaseConnectionOptions.WriterLockDatabase"/>, default
/// <see cref="CharacterDatabaseWriterLock.DefaultPostgresLockDatabase"/>), which exists before the characters database does; the characters database name is in the key. The health check
/// looks the held lock up in <c>pg_locks</c> (a bigint key is stored as classid = high 32 bits, objid = low 32 bits, objsubid = 1,
/// https://www.postgresql.org/docs/16/view-pg-locks.html). <c>pg_locks</c> is cluster-wide, so right after taking the lock the session asks whether <b>another</b> session holds the same key in
/// <b>any</b> database; two worlds configured with different lock databases are therefore still mutually exclusive (both refuse if they start at the same instant). Pooling is off; Npgsql's
/// keepalive (30 s) keeps an idle session alive. A session that was not heard from for <see cref="StaleSessionTimeoutFor"/> is ended by <c>idle_session_timeout</c> (PostgreSQL 14 and later,
/// https://www.postgresql.org/docs/16/runtime-config-client.html), and <c>tcp_keepalives_idle</c> / <c>tcp_keepalives_interval</c> let the server notice a dead peer (best effort: a server
/// platform that rejects them keeps its defaults).
/// </para>
/// <para>
/// Loss: a periodic check (<see cref="HealthInterval"/>) runs <c>SELECT 1</c>-equivalent lookups on the lock connection and calls <c>onLost</c> once when the lock is no longer held (the connection
/// died, or the server dropped the lock). While it is lost this process does not exclude a second world. Every later tick closes the dead connection and tries to take the lock on a fresh one:
/// when that works <c>onRecovered</c> is called and the lock is held again (a later loss is reported afresh). When the lock is refused the holder's server session id is read (PostgreSQL: the
/// <c>pid</c> in <c>pg_locks</c>; MariaDB/MySQL: <c>IS_USED_LOCK</c>) and compared with the id this process's own earlier session had. A different session is another world writing the database:
/// <c>onConflict</c> is called at once, once per loss. Every session id this process opened since the lock was last held counts as its own (a take that failed after the server granted the lock
/// leaves such a session behind). An own session that the server has not ended yet (no TCP reset reached it) is not a conflict, it is reported through <c>onRetakeBlocked</c> (first time and
/// every tenth) and retried on every tick until the server ends the session (the idle timeout above); but ids are only names (a server restart or a reused backend pid can make a rival look
/// like one), so an own holder still there one stale timeout plus one check interval after the first such retake is declared a conflict. A server that cannot be reached is
/// reported the same way. The opposite failure also exists: a host that died or a network partition can leave the server holding the dead session, so the lock stays taken until the server ends it;
/// <c>pg_terminate_backend(pid)</c> / <c>KILL &lt;id&gt;</c> of the stale session clears it at once.
/// </para>
/// Owned by <see cref="CharacterDatabaseWriterLock"/>; <see cref="Dispose"/> may be called from any thread. Callbacks run on thread-pool threads, never under the internal gate. Messages carry
/// only the lock identity and a scrubbed driver message, never the connection string.
/// </summary>
internal sealed class ServerAdvisoryWriterLock : IDisposable
{
    public const string NamePrefix = "arcanecore.world.writer:";

    /// <summary>How often the held lock is verified on its connection.</summary>
    public static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(30);

    private const int MaxMySqlLockName = 64;
    private const int CommandTimeoutSeconds = 5;
    private const int PostgresIdleSessionTimeoutMinVersion = 140000;
    private const int PostgresKeepAliveProbeSeconds = 5;
    private static readonly TimeSpan MinimumStaleSessionTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly bool _postgres;
    private readonly string _mySqlName;
    private readonly long _postgresKey;
    private readonly string _connectionString;
    private readonly string _lockDatabase;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _staleTimeout;
    private readonly Action<string>? _onLost;
    private readonly Action<string>? _onRecovered;
    private readonly Action<string>? _onConflict;
    private readonly Action<string>? _onRetakeBlocked;
    private DbConnection? _connection; // null while lost and between the swap-out and the replacement of a retake; whoever swaps it out disposes it
    private Timer? _timer;
    private volatile bool _disposed; // written under the gate; read lock-free by IsLost
    private volatile bool _lost;
    private bool _retaking;
    private bool _conflictReported;
    private long _sessionId; // the server session (PostgreSQL backend pid, MariaDB connection id) that holds, or last held, the lock
    private readonly HashSet<long> _ownSessions = []; // every server session this process opened since the lock was last held (guarded by the gate): a holder in this set is this process's own dead session, never a rival
    private long _ownBlockedSince; // Time timestamp of the first retake of this loss that found the lock held by an own session; 0 when none
    private int _blockedReports; // retakes that could not be completed in the current loss, for the throttle of onRetakeBlocked

    private ServerAdvisoryWriterLock(DbConnection connection, bool postgres, string mySqlName, long postgresKey, string connectionString, string lockDatabase, string description,
        TimeSpan interval, TimeSpan staleTimeout, long sessionId, Action<string>? onLost, Action<string>? onRecovered, Action<string>? onConflict, Action<string>? onRetakeBlocked)
    {
        _connection = connection;
        _sessionId = sessionId;
        _ownSessions.Add(sessionId);
        _onRetakeBlocked = onRetakeBlocked;
        _postgres = postgres;
        _mySqlName = mySqlName;
        _postgresKey = postgresKey;
        _connectionString = connectionString;
        _lockDatabase = lockDatabase;
        _interval = interval;
        _staleTimeout = staleTimeout;
        _onLost = onLost;
        _onRecovered = onRecovered;
        _onConflict = onConflict;
        Description = description;
        _timer = new Timer(static state => ((ServerAdvisoryWriterLock)state!).CheckHealth(), this, interval, interval);
    }

    /// <summary>A human-readable identity of the lock (used as the "path" in the refusal message).</summary>
    public string Description { get; }

    /// <summary>True from the moment the held lock is found lost until it has been taken again; false after <see cref="Dispose"/>.</summary>
    internal bool IsLost => _lost && !_disposed; // lock-free: a health check holds the gate for the length of a server round trip, and callers (WorldWriterGuard.LockPath) must not wait on it

    /// <summary>The clock the own-session wait is bounded by (tests step a manual one).</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Runs on a freshly taken lock's connection right after the take, inside the retake's failure handling (tests make the post-take step fail with it).</summary>
    internal Action<DbConnection>? AfterTakeHook { get; set; }

    /// <summary>Every server session id this process has opened since the lock was last held (tests).</summary>
    internal long[] OwnSessionsForTest()
    {
        lock (_gate) return [.. _ownSessions];
    }

    /// <summary>The database name from the connection string of a server provider, or null when the provider is not a server engine, the string is malformed or names no database.</summary>
    internal static string? DatabaseNameOf(DatabaseConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) return null;
        try
        {
            string? name = options.Provider switch
            {
                DatabaseProvider.MariaDb or DatabaseProvider.MySql => new MySqlConnectionStringBuilder(options.ConnectionString).Database,
                DatabaseProvider.PostgreSql => new NpgsqlConnectionStringBuilder(options.ConnectionString).Database,
                _ => null,
            };
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The MariaDB/MySQL lock name for a database: <see cref="NamePrefix"/> + lower-cased name, or the prefix + 32 hex characters of SHA-256(lower-cased name) when that exceeds 64 characters.</summary>
    internal static string MySqlLockName(string databaseName)
    {
        string folded = databaseName.ToLowerInvariant();
        string name = NamePrefix + folded;
        if (name.Length <= MaxMySqlLockName) return name;
        return NamePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folded)).AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>The PostgreSQL advisory key for a database: the first 8 bytes (little-endian) of SHA-256(<see cref="NamePrefix"/> + name).</summary>
    internal static long PostgresLockKey(string databaseName)
        => BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(NamePrefix + databaseName)));

    /// <summary>
    /// How long the server may leave the lock session idle before it ends it: four health intervals (a live session is checked every interval, so it is never that idle), at least 10 s,
    /// in whole seconds.
    /// </summary>
    internal static TimeSpan StaleSessionTimeoutFor(TimeSpan interval)
    {
        TimeSpan stale = interval * 4;
        if (stale < MinimumStaleSessionTimeout) stale = MinimumStaleSessionTimeout;
        return TimeSpan.FromSeconds(Math.Ceiling(stale.TotalSeconds));
    }

    /// <summary>One line, <c>ExceptionType: message</c>, with the connection string's secrets and any <c>Password=...</c> pair removed (a driver message can quote the connection string).</summary>
    internal static string DescribeFailure(Exception ex, string connectionString)
        => $"{ex.GetType().Name}: {ConnectionStringRedactor.Scrub(ex.Message.ReplaceLineEndings(" "), connectionString)}";

    /// <summary>A command on <paramref name="connection"/> that gives up after <see cref="CommandTimeoutSeconds"/> instead of waiting on a hung server for the driver's default.</summary>
    internal static DbCommand CreateCommand(DbConnection connection, string text)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = CommandTimeoutSeconds;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Takes the lock for a server provider. Throws <see cref="CharacterDatabaseAlreadyOwnedException"/> when another session holds it. A server that cannot be reached or refuses the
    /// login propagates as the driver's own <see cref="DbException"/> (the caller classifies it like any other database start-up failure).
    /// </summary>
    /// <param name="onLost">Called once per loss, on a thread-pool thread, with a reason when the held lock is found lost.</param>
    /// <param name="healthInterval">The health-check period; null is <see cref="HealthInterval"/>.</param>
    /// <param name="onRecovered">Called on a thread-pool thread when a lost lock has been taken again.</param>
    /// <param name="onConflict">Called once per loss, on a thread-pool thread, at the first retake that finds the lost lock held by a session other than this process's own earlier one.</param>
    /// <param name="onRetakeBlocked">Called on a thread-pool thread, throttled (the first and every tenth try of a loss), with why a retake could not complete: the server is unreachable, or this process's own dead session still holds the lock.</param>
    /// <param name="staleSessionTimeout">How long the server may leave this session idle before it ends it; null is <see cref="StaleSessionTimeoutFor"/> of the interval.</param>
    public static ServerAdvisoryWriterLock TryAcquire(DatabaseConnectionOptions options, string databaseName, Action<string>? onLost = null, TimeSpan? healthInterval = null,
        Action<string>? onRecovered = null, Action<string>? onConflict = null, TimeSpan? staleSessionTimeout = null, Action<string>? onRetakeBlocked = null)
    {
        bool postgres = options.Provider == DatabaseProvider.PostgreSql;
        string mySqlName = MySqlLockName(databaseName);
        long key = PostgresLockKey(databaseName);
        string lockDatabase = string.IsNullOrWhiteSpace(options.WriterLockDatabase) ? CharacterDatabaseWriterLock.DefaultPostgresLockDatabase : options.WriterLockDatabase;
        string description = postgres
            ? $"PostgreSQL advisory lock {key} for database '{databaseName}' (taken in database '{lockDatabase}')"
            : $"{(options.Provider == DatabaseProvider.MySql ? "MySQL" : "MariaDB")} advisory lock '{mySqlName}'";

        TimeSpan interval = healthInterval ?? HealthInterval;
        TimeSpan stale = staleSessionTimeout ?? StaleSessionTimeoutFor(interval);
        DbConnection connection = Open(postgres, options.ConnectionString, lockDatabase, stale, interval);
        try
        {
            long session = ReadSessionId(connection, postgres); // before the take: a failure after the take must not leave a session we cannot name
            TakeResult taken = TryTake(connection, postgres, mySqlName, key, session);
            if (taken.Kind != TakeKind.Taken) throw new CharacterDatabaseAlreadyOwnedException(description);
            return new ServerAdvisoryWriterLock(connection, postgres, mySqlName, key, options.ConnectionString, lockDatabase, description, interval, stale, taken.SessionId, onLost, onRecovered, onConflict, onRetakeBlocked);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Whether this session still holds the lock, asked of the server now. False when the connection is dead or the lock is gone. Used by the periodic check and by tests.</summary>
    internal bool IsHeld()
    {
        lock (_gate)
        {
            if (_disposed || _lost || _connection is null) return false;
            return TryVerify(out _);
        }
    }

    /// <summary>Runs one scalar statement on the lock connection (tests read the session's settings with it).</summary>
    internal object? ScalarForTest(string sql)
    {
        lock (_gate)
        {
            using DbCommand command = CreateCommand(_connection ?? throw new InvalidOperationException("the lock is lost: it has no connection"), sql);
            return command.ExecuteScalar();
        }
    }

    /// <summary>The server session that holds the lock now (PostgreSQL backend pid, MariaDB/MySQL connection id); after a loss, the one that held it last.</summary>
    internal long SessionId
    {
        get { lock (_gate) return _sessionId; }
    }

    /// <summary>How a lost lock's dead connection is closed at a retake (tests keep it open to stand for a session the server has not ended).</summary>
    internal Action<DbConnection> CloseDeadConnection { get; set; } = static connection => connection.Dispose();

    /// <summary>Marks the lock lost without a report, as if a health check had failed while the server session is still alive (tests).</summary>
    internal void MarkLostForTest()
    {
        lock (_gate) _lost = true;
    }

    /// <summary>Takes the connection out of a lock that is not lost, so a test can end it itself (tests).</summary>
    internal DbConnection DetachConnectionForTest()
    {
        lock (_gate)
        {
            DbConnection connection = _connection ?? throw new InvalidOperationException("no connection");
            _connection = null;
            return connection;
        }
    }

    private bool TryVerify(out string reason)
    {
        if (_connection is null)
        {
            reason = "the lock has no connection";
            return false;
        }

        try
        {
            using DbCommand command = _postgres
                // A bigint key is stored as classid (high 32 bits) and objid (low 32 bits), objsubid 1; oid columns are compared as bigint.
                ? CreateCommand(_connection, "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted AND objsubid = 1 AND classid::bigint = @hi AND objid::bigint = @lo")
                : CreateCommand(_connection, "SELECT IS_USED_LOCK(@name) = CONNECTION_ID()");
            if (_postgres)
            {
                AddParameter(command, "@hi", (long)(uint)(_postgresKey >> 32));
                AddParameter(command, "@lo", (long)(uint)_postgresKey);
            }
            else
            {
                AddParameter(command, "@name", _mySqlName);
            }

            object? result = command.ExecuteScalar();
            bool held = result is not null and not DBNull && Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
            reason = held ? string.Empty : "the server no longer lists this session as the lock holder";
            return held;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = $"the lock connection failed ({DescribeFailure(ex, _connectionString)})";
            return false;
        }
    }

    /// <summary>One health tick: verifies a held lock, or retakes a lost one. The timer calls it; tests call it to step the state machine without waiting.</summary>
    internal void CheckHealth()
    {
        string? lostMessage = null;
        lock (_gate)
        {
            if (_disposed || _retaking) return;
            if (!_lost)
            {
                if (TryVerify(out string reason)) return;
                _lost = true;
                lostMessage = $"{Description}: {reason}";
            }
            else
            {
                _retaking = true;
            }
        }

        if (lostMessage is not null)
        {
            _onLost?.Invoke(lostMessage);
            return;
        }

        Retake();
    }

    /// <summary>
    /// One attempt, on a lost lock, to take it again on a fresh connection. The dead connection is swapped out under the gate (and closed outside it, by this method alone) first, so that a
    /// session of this very process that the server has not ended yet is not mistaken for another world. Runs outside the gate (connecting can take the connect timeout); an unreachable server
    /// leaves the lock lost for the next tick.
    /// </summary>
    private void Retake()
    {
        DbConnection? fresh = null;
        TakeResult result = NotTaken; // default(TakeResult) would read as Taken (the first enum member)
        string? failure = null;
        long ownEarlierSession = 0;
        TimeSpan ownWaitLimit = _staleTimeout + _interval; // the server ends a dead session of ours within the stale timeout; one more check interval covers the tick that notices
        try
        {
            DbConnection? old;
            lock (_gate)
            {
                old = _connection;
                _connection = null;
                ownEarlierSession = _sessionId;
            }

            if (old is not null)
            {
                try { CloseDeadConnection(old); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { } // a connection that is already dead may fail to close; it is being replaced
            }

            try
            {
                fresh = Open(_postgres, _connectionString, _lockDatabase, _staleTimeout, _interval);
                long session = ReadSessionId(fresh, _postgres); // before the take, and recorded before it: if anything after the take fails the server may still hold the lock in this session
                lock (_gate) _ownSessions.Add(session);
                result = TryTake(fresh, _postgres, _mySqlName, _postgresKey, session);
                AfterTakeHook?.Invoke(fresh);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = NotTaken; // a failure after the take: the connection is disposed below and the lock is not held by this lock object
                failure = DescribeFailure(ex, _connectionString); // the server cannot be reached (or refused a statement): still lost, tried again on the next tick
            }
        }
        finally
        {
            string? notice = null;
            Action<string>? callback = null;
            lock (_gate)
            {
                _retaking = false;
                if (_disposed || result.Kind != TakeKind.Taken)
                {
                    // A lock taken on a connection that Dispose raced past is released by ending its session here.
                    fresh?.Dispose();
                    if (!_disposed)
                    {
                        if (failure is not null)
                        {
                            notice = Blocked($"{Description}: the lock could not be retaken, the server was not usable ({failure})");
                            callback = _onRetakeBlocked;
                        }
                        else if (result.Kind == TakeKind.Held && !_ownSessions.Contains(result.SessionId))
                        {
                            if (!_conflictReported)
                            {
                                _conflictReported = true;
                                notice = $"{Description}: held by another session (server session {result.SessionId}, not this process's earlier session {ownEarlierSession})";
                                callback = _onConflict;
                            }
                        }
                        else if (result.Kind == TakeKind.Held)
                        {
                            long now = Time.GetTimestamp();
                            if (_ownBlockedSince == 0) _ownBlockedSince = Math.Max(1, now);
                            if (Time.GetElapsedTime(_ownBlockedSince, now) > ownWaitLimit)
                            {
                                // A session id is only a name: a server restart or a reused backend pid can make a rival look like our dead session. A real dead session is gone within the stale timeout.
                                if (!_conflictReported)
                                {
                                    _conflictReported = true;
                                    notice = $"{Description}: still held by server session {result.SessionId}, which this process opened earlier, more than {ownWaitLimit.TotalSeconds:0} s after the loss: the server should have ended it by now, so it is treated as another session (a reused session id)";
                                    callback = _onConflict;
                                }
                            }
                            else
                            {
                                notice = Blocked($"{Description}: still held by this process's own earlier session {result.SessionId}, which the server has not ended yet");
                                callback = _onRetakeBlocked;
                            }
                        }
                    }
                }
                else
                {
                    _connection = fresh!;
                    _sessionId = result.SessionId;
                    _ownSessions.Clear();
                    _ownSessions.Add(result.SessionId);
                    _ownBlockedSince = 0;
                    _lost = false;
                    _conflictReported = false;
                    _blockedReports = 0;
                    notice = Description;
                    callback = _onRecovered;
                }
            }

            if (notice is not null) callback?.Invoke(notice);
        }
    }

    /// <summary>Counts a blocked retake and returns the text for the first of every ten, or null (so the report is throttled). Caller holds the gate.</summary>
    private string? Blocked(string text) => _blockedReports++ % 10 == 0 ? text : null;

    private static DbConnection Open(bool postgres, string connectionString, string lockDatabase, TimeSpan staleTimeout, TimeSpan interval)
        => postgres ? OpenPostgres(connectionString, lockDatabase, staleTimeout, interval) : OpenMySql(connectionString, staleTimeout);

    private static MySqlConnection OpenMySql(string connectionString, TimeSpan staleTimeout)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Pooling = false, Database = string.Empty };
        var connection = new MySqlConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            // The server ends a session that sends nothing for this long; the health check sends something every interval, a dead host sends nothing. The default is 28800 s (8 h).
            Execute(connection, FormattableString.Invariant($"SET SESSION wait_timeout = {WholeSeconds(staleTimeout)}"));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static NpgsqlConnection OpenPostgres(string connectionString, string lockDatabase, TimeSpan staleTimeout, TimeSpan interval)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30, Database = lockDatabase };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            long serverVersion = Convert.ToInt64(Scalar(connection, "SELECT current_setting('server_version_num')::bigint"), CultureInfo.InvariantCulture);
            // idle_session_timeout (milliseconds) exists from PostgreSQL 14; it ends a session idle outside a transaction for that long. The health query keeps a live session below it.
            if (serverVersion >= PostgresIdleSessionTimeoutMinVersion)
                Execute(connection, FormattableString.Invariant($"SET idle_session_timeout = {(long)staleTimeout.TotalMilliseconds}"));

            // Server-side TCP keepalives so that the server notices a peer that vanished without a reset. A platform that does not support a keepalive setting rejects it: the default stays.
            long idle = Math.Max(1, (long)Math.Ceiling(interval.TotalSeconds / 2));
            TrySet(connection, FormattableString.Invariant($"SET tcp_keepalives_idle = {idle}"));
            TrySet(connection, FormattableString.Invariant($"SET tcp_keepalives_interval = {PostgresKeepAliveProbeSeconds}"));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static long WholeSeconds(TimeSpan value) => Math.Max(1, (long)Math.Ceiling(value.TotalSeconds));

    private static void Execute(DbConnection connection, string sql)
    {
        using DbCommand command = CreateCommand(connection, sql);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using DbCommand command = CreateCommand(connection, sql);
        return command.ExecuteScalar();
    }

    private static void TrySet(DbConnection connection, string sql)
    {
        try { Execute(connection, sql); }
        catch (PostgresException) { }
    }

    private enum TakeKind { Taken, Held, Unknown }

    /// <summary>The outcome of a take. <see cref="SessionId"/> is this session's id (read before the take) when taken, the holder's when held, 0 when unknown.</summary>
    private readonly record struct TakeResult(TakeKind Kind, long SessionId);

    private static readonly TakeResult NotTaken = new(TakeKind.Unknown, 0);

    /// <summary>
    /// Takes the lock on a freshly opened connection whose session id <paramref name="sessionId"/> was read beforehand. Held: another session holds it (on PostgreSQL: in any database of the cluster, <c>pg_locks</c> is cluster-wide), with that session's id; what
    /// this call took is given back before it returns. Unknown: it was refused but the holder is gone by the time it is looked up.
    /// </summary>
    private static TakeResult TryTake(DbConnection connection, bool postgres, string mySqlName, long postgresKey, long sessionId)
    {
        if (!postgres)
        {
            if (TryTakeMySql(connection, mySqlName)) return new TakeResult(TakeKind.Taken, sessionId);
            using DbCommand holder = CreateCommand(connection, "SELECT IS_USED_LOCK(@name)");
            AddParameter(holder, "@name", mySqlName);
            object? id = holder.ExecuteScalar();
            return id is null or DBNull ? new TakeResult(TakeKind.Unknown, 0) : new TakeResult(TakeKind.Held, Convert.ToInt64(id, CultureInfo.InvariantCulture));
        }

        bool taken = TryTakePostgres(connection, postgresKey);
        long? other = OtherPostgresHolder(connection, postgresKey);
        if (taken && other is null) return new TakeResult(TakeKind.Taken, sessionId);
        if (taken)
        {
            // The same key is held in another lock database (a world configured with a different WriterLockDatabase).
            using DbCommand release = CreateCommand(connection, "SELECT pg_advisory_unlock(@key)");
            AddParameter(release, "@key", postgresKey);
            release.ExecuteScalar();
        }

        return other is { } holderPid ? new TakeResult(TakeKind.Held, holderPid) : new TakeResult(TakeKind.Unknown, 0);
    }

    /// <summary>This connection's server session id (PostgreSQL backend pid, MariaDB/MySQL connection id), read before the lock is taken.</summary>
    private static long ReadSessionId(DbConnection connection, bool postgres)
        => Convert.ToInt64(Scalar(connection, postgres ? "SELECT pg_backend_pid()" : "SELECT CONNECTION_ID()"), CultureInfo.InvariantCulture);

    private static bool TryTakeMySql(DbConnection connection, string name)
    {
        using DbCommand command = CreateCommand(connection, "SELECT GET_LOCK(@name, 0)");
        AddParameter(command, "@name", name);
        object? result = command.ExecuteScalar();
        // NULL is an error in GET_LOCK (out of memory, killed thread): not "held elsewhere".
        if (result is null or DBNull) throw new InvalidOperationException($"GET_LOCK('{name}', 0) returned NULL (the server could not evaluate the lock)");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    private static bool TryTakePostgres(DbConnection connection, long key)
    {
        using DbCommand command = CreateCommand(connection, "SELECT pg_try_advisory_lock(@key)");
        AddParameter(command, "@key", key);
        return command.ExecuteScalar() is true;
    }

    /// <summary>The pid of a session other than this one that holds the same bigint advisory key, in any database (no database filter: <c>pg_locks</c> lists the whole cluster); null when none.</summary>
    private static long? OtherPostgresHolder(DbConnection connection, long key)
    {
        using DbCommand command = CreateCommand(connection, "SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND classid::bigint = @hi AND objid::bigint = @lo AND pid <> pg_backend_pid() LIMIT 1");
        AddParameter(command, "@hi", (long)(uint)(key >> 32));
        AddParameter(command, "@lo", (long)(uint)key);
        object? pid = command.ExecuteScalar();
        return pid is null or DBNull ? null : Convert.ToInt64(pid, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Releases the lock (explicitly, with a 5 s command timeout, then by ending the session). Idempotent; a failure to talk to the server is swallowed because closing the session releases
    /// it anyway, and a dead server must not hold up the shutdown.
    /// </summary>
    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
            if (!_lost && _connection is not null)
            {
                try
                {
                    using DbCommand command = _postgres
                        ? CreateCommand(_connection!, "SELECT pg_advisory_unlock(@key)")
                        : CreateCommand(_connection!, "SELECT RELEASE_LOCK(@name)");
                    if (_postgres) AddParameter(command, "@key", _postgresKey);
                    else AddParameter(command, "@name", _mySqlName);
                    command.ExecuteScalar();
                }
                catch (Exception ex) when (ex is DbException or InvalidOperationException or ObjectDisposedException or TimeoutException) { }
            }

            _connection?.Dispose();
            _connection = null;
        }

        timer?.Dispose();
    }
}

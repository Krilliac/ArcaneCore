using System.Buffers.Binary;
using System.Data.Common;
using System.Diagnostics;
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
/// died, or the server dropped the lock). While it is lost this process does not exclude a second world, and every later tick tries to take the lock again on a fresh connection (the dead one is
/// closed first): when that works <c>onRecovered</c> is called and the lock is held again (a later loss is reported afresh); when another session holds it, <c>onConflict</c> is called once, but only
/// after <see cref="StaleSessionTimeoutFor"/> plus one interval of continuous refusals, because until then the holder may be this process's own dead session that the server has not ended yet.
/// A server that cannot be reached leaves the lock lost and is retried on every tick. The opposite failure also exists: a host that died or a network partition can leave the server holding the
/// dead session, so the lock stays taken until the server ends it (the idle timeout above); <c>pg_terminate_backend(pid)</c> / <c>KILL &lt;id&gt;</c> of the stale session clears it at once.
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
    private DbConnection _connection;
    private Timer? _timer;
    private bool _disposed;
    private bool _lost;
    private bool _retaking;
    private bool _conflictReported;
    private long _conflictSince; // Stopwatch timestamp of the first refusal of the current run of refusals; 0 = none

    private ServerAdvisoryWriterLock(DbConnection connection, bool postgres, string mySqlName, long postgresKey, string connectionString, string lockDatabase, string description,
        TimeSpan interval, TimeSpan staleTimeout, Action<string>? onLost, Action<string>? onRecovered, Action<string>? onConflict)
    {
        _connection = connection;
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
    internal bool IsLost
    {
        get { lock (_gate) return _lost && !_disposed; }
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
    /// <param name="onConflict">Called once per loss, on a thread-pool thread, when a lost lock stays held by another session beyond the stale-session window.</param>
    /// <param name="staleSessionTimeout">How long the server may leave this session idle before it ends it; null is <see cref="StaleSessionTimeoutFor"/> of the interval.</param>
    public static ServerAdvisoryWriterLock TryAcquire(DatabaseConnectionOptions options, string databaseName, Action<string>? onLost = null, TimeSpan? healthInterval = null,
        Action<string>? onRecovered = null, Action<string>? onConflict = null, TimeSpan? staleSessionTimeout = null)
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
            if (!TryTake(connection, postgres, mySqlName, key)) throw new CharacterDatabaseAlreadyOwnedException(description);
            return new ServerAdvisoryWriterLock(connection, postgres, mySqlName, key, options.ConnectionString, lockDatabase, description, interval, stale, onLost, onRecovered, onConflict);
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
            if (_disposed || _lost) return false;
            return TryVerify(out _);
        }
    }

    /// <summary>Runs one scalar statement on the lock connection (tests read the session's settings with it).</summary>
    internal object? ScalarForTest(string sql)
    {
        lock (_gate)
        {
            using DbCommand command = CreateCommand(_connection, sql);
            return command.ExecuteScalar();
        }
    }

    private bool TryVerify(out string reason)
    {
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

    private void CheckHealth()
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
    /// One attempt, on a lost lock, to take it again on a fresh connection. The dead connection is closed first so that a session of this very process that the server has not ended yet
    /// cannot keep the lock from us. Runs outside the gate (connecting can take the connect timeout); an unreachable server leaves the lock lost for the next tick.
    /// </summary>
    private void Retake()
    {
        DbConnection? fresh = null;
        bool taken = false, heldElsewhere = false;
        try
        {
            DbConnection old;
            lock (_gate) old = _connection;
            try { old.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { } // a connection that is already dead may fail to close; it is being replaced

            try
            {
                fresh = Open(_postgres, _connectionString, _lockDatabase, _staleTimeout, _interval);
                if (TryTake(fresh, _postgres, _mySqlName, _postgresKey)) taken = true;
                else heldElsewhere = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The server cannot be reached (or refused a statement): still lost, tried again on the next tick.
            }
        }
        finally
        {
            string? notice = null;
            bool conflict = false;
            lock (_gate)
            {
                _retaking = false;
                if (_disposed || !taken)
                {
                    // A lock taken on a connection that Dispose raced past is released by ending its session here.
                    fresh?.Dispose();
                    if (!_disposed && heldElsewhere)
                    {
                        if (_conflictSince == 0) _conflictSince = Stopwatch.GetTimestamp();
                        TimeSpan refused = Stopwatch.GetElapsedTime(_conflictSince);
                        if (!_conflictReported && refused >= _staleTimeout + _interval)
                        {
                            _conflictReported = true;
                            conflict = true;
                            notice = $"{Description}: held by another session for {(int)refused.TotalSeconds} s";
                        }
                    }
                }
                else
                {
                    _connection = fresh!;
                    _lost = false;
                    _conflictSince = 0;
                    _conflictReported = false;
                    notice = Description;
                }
            }

            if (notice is not null)
            {
                if (conflict) _onConflict?.Invoke(notice);
                else _onRecovered?.Invoke(notice);
            }
        }
    }

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

    /// <summary>Takes the lock on a freshly opened connection. False when another session holds it (on PostgreSQL: in any database of the cluster); what this call took is given back before it returns false.</summary>
    private static bool TryTake(DbConnection connection, bool postgres, string mySqlName, long postgresKey)
    {
        if (!postgres) return TryTakeMySql(connection, mySqlName);
        if (!TryTakePostgres(connection, postgresKey)) return false;
        if (!HeldByAnotherPostgresSession(connection, postgresKey)) return true;

        // The same key is held in another lock database (a world configured with a different WriterLockDatabase): pg_locks is cluster-wide.
        using DbCommand release = CreateCommand(connection, "SELECT pg_advisory_unlock(@key)");
        AddParameter(release, "@key", postgresKey);
        release.ExecuteScalar();
        return false;
    }

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

    /// <summary>Whether a session other than this one holds the same bigint advisory key, in any database (no database filter: <c>pg_locks</c> lists the whole cluster).</summary>
    private static bool HeldByAnotherPostgresSession(DbConnection connection, long key)
    {
        using DbCommand command = CreateCommand(connection, "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND classid::bigint = @hi AND objid::bigint = @lo AND pid <> pg_backend_pid()");
        AddParameter(command, "@hi", (long)(uint)(key >> 32));
        AddParameter(command, "@lo", (long)(uint)key);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
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
            if (!_lost)
            {
                try
                {
                    using DbCommand command = _postgres
                        ? CreateCommand(_connection, "SELECT pg_advisory_unlock(@key)")
                        : CreateCommand(_connection, "SELECT RELEASE_LOCK(@name)");
                    if (_postgres) AddParameter(command, "@key", _postgresKey);
                    else AddParameter(command, "@name", _mySqlName);
                    command.ExecuteScalar();
                }
                catch (Exception ex) when (ex is DbException or InvalidOperationException or ObjectDisposedException or TimeoutException) { }
            }

            _connection.Dispose();
        }

        timer?.Dispose();
    }
}

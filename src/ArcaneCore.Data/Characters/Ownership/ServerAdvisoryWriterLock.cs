using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// no fallback database that two worlds could disagree on. Pooling is off: a pooled connection would be reset and reused instead of ended. <c>wait_timeout</c> is raised for the session so the
/// server does not close the idle connection (and with it the lock) after its default 8 hours.
/// </para>
/// <para>
/// PostgreSQL: <c>SELECT pg_try_advisory_lock(key)</c> (bigint) returns true when taken and false when another session holds it, without waiting; session level, so it is released by
/// <c>pg_advisory_unlock(key)</c> or at session end. https://www.postgresql.org/docs/16/functions-admin.html#FUNCTIONS-ADVISORY-LOCKS. Advisory locks are scoped to the database the session is
/// connected to, so every world connects to the same fixed maintenance database (<see cref="DatabaseConnectionOptions.WriterLockDatabase"/>, default
/// <see cref="CharacterDatabaseWriterLock.DefaultPostgresLockDatabase"/>), which exists before the characters database does; the characters database name is in the key. The health check
/// looks the held lock up in <c>pg_locks</c> (a bigint key is stored as classid = high 32 bits, objid = low 32 bits, objsubid = 1,
/// https://www.postgresql.org/docs/16/view-pg-locks.html). Pooling is off; Npgsql's keepalive (30 s) keeps an idle session alive.
/// </para>
/// <para>
/// Loss: a periodic check (<see cref="HealthInterval"/>) runs <c>SELECT 1</c>-equivalent lookups on the lock connection and calls <c>onLost</c> once when the lock is no longer held (the connection
/// died, or the server dropped the lock). After that this process no longer excludes a second world. The opposite failure also exists: a host that died or a network partition can leave the server
/// holding the dead session, so the lock stays taken until the server notices (TCP timeouts); <c>pg_terminate_backend(pid)</c> / <c>KILL &lt;id&gt;</c> of the stale session clears it.
/// </para>
/// Owned by <see cref="CharacterDatabaseWriterLock"/>; <see cref="Dispose"/> may be called from any thread. Messages carry only the lock identity, never the connection string.
/// </summary>
internal sealed class ServerAdvisoryWriterLock : IDisposable
{
    public const string NamePrefix = "arcanecore.world.writer:";

    /// <summary>How often the held lock is verified on its connection.</summary>
    public static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(30);

    private const int MaxMySqlLockName = 64;
    private const int CommandTimeoutSeconds = 5;

    private readonly object _gate = new();
    private readonly DbConnection _connection;
    private readonly bool _postgres;
    private readonly string _mySqlName;
    private readonly long _postgresKey;
    private readonly Action<string>? _onLost;
    private Timer? _timer;
    private bool _disposed;
    private bool _lost;

    private ServerAdvisoryWriterLock(DbConnection connection, bool postgres, string mySqlName, long postgresKey, string description, Action<string>? onLost, TimeSpan interval)
    {
        _connection = connection;
        _postgres = postgres;
        _mySqlName = mySqlName;
        _postgresKey = postgresKey;
        _onLost = onLost;
        Description = description;
        _timer = new Timer(static state => ((ServerAdvisoryWriterLock)state!).CheckHealth(), this, interval, interval);
    }

    /// <summary>A human-readable identity of the lock (used as the "path" in the refusal message).</summary>
    public string Description { get; }

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
    /// Takes the lock for a server provider. Throws <see cref="CharacterDatabaseAlreadyOwnedException"/> when another session holds it. A server that cannot be reached or refuses the
    /// login propagates as the driver's own <see cref="DbException"/> (the caller classifies it like any other database start-up failure).
    /// </summary>
    /// <param name="onLost">Called once, on a thread-pool thread, with a reason when the held lock is found lost.</param>
    /// <param name="healthInterval">The health-check period; null is <see cref="HealthInterval"/>.</param>
    public static ServerAdvisoryWriterLock TryAcquire(DatabaseConnectionOptions options, string databaseName, Action<string>? onLost = null, TimeSpan? healthInterval = null)
    {
        bool postgres = options.Provider == DatabaseProvider.PostgreSql;
        string mySqlName = MySqlLockName(databaseName);
        long key = PostgresLockKey(databaseName);
        string lockDatabase = string.IsNullOrWhiteSpace(options.WriterLockDatabase) ? CharacterDatabaseWriterLock.DefaultPostgresLockDatabase : options.WriterLockDatabase;
        string description = postgres
            ? $"PostgreSQL advisory lock {key} for database '{databaseName}' (taken in database '{lockDatabase}')"
            : $"{(options.Provider == DatabaseProvider.MySql ? "MySQL" : "MariaDB")} advisory lock '{mySqlName}'";

        DbConnection connection = postgres ? OpenPostgres(options.ConnectionString, lockDatabase) : OpenMySql(options.ConnectionString);
        try
        {
            bool taken = postgres ? TryTakePostgres(connection, key) : TryTakeMySql(connection, mySqlName);
            if (!taken) throw new CharacterDatabaseAlreadyOwnedException(description);
            return new ServerAdvisoryWriterLock(connection, postgres, mySqlName, key, description, onLost, healthInterval ?? HealthInterval);
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
            if (_disposed) return false;
            return TryVerify(out _);
        }
    }

    private bool TryVerify(out string reason)
    {
        try
        {
            using DbCommand command = _connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            DbParameter first = command.CreateParameter();
            if (_postgres)
            {
                // A bigint key is stored as classid (high 32 bits) and objid (low 32 bits), objsubid 1; oid columns are compared as bigint.
                command.CommandText = "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted AND objsubid = 1 AND classid::bigint = @hi AND objid::bigint = @lo";
                first.ParameterName = "@hi";
                first.Value = (long)(uint)(_postgresKey >> 32);
                DbParameter low = command.CreateParameter();
                low.ParameterName = "@lo";
                low.Value = (long)(uint)_postgresKey;
                command.Parameters.Add(first);
                command.Parameters.Add(low);
            }
            else
            {
                command.CommandText = "SELECT IS_USED_LOCK(@name) = CONNECTION_ID()";
                first.ParameterName = "@name";
                first.Value = _mySqlName;
                command.Parameters.Add(first);
            }

            object? result = command.ExecuteScalar();
            bool held = result is not null and not DBNull && Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
            reason = held ? string.Empty : "the server no longer lists this session as the lock holder";
            return held;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = $"the lock connection failed ({ex.GetType().Name}: {ex.Message})";
            return false;
        }
    }

    private void CheckHealth()
    {
        string reason;
        lock (_gate)
        {
            if (_disposed || _lost) return;
            if (TryVerify(out reason)) return;
            _lost = true;
            _timer?.Dispose();
            _timer = null;
        }

        _onLost?.Invoke($"{Description}: {reason}");
    }

    private static MySqlConnection OpenMySql(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Pooling = false, Database = string.Empty };
        var connection = new MySqlConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            using DbCommand timeout = connection.CreateCommand();
            // 2147483 s (24.8 days) is the largest value every platform accepts; the default is 28800 s (8 h).
            timeout.CommandText = "SET SESSION wait_timeout = 2147483";
            timeout.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static NpgsqlConnection OpenPostgres(string connectionString, string lockDatabase)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30, Database = lockDatabase };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool TryTakeMySql(DbConnection connection, string name)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@name, 0)";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = name;
        command.Parameters.Add(parameter);
        object? result = command.ExecuteScalar();
        // NULL is an error in GET_LOCK (out of memory, killed thread): not "held elsewhere".
        if (result is null or DBNull) throw new InvalidOperationException($"GET_LOCK('{name}', 0) returned NULL (the server could not evaluate the lock)");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    private static bool TryTakePostgres(DbConnection connection, long key)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        return command.ExecuteScalar() is true;
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
                    using DbCommand command = _connection.CreateCommand();
                    command.CommandTimeout = CommandTimeoutSeconds;
                    DbParameter parameter = command.CreateParameter();
                    if (_postgres)
                    {
                        command.CommandText = "SELECT pg_advisory_unlock(@key)";
                        parameter.ParameterName = "@key";
                        parameter.Value = _postgresKey;
                    }
                    else
                    {
                        command.CommandText = "SELECT RELEASE_LOCK(@name)";
                        parameter.ParameterName = "@name";
                        parameter.Value = _mySqlName;
                    }

                    command.Parameters.Add(parameter);
                    command.ExecuteScalar();
                }
                catch (Exception ex) when (ex is DbException or InvalidOperationException or ObjectDisposedException or TimeoutException) { }
            }

            _connection.Dispose();
        }

        timer?.Dispose();
    }
}

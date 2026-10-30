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
/// world process. The server releases it when that session ends (the process exits or crashes, the network drops, the connection is closed), so there is no lease and no stale lock.
/// <para>
/// Identity: both engines key the lock on the characters <b>database name</b> from the connection string (the MariaDB/MySQL lock name is <see cref="NamePrefix"/> + name, replaced by the
/// prefix + 32 hex characters of SHA-256(name) when it would exceed 64 characters; the PostgreSQL key is the first 8 bytes of SHA-256(<see cref="NamePrefix"/> + name) as a little-endian signed
/// 64-bit integer), so the characters databases of different realms do not collide and two worlds configured with the same database name on the same server do. The connection string is
/// otherwise used as configured (host, credentials, TLS).
/// </para>
/// <para>
/// MariaDB/MySQL: <c>SELECT GET_LOCK(name, 0)</c> returns 1 when taken, 0 when another session holds it (timeout 0 = do not wait) and NULL on an error; the lock is released by
/// <c>RELEASE_LOCK(name)</c> or when the session ends, and a name is limited to 64 characters. https://mariadb.com/kb/en/get_lock/ and
/// https://dev.mysql.com/doc/refman/8.4/en/locking-functions.html. GET_LOCK needs no database, so the connection is opened without one (the schema bootstrapper may not have created
/// the characters database yet). Pooling is off: a pooled connection would be reset and reused instead of ended. <c>wait_timeout</c> is raised for the session so the server does not
/// close the idle connection (and with it the lock) after its default 8 hours.
/// </para>
/// <para>
/// PostgreSQL: <c>SELECT pg_try_advisory_lock(key)</c> (bigint) returns true when taken and false when another session holds it, without waiting; session level, so it is released by
/// <c>pg_advisory_unlock(key)</c> or at session end. https://www.postgresql.org/docs/16/functions-admin.html#FUNCTIONS-ADVISORY-LOCKS. Advisory locks are scoped to the database the session
/// is connected to, so the connection goes to the characters database, or to the maintenance database <c>postgres</c> when the characters database does not exist yet (its first
/// bootstrap; a world started in that window is not excluded). Pooling is off; Npgsql's keepalive (30 s) keeps an idle session alive.
/// </para>
/// Owned by <see cref="CharacterDatabaseWriterLock"/>; <see cref="Dispose"/> may be called from any thread. A lost connection (server restart, network partition) silently loses the lock:
/// that is a limit, not detected here. Messages carry only the lock identity, never the connection string.
/// </summary>
internal sealed class ServerAdvisoryWriterLock : IDisposable
{
    public const string NamePrefix = "arcanecore.world.writer:";
    private const int MaxMySqlLockName = 64;
    private const string PostgresMaintenanceDatabase = "postgres";
    private const string InvalidCatalogName = "3D000";

    private readonly DbConnection _connection;
    private readonly bool _postgres;
    private readonly string _mySqlName;
    private readonly long _postgresKey;
    private int _disposed;

    private ServerAdvisoryWriterLock(DbConnection connection, bool postgres, string mySqlName, long postgresKey, string description)
    {
        _connection = connection;
        _postgres = postgres;
        _mySqlName = mySqlName;
        _postgresKey = postgresKey;
        Description = description;
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

    /// <summary>The MariaDB/MySQL lock name for a database: <see cref="NamePrefix"/> + name, or the prefix + 32 hex characters of SHA-256(name) when that exceeds 64 characters.</summary>
    internal static string MySqlLockName(string databaseName)
    {
        string name = NamePrefix + databaseName;
        if (name.Length <= MaxMySqlLockName) return name;
        return NamePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databaseName)).AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>The PostgreSQL advisory key for a database: the first 8 bytes (little-endian) of SHA-256(<see cref="NamePrefix"/> + name).</summary>
    internal static long PostgresLockKey(string databaseName)
        => BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(NamePrefix + databaseName)));

    /// <summary>
    /// Takes the lock for a server provider. Throws <see cref="CharacterDatabaseAlreadyOwnedException"/> when another session holds it. A server that cannot be reached or refuses the
    /// login propagates as the driver's own <see cref="DbException"/> (the caller classifies it like any other database start-up failure).
    /// </summary>
    public static ServerAdvisoryWriterLock TryAcquire(DatabaseConnectionOptions options, string databaseName)
    {
        bool postgres = options.Provider == DatabaseProvider.PostgreSql;
        string mySqlName = MySqlLockName(databaseName);
        long key = PostgresLockKey(databaseName);
        string description = postgres
            ? $"PostgreSQL advisory lock {key} for database '{databaseName}'"
            : $"{(options.Provider == DatabaseProvider.MySql ? "MySQL" : "MariaDB")} advisory lock '{mySqlName}'";

        DbConnection connection = postgres ? OpenPostgres(options.ConnectionString) : OpenMySql(options.ConnectionString);
        try
        {
            bool taken = postgres ? TryTakePostgres(connection, key) : TryTakeMySql(connection, mySqlName);
            if (!taken) throw new CharacterDatabaseAlreadyOwnedException(description);
            return new ServerAdvisoryWriterLock(connection, postgres, mySqlName, key, description);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
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

    private static NpgsqlConnection OpenPostgres(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30 };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (PostgresException ex) when (ex.SqlState == InvalidCatalogName)
        {
            connection.Dispose();
            builder.Database = PostgresMaintenanceDatabase;
            var maintenance = new NpgsqlConnection(builder.ConnectionString);
            try
            {
                maintenance.Open();
                return maintenance;
            }
            catch
            {
                maintenance.Dispose();
                throw;
            }
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

    /// <summary>Releases the lock (explicitly, then by ending the session). Idempotent; a failure to talk to the server is swallowed because closing the session releases it anyway.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            using DbCommand command = _connection.CreateCommand();
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
        finally { _connection.Dispose(); }
    }
}

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Schema;

/// <summary>
/// Serializes schema changes of one component across processes (the realm and the world daemon
/// both bootstrap the auth schema). The context's connection must be open for the whole life of
/// the lock; the bootstrapper guarantees that.
/// <list type="bullet">
/// <item>SQLite: a write transaction (<c>BEGIN IMMEDIATE</c>) kept open for the whole bootstrap and
/// committed at the end. It excludes every other writer of the file, so the upgrade is also
/// atomic. A different process or connection waits (up to the timeout) until it ends.</item>
/// <item>MariaDB/MySQL: the named lock <c>GET_LOCK(SHA1('arcanecore_schema:&lt;database&gt;:&lt;component&gt;'), 0)</c>,
/// polled until the deadline, released with <c>RELEASE_LOCK</c>. Session scoped.</item>
/// <item>PostgreSQL: the advisory lock <c>pg_try_advisory_lock(key)</c> where the key is the 64-bit
/// FNV-1a hash of <c>arcanecore_schema:&lt;component&gt;</c>, polled until the deadline, released with
/// <c>pg_advisory_unlock</c>. Session scoped, per database.</item>
/// </list>
/// Locks are named per component, so components sharing one database do not block each other.
/// </summary>
internal sealed class SchemaLock : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly DbContext _db;
    private readonly string _component;
    private IDbContextTransaction? _transaction;
    private bool _heldByName;

    private SchemaLock(DbContext db, string component)
    {
        _db = db;
        _component = component;
    }

    /// <summary>The lock name of a component on MariaDB/MySQL, hashed in SQL (documented contract).</summary>
    internal const string NamePrefix = "arcanecore_schema:";

    /// <summary>The PostgreSQL advisory-lock key of a component (documented contract).</summary>
    internal static long AdvisoryKey(string component)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(NamePrefix + component))
        {
            hash ^= b;
            hash = unchecked(hash * prime);
        }

        return unchecked((long)hash);
    }

    public static async Task<SchemaLock> AcquireAsync(DbContext db, string component, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var held = new SchemaLock(db, component);
        try
        {
            switch (db.Database.ProviderName)
            {
                case "Microsoft.EntityFrameworkCore.Sqlite":
                    await held.BeginSqliteWriteAsync(timeout, cancellationToken).ConfigureAwait(false);
                    break;

                case "Pomelo.EntityFrameworkCore.MySql":
                    await held.PollAsync(
                        "SELECT GET_LOCK(SHA1(CONCAT(@prefix, DATABASE(), ':', @component)), 0)",
                        [("@prefix", NamePrefix), ("@component", component)],
                        timeout, cancellationToken).ConfigureAwait(false);
                    break;

                case "Npgsql.EntityFrameworkCore.PostgreSQL":
                    await held.PollAsync(
                        "SELECT pg_try_advisory_lock(@key)", [("@key", AdvisoryKey(component))], timeout, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new NotSupportedException($"no schema lock for provider {db.Database.ProviderName}");
            }

            return held;
        }
        catch
        {
            await held.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Make the bootstrap's work permanent (SQLite commits its transaction; the named locks have nothing to commit).</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            // Not completed: rolls the whole bootstrap back.
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        if (_heldByName)
        {
            _heldByName = false;
            try
            {
                switch (_db.Database.ProviderName)
                {
                    case "Pomelo.EntityFrameworkCore.MySql":
                        await ExecuteScalarAsync(
                            "SELECT RELEASE_LOCK(SHA1(CONCAT(@prefix, DATABASE(), ':', @component)))",
                            [("@prefix", NamePrefix), ("@component", _component)], CancellationToken.None).ConfigureAwait(false);
                        break;
                    case "Npgsql.EntityFrameworkCore.PostgreSQL":
                        await ExecuteScalarAsync(
                            "SELECT pg_advisory_unlock(@key)", [("@key", AdvisoryKey(_component))], CancellationToken.None).ConfigureAwait(false);
                        break;
                }
            }
            catch (DbException)
            {
                // The connection is gone, and the server releases a session's locks when the
                // session ends; the error that broke the connection is the one worth reporting.
            }
        }
    }

    private async Task BeginSqliteWriteAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Microsoft.Data.Sqlite turns the command timeout into SQLite's busy timeout, so the
        // BEGIN IMMEDIATE below waits at most that long for another writer.
        DbConnection connection = _db.Database.GetDbConnection();
        var sqlite = connection as SqliteConnection;
        int previous = sqlite?.DefaultTimeout ?? 0;
        if (sqlite is not null)
        {
            sqlite.DefaultTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        }

        try
        {
            _transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw Timeout(timeout, ex);
        }
        finally
        {
            if (sqlite is not null)
            {
                sqlite.DefaultTimeout = previous;
            }
        }
    }

    private async Task PollAsync(
        string sql, (string Name, object Value)[] parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            object? result = await ExecuteScalarAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
            if (result is true || (result is not null && result is not DBNull && Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1))
            {
                _heldByName = true;
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw Timeout(timeout, null);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<object?> ExecuteScalarAsync(string sql, (string Name, object Value)[] parameters, CancellationToken cancellationToken)
    {
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private SchemaMismatchException Timeout(TimeSpan timeout, Exception? inner)
        => new(
            $"timed out after {timeout.TotalSeconds:0.#} s waiting for the {_component} schema lock; another process is changing " +
            "this database's schema, or one that died still holds the lock." + (inner is null ? string.Empty : " " + inner.Message))
        {
            Reason = SchemaMismatchReason.LockTimeout,
        };
}

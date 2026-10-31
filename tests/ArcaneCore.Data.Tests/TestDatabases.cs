using Microsoft.Data.Sqlite;
using System.Data.Common;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Hands out throw-away databases per provider and drops them afterwards. SQLite is always
/// available; MariaDB and PostgreSQL need a server connection string (without a database
/// name) in <c>ARCANECORE_TEST_MARIADB</c> / <c>ARCANECORE_TEST_POSTGRES</c>.
/// </summary>
internal sealed class TestDatabases : IAsyncDisposable
{
    public const string MariaDbVariable = "ARCANECORE_TEST_MARIADB";
    public const string PostgresVariable = "ARCANECORE_TEST_POSTGRES";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<DatabaseConnectionOptions> _created = [];
    private readonly object _gate = new();

    public static IEnumerable<object[]> AvailableProviders()
    {
        yield return [DatabaseProvider.Sqlite];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MariaDbVariable)))
        {
            yield return [DatabaseProvider.MariaDb];
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable)))
        {
            yield return [DatabaseProvider.PostgreSql];
        }
    }

    /// <summary>A connection to a database that does not exist yet (the bootstrapper creates it).</summary>
    public Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        string name = "arcane_t_" + Guid.NewGuid().ToString("N")[..16];
        string connectionString = provider switch
        {
            DatabaseProvider.Sqlite => $"Data Source={SqlitePath(name)}",
            DatabaseProvider.MariaDb or DatabaseProvider.MySql =>
                Environment.GetEnvironmentVariable(MariaDbVariable)!.TrimEnd(';') + $";Database={name}",
            DatabaseProvider.PostgreSql =>
                Environment.GetEnvironmentVariable(PostgresVariable)!.TrimEnd(';') + $";Database={name}",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        var options = new DatabaseConnectionOptions { Provider = provider, ConnectionString = connectionString };
        lock (_gate)
        {
            _created.Add(options);
        }

        return Task.FromResult(options);
    }

    /// <summary>
    /// Drop one database now instead of at the end of the class. A theory that opens one database per injected
    /// fault would otherwise leave hundreds on the server until its class finishes.
    /// </summary>
    public async Task ReleaseAsync(DatabaseConnectionOptions options)
    {
        lock (_gate)
        {
            if (!_created.Remove(options))
            {
                return;
            }
        }

        ClearPool(options);
        if (options.Provider != DatabaseProvider.Sqlite)
        {
            await DropAsync(options);
        }
        else
        {
            string path = new SqliteConnectionStringBuilder(options.ConnectionString).DataSource;
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // best effort; DisposeAsync removes the directory
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Only this fixture's own pools. xunit runs test classes in parallel and each owns a
        // TestDatabases, so ClearAllPools() here would dispose pooled handles another class is
        // acquiring at that instant (ObjectDisposedException / "database is locked" in an
        // unrelated test). Every connection string handed out is passed to the code under test
        // unchanged, so it is also the pool key.
        foreach (DatabaseConnectionOptions options in _created)
        {
            ClearPool(options);
        }

        foreach (DatabaseConnectionOptions options in _created.Where(o => o.Provider != DatabaseProvider.Sqlite))
        {
            await DropAsync(options);
        }

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private const int DropAttempts = 4;
    private const int DropCommandTimeoutSeconds = 120;

    /// <summary>
    /// Drop a provider database. PostgreSQL refuses a plain DROP while any session is attached, so it runs
    /// <c>DROP DATABASE ... WITH (FORCE)</c> (terminates the remaining sessions) from the <c>postgres</c> maintenance
    /// database; MariaDB runs <c>DROP DATABASE IF EXISTS</c> from a connection with no default database. The command
    /// timeout is long because a busy shared server can take well over the 30 s default, and a failed attempt is retried
    /// a bounded number of times with a growing pause before the last exception is rethrown.
    /// </summary>
    private static async Task DropAsync(DatabaseConnectionOptions options)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DropOnceAsync(options);
                return;
            }
            catch (Exception ex) when (attempt < DropAttempts && (ex is DbException || ex is TimeoutException))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }
    }

    private static async Task DropOnceAsync(DatabaseConnectionOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProvider.MariaDb or DatabaseProvider.MySql:
            {
                var builder = new MySqlConnectionStringBuilder(options.ConnectionString);
                string name = builder.Database;
                builder.Database = string.Empty;
                builder.Pooling = false;
                await using var connection = new MySqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using MySqlCommand command = connection.CreateCommand();
                command.CommandTimeout = DropCommandTimeoutSeconds;
                command.CommandText = "DROP DATABASE IF EXISTS `" + name.Replace("`", "``", StringComparison.Ordinal) + "`";
                await command.ExecuteNonQueryAsync();
                break;
            }

            case DatabaseProvider.PostgreSql:
            {
                var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
                string name = builder.Database!;
                builder.Database = "postgres";
                builder.Pooling = false;
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandTimeout = DropCommandTimeoutSeconds;
                command.CommandText = "DROP DATABASE IF EXISTS \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>Whether the provider database named in <paramref name="options"/> exists on the server.</summary>
    public static async Task<bool> ExistsAsync(DatabaseConnectionOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProvider.MariaDb or DatabaseProvider.MySql:
            {
                var builder = new MySqlConnectionStringBuilder(options.ConnectionString);
                string name = builder.Database;
                builder.Database = string.Empty;
                builder.Pooling = false;
                await using var connection = new MySqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using MySqlCommand command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @n";
                command.Parameters.AddWithValue("@n", name);
                return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
            }

            case DatabaseProvider.PostgreSql:
            {
                var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
                string name = builder.Database!;
                builder.Database = "postgres";
                builder.Pooling = false;
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @n";
                command.Parameters.AddWithValue("n", name);
                return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>
    /// Close the idle pooled SQLite handles of exactly this connection string (the pool key) and nothing else.
    /// Tests must use this instead of <c>SqliteConnection.ClearAllPools()</c>: closing another class's last
    /// handle on a WAL database checkpoints that database, rewriting its file mid-test (the 2026-10-07
    /// SchemaPlannerTests fingerprint flake), and disposes handles that class is acquiring.
    /// <c>NoGlobalSqlitePoolClearing</c> in <see cref="TestDatabasesIsolationTests"/> keeps it out.
    /// </summary>
    public static void ClearSqlitePool(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static void ClearPool(DatabaseConnectionOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProvider.Sqlite:
                ClearSqlitePool(options.ConnectionString);
                break;
            case DatabaseProvider.MariaDb or DatabaseProvider.MySql:
                using (var connection = new MySqlConnection(options.ConnectionString))
                {
                    MySqlConnection.ClearPool(connection);
                }

                break;
            case DatabaseProvider.PostgreSql:
                using (var connection = new NpgsqlConnection(options.ConnectionString))
                {
                    NpgsqlConnection.ClearPool(connection);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private string SqlitePath(string name)
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, name + ".db");
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    private static async Task DropAsync(DatabaseConnectionOptions options)
    {
        var builder = new DbContextOptionsBuilder<DbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, options);
        await using var db = new DbContext(builder.Options);
        await db.Database.EnsureDeletedAsync();
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

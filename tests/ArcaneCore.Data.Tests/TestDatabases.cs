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
        _created.Add(options);
        return Task.FromResult(options);
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
            var builder = new DbContextOptionsBuilder<DbContext>();
            DataServiceCollectionExtensions.ConfigureProvider(builder, options);
            await using var db = new DbContext(builder.Options);
            await db.Database.EnsureDeletedAsync();
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

    private static void ClearPool(DatabaseConnectionOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProvider.Sqlite:
                using (var connection = new SqliteConnection(options.ConnectionString))
                {
                    SqliteConnection.ClearPool(connection);
                }

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

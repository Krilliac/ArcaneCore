using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <see cref="TestDatabases"/> must drop a provider database even while a test still holds a connection to it
/// (PostgreSQL refuses a plain DROP DATABASE with sessions attached).
/// </summary>
public sealed class TestDatabasesDropTests
{
    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Release_DropsDatabase_EvenWithAnExtraOpenConnection(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions db = await databases.CreateAsync(provider);

        if (provider == DatabaseProvider.Sqlite)
        {
            using var sqlite = new SqliteConnection(db.ConnectionString);
            sqlite.Open();
            string path = new SqliteConnectionStringBuilder(db.ConnectionString).DataSource;
            Assert.True(File.Exists(path));
            sqlite.Close();
            await databases.ReleaseAsync(db);
            Assert.False(File.Exists(path));
            return;
        }

        await CreateAsync(db);
        Assert.True(await TestDatabases.ExistsAsync(db));

        // Pooled and left open: this session is what blocks a plain DROP on PostgreSQL.
        if (provider == DatabaseProvider.PostgreSql)
        {
            await using var held = new NpgsqlConnection(db.ConnectionString);
            await held.OpenAsync();
            await databases.ReleaseAsync(db);
        }
        else
        {
            await using var held = new MySqlConnection(db.ConnectionString);
            await held.OpenAsync();
            await databases.ReleaseAsync(db);
        }

        Assert.False(await TestDatabases.ExistsAsync(db));
    }

    private static async Task CreateAsync(DatabaseConnectionOptions db)
    {
        if (db.Provider == DatabaseProvider.PostgreSql)
        {
            var builder = new NpgsqlConnectionStringBuilder(db.ConnectionString);
            string name = builder.Database!;
            builder.Database = "postgres";
            builder.Pooling = false;
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "CREATE DATABASE \"" + name + "\"";
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            var builder = new MySqlConnectionStringBuilder(db.ConnectionString);
            string name = builder.Database;
            builder.Database = string.Empty;
            builder.Pooling = false;
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using MySqlCommand command = connection.CreateCommand();
            command.CommandText = "CREATE DATABASE `" + name + "`";
            await command.ExecuteNonQueryAsync();
        }
    }
}

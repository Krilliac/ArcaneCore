using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// EF Core's SQLite creator puts a database it creates in WAL mode, but a file that already exists (an operator's
/// empty file, or the MockClient fixture's pre-created ones) skips creation and kept the rollback journal. There a
/// writer cannot commit while another connection reads, and the server's background write queues and a character
/// create failed with "database is locked" (the MockClient lifecycle flake, 2026-10-08). The bootstrap now switches
/// every file-backed SQLite database to WAL.
/// </summary>
public sealed class SqliteWriteAheadLogTests
{
    [Fact]
    public async Task APreCreatedEmptyFile_IsBootstrappedInWalMode_AndAWriteCommitsWhileAnotherConnectionReads()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "auth.db");
        await using (new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            string connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 1 }.ToString();
            await using (ServiceProvider provider = Build(("Database:Provider", "Sqlite"), ("Database:ConnectionString", connectionString)))
            {
                int exit = await DatabaseStartup.InitializeAsync(() => provider.GetRequiredService<AuthDbInitializer>().InitializeAsync(), provider, new StringWriter());
                Assert.Equal(DbUpgradeExitCodes.Ok, exit);
            }

            await using var reader = new SqliteConnection(connectionString);
            await reader.OpenAsync();
            Assert.Equal("wal", await ScalarAsync(reader, "PRAGMA journal_mode"));

            // A long read on one connection (an open read transaction) ...
            await using SqliteTransaction read = reader.BeginTransaction(deferred: true);
            await ScalarAsync(reader, "SELECT count(*) FROM sqlite_master", read);

            // ... must not stop another connection's write from committing (rollback journal: "database is locked").
            await using var writer = new SqliteConnection(connectionString);
            await writer.OpenAsync();
            await ScalarAsync(writer, "CREATE TABLE wal_probe(id INTEGER)");
            await ScalarAsync(writer, "INSERT INTO wal_probe VALUES (1)");
            Assert.Equal(1L, await ScalarAsync(writer, "SELECT count(*) FROM wal_probe"));
        }
        finally
        {
            // Pooling=False: every connection is already closed, so the files can go.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return await command.ExecuteScalarAsync();
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthDatabase(config);
        services.AddDatabaseResilience(config);
        return services.BuildServiceProvider();
    }
}

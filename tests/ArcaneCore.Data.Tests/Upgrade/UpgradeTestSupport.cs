using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>Helpers of the database-upgrade tests (plan, drift, executor, CLI).</summary>
internal static partial class UpgradeTestSupport
{
    public static string Quote(DbContext db, string identifier) => TestContexts.Quote(db, identifier);

    public static Task SetVersionAsync(DbContext db, string component, int version)
        => SchemaProbe.ExecuteAsync(db, $"UPDATE {Quote(db, component + "_schema")} SET {Quote(db, "Version")} = {version}");

    public static async Task<int?> ReadVersionAsync(DbContext db, string component)
    {
        await using ConnectionScope scope = await ConnectionScope.OpenAsync(db);
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT {Quote(db, "Version")} FROM {Quote(db, component + "_schema")} WHERE {Quote(db, "Id")} = 1";
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> CountRowsAsync(DbContext db, string table)
    {
        await using ConnectionScope scope = await ConnectionScope.OpenAsync(db);
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(db, table)}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Tables, columns, indexes and the version row of one component's model as the database holds
    /// them now, written with the independent test reader, as one comparable text.
    /// </summary>
    public static async Task<string> SnapshotAsync(DbContext db, SchemaDefinition definition)
    {
        var text = new StringBuilder();
        string[] tables = [.. SchemaProbe.ModelTables(db).Order(StringComparer.Ordinal)];
        foreach (string table in tables)
        {
            bool exists = await SchemaCatalog.TableExistsAsync(db, table);
            text.Append(table).Append(exists ? " present" : " absent").Append('\n');
            if (!exists)
            {
                continue;
            }

            text.Append("  columns ").AppendJoin(',', await SchemaProbe.ActualColumnsAsync(db, table)).Append('\n');
            foreach (IndexShape index in await SchemaProbe.ActualIndexesAsync(db, [table]))
            {
                text.Append("  index ").Append(index).Append('\n');
            }
        }

        bool hasVersionTable = await SchemaCatalog.TableExistsAsync(db, definition.VersionTable);
        text.Append("version table ").Append(hasVersionTable ? "present" : "absent").Append('\n');
        if (hasVersionTable)
        {
            text.Append("rows ").Append(await CountRowsAsync(db, definition.VersionTable)).Append('\n');
            text.Append("version ").Append(await ReadVersionAsync(db, definition.Component)).Append('\n');
        }

        return text.ToString();
    }

    public static string SqlitePath(DatabaseConnectionOptions connection)
        => new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource;

    /// <summary>The SQLite database file's content hash and last write time, "absent" when there is no file.</summary>
    public static string FileFingerprint(string path)
    {
        if (!File.Exists(path))
        {
            return "absent";
        }

        // Shared read: a pooled SQLite connection of the same process keeps the file open.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)) + "@" + File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <see cref="FileFingerprint"/> of a SQLite test database after its write-ahead log is folded into the file.
    /// EF Core creates SQLite databases in WAL mode, so committed work sits in <c>-wal</c> until the last connection
    /// closes and checkpoints it into the file. A plain fingerprint is then neither stable (any close of the last
    /// pooled handle, by this test or another, rewrites the file with the same logical content) nor complete (a write
    /// that is still in the WAL does not show). Clearing this database's own pool closes its idle handles, the last
    /// close checkpoints, and the fingerprint covers every committed byte. Use it on both sides of a
    /// "writes nothing" assertion.
    /// </summary>
    public static string SettledFileFingerprint(DatabaseConnectionOptions connection)
    {
        string path = SqlitePath(connection);
        TestDatabases.ClearSqlitePool(connection.ConnectionString);
        if (File.Exists(path + "-wal"))
        {
            // Not settled: a connection outside the pool still has the database open, so the WAL may hold writes the
            // file does not. Comparing such fingerprints would prove nothing.
            throw new InvalidOperationException($"{path} still has a write-ahead log after its pool was cleared; a connection is still open");
        }

        return FileFingerprint(path);
    }

    public static string AuctionInsert(DbContext db, int id, int itemGuid) =>
        $"INSERT INTO {Quote(db, "auction")} ({Quote(db, "id")}, {Quote(db, "house_id")}, {Quote(db, "item_guid")}, {Quote(db, "item_id")}, {Quote(db, "item_count")}, " +
        $"{Quote(db, "seller_guid")}, {Quote(db, "start_bid")}, {Quote(db, "buyout_price")}, {Quote(db, "expire_time")}, {Quote(db, "buyer_guid")}, " +
        $"{Quote(db, "last_bid")}, {Quote(db, "deposit")}) VALUES ({id}, 1, {itemGuid}, 1, 1, 1, 1, 1, 1, 0, 0, 0)";

    /// <summary>The definition plus one more step that repairs the indexes of <paramref name="table"/> (a stand-in for a future release).</summary>
    public static SchemaDefinition WithIndexRepairStep(SchemaDefinition schema, string table)
    {
        int next = schema.CurrentVersion + 1;
        return new SchemaDefinition
        {
            Component = schema.Component,
            CurrentVersion = next,
            Version1Tables = schema.Version1Tables,
            Steps = [.. schema.Steps, new SchemaStep(next, [new EnsureIndexesChange(table)])],
        };
    }

    /// <summary>A new baseline database file from the frozen candidate baseline (SQLite).</summary>
    public static async Task<DatabaseConnectionOptions> NewBaselineAsync(TestDatabases databases, CandidateBaseline.Variant variant)
    {
        DatabaseConnectionOptions connection = await databases.CreateAsync(DatabaseProvider.Sqlite);
        await CandidateBaseline.CreateAsync(SqlitePath(connection), variant);
        return connection;
    }

    /// <summary>Run <c>arcane-db</c> in-process against one database holding every component.</summary>
    public static Task<(int Code, string Out, string Err)> RunCliAsync(DatabaseConnectionOptions single, params string[] args)
        => RunCliAsync(new DatabaseOptions { Provider = single.Provider, ConnectionString = single.ConnectionString }, args);

    public static async Task<(int Code, string Out, string Err)> RunCliAsync(DatabaseOptions options, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await Schema.Upgrade.Cli.DbUpgradeCli.RunAsync(args, options, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>Take the component's schema lock from a separate, unpooled connection, the way another process would.</summary>
    public static async Task<IAsyncDisposable> HoldSchemaLockAsync(DatabaseConnectionOptions options, string component)
    {
        DbConnection connection = options.Provider switch
        {
            DatabaseProvider.Sqlite => new SqliteConnection(options.ConnectionString + ";Pooling=False"),
            DatabaseProvider.PostgreSql => new NpgsqlConnection(options.ConnectionString + ";Pooling=false"),
            _ => new MySqlConnection(options.ConnectionString + ";Pooling=false"),
        };
        await connection.OpenAsync();
        string sql = options.Provider switch
        {
            DatabaseProvider.Sqlite => "BEGIN IMMEDIATE",
            DatabaseProvider.PostgreSql => $"SELECT pg_advisory_lock({SchemaBootstrapper.AdvisoryLockKey(component)})",
            _ => $"SELECT GET_LOCK(SHA1(CONCAT('arcanecore_schema:', DATABASE(), ':{component}')), 0)",
        };
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteScalarAsync();
        }

        return new HeldLock(connection);
    }

    private sealed class HeldLock(DbConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    public static int CountStatements(IEnumerable<string> commands, Regex pattern) => commands.Count(c => pattern.IsMatch(c));

    [GeneratedRegex(@"^\s*CREATE\s+TABLE\b", RegexOptions.IgnoreCase)]
    public static partial Regex CreateTable();

    [GeneratedRegex(@"^\s*CREATE\s+(UNIQUE\s+)?INDEX\b", RegexOptions.IgnoreCase)]
    public static partial Regex CreateIndex();

    [GeneratedRegex(@"^\s*ALTER\s+TABLE\b", RegexOptions.IgnoreCase)]
    public static partial Regex AlterTable();
}

using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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

    public static int CountStatements(IEnumerable<string> commands, Regex pattern) => commands.Count(c => pattern.IsMatch(c));

    [GeneratedRegex(@"^\s*CREATE\s+TABLE\b", RegexOptions.IgnoreCase)]
    public static partial Regex CreateTable();

    [GeneratedRegex(@"^\s*CREATE\s+(UNIQUE\s+)?INDEX\b", RegexOptions.IgnoreCase)]
    public static partial Regex CreateIndex();

    [GeneratedRegex(@"^\s*ALTER\s+TABLE\b", RegexOptions.IgnoreCase)]
    public static partial Regex AlterTable();
}

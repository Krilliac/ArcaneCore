using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MySqlConnector;
using Npgsql;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>Seeding and reading helpers of the <c>arcane-db copy</c> tests.</summary>
internal static partial class DbCopyTestSupport
{
    /// <summary>The table that gets many rows, so a copy crosses several insert batches.</summary>
    public const string BulkTable = "account";

    public static (string Table, string[] Columns)[] ModelTables(DbContext db, SchemaDefinition schema)
        => [.. db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables
            .Where(t => t.Name != schema.VersionTable)
            .Select(t => (t.Name, t.Columns.Select(c => c.Name).ToArray()))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Bootstrap <paramref name="component"/> in a SQLite file and give EVERY table of its model 3 rows (odd rows carry NULL in every nullable
    /// non-key column), <see cref="BulkTable"/> <paramref name="bulkRows"/>: values come from each column's CLR type, so booleans, timestamps,
    /// decimals, blobs, GUIDs and every integer width are all present. The bootstrap's own seed rows stay.
    /// </summary>
    public static async Task SeedAsync(string component, DatabaseConnectionOptions sqlite, int bulkRows)
    {
        await SchemaProbe.EnsureCurrentAsync(component, sqlite);
        await using DbContext db = SchemaProbe.CreateContext(component, sqlite);
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        string version = SchemaProbe.SchemaOf(component).VersionTable;
        await using var connection = new SqliteConnection(sqlite.ConnectionString + ";Pooling=False");
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        foreach (ITable table in model.Tables.Where(t => t.Name != version))
        {
            int rows = table.Name == BulkTable ? bulkRows : 3;
            var keys = new HashSet<string>(table.PrimaryKey?.Columns.Select(c => c.Name) ?? [], StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows; i++)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                var names = new List<string>();
                var placeholders = new List<string>();
                foreach (IColumn column in table.Columns)
                {
                    IProperty property = column.PropertyMappings[0].Property;
                    Type clr = property.GetValueConverter()?.ProviderClrType ?? property.ClrType;
                    bool nullIt = column.IsNullable && i % 2 == 1 && !keys.Contains(column.Name);
                    command.Parameters.AddWithValue("$p" + names.Count, nullIt ? DBNull.Value : Generate(clr, column.Name, i, property.GetMaxLength()));
                    placeholders.Add("$p" + names.Count);
                    names.Add("\"" + column.Name + "\"");
                }

                command.CommandText = $"INSERT INTO \"{table.Name}\" ({string.Join(", ", names)}) VALUES ({string.Join(", ", placeholders)})";
                try
                {
                    await command.ExecuteNonQueryAsync();
                }
                catch (SqliteException ex)
                {
                    throw new InvalidOperationException($"seeding {table.Name} row {i}: {ex.Message}", ex);
                }
            }
        }

        await transaction.CommitAsync();
    }

    private static object Generate(Type type, string column, int i, int? maxLength)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsEnum)
        {
            t = Enum.GetUnderlyingType(t);
        }

        if (t == typeof(bool)) return i % 2 == 0;
        if (t == typeof(byte)) return (byte)(100 + (i % 100));
        if (t == typeof(sbyte)) return (sbyte)(i % 100);
        if (t == typeof(short)) return (short)(2000 + i);
        if (t == typeof(ushort)) return (ushort)(20000 + i);
        if (t == typeof(int)) return 500000 + i;
        if (t == typeof(uint)) return (uint)(500000 + i);
        if (t == typeof(long)) return 5_000_000_000L + i;
        if (t == typeof(ulong)) return (ulong)(5_000_000_000L + i);
        if (t == typeof(float)) return (1.5f * i) + 0.25f;
        if (t == typeof(double)) return (2.5 * i) + 0.125;
        if (t == typeof(decimal)) return (3.25m * i) + 0.5m;
        if (t == typeof(DateTime)) return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i).AddTicks(1234560);
        if (t == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i);
        if (t == typeof(TimeSpan)) return TimeSpan.FromSeconds(i + 1);
        if (t == typeof(Guid)) return new Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        if (t == typeof(byte[])) return new byte[] { (byte)i, 1, 2, 3, (byte)(i >> 8) };
        if (t == typeof(char)) return (char)('a' + (i % 26));
        if (t == typeof(string))
        {
            string s = $"{column}-{i}";
            return maxLength is { } max && s.Length > max ? s[^max..] : s;
        }

        throw new NotSupportedException($"the seeder has no value for {t} (column {column})");
    }

    /// <summary>The row count of every model table of the database (a table that is absent counts -1).</summary>
    public static async Task<Dictionary<string, long>> CountsAsync(string component, DatabaseConnectionOptions connection)
    {
        await using DbContext db = SchemaProbe.CreateContext(component, connection);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string table, _) in ModelTables(db, SchemaProbe.SchemaOf(component)))
        {
            counts[table] = await SchemaCatalog.TableExistsAsync(db, table) ? await UpgradeTestSupport.CountRowsAsync(db, table) : -1;
        }

        return counts;
    }

    /// <summary>
    /// Every row of every model table, each written as one text with the values normalised so the same logical value is the same text on every
    /// engine (integers and booleans as digits, timestamps as UTC microsecond ticks, GUIDs lower case, decimals without trailing zeros, blobs as
    /// hex), the rows of a table sorted. Independent of the copy's own conversion code.
    /// </summary>
    public static async Task<Dictionary<string, string[]>> DumpAsync(string component, DatabaseConnectionOptions connection)
    {
        await using DbContext db = SchemaProbe.CreateContext(component, connection);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await db.Database.OpenConnectionAsync();
        DbConnection raw = db.Database.GetDbConnection();
        foreach ((string table, string[] columns) in ModelTables(db, SchemaProbe.SchemaOf(component)))
        {
            await using DbCommand command = raw.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", columns.Select(c => UpgradeTestSupport.Quote(db, c)))} FROM {UpgradeTestSupport.Quote(db, table)}";
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                var line = new StringBuilder();
                for (int i = 0; i < columns.Length; i++)
                {
                    line.Append(Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i))).Append('|');
                }

                rows.Add(line.ToString());
            }

            rows.Sort(StringComparer.Ordinal);
            result[table] = [.. rows];
        }

        return result;
    }

    private static string Normalize(object? value)
    {
        switch (value)
        {
            case null: return "<null>";
            case bool b: return b ? "1" : "0";
            case byte[] bytes: return "x" + Convert.ToHexString(bytes);
            case DateTime t: return "t" + (t.Ticks / 10).ToString(CultureInfo.InvariantCulture);
            case DateTimeOffset o: return "t" + (o.UtcTicks / 10).ToString(CultureInfo.InvariantCulture);
            case Guid g: return g.ToString("D").ToLowerInvariant();
            case decimal d: return d.ToString("0.#############################", CultureInfo.InvariantCulture);
            case float f: return ((double)f).ToString("R", CultureInfo.InvariantCulture);
            case double f: return f.ToString("R", CultureInfo.InvariantCulture);
            case TimeSpan s: return s.ToString("c", CultureInfo.InvariantCulture);
            case string text:
                if (SqliteDate().IsMatch(text))
                {
                    return "t" + (DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcTicks / 10).ToString(CultureInfo.InvariantCulture);
                }

                if (Guid.TryParseExact(text, "D", out Guid guid))
                {
                    return guid.ToString("D").ToLowerInvariant();
                }

                if (SqliteDecimal().IsMatch(text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
                {
                    return number.ToString("0.#############################", CultureInfo.InvariantCulture);
                }

                return text;
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(\.\d+)?([+-]\d\d:\d\d)?$")]
    private static partial Regex SqliteDate();

    [GeneratedRegex(@"^-?\d+\.\d+$")]
    private static partial Regex SqliteDecimal();

    public static DatabaseConnectionOptions NewSqlite(TestDatabases databases) => databases.CreateAsync(DatabaseProvider.Sqlite).GetAwaiter().GetResult();

    public static string ConnectionFlag(DatabaseConnectionOptions options) => options.ConnectionString;

    public static string ProviderFlag(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => "sqlite",
        DatabaseProvider.MariaDb => "mariadb",
        DatabaseProvider.MySql => "mysql",
        DatabaseProvider.PostgreSql => "postgresql",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>Clear the pooled handles of a connection string whatever the engine.</summary>
    public static void ClearPool(DatabaseConnectionOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProvider.Sqlite:
                TestDatabases.ClearSqlitePool(options.ConnectionString);
                break;
            case DatabaseProvider.PostgreSql:
                using (var pg = new NpgsqlConnection(options.ConnectionString))
                {
                    NpgsqlConnection.ClearPool(pg);
                }

                break;
            default:
                using (var my = new MySqlConnection(options.ConnectionString))
                {
                    MySqlConnection.ClearPool(my);
                }

                break;
        }
    }
}

/// <summary>
/// A theory over server providers: reported as SKIPPED (with the reason) when neither ARCANECORE_TEST_MARIADB nor ARCANECORE_TEST_POSTGRES is set,
/// so a run without a server shows a skip, never a pass and never xunit's "no data" error.
/// </summary>
internal sealed class ServerProviderTheoryAttribute : Xunit.TheoryAttribute
{
    public ServerProviderTheoryAttribute()
    {
        if (!TestDatabases.AvailableProviders().Any(p => (DatabaseProvider)p[0] != DatabaseProvider.Sqlite))
        {
            Skip = $"Set {TestDatabases.MariaDbVariable} or {TestDatabases.PostgresVariable} to run the server-provider copy tests.";
        }
    }
}

using System.Reflection;
using Microsoft.Data.Sqlite;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// A frozen, populated database of the 2026-10-03 integration candidate lineage (auth 2,
/// characters 6, world 6), loaded from checked-in SQL. The DDL and the rows are literals:
/// nothing here is derived from the current model, so a model change cannot move the
/// baseline. Provenance and the capture method are in <c>Baselines/README.md</c>.
/// <para>
/// Nothing was ever released; "released" in the handoff means this candidate lineage
/// (docs/integration/fleet-20261003.md).
/// </para>
/// </summary>
public static class CandidateBaseline
{
    /// <summary>The candidate head the DDL was captured at (git rev-parse 43f1e22).</summary>
    public const string SourceCommit = "43f1e221f582001a1a9b29c05c1eaf935741bb15";

    /// <summary>The versions of the baseline lineage. These are history, not the current schema.</summary>
    public const int AuthVersion = 2;
    public const int CharactersVersion = 6;
    public const int WorldVersion = 6;

    private static readonly string[] IndexesPresentInEveryLineage = ["account", "characters"];

    public enum Variant
    {
        /// <summary>Created at the candidate head: every index the candidate's fresh path made.</summary>
        ReleasedFresh,

        /// <summary>
        /// A database that was upgraded step by step by the historic bootstrapper: no index of a
        /// table created after version 1. The CREATE INDEX statements of every table except
        /// account and characters are removed from the captured DDL.
        /// </summary>
        HistoricUpgraded,
    }

    public static IEnumerable<object[]> Variants() =>
        [[Variant.ReleasedFresh], [Variant.HistoricUpgraded]];

    /// <summary>Create the SQLite file at <paramref name="path"/> with the baseline schema, rows and version rows.</summary>
    public static async Task CreateAsync(string path, Variant variant)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        foreach (string statement in Statements("candidate-2-6-6.sqlite.ddl.sql"))
        {
            if (variant == Variant.HistoricUpgraded && IsDroppedIndex(statement))
            {
                continue;
            }

            await Run(connection, transaction, statement);
        }

        foreach (string statement in Statements("candidate-2-6-6.sqlite.data.sql"))
        {
            await Run(connection, transaction, statement);
        }

        await Run(connection, transaction, $"INSERT INTO \"auth_schema\" (\"Id\", \"Version\") VALUES (1, {AuthVersion})");
        await Run(connection, transaction, $"INSERT INTO \"characters_schema\" (\"Id\", \"Version\") VALUES (1, {CharactersVersion})");
        await Run(connection, transaction, $"INSERT INTO \"world_schema\" (\"Id\", \"Version\") VALUES (1, {WorldVersion})");
        await transaction.CommitAsync();
    }

    /// <summary>Every table of the baseline except the version tables, with its column names.</summary>
    public static async Task<IReadOnlyDictionary<string, string[]>> TablesAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        var tables = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '%\\_schema' ESCAPE '\\' ORDER BY name";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables[reader.GetString(0)] = [];
            }
        }

        foreach (string table in tables.Keys.ToArray())
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info(@t) ORDER BY cid";
            command.Parameters.AddWithValue("@t", table);
            var columns = new List<string>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }

            tables[table] = [.. columns];
        }

        return tables;
    }

    private static bool IsDroppedIndex(string statement)
    {
        if (!statement.StartsWith("CREATE ", StringComparison.Ordinal) || !statement.Contains(" INDEX ", StringComparison.Ordinal))
        {
            return false;
        }

        int on = statement.IndexOf(" ON \"", StringComparison.Ordinal);
        string table = statement[(on + 5)..statement.IndexOf('"', on + 5)];
        return !IndexesPresentInEveryLineage.Contains(table, StringComparer.Ordinal);
    }

    private static IEnumerable<string> Statements(string resource)
    {
        Assembly assembly = typeof(CandidateBaseline).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(resource, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task Run(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

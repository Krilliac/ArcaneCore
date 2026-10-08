using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace ArcaneCore.Data.Tests.Upgrade.CodexLine;

/// <summary>
/// Synthetic databases of the Codex line: the SQLite schema its commit 0e07c29b created (codex-0e07c29b-*.sql, generated
/// by running that commit's SchemaBootstrapper on empty files and reading sqlite_master; identical to the live Codex-line
/// databases of 2026-10-07), optionally rolled back to an earlier Codex version by dropping the objects of the later
/// Codex steps exactly as git show 0e07c29b defines them. Shared with ArcaneCore.World.Tests (linked source and resources),
/// so it uses nothing but Microsoft.Data.Sqlite.
/// </summary>
internal static class CodexLineDatabase
{
    public const int AuthVersion = 4;
    public const int CharactersVersion = 25;
    public const int WorldVersion = 27;

    /// <summary>What each Codex step added, undone newest first to reach an earlier Codex version.</summary>
    private static readonly Dictionary<(string Component, int Version), string[]> s_undo = new()
    {
        [("characters", 25)] = ["DROP TABLE \"managed_playerbot\""],
        [("characters", 24)] =
        [
            "ALTER TABLE \"character_pet\" DROP COLUMN \"Name\"",
            "ALTER TABLE \"character_pet\" DROP COLUMN \"NameTimestamp\"",
            "ALTER TABLE \"character_pet\" DROP COLUMN \"RenameAllowed\"",
        ],
        [("characters", 23)] = ["DROP TABLE \"character_pet_cooldown\""],
        [("characters", 22)] = ["DROP TABLE \"character_item_cooldown_owner\""],
        [("characters", 21)] = ["DROP TABLE \"character_pet\""],
        [("world", 27)] = ["DROP TABLE \"creature_ai_text_template\""],
        [("world", 26)] = ["DROP TABLE \"npc_template_service_metadata\""],
        [("world", 25)] = ["DROP TABLE \"playercreateinfo_skills\""],
        [("world", 24)] = ["DROP TABLE \"spell_enchant_charges\""],
        [("world", 23)] =
        [
            "ALTER TABLE \"creature_template\" DROP COLUMN \"DisplayScale2\"",
            "ALTER TABLE \"creature_template\" DROP COLUMN \"DisplayScale3\"",
            "ALTER TABLE \"creature_template\" DROP COLUMN \"DisplayScale4\"",
        ],
        [("world", 22)] = ["DROP TABLE \"spell_proc_item_enchant\""],
        [("world", 21)] = ["DROP TABLE \"reserved_name\""],
    };

    public static int TipVersion(string component) => component switch
    {
        "auth" => AuthVersion,
        "characters" => CharactersVersion,
        "world" => WorldVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    /// <summary>The DDL of the Codex commit for <paramref name="component"/> (auth, characters, world).</summary>
    public static string Ddl(string component)
    {
        string name = $"CodexLine/codex-0e07c29b-{component}.sql";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string ConnectionString(string path) => $"Data Source={path};Pooling=False";

    /// <summary>Create <paramref name="path"/> as the Codex line left it at <paramref name="version"/> (default: its last version).</summary>
    public static async Task CreateAsync(string path, string component, int? version = null)
    {
        int tip = TipVersion(component);
        int target = version ?? tip;
        if (target > tip || (component != "auth" && target <= 20) || (component == "auth" && target != tip))
        {
            throw new ArgumentOutOfRangeException(nameof(version), $"{component} Codex versions run 21..{tip}");
        }

        await using var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync();
        await ExecuteAsync(connection, Ddl(component));
        for (int v = tip; v > target; v--)
        {
            foreach (string statement in s_undo[(component, v)])
            {
                await ExecuteAsync(connection, statement);
            }
        }

        await ExecuteAsync(connection, $"UPDATE \"{component}_schema\" SET \"Version\" = {target} WHERE \"Id\" = 1");
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync();
        await ExecuteAsync(connection, sql);
    }

    /// <summary>
    /// Insert one row with the given values; every other NOT NULL column without a default gets its type's zero
    /// (INTEGER/REAL 0, TEXT '', BLOB empty), the way a Codex build would have written a mostly-default row.
    /// </summary>
    public static async Task InsertAsync(string path, string table, params (string Column, object Value)[] values)
    {
        await using var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync();
        var columns = new List<(string Name, string Type, bool NotNull, bool HasDefault, bool IntegerKey)>();
        await using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(1), reader.GetString(2).ToUpperInvariant(), reader.GetInt32(3) == 1, !reader.IsDBNull(4),
                    reader.GetInt32(5) == 1 && reader.GetString(2).Equals("INTEGER", StringComparison.OrdinalIgnoreCase)));
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"table {table} does not exist");
        }

        var given = values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string column in given.Keys.Where(c => columns.All(k => !k.Name.Equals(c, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException($"table {table} has no column {column}");
        }

        var names = new List<string>();
        await using SqliteCommand insert = connection.CreateCommand();
        foreach ((string name, string type, bool notNull, bool hasDefault, bool integerKey) in columns)
        {
            object? value;
            if (given.TryGetValue(name, out object? v))
            {
                value = v;
            }
            else if (notNull && !hasDefault && !(integerKey && columns.Count(c => c.IntegerKey) == 1))
            {
                value = type switch
                {
                    "TEXT" => string.Empty,
                    "BLOB" => Array.Empty<byte>(),
                    "REAL" => 0.0,
                    _ => 0L,
                };
            }
            else
            {
                continue;
            }

            string parameter = "@p" + names.Count.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            insert.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
        }

        insert.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) " +
                             $"VALUES ({string.Join(", ", names.Select((_, i) => "@p" + i.ToString(CultureInfo.InvariantCulture)))})";
        await insert.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}

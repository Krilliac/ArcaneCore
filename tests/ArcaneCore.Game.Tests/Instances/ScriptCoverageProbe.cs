using System.Reflection;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Data.Sqlite;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Reads the script facts of a real world database and probes instance scripts behaviourally (test helpers).</summary>
internal static class ScriptCoverageProbe
{
    /// <summary>The world DB named by ARCANECORE_TEST_WORLD_DB, or null (unset or missing).</summary>
    public static string? WorldDbPath()
        => Environment.GetEnvironmentVariable("ARCANECORE_TEST_WORLD_DB") is { Length: > 0 } p && File.Exists(p) ? p : null;

    public sealed record World(CreatureContent Content, uint[] DungeonMaps, uint[] RaidMaps);

    /// <summary>Opens <paramref name="path"/> read-only and immutable (no lock, no sidecar file, never written).</summary>
    public static World Load(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        var dungeons = new List<uint>();
        var raids = new List<uint>();
        foreach ((uint map, long type) in Rows(connection, "SELECT Entry, MapType FROM map_template WHERE MapType IN (1, 2) ORDER BY Entry", r => ((uint)r.GetInt64(0), r.GetInt64(1))))
        {
            (type == 1 ? dungeons : raids).Add(map);
        }

        CreatureSpawn[] spawns = [.. Rows(connection,
            "SELECT s.Guid, s.Entry, s.MapId FROM creature_spawn s JOIN map_template m ON m.Entry = s.MapId WHERE m.MapType IN (1, 2)",
            r => new CreatureSpawn { Guid = (uint)r.GetInt64(0), Entry = (uint)r.GetInt64(1), MapId = (uint)r.GetInt64(2), X = 0, Y = 0, Z = 0 })];
        var entries = spawns.Select(s => s.Entry).ToHashSet();
        CreatureTemplate[] templates = [.. Rows(connection, "SELECT Entry, Name, AIName, ScriptName FROM creature_template", r => new CreatureTemplate
        {
            Entry = (uint)r.GetInt64(0),
            Name = r.IsDBNull(1) ? string.Empty : r.GetString(1),
            AIName = r.IsDBNull(2) ? string.Empty : r.GetString(2),
            ScriptName = r.IsDBNull(3) ? string.Empty : r.GetString(3),
        }).Where(t => entries.Contains(t.Entry))];
        return new World(new CreatureContent(templates, spawns, [], [], []), [.. dungeons], [.. raids]);
    }

    private static List<T> Rows<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> read)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    /// <summary>The InstanceData class declared with [InstanceScript(mapId)], or null.</summary>
    public static Type? InstanceType(uint mapId)
        => typeof(InstanceScriptRegistry).Assembly.GetTypes()
            .FirstOrDefault(t => t.GetCustomAttribute<InstanceScriptAttribute>()?.MapId == mapId && typeof(InstanceData).IsAssignableFrom(t));

    private static readonly Type[] DefaultAis = [typeof(AggressorAI), typeof(ReactorAI), typeof(CritterAI), typeof(GuardAI)];

    /// <summary>
    /// The entries (of <paramref name="entries"/>) that get a script AI when the instance script of <paramref name="mapId"/> runs: the instance is
    /// built in the dungeon test kit (which stands in for the map), each entry is spawned with a template that has no AIName and no ScriptName, and an
    /// entry counts when its creature's AI is not one of the plain default AIs. Behavioural: it sees <c>RegisterEntryAi</c> in any hook and the
    /// raid boss selection. Empty when the map has no instance script.
    /// </summary>
    public static HashSet<uint> ScriptedEntries(uint mapId, IReadOnlyCollection<uint> entries)
    {
        if (InstanceType(mapId) is not { } type || entries.Count == 0) return [];
        uint[] list = [.. entries.Distinct()];
        using var run = new DungeonScriptTestKit(map => (InstanceData)Activator.CreateInstance(type, map)!, list, list, []);
        return [.. list.Where(e => run.Creatures.Creatures.Where(c => c.Entry == e).Any(c => c.AI is { } ai && !DefaultAis.Contains(ai.GetType())))];
    }
}

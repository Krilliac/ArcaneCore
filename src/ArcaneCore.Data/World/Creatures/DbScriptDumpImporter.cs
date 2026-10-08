using System.Globalization;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// Reads the four cmangos ClassicDB DB-script tables and ScriptDev2 script_waypoint. Script steps have the same columns as
/// dbscripts_on_relay and are executed by the same map runner (mangos-classic DBScripts/ScriptMgr.cpp LoadScripts;
/// ScriptDevAI/system/system.cpp LoadScriptWaypoints). This class only parses source rows; storage is handled separately.
/// </summary>
public sealed class DbScriptDumpImporter
{
    private readonly Dictionary<string, IReadOnlyList<string>> _tableRegistry = MySqlDumpReader.NewTableRegistry();
    private readonly Dictionary<(DbScriptKind Kind, uint Id), List<RelayScriptStep>> _scripts = [];
    private readonly Dictionary<(uint Entry, uint PathId, uint Point), CreatureWaypoint> _waypoints = [];
    private readonly HashSet<(uint Entry, uint PathId)> _rejectedPaths = [];

    public IReadOnlyCollection<(DbScriptKind Kind, RelayScriptStep Step)> Scripts
        => [.. _scripts.SelectMany(pair => pair.Value.Select(step => (pair.Key.Kind, step)))];

    public IReadOnlyCollection<(uint Entry, uint PathId, CreatureWaypoint Point)> ScriptWaypoints
        => [.. _waypoints.Where(pair => !_rejectedPaths.Contains((pair.Key.Entry, pair.Key.PathId)))
            .Select(pair => (pair.Key.Entry, pair.Key.PathId, pair.Value))];

    /// <summary>Read a dump; a later input's rows replace every script with the same kind and id.</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var touched = new HashSet<(DbScriptKind Kind, uint Id)>();
        foreach (object item in new MySqlDumpReader(dump, _tableRegistry).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            if (TryKind(row.Table, out DbScriptKind kind))
            {
                uint id = U32(row, "id");
                var key = (kind, id);
                if (touched.Add(key) || !_scripts.TryGetValue(key, out _))
                {
                    _scripts[key] = [];
                }

                List<RelayScriptStep> steps = _scripts[key];
                steps.Add(ParseStep(row, (uint)steps.Count));
            }
            else if (row.Table.Equals("script_waypoint", StringComparison.OrdinalIgnoreCase))
            {
                uint entry = U32(row, "Entry");
                uint pathId = U32(row, "PathId");
                uint point = U32(row, "Point");
                // ScriptDevAI/system/system.cpp LoadScriptWaypoints (:92-96, 113-114): a point 0 rejects the whole path of that entry.
                if (point == 0)
                {
                    _rejectedPaths.Add((entry, pathId));
                    continue;
                }

                if (entry == 0)
                {
                    continue;
                }

                _waypoints[(entry, pathId, point)] = new CreatureWaypoint(
                    point, F32(row, "PositionX"), F32(row, "PositionY"), F32(row, "PositionZ"),
                    F32(row, "Orientation"), U32(row, "WaitTime")) { ScriptId = U32(row, "ScriptId") };
            }
        }
    }

    private static bool TryKind(string table, out DbScriptKind kind)
    {
        kind = table.ToLowerInvariant() switch
        {
            "dbscripts_on_quest_start" => DbScriptKind.QuestStart,
            "dbscripts_on_quest_end" => DbScriptKind.QuestEnd,
            "dbscripts_on_gossip" => DbScriptKind.Gossip,
            "dbscripts_on_event" => DbScriptKind.Event,
            _ => (DbScriptKind)byte.MaxValue,
        };
        return kind != (DbScriptKind)byte.MaxValue;
    }

    private static RelayScriptStep ParseStep(DumpRow row, uint ordinal)
        => new(U32(row, "id"), U32(row, "delay"), U32(row, "priority"), U32(row, "command"),
            U32(row, "datalong"), U32(row, "datalong2"), U32(row, "datalong3"), U32(row, "buddy_entry"),
            U32(row, "search_radius"), U32(row, "data_flags"), I32(row, "dataint"), I32(row, "dataint2"),
            I32(row, "dataint3"), I32(row, "dataint4"), F32(row, "datafloat"), F32(row, "x"), F32(row, "y"),
            F32(row, "z"), F32(row, "o"), F32(row, "speed"), U32(row, "condition_id"), ordinal);

    private static uint U32(DumpRow row, string name)
    {
        if (!row.TryGet(out string? value, name) || string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static int I32(DumpRow row, string name)
    {
        if (!row.TryGet(out string? value, name) || string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static float F32(DumpRow row, string name)
    {
        if (!row.TryGet(out string? value, name) || string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

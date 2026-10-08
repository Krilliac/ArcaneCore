using System.Globalization;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.World.Creatures;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// The <c>map_template</c> columns Map.dbc does not carry (parent, player limit, reset delay, ghost entrance, script name), from a cmangos
/// classic-db dump's <c>instance_template</c> (<c>map, parent, levelMin, levelMax, maxPlayers, reset_delay, ghostEntranceMap,
/// ghostEntranceX, ghostEntranceY, ScriptName, mountAllowed</c>; ObjectMgr::LoadInstanceTemplate) or a vmangos dump's own
/// <c>map_template</c> (<c>entry, patch, parent, map_type, linked_zone, player_limit, reset_delay, ghost_entrance_map/x/y, map_name,
/// script_name</c>, one row per map and content patch; vmangos takes the newest patch up to its last, <see cref="LastPatch"/>, 1.12).
/// cmangos stores the ghost entrance map unsigned, so its "none" is (0, 0, 0): read as vmangos' -1 (Naxxramas, the battlegrounds, the
/// test maps). The dumps are GPL data and are never committed.
/// </summary>
public sealed class InstanceTemplateDumpImporter
{
    /// <summary>vmangos <c>WOW_PATCH_112</c> (Progression.h:76): the content patch of build 5875.</summary>
    public const uint LastPatch = 10;

    private const string ClassicTable = "instance_template";
    private const string VmangosTable = "map_template";

    private readonly SortedDictionary<uint, MapInstanceData> _maps = [];
    private readonly Dictionary<uint, uint> _patches = [];

    /// <summary>Whether any dump read so far carried <c>instance_template</c> or <c>map_template</c>.</summary>
    public bool SawTable { get; private set; }

    /// <summary>The dungeon columns per map id (a later dump's row replaces an earlier one).</summary>
    public IReadOnlyDictionary<uint, MapInstanceData> Maps => _maps;

    /// <summary>Read one dump (call again for further files).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            if (string.Equals(row.Table, ClassicTable, StringComparison.OrdinalIgnoreCase))
            {
                SawTable = true;
                ReadClassic(row);
            }
            else if (string.Equals(row.Table, VmangosTable, StringComparison.OrdinalIgnoreCase))
            {
                SawTable = true;
                ReadVmangos(row);
            }
        }
    }

    private void ReadClassic(DumpRow row)
    {
        uint map = Unsigned(row, ClassicTable, "map");
        int ghostMap = checked((int)Unsigned(row, ClassicTable, "ghostEntranceMap"));
        float ghostX = Float(row, ClassicTable, "ghostEntranceX");
        float ghostY = Float(row, ClassicTable, "ghostEntranceY");
        if (ghostMap == 0 && ghostX == 0f && ghostY == 0f)
        {
            ghostMap = -1;
        }

        _maps[map] = new MapInstanceData(
            Unsigned(row, ClassicTable, "parent"),
            Unsigned(row, ClassicTable, "maxPlayers"),
            Unsigned(row, ClassicTable, "reset_delay"),
            ghostMap, ghostX, ghostY,
            Text(row, "ScriptName"));
    }

    private void ReadVmangos(DumpRow row)
    {
        uint map = Unsigned(row, VmangosTable, "entry");
        uint patch = row.Has("patch") ? Unsigned(row, VmangosTable, "patch") : 0;
        if (patch > LastPatch || (_patches.TryGetValue(map, out uint seen) && seen > patch))
        {
            return;
        }

        _patches[map] = patch;
        _maps[map] = new MapInstanceData(
            Unsigned(row, VmangosTable, "parent"),
            Unsigned(row, VmangosTable, "player_limit", "MaxPlayers"),
            Unsigned(row, VmangosTable, "reset_delay", "ResetDelay"),
            Signed(row, VmangosTable, "ghost_entrance_map", "GhostEntranceMap"),
            Float(row, VmangosTable, "ghost_entrance_x", "GhostEntranceX"),
            Float(row, VmangosTable, "ghost_entrance_y", "GhostEntranceY"),
            Text(row, "script_name", "ScriptName"));
    }

    private static string Raw(DumpRow row, string table, params string[] columns)
        => row.TryGet(out string? raw, columns) && raw is not null
            ? raw
            : throw new ImportSchemaException(table, columns[0], $"table `{table}`: the column `{columns[0]}` is missing or NULL");

    private static uint Unsigned(DumpRow row, string table, params string[] columns)
    {
        string raw = Raw(row, table, columns);
        return uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
            ? value
            : throw new ImportSchemaException(table, columns[0], $"table `{table}`, column `{columns[0]}`: value '{raw}' is not an unsigned number");
    }

    private static int Signed(DumpRow row, string table, params string[] columns)
    {
        string raw = Raw(row, table, columns);
        return int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ImportSchemaException(table, columns[0], $"table `{table}`, column `{columns[0]}`: value '{raw}' is not a number");
    }

    private static float Float(DumpRow row, string table, params string[] columns)
    {
        string raw = Raw(row, table, columns);
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? value
            : throw new ImportSchemaException(table, columns[0], $"table `{table}`, column `{columns[0]}`: value '{raw}' is not a number");
    }

    private static string Text(DumpRow row, params string[] columns)
    {
        string text = row.TryGet(out string? raw, columns) ? raw ?? string.Empty : string.Empty;
        return text.Length <= 128 ? text : throw new ImportSchemaException(VmangosTable, columns[0], $"script name '{text[..32]}...' exceeds 128 characters");
    }
}

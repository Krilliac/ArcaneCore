namespace ArcaneCore.Data.Content.Maps;

/// <summary><c>map_template</c> row (vmangos world DB). Keyed by <see cref="Entry"/>.</summary>
public sealed class MapTemplateRow
{
    public uint Entry { get; set; }
    public uint Parent { get; set; }
    public byte MapType { get; set; }
    public uint LinkedZone { get; set; }
    public uint PlayerLimit { get; set; }
    public uint ResetDelay { get; set; }
    public int GhostEntranceMap { get; set; } = -1;
    public float GhostEntranceX { get; set; }
    public float GhostEntranceY { get; set; }
    public string MapName { get; set; } = string.Empty;
    public string ScriptName { get; set; } = string.Empty;
}

/// <summary><c>area_template</c> row (vmangos world DB, AreaTable.dbc columns). Keyed by <see cref="Entry"/>.</summary>
public sealed class AreaTemplateRow
{
    public uint Entry { get; set; }
    public uint MapId { get; set; }
    public uint ZoneId { get; set; }
    public uint ExploreFlag { get; set; }
    public uint Flags { get; set; }
    public int AreaLevel { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint Team { get; set; }
    public uint LiquidTypeId { get; set; }
}

/// <summary><c>areatrigger_template</c> row (vmangos world DB, AreaTrigger.dbc columns). Keyed by <see cref="Id"/>.</summary>
public sealed class AreaTriggerTemplateRow
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; }
    public float BoxX { get; set; }
    public float BoxY { get; set; }
    public float BoxZ { get; set; }
    public float BoxOrientation { get; set; }
}

/// <summary><c>areatrigger_teleport</c> row (vmangos world DB). Keyed by <see cref="Id"/> (the area trigger).</summary>
public sealed class AreaTriggerTeleportRow
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public byte RequiredLevel { get; set; }
    public uint TargetMap { get; set; }
    public float TargetPositionX { get; set; }
    public float TargetPositionY { get; set; }
    public float TargetPositionZ { get; set; }
    public float TargetOrientation { get; set; }

    /// <summary><c>required_item</c>: an item entry the player must carry (0 = none). Added by world schema step <c>AreaTriggerQuestWorldModule</c>.</summary>
    public uint RequiredItem { get; set; }

    /// <summary><c>required_item2</c>: a second item entry the player must carry (0 = none).</summary>
    public uint RequiredItem2 { get; set; }

    /// <summary><c>required_quest_done</c>: a quest the player must have turned in (0 = none).</summary>
    public uint RequiredQuestDone { get; set; }

    /// <summary>
    /// <c>condition_id</c> (classic-db) or <c>required_condition</c> (vmangos): a <c>conditions</c> table id the player must satisfy
    /// (0 = none). Both dump columns map here.
    /// </summary>
    public uint RequiredCondition { get; set; }
}

/// <summary><c>game_tele</c> row (vmangos/cmangos world DB). Keyed by <see cref="Id"/>.</summary>
public sealed class GameTeleRow
{
    public uint Id { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public float Orientation { get; set; }
    public uint Map { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The <c>map_template</c> columns a dump carries and Map.dbc does not (cmangos <c>instance_template</c>, vmangos <c>map_template</c>):
/// the parent map, the player limit, the reset delay in days, the ghost entrance (map -1: none) and the instance script name.
/// </summary>
public sealed record MapInstanceData(
    uint Parent,
    uint PlayerLimit,
    uint ResetDelay,
    int GhostEntranceMap,
    float GhostEntranceX,
    float GhostEntranceY,
    string ScriptName);

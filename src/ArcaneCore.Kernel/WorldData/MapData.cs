namespace ArcaneCore.Kernel.WorldData;

/// <summary>Kind of map (vmangos <c>map_template.map_type</c>, <c>MapTypes</c> in SharedDefines.h).</summary>
public enum MapType : byte
{
    /// <summary><c>MAP_COMMON</c>: a continent or other shared world map.</summary>
    Common = 0,

    /// <summary><c>MAP_INSTANCE</c>: a dungeon.</summary>
    Instance = 1,

    /// <summary><c>MAP_RAID</c>.</summary>
    Raid = 2,

    /// <summary><c>MAP_BATTLEGROUND</c>.</summary>
    Battleground = 3,
}

/// <summary>
/// One map (vmangos world DB <c>map_template</c>, the server-side replacement of Map.dbc):
/// <c>entry, parent, map_type, linked_zone, player_limit, reset_delay, ghost_entrance_map,
/// ghost_entrance_x, ghost_entrance_y, map_name, script_name</c>.
/// </summary>
public sealed record MapTemplate(
    uint Entry,
    uint Parent,
    MapType MapType,
    uint LinkedZone,
    uint PlayerLimit,
    uint ResetDelay,
    int GhostEntranceMap,
    float GhostEntranceX,
    float GhostEntranceY,
    string Name,
    string ScriptName)
{
    /// <summary>vmangos <c>MapEntry::IsContinent</c>: Eastern Kingdoms (0) or Kalimdor (1).</summary>
    public bool IsContinent => Entry is 0 or 1;

    /// <summary>vmangos <c>MapEntry::IsDungeon</c>: an instance or a raid.</summary>
    public bool IsDungeon => MapType is MapType.Instance or MapType.Raid;

    /// <summary>vmangos <c>MapEntry::IsRaid</c>.</summary>
    public bool IsRaid => MapType == MapType.Raid;

    /// <summary>vmangos <c>MapEntry::IsBattleGround</c>.</summary>
    public bool IsBattleground => MapType == MapType.Battleground;

    /// <summary>vmangos <c>MapEntry::Instanceable</c>: dungeon, raid or battleground.</summary>
    public bool Instanceable => MapType is MapType.Instance or MapType.Raid or MapType.Battleground;
}

/// <summary>
/// One area or zone (vmangos world DB <c>area_template</c>, the replacement of AreaTable.dbc):
/// <c>Entry, MapId, ZoneId, ExploreFlag, Flags, AreaLevel, Name, Team, LiquidTypeId</c>.
/// <see cref="ExploreFlag"/> is the value terrain files store per cell.
/// </summary>
public sealed record AreaTemplate(
    uint Entry,
    uint MapId,
    uint ZoneId,
    uint ExploreFlag,
    uint Flags,
    int AreaLevel,
    string Name,
    uint Team,
    uint LiquidTypeId)
{
    /// <summary>vmangos <c>AreaEntry::IsZone</c>: a zone has no parent zone.</summary>
    public bool IsZone => ZoneId == 0;
}

/// <summary>
/// An area trigger volume (vmangos world DB <c>areatrigger_template</c>, the replacement of
/// AreaTrigger.dbc): a sphere when <see cref="Radius"/> &gt; 0, otherwise a box of
/// <see cref="BoxX"/> × <see cref="BoxY"/> × <see cref="BoxZ"/> turned by <see cref="BoxOrientation"/>.
/// </summary>
public sealed record AreaTriggerTemplate(
    uint Id,
    uint MapId,
    float X,
    float Y,
    float Z,
    float Radius,
    float BoxX,
    float BoxY,
    float BoxZ,
    float BoxOrientation,
    string Name);

/// <summary>
/// Where an area trigger sends a player (world DB <c>areatrigger_teleport</c>:
/// <c>id, name, message, required_level, target_map, target_position_x/y/z,
/// target_orientation</c>) and what a player must have to be sent. The entry requirements are
/// the classic-db / vmangos columns <c>required_item</c>, <c>required_item2</c>,
/// <c>required_quest_done</c> and the conditions-table reference (<c>condition_id</c> in
/// classic-db, <c>required_condition</c> in vmangos); 0 means none. <see cref="Message"/> is
/// shown for any refused requirement when it is not empty
/// (<c>AreaTriggerRequirements</c>, docs/areas/area-triggers.md).
/// </summary>
public sealed record AreaTriggerTeleport(
    uint Id,
    string Name,
    string Message,
    byte RequiredLevel,
    uint TargetMap,
    float TargetX,
    float TargetY,
    float TargetZ,
    float TargetOrientation,
    uint RequiredItem = 0,
    uint RequiredItem2 = 0,
    uint RequiredQuestDone = 0,
    uint RequiredCondition = 0);

/// <summary>A named teleport location for <c>.tele</c> (vmangos/cmangos world DB <c>game_tele</c>).</summary>
public sealed record GameTele(uint Id, float X, float Y, float Z, float Orientation, uint MapId, string Name);

/// <summary>Everything the map layer reads from the world database at startup.</summary>
public sealed record MapContent(
    IReadOnlyList<MapTemplate> Maps,
    IReadOnlyList<AreaTemplate> Areas,
    IReadOnlyList<AreaTriggerTemplate> AreaTriggers,
    IReadOnlyList<AreaTriggerTeleport> AreaTriggerTeleports,
    IReadOnlyList<GameTele> GameTeles)
{
    public static MapContent Empty { get; } = new([], [], [], [], []);
}

/// <summary>Read access to the world database's map tables (docs/areas/grid-terrain.md).</summary>
public interface IMapDataStore
{
    /// <summary>Load every map, area, area trigger and teleport location row.</summary>
    Task<MapContent> LoadAsync(CancellationToken cancellationToken = default);
}

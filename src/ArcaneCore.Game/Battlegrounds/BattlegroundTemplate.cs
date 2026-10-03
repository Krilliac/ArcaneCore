namespace ArcaneCore.Game.Battlegrounds;

/// <summary>A team's start position inside a battleground (vmangos <c>SetTeamStartLoc</c>; the data lane resolves the WorldSafeLocs id).</summary>
public readonly record struct BattlegroundStartLocation(float X, float Y, float Z, float Orientation);

/// <summary>
/// One row of <c>battleground_template</c> as vmangos loads it (BattleGroundMgr.cpp:1332-1399): the player limits, the level
/// range, the mark spells and the start locations. The values come from the database, never from a literal in the rules
/// (the Warsong Gulch minimum level was 20 before client patch 1.8.0 and is 10 in classic-db).
/// </summary>
public sealed record BattlegroundTemplate
{
    public required BattlegroundType Type { get; init; }

    /// <summary>The battleground map (vmangos <c>GetBattleGrounMapIdByTypeId</c>: AV 30, WS 489, AB 529).</summary>
    public required uint MapId { get; init; }

    public required string Name { get; init; }

    public required uint MinPlayersPerTeam { get; init; }

    public required uint MaxPlayersPerTeam { get; init; }

    public required uint MinLevel { get; init; }

    public required uint MaxLevel { get; init; }

    public uint AllianceWinSpell { get; init; }

    public uint AllianceLoseSpell { get; init; }

    public uint HordeWinSpell { get; init; }

    public uint HordeLoseSpell { get; init; }

    public BattlegroundStartLocation AllianceStart { get; init; }

    public BattlegroundStartLocation HordeStart { get; init; }

    public uint PlayerSkinRefLootId { get; init; }

    /// <summary>The queue the type is queued in.</summary>
    public BattlegroundQueueType QueueType => (BattlegroundQueueType)(byte)Type;
}

namespace ArcaneCore.Kernel.WorldData;

/// <summary>
/// One <c>battleground_template</c> row (cmangos classic-db layout: <c>id, MinPlayersPerTeam, MaxPlayersPerTeam, MinLvl, MaxLvl,
/// AllianceStartLoc, HordeStartLoc, StartMaxDist, PlayerSkinReflootId</c>; the start locations are WorldSafeLocs ids). vmangos adds a
/// <c>patch</c> column and the four mark spells; the classic-db row has no spell columns, so they are 0 unless a vmangos dump supplied them.
/// </summary>
public sealed record BattlegroundTemplateRecord(
    uint Id,
    uint MinPlayersPerTeam,
    uint MaxPlayersPerTeam,
    uint MinLevel,
    uint MaxLevel,
    uint AllianceStartLoc,
    uint HordeStartLoc,
    float StartMaxDist,
    uint PlayerSkinRefLootId,
    uint AllianceWinSpell = 0,
    uint AllianceLoseSpell = 0,
    uint HordeWinSpell = 0,
    uint HordeLoseSpell = 0);

/// <summary>
/// One row of <c>creature_battleground</c> or <c>gameobject_battleground</c>: the spawn <paramref name="Guid"/> belongs to the battleground
/// event (<paramref name="Event1"/>, <paramref name="Event2"/>) and is in the world only while that event is active (vmangos
/// <c>BattleGroundMgr::LoadBattleEventIndexes</c>, BattleGroundMgr.cpp:1631-1729).
/// </summary>
public sealed record BattlegroundEventIndex(uint Guid, byte Event1, byte Event2);

/// <summary>One <c>battlemaster_entry</c> row: the creature entry is a battlemaster of the battleground type (vmangos <c>LoadBattleMastersEntry</c>).</summary>
public sealed record BattlemasterRecord(uint CreatureEntry, uint BattlegroundTypeId);

/// <summary>The battleground content of the world database.</summary>
public sealed record BattlegroundContent(
    IReadOnlyList<BattlegroundTemplateRecord> Templates,
    IReadOnlyList<BattlegroundEventIndex> CreatureEvents,
    IReadOnlyList<BattlegroundEventIndex> GameObjectEvents,
    IReadOnlyList<BattlemasterRecord> Battlemasters)
{
    public static BattlegroundContent Empty { get; } = new([], [], [], []);
}

/// <summary>Reads the battleground content (world thread never; the feature loads it once at startup).</summary>
public interface IBattlegroundContentStore
{
    Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default);
}

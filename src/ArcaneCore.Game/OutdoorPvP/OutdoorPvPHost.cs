using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.OutdoorPvP;

/// <summary>A player as an outdoor PvP script sees it: its guid and team.</summary>
public readonly record struct OutdoorPvPPlayer(ObjectGuid Guid, Team Team);

/// <summary>A summon position (vmangos <c>go_type</c> / <c>creature_type</c> rows of the zone scripts).</summary>
public readonly record struct OutdoorPvPSpawn(uint Entry, uint MapId, float X, float Y, float Z, float Orientation);

/// <summary>
/// The capture-point numbers vmangos reads from the GAMEOBJECT_TYPE_CAPTURE_POINT template (GameObjectDefines.h:488-511,
/// ZoneScript.cpp:183-186 <c>SetCapturePointData</c>): radius (data0), the three slider world states (data2, data3, data13),
/// neutralPercent (data12), minTime (data16) and maxTime (data17).
/// </summary>
public readonly record struct CapturePointTemplate(
    float Radius, uint WorldStateDisplay, uint WorldStatePosition, uint WorldStateNeutral, uint NeutralPercent, uint MinTime, uint MaxTime);

/// <summary>
/// The world operations the outdoor PvP scripts need (vmangos ZoneScript / OPvPCapturePoint helpers). The world daemon implements it
/// over the map, spell, quest and creature systems; tests use a recording fake. World thread.
/// </summary>
public interface IOutdoorPvPHost
{
    /// <summary>SMSG_UPDATE_WORLD_STATE to one player (vmangos Player::SendUpdateWorldState).</summary>
    void SendWorldState(ObjectGuid player, uint state, uint value);

    /// <summary>vmangos <c>CastSpell(player, spell, true)</c> on the player itself.</summary>
    void CastOnSelf(ObjectGuid player, uint spellId);

    /// <summary>vmangos <c>RemoveAurasDueToSpell</c>.</summary>
    void RemoveAura(ObjectGuid player, uint spellId);

    bool HasAura(ObjectGuid player, uint spellId);

    /// <summary>
    /// The players within <paramref name="radius"/> of the point for whom outdoor PvP is active (vmangos
    /// <c>AnyPlayerInObjectRangeCheck</c> + <c>Player::IsOutdoorPvPActive</c>, Player.cpp:6720-6724).
    /// </summary>
    IEnumerable<OutdoorPvPPlayer> ActivePlayersNear(uint mapId, float x, float y, float z, float radius);

    /// <summary>The capture-point template of <paramref name="entry"/>, or null when the template is not loaded or not type 29.</summary>
    CapturePointTemplate? CapturePoint(uint entry);

    /// <summary>vmangos <c>Map::SummonGameObject</c> (no despawn). Null when the template is missing or the map is not loaded.</summary>
    /// <remarks><paramref name="spawnedByDefault"/>: vmangos <c>SetSpawnedByDefault(true)</c>, so a use resets it instead of deleting it.</remarks>
    ObjectGuid? SummonObject(OutdoorPvPSpawn spawn, bool spawnedByDefault = false);

    void RemoveObject(ObjectGuid guid);

    /// <summary>vmangos <c>SetGoArtKit</c> + <c>SendGameObjectCustomAnim</c>, only when the art kit changes.</summary>
    void SetBannerArt(ObjectGuid guid, uint artKit, uint animation);

    /// <summary>vmangos <c>GameObject::PlayDirectSound</c>.</summary>
    void PlayObjectSound(ObjectGuid guid, uint soundId);

    /// <summary>
    /// vmangos <c>Map::SummonCreature(..., TEMPSUMMON_MANUAL_DESPAWN)</c>. <paramref name="faction"/> 0 keeps the template's;
    /// <paramref name="aura"/> 0 adds none (the flight master's and the Spirit of Victory's particle auras).
    /// </summary>
    ObjectGuid? SummonCreature(OutdoorPvPSpawn spawn, uint faction = 0, uint aura = 0);

    void RemoveCreature(ObjectGuid guid);

    /// <summary>
    /// vmangos <c>Creature::JoinCreatureGroup(leader, ATTACK_DISTANCE, leader-&gt;GetAngle(member) - member-&gt;GetOrientation(),
    /// OPTION_FORMATION_MOVE | OPTION_AGGRO_TOGETHER | OPTION_EVADE_TOGETHER)</c>: the member follows the leader at that slot and fights
    /// and evades with the group. Nothing when either is gone.
    /// </summary>
    void JoinCreatureGroup(ObjectGuid member, ObjectGuid leader);

    /// <summary>
    /// vmangos <c>MotionMaster::Clear(false, true)</c> then <c>MoveWaypoint(0, PATH_FROM_SPECIAL, 1000, 0, pathId, false)</c>. ArcaneCore keeps
    /// no creature_movement_special table; the path is looked up as the creature entry's own path 0 (creature_movement_template), which is
    /// where cmangos-format data keeps it. False when the creature or the path is missing.
    /// </summary>
    bool StartSpecialPath(ObjectGuid creature, uint pathId);

    /// <summary>vmangos <c>pCreature-&gt;CastSpell(pCreature, spell, false)</c>.</summary>
    void CreatureCastOnSelf(ObjectGuid creature, uint spellId);

    /// <summary>vmangos <c>Map::SendDefenseMessage</c> (Map.cpp:1869-1885): the broadcast text to every player of the map.</summary>
    void SendDefenseMessage(uint mapId, uint zoneId, uint broadcastTextId);

    /// <summary>vmangos <c>World::SendZoneText</c>: a system message to every player in the zone.</summary>
    void SendZoneText(uint zoneId, string text);

    /// <summary>vmangos DoSilithystYell: the nearest creature of <paramref name="entry"/> to the player says the broadcast text.</summary>
    void NearestCreatureSays(ObjectGuid player, uint entry, uint broadcastTextId);

    /// <summary>vmangos <c>Player::KilledMonsterCredit</c>.</summary>
    void KilledMonsterCredit(ObjectGuid player, uint creatureEntry);

    /// <summary>The distance from the player to a point, or null when the player is not online on that map.</summary>
    float? DistanceTo(ObjectGuid player, uint mapId, float x, float y, float z);
}

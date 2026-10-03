namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// The part of a character that changes while it is in the world, captured on the world
/// thread and persisted asynchronously (logout, disconnect, autosave, shutdown).
/// <see cref="ActionButtons"/> is null when the action bar did not change since the last save;
/// <see cref="Home"/> is null when the bind point is not to be written.
/// <see cref="Inventory"/> is null when no item changed since the last save (items area).
/// <see cref="Life"/> is complete when present (health, power, experience, death state) and null
/// only for a state that does not describe a live player (the stored life is then left alone).
/// </summary>
public sealed record CharacterState(
    int Id,
    uint MapId,
    uint ZoneId,
    float X,
    float Y,
    float Z,
    float Orientation,
    byte Level,
    uint PlayedTime,
    uint LevelPlayedTime = 0,
    uint Money = 0,
    byte ActionBarToggles = 0,
    IReadOnlyList<ActionButton>? ActionButtons = null,
    HomeBind? Home = null,
    Items.InventorySnapshot? Inventory = null,
    CharacterLife? Life = null);

/// <summary>A hearthstone bind point (vmangos character_homebind; sent in SMSG_BINDPOINTUPDATE).</summary>
public readonly record struct HomeBind(uint MapId, uint ZoneId, float X, float Y, float Z)
{
    /// <summary>
    /// True for the all-zero value of a character created before characters schema v2, which
    /// has no bind point yet; the race/class start position is used instead (vmangos
    /// Player::_LoadHomeBind falls back to playercreateinfo the same way).
    /// </summary>
    public bool IsUnset => MapId == 0 && ZoneId == 0 && X == 0 && Y == 0 && Z == 0;
}

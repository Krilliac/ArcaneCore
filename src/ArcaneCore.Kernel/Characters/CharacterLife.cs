namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// The part of a character's life that survives a logout: current health and power, experience,
/// the recent-death window and the ghost state with its corpse. vmangos keeps these in the
/// <c>characters</c> row (health, power1-5, xp, death_expire_time, the ghost flag) and in the
/// <c>corpse</c> table; ArcaneCore keeps them in their own tables (<c>character_vitals</c>,
/// <c>character_corpse</c>) so the shared characters row is untouched.
/// </summary>
/// <param name="Health">UNIT_FIELD_HEALTH at save time (Player.cpp:16476 saves GetHealth()).</param>
/// <param name="Powers">UNIT_FIELD_POWER1..5 (mana, rage, focus, energy, happiness; vmangos MAX_POWERS = 5), always five values.</param>
/// <param name="Xp">PLAYER_XP.</param>
/// <param name="DeathExpireUnix">Unix second until which recent deaths count (vmangos m_deathExpireTime); 0 = none.</param>
/// <param name="IsGhost">The character is a released spirit (PLAYER_FLAGS_GHOST).</param>
/// <param name="Corpse">The released body, null while the character is alive or has no body.</param>
public sealed record CharacterLife(
    uint Health,
    IReadOnlyList<uint> Powers,
    uint Xp,
    long DeathExpireUnix,
    bool IsGhost,
    CorpseSnapshot? Corpse);

/// <summary>
/// A player's released body (vmangos <c>corpse</c> row: position, map, time, type). The ghost
/// time is a Unix second (vmangos Corpse::m_time); <paramref name="Type"/> is the
/// <c>CorpseType</c> value (1 = resurrectable PvE, 2 = resurrectable PvP).
/// </summary>
public sealed record CorpseSnapshot(uint MapId, float X, float Y, float Z, float Orientation, long GhostTimeUnix, byte Type);

/// <summary>Read side of <see cref="CharacterLife"/> (the write side is <see cref="CharacterState.Life"/>).</summary>
public interface ICharacterLifeStore
{
    /// <summary>The stored life of a character, or null when it was never saved with one (a fresh character).</summary>
    Task<CharacterLife?> LoadAsync(int characterId, CancellationToken cancellationToken = default);
}

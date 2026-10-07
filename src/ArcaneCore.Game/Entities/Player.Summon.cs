namespace ArcaneCore.Game.Entities;

/// <summary>Where a summon offered to a player leads and until when it may be accepted (vmangos Player::SetSummonPoint, Player.h:1809-1816).</summary>
public readonly record struct PendingSummon(ObjectGuid Summoner, uint MapId, float X, float Y, float Z, long ExpiresAtMs);

// The summon request of SPELL_EFFECT_SUMMON_PLAYER (Ritual of Summoning): vmangos m_summon_expire / m_summon_mapid / m_summon_x..z.
public sealed partial class Player
{
    /// <summary>vmangos MAX_PLAYER_SUMMON_DELAY (Player.h:655): a summon may be accepted for two minutes.</summary>
    public const int SummonAcceptMs = 2 * 60 * 1000;

    /// <summary>The summon offered to this player, or null (world thread).</summary>
    public PendingSummon? PendingSummon { get; set; }
}

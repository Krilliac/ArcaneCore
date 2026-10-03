using ArcaneCore.Game.Combat;

namespace ArcaneCore.Game.Entities;

public sealed partial class Player
{
    /// <summary>
    /// This player's half of a pending or running duel, or null (vmangos Player::m_duel). Runtime only: neither reference
    /// persists it (vmangos clears it at load, Player.cpp:14945-14947). Owned by the duel service; world thread only.
    /// </summary>
    public DuelInfo? Duel { get; internal set; }

    /// <summary>PLAYER_DUEL_ARBITER (0xBC, 2 words, public): the duel flag object's GUID while a duel is requested or running.</summary>
    public ulong DuelArbiter
    {
        get => GetUInt64(UpdateFields.PlayerDuelArbiter);
        internal set => SetUInt64(UpdateFields.PlayerDuelArbiter, value);
    }

    /// <summary>PLAYER_DUEL_TEAM (0xC4, public): 1 or 2 while a duel is running, else 0. The 1.12 client reads it with the arbiter for hostility.</summary>
    public uint DuelTeam
    {
        get => GetUInt32(UpdateFields.PlayerDuelTeam);
        internal set => SetUInt32(UpdateFields.PlayerDuelTeam, value);
    }
}

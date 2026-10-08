namespace ArcaneCore.Game.Entities;

// vmangos Player::m_personalXpRate (Player.cpp:113, Player.h:1465-1466): set by .modify xprate, not saved.
public sealed partial class Player
{
    /// <summary>
    /// The player's own experience multiplier (vmangos m_personalXpRate), or a negative value when none was set (the constructor's -1). Player::GiveXP
    /// multiplies every gain by it when it is not negative (Player.cpp:3018-3019). Kept for the session only, as vmangos does (it is never saved).
    /// </summary>
    public float PersonalXpRate { get; set; } = -1.0f;
}

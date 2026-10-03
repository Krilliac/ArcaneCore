using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Kernel.WorldData;

/// <summary>
/// The action bar a new character starts with (vmangos <c>playercreateinfo_action</c>, loaded by
/// <c>ObjectMgr::LoadPlayerInfo</c>, ObjectMgr.cpp:4737-4796, and written by
/// <c>MasterPlayer::Create</c>, MasterPlayer.cpp:30-39).
/// </summary>
public interface IStartActionSource
{
    /// <summary>The buttons of a race and class pair, ordered by button; empty when the table has none for it.</summary>
    Task<IReadOnlyList<ActionButton>> GetAsync(byte race, byte cls, CancellationToken cancellationToken = default);
}

/// <summary>
/// The static half of vmangos <c>Player::IsActionButtonDataValid</c> (Player.cpp:5900-5933): the
/// button and action ranges. Whether the spell or item exists is checked against the loaded
/// templates where they are known (the creation hook).
/// </summary>
public static class StartActionRules
{
    /// <summary>vmangos MAX_ACTION_BUTTONS (Player.h): buttons 0..119.</summary>
    public const int MaxActionButtons = 120;

    /// <summary>vmangos MAX_ACTION_BUTTON_ACTION_VALUE (0x00FFFFFF+1, Player.h:144): an action must be below it.</summary>
    public const uint MaxActionValue = 0x01000000;

    /// <summary>ACTION_BUTTON_SPELL.</summary>
    public const byte TypeSpell = 0;

    /// <summary>ACTION_BUTTON_MACRO.</summary>
    public const byte TypeMacro = 64;

    /// <summary>ACTION_BUTTON_ITEM.</summary>
    public const byte TypeItem = 128;

    public static bool IsInRange(byte button, uint action) => button < MaxActionButtons && action < MaxActionValue;
}

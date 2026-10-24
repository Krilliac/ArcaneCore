using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.WorldState.Events;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// The battleground weekend calendar (<see cref="IBattlegroundCalendar"/>) read from the live game-event state, as vmangos
/// <c>BattleGroundMgr::IsBgWeekend</c> reads <c>IsActiveHoliday</c> (BattleGroundMgr.cpp:1613): a type's weekend is on while its
/// "Call to Arms" event runs. In classic-db those are events 18 (Alterac Valley), 19 (Warsong Gulch) and 20 (Arathi Basin) with the
/// client holiday ids 283, 284 and 285. The state is looked up on every call, so attach order and an event reload do not matter; with
/// no game-event state (the feature is absent) no weekend is on. World thread.
/// </summary>
internal sealed class GameEventBattlegroundCalendar(Func<IGameEventState?> events) : IBattlegroundCalendar
{
    /// <summary>Client holiday id of "Call to Arms: Alterac Valley".</summary>
    public const uint AlteracValleyHoliday = 283;

    /// <summary>Client holiday id of "Call to Arms: Warsong Gulch".</summary>
    public const uint WarsongGulchHoliday = 284;

    /// <summary>Client holiday id of "Call to Arms: Arathi Basin".</summary>
    public const uint ArathiBasinHoliday = 285;

    public bool IsBattlegroundWeekend(BattlegroundType type)
    {
        uint holiday = type switch
        {
            BattlegroundType.AlteracValley => AlteracValleyHoliday,
            BattlegroundType.WarsongGulch => WarsongGulchHoliday,
            BattlegroundType.ArathiBasin => ArathiBasinHoliday,
            _ => 0,
        };

        return holiday != 0 && events()?.IsActiveHoliday(holiday) == true;
    }
}

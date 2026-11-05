using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots.Groups;

namespace ArcaneCore.World.Playerbots;

public sealed partial class ManagedPlayerbotFeature
{
    /// <summary>The bots' summons (Ritual of Summoning, the meeting stone, accepting): one state for the whole feature; world thread only.</summary>
    private readonly PlayerbotSummons _summons = new();

    /// <summary>The summons seam of the think (<see cref="PlayerbotSummons.Drive"/>); a fault is recorded and the bot goes on with its driver.</summary>
    internal PlayerbotSummons Summons => _summons;

    /// <summary>
    /// True when the bot holds still for a summon (it is the warlock channelling the ritual, a helper at the portal or a bot using a meeting stone):
    /// no other driver thinks for it this tick. Also where a bot accepts a summon request and where a summon begins.
    /// </summary>
    private bool SummonHolds(ActiveBot active, Player driven, uint sinceLast)
    {
        try
        {
            return _summons.Drive(active.Session, driven, _options, coordinator?.GroupOf(active.Record.BotId)?.State, sinceLast);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.summons", ex);
            return false;
        }
    }
}

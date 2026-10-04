using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Reputation;

public sealed partial class ReputationService
{
    /// <summary>
    /// Raised on the world thread for every faction whose standing was set on a live player (the main faction and each
    /// spillover target), after the client was told: vmangos Player::ReputationChanged (Player.cpp:14239-14264) completes or
    /// reverts the reputation-objective quests. Not raised for staged (not yet published) changes.
    /// </summary>
    public event Action<Player, uint>? ReputationChanged;

    private void RaiseChanged(Player player, PlayerReputation rep)
    {
        foreach (uint faction in rep.TakeChangedFactions())
        {
            ReputationChanged?.Invoke(player, faction);
        }
    }
}

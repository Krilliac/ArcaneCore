using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.WorldState.Exploration;

/// <summary>vmangos <c>Player::CheckAreaExploreAndOutdoor</c> (Player.cpp:6089-6204), called by the zone tracker when a player moved. World thread.</summary>
public interface IExplorationChecker
{
    void CheckAreaExplore(Player player);
}

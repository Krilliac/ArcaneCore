using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Graveyards;

/// <summary>
/// A system that decides the graveyard of a spirit itself, instead of the zone links: battlegrounds (vmangos
/// <c>Player::RepopAtGraveyard</c> asks <c>BattleGround::GetClosestGraveYard</c> for a player in one, Player.cpp:4999-5004).
/// <see cref="GraveyardRepopService"/> asks the overrides in registration order for a player that is in a battleground map and
/// leaves the spirit where it is when none answers; the battleground area registers its implementation. Nothing in this lane
/// implements it.
/// </summary>
public interface IGraveyardOverride
{
    /// <summary>
    /// Whether this override handles <paramref name="player"/>. When it does, <paramref name="graveyard"/> is the safe location to
    /// send the spirit to (null: stay where it is).
    /// </summary>
    bool TryChoose(Player player, out WorldSafeLoc? graveyard);
}

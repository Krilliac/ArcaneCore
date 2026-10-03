using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.WorldState.Exploration;

/// <summary>
/// Told that a player's explored-zones words changed (a discovery, a GM command) so they can be
/// persisted (vmangos saves them with the character, Player.cpp:16481; ArcaneCore also writes on
/// discovery so a crash does not lose a map reveal). World thread; must not block.
/// </summary>
public interface IExploredZonesSink
{
    void Changed(Player player);
}

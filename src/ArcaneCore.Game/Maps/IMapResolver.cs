using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// Chooses which instance of a map a player enters — the seam between the map registry of
/// <see cref="WorldRuntime"/> (keyed by map and instance) and the instance/bind rules
/// (docs/integration/instances.md). Install one with <see cref="WorldRuntime.MapResolver"/>.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public interface IMapResolver
{
    /// <summary>
    /// The map a player logging in on <see cref="WorldObject.MapId"/> enters. May relocate the
    /// player first (vmangos <c>Player::LoadFromDB</c>: an instance reset meanwhile sends the
    /// player to the entrance). Must return a map.
    /// </summary>
    Map ResolveLoginMap(Player player);

    /// <summary>
    /// Checks that do not need the destination map yet, run when a far teleport starts
    /// (vmangos <c>MapManager::CanPlayerEnter</c> + <c>Map::CanEnter</c> of an existing map).
    /// Returns false — after telling the client why — to refuse the teleport.
    /// </summary>
    bool CanEnter(Player player, uint mapId);

    /// <summary>
    /// The map instance a far-teleported player enters once its client confirmed the new world
    /// (vmangos <c>MapManager::CreateInstance</c> + <c>DungeonMap::Add</c>: binds the player or its
    /// group). Null — after telling the client why — refuses the entry; the teleport then returns
    /// the player to where it started.
    /// </summary>
    Map? ResolveEntry(Player player, uint mapId);

    /// <summary>The player has entered <paramref name="map"/> and has the full login packets (raid welcome etc.).</summary>
    void OnEntered(Player player, Map map);
}

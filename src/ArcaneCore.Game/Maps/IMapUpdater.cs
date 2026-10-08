using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Updates;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// A per-map simulation system (creatures today; game objects, dynamic objects, …) driven by
/// <see cref="Map.Update"/> — the place vmangos' <c>Map::Update</c> visits its active grids and
/// updates their non-player objects. Attach with <see cref="Map.AddUpdater"/>.
/// <para>Thread affinity: world thread. Updaters run after the players' packets and timers and
/// before the visibility/values/flush phases, so the blocks they queue go out in the same tick.</para>
/// </summary>
public interface IMapUpdater
{
    /// <summary>Advance the system by <paramref name="diffMs"/> (world thread).</summary>
    void Update(Map map, uint diffMs);

    /// <summary>
    /// <paramref name="player"/> is leaving the map; its client state (visible set) is about to be
    /// cleared, so drop any per-player tracking (world thread).
    /// </summary>
    void OnPlayerRemoved(Map map, Player player);

    /// <summary>
    /// <paramref name="player"/> is entering the map: it is registered, but its own create block has not been queued yet, so
    /// a packet sent here reaches the client before it (vmangos <c>Map::Add</c> runs <c>SendInitTransports</c> before
    /// <c>SendInitSelf</c>). World thread; nothing by default.
    /// </summary>
    void OnPlayerAdding(Map map, Player player)
    {
    }

    /// <summary>
    /// <paramref name="player"/>'s own create packet is being written, after <see cref="OnPlayerAdding"/>: blocks queued on
    /// <paramref name="selfPacket"/> here come ahead of the player's items and itself in the same packet (vmangos
    /// <c>Map::SendInitSelf</c> puts the player's transport first and sets <see cref="UpdateData.HasTransport"/>). World
    /// thread; nothing by default.
    /// </summary>
    void OnWritingSelf(Map map, Player player, UpdateData selfPacket)
    {
    }
}

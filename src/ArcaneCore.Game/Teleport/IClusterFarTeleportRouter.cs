using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Teleport;

/// <summary>
/// The seam by which <see cref="TeleportService"/> hands a far teleport to a map this worker does not own (Cluster:Worker:OwnedMaps) to the
/// cluster transfer instead of a local map change. The World host implements it on top of the player transfer coordinator: it resolves the
/// owning worker (static map ownership, or the instance binding with capacity in <c>ClusterMapPlacementTable</c>) and starts the transfer. The
/// target worker then sends SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD and completes on MSG_MOVE_WORLDPORT_ACK
/// (<see cref="TeleportService.BeginClusterArrival"/>), so the client sees the retail far-teleport sequence exactly once.
/// <para>
/// Every source path (hearthstone, summon, portals, GM tele and worldport, spirit release, dungeon and battleground entry, continent
/// transports) reaches <see cref="TeleportService.TeleportTo(Player, uint, float, float, float, float, TeleportOptions)"/>, so this is the single
/// hook. A destination this worker owns never reaches the router; nothing is ever transferred mid-map.
/// </para>
/// <para>Thread affinity: called on the world thread. Implementations must not block it; the transfer itself runs asynchronously.</para>
/// </summary>
public interface IClusterFarTeleportRouter
{
    /// <summary>
    /// Starts a transfer of <paramref name="player"/> to <paramref name="destination"/> on another worker. Returns true when the transfer was
    /// accepted (it now runs on its own; the player stays where it is until the source freezes it) and false when it was refused (no eligible
    /// worker, worker or instance at capacity, a transfer already in flight): nothing has changed and the teleport fails.
    /// <paramref name="options"/> are the teleport options of the request (a passenger that stays aboard, for example).
    /// </summary>
    bool TryBeginTransfer(Player player, TeleportDestination destination, TeleportOptions options);
}

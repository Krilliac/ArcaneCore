using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Teleport;

/// <summary>
/// The seam by which <see cref="TeleportService"/> hands a far teleport to a map this worker does not own (Cluster:Worker:OwnedMaps) to the
/// cluster transfer instead of a local map change. The World host implements it on top of the player transfer coordinator: it resolves the
/// owning worker (static map ownership, or the instance binding with capacity in <c>ClusterMapPlacementTable</c>) and starts the transfer. The
/// target worker then sends SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD and completes on MSG_MOVE_WORLDPORT_ACK
/// (<see cref="TeleportService.BeginClusterArrival"/>), so the client sees the retail far-teleport sequence exactly once.
/// <para>
/// Every source path that ends in <see cref="TeleportService.TeleportTo(Player, uint, float, float, float, float, TeleportOptions)"/>
/// (hearthstone, summon, portals, GM tele and worldport, spirit release, dungeon entry) reaches this hook. A destination this worker owns
/// never reaches the router. Not covered: a ship or zeppelin whose next map is unowned. <c>TransportSystem.TeleportTransport</c> holds the
/// ship before it calls <c>TeleportTo</c>, so the router never sees it; moving a ship and its passengers across workers is not routed here.
/// Battleground entry is gated locally by <see cref="TeleportService.BattlegroundEntryAllowed"/> before the router is asked.
/// </para>
/// <para>
/// Thread affinity: called on the world thread, possibly inside a map update. Implementations must not block it, and must defer anything
/// that touches map state; the transfer itself runs asynchronously. The player is taken off its transport and its movement reset only
/// after this returns true, so a snapshot taken inside the call still sees the transport and the moving flags. The ghost revive for a
/// dungeon holding its corpse runs after acceptance too.
/// </para>
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

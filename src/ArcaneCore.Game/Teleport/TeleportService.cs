using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Teleport;

/// <summary>A teleport destination (vmangos <c>WorldLocation m_teleportDest</c>).</summary>
public readonly record struct TeleportDestination(uint MapId, float X, float Y, float Z, float Orientation);

/// <summary>The stage of a pending teleport.</summary>
public enum TeleportStage
{
    /// <summary>Same-map teleport: MSG_MOVE_TELEPORT_ACK sent, waiting for the client's ack (vmangos <c>mSemaphoreTeleport_Near</c>).</summary>
    Near,

    /// <summary>Different-map teleport scheduled for the end of the current map update (vmangos <c>ScheduleFarTeleport</c>).</summary>
    FarScheduled,

    /// <summary>SMSG_NEW_WORLD sent, the player is in no map, waiting for MSG_MOVE_WORLDPORT_ACK (vmangos <c>mSemaphoreTeleport_Far</c>).</summary>
    Far,

    /// <summary>MSG_MOVE_WORLDPORT_ACK received; the player enters the new map at the start of the next tick.</summary>
    Arriving,
}

/// <summary>
/// Teleports (vmangos <c>Player::TeleportTo</c>, <c>ExecuteTeleportFar</c>,
/// <c>ExecuteTeleportNear</c>, <c>WorldSession::HandleMoveWorldportAckOpcode</c>).
/// <para>
/// A same-map teleport sends MSG_MOVE_TELEPORT_ACK and moves the player when the client acks.
/// A different-map teleport, after the current map update, clears the selection, sends
/// SMSG_TRANSFER_PENDING, takes the player out of its map and sends SMSG_NEW_WORLD; the
/// client's MSG_MOVE_WORLDPORT_ACK puts it into the new map with the login packets (the world
/// daemon supplies them through the two callbacks).
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class TeleportService
{
    private readonly WorldRuntime _world;
    private readonly Action<Player> _beforeAddToMap;
    private readonly Action<Player> _afterAddToMap;
    private readonly ILogger _logger;
    private readonly Dictionary<ObjectGuid, Pending> _pending = [];

    /// <param name="world">The world whose players this service teleports.</param>
    /// <param name="beforeAddToMap">Sends the packets that precede the player's create block on a new map (vmangos SendInitialPacketsBeforeAddToMap).</param>
    /// <param name="afterAddToMap">Sends the packets that follow it (vmangos SendInitialPacketsAfterAddToMap).</param>
    /// <param name="logger">Diagnostics.</param>
    public TeleportService(WorldRuntime world, Action<Player> beforeAddToMap, Action<Player> afterAddToMap, ILogger? logger = null)
    {
        _world = world;
        _beforeAddToMap = beforeAddToMap;
        _afterAddToMap = afterAddToMap;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Number of players with a teleport in progress.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>vmangos <c>Player::IsBeingTeleported</c>.</summary>
    public bool IsBeingTeleported(Player player) => _pending.ContainsKey(player.Guid);

    /// <summary>vmangos <c>Player::IsBeingTeleportedNear</c>.</summary>
    public bool IsBeingTeleportedNear(Player player) => StageOf(player) == TeleportStage.Near;

    /// <summary>vmangos <c>Player::IsBeingTeleportedFar</c> (scheduled, sent or arriving).</summary>
    public bool IsBeingTeleportedFar(Player player) => StageOf(player) is TeleportStage.FarScheduled or TeleportStage.Far or TeleportStage.Arriving;

    /// <summary>The stage of the player's teleport, or null when none is in progress.</summary>
    public TeleportStage? StageOf(Player player) => _pending.TryGetValue(player.Guid, out Pending? p) ? p.Stage : null;

    /// <summary>The destination of the player's teleport (vmangos <c>GetTeleportDest</c>).</summary>
    public TeleportDestination? DestinationOf(Player player) => _pending.TryGetValue(player.Guid, out Pending? p) ? p.Destination : null;

    /// <summary>
    /// Start a teleport (vmangos <c>Player::TeleportTo</c>). Fails — returns false and changes
    /// nothing — for invalid coordinates (<c>MapManager::IsValidMapCoord</c>), a map that is not
    /// in the registry, a battleground map (entered only through the battleground system, which
    /// ArcaneCore has not got yet), a player in no map, or a far teleport already under way.
    /// </summary>
    public bool TeleportTo(Player player, uint mapId, float x, float y, float z, float orientation)
    {
        if (!GridDefines.IsValidMapCoord(x, y, z, orientation))
        {
            _logger.LogWarning("teleport of {Player} to invalid coordinates {X} {Y} {Z} {O} on map {MapId}", player.Name, x, y, z, orientation, mapId);
            return false;
        }

        WorldMaps maps = WorldMaps.Of(_world);
        MapTemplate? target = maps.Registry.Find(mapId);
        if (target is null)
        {
            _logger.LogWarning("teleport of {Player} to unknown map {MapId}", player.Name, mapId);
            return false;
        }

        if (target.IsBattleground)
        {
            return false;
        }

        Map? current = player.Map;
        if (current is null || IsBeingTeleportedFar(player))
        {
            return false;
        }

        // vmangos TeleportTo: reset the client time stamp and stop movement, leave any transport.
        ResetMovementForTeleport(player);

        var destination = new TeleportDestination(mapId, x, y, z, orientation);
        if (current.MapId == mapId)
        {
            _pending[player.Guid] = new Pending(destination, TeleportStage.Near, current, default);
            MovementInfo moved = player.Movement;
            moved.X = x;
            moved.Y = y;
            moved.Z = z;
            moved.Orientation = orientation;
            moved.Time = _world.NowMs;
            player.Session.Send(
                WorldOpcode.MsgMoveTeleportAck,
                TeleportPackets.BuildMoveTeleportAck(player.Guid, player.NextMovementCounter(), moved));
            return true;
        }

        // Instance creation stub: dungeons and raids get (or reuse) an instance id and binding.
        maps.Instances.GetOrCreateInstance(player, target);
        var pending = new Pending(destination, TeleportStage.FarScheduled, current, new TeleportDestination(player.MapId, player.X, player.Y, player.Z, player.Orientation));
        _pending[player.Guid] = pending;
        current.RunAfterUpdate(() => ExecuteTeleportFar(player, pending));
        return true;
    }

    /// <summary>Teleport the player to its hearthstone bind point (vmangos <c>Player::TeleportToHomebind</c>).</summary>
    public bool TeleportToHomebind(Player player) =>
        TeleportTo(player, player.Home.MapId, player.Home.X, player.Home.Y, player.Home.Z, player.Orientation);

    /// <summary>
    /// The client acknowledged a same-map teleport (vmangos <c>HandleMoveTeleportAckOpcode</c> →
    /// <c>ExecuteTeleportNear</c>). Ignored unless a near teleport is pending and the GUID is
    /// the player's. Observers around the old and the new position get MSG_MOVE_TELEPORT.
    /// </summary>
    public bool HandleTeleportAck(Player player, ulong moverGuid)
    {
        if (!_pending.TryGetValue(player.Guid, out Pending? pending) || pending.Stage != TeleportStage.Near)
        {
            return false;
        }

        if (moverGuid != player.Guid.Value)
        {
            return false;
        }

        _pending.Remove(player.Guid);
        Map? map = player.Map;
        if (map is null || !ReferenceEquals(map, pending.SourceMap))
        {
            return false;
        }

        TeleportDestination dest = pending.Destination;
        SendTeleportToObservers(map, player, dest);
        player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
        SendTeleportToObservers(map, player, dest);
        UpdateZone(map, player);
        player.NeedsVisibilityUpdate = true;
        return true;
    }

    /// <summary>
    /// The client finished loading the new map (vmangos <c>HandleMoveWorldportAckOpcode</c>).
    /// Ignored unless SMSG_NEW_WORLD was sent; the player enters the new map at the start of
    /// the next world tick (maps cannot be created while the world iterates them).
    /// </summary>
    public bool HandleWorldportAck(Player player)
    {
        if (!_pending.TryGetValue(player.Guid, out Pending? pending) || pending.Stage != TeleportStage.Far)
        {
            return false;
        }

        pending.Stage = TeleportStage.Arriving;
        _world.Post(() => CompleteTeleportFar(player, pending));
        return true;
    }

    /// <summary>Forget the player's teleport (logout or disconnect).</summary>
    public void Forget(Player player)
    {
        if (_pending.Remove(player.Guid, out Pending? pending))
        {
            pending.SourceMap.EndTransit(player);
        }
    }

    // vmangos Player::ExecuteTeleportFar (run after the source map's update).
    private void ExecuteTeleportFar(Player player, Pending pending)
    {
        if (!_pending.TryGetValue(player.Guid, out Pending? current) || !ReferenceEquals(current, pending))
        {
            return; // logged out or superseded
        }

        Map source = pending.SourceMap;
        if (!ReferenceEquals(player.Map, source) || !_world.IsOnline(player.Guid))
        {
            _pending.Remove(player.Guid);
            return;
        }

        TeleportDestination dest = pending.Destination;
        player.Selection = ObjectGuid.Empty;
        player.Session.Send(WorldOpcode.SmsgTransferPending, TeleportPackets.BuildTransferPending(dest.MapId));
        source.RemovePlayer(player);
        source.BeginTransit(player);

        // The player is in no map now; a save while in transit stores the destination, as
        // vmangos SaveToDB does with m_teleportDest during a far teleport.
        player.MapId = dest.MapId;
        player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
        pending.Stage = TeleportStage.Far;
        player.Session.Send(WorldOpcode.SmsgNewWorld, TeleportPackets.BuildNewWorld(dest.MapId, dest.X, dest.Y, dest.Z, dest.Orientation));
    }

    // vmangos HandleMoveWorldportAckOpcode after the semaphore check.
    private void CompleteTeleportFar(Player player, Pending pending)
    {
        if (!_pending.TryGetValue(player.Guid, out Pending? current) || !ReferenceEquals(current, pending))
        {
            return;
        }

        _pending.Remove(player.Guid);
        pending.SourceMap.EndTransit(player);
        if (!_world.IsOnline(player.Guid) || player.Map is not null)
        {
            return;
        }

        TeleportDestination dest = pending.Destination;
        WorldMaps maps = WorldMaps.Of(_world);
        if (!maps.Registry.Contains(dest.MapId) || !GridDefines.IsValidMapCoord(dest.X, dest.Y, dest.Z, dest.Orientation))
        {
            // vmangos: an invalid destination sends the player to its bind point.
            _logger.LogError("{Player} far-teleported to invalid destination {Dest}; using the bind point", player.Name, dest);
            dest = new TeleportDestination(player.Home.MapId, player.Home.X, player.Home.Y, player.Home.Z, player.Orientation);
        }

        if (!TryEnterMap(player, dest))
        {
            // vmangos HandleReturnOnTeleportFail: back to where the teleport started.
            TeleportDestination origin = pending.Origin;
            if (!TryEnterMap(player, origin))
            {
                _logger.LogError("{Player} could not enter map {MapId} nor return to map {Origin}; disconnecting", player.Name, dest.MapId, origin.MapId);
                player.Session.Kick();
            }
        }
    }

    private bool TryEnterMap(Player player, TeleportDestination dest)
    {
        Map map = _world.GetMap(dest.MapId);
        if (map.FindObject(player.Guid) is not null)
        {
            return false;
        }

        player.MapId = dest.MapId;
        player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
        UpdateZone(map, player);
        _beforeAddToMap(player);
        map.AddPlayer(player);
        _afterAddToMap(player);
        return true;
    }

    private static void UpdateZone(Map map, Player player)
    {
        (uint zoneId, _) = map.GetZoneAndAreaId(player.X, player.Y, player.Z);
        if (zoneId != 0)
        {
            player.ZoneId = zoneId;
        }
    }

    private void ResetMovementForTeleport(Player player)
    {
        MovementInfo movement = player.Movement;
        movement.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.TurnLeft | MovementFlags.TurnRight | MovementFlags.OnTransport);
        movement.TransportGuid = 0;
        movement.TransportX = 0;
        movement.TransportY = 0;
        movement.TransportZ = 0;
        movement.TransportOrientation = 0;
        player.ApplyMovement(movement, _world.NowMs);
    }

    // vmangos MovementPacketSender::SendTeleportToObservers → SendObjectMessageToSet: the
    // players within visibility distance of the mover's current position (Map::MessageBroadcast
    // visits the cells within GetVisibilityDistance), except the mover itself. Only clients that
    // have the mover can apply the packet, so the candidates are its observers.
    private void SendTeleportToObservers(Map map, Player player, TeleportDestination dest)
    {
        MovementInfo moved = player.Movement;
        moved.X = dest.X;
        moved.Y = dest.Y;
        moved.Z = dest.Z;
        moved.Orientation = dest.Orientation;
        moved.Time = _world.NowMs;
        byte[] packet = TeleportPackets.BuildMoveTeleport(player.Guid, moved);
        foreach (Player observer in map.ObserversOf(player).ToArray())
        {
            if (!ReferenceEquals(observer, player) && Map.IsWithinVisibilityDistance(observer, player, alreadyVisible: true))
            {
                observer.Session.Send(WorldOpcode.MsgMoveTeleport, packet);
            }
        }
    }

    private sealed class Pending(TeleportDestination destination, TeleportStage stage, Map sourceMap, TeleportDestination origin)
    {
        public TeleportDestination Destination { get; } = destination;

        public TeleportStage Stage { get; set; } = stage;

        public Map SourceMap { get; } = sourceMap;

        public TeleportDestination Origin { get; } = origin;
    }
}

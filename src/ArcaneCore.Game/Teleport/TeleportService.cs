using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.WorldState.Zones;
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

    /// <summary>
    /// Raised on the world thread when a teleport of a player has finished: after a same-map teleport was acknowledged, or after the
    /// player entered the new map (vmangos runs delayed operations such as DELAYED_RESURRECT_PLAYER at that point).
    /// </summary>
    public event Action<Player>? TeleportCompleted;

    /// <summary>
    /// Raised on the world thread when a same-map teleport starts, before MSG_MOVE_TELEPORT_ACK goes out (vmangos Player::TeleportTo near
    /// branch: a pet farther from the destination than the grid activation distance is unsummoned temporarily, Player.cpp:1911-1921).
    /// </summary>
    public event Action<Player, TeleportDestination>? NearTeleportStarting;

    /// <summary>
    /// Raised on the world thread after <see cref="TeleportCompleted"/> when the teleport scheduled vmangos' DELAYED_CAST_HONORLESS_TARGET:
    /// every far teleport (Player::ExecuteTeleportFar, Player.cpp:2083) and a same-map teleport without
    /// <see cref="TeleportOptions.NotLeaveCombat"/> (Player.cpp:1923-1927). The flag says which; a far arrival casts only into a PvP-enforced
    /// area (HandleMoveWorldportAckOpcode, MovementHandler.cpp:193-195), which the listener decides.
    /// </summary>
    public event Action<Player, bool>? HonorlessTargetDue;

    /// <summary>
    /// Raised on the world thread when a far teleport is carried out, while the player is still in its old map (vmangos
    /// Player::ExecuteTeleportFar: "remove pet on map change", UnsummonPetTemporaryIfAny, Player.cpp:2045-2048, before the old map's
    /// Remove).
    /// </summary>
    public event Action<Player>? FarTeleportExecuting;

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
    /// Whether <see cref="TeleportTo"/> would start this teleport right now: the same checks, in the
    /// same order, with nothing changed (quest reward preflight). A later call may still be refused
    /// because the player or the target instance changed in between.
    /// </summary>
    public bool CanTeleportTo(Player player, uint mapId, float x, float y, float z, float orientation)
        => Check(player, mapId, x, y, z, orientation, log: false);

    private bool Check(Player player, uint mapId, float x, float y, float z, float orientation, bool log)
    {
        if (player.IsQuestSettlementPending)
        {
            return false;
        }

        if (!GridDefines.IsValidMapCoord(x, y, z, orientation))
        {
            if (log)
            {
                _logger.LogWarning("teleport of {Player} to invalid coordinates {X} {Y} {Z} {O} on map {MapId}", player.Name, x, y, z, orientation, mapId);
            }

            return false;
        }

        WorldMaps maps = WorldMaps.Of(_world);
        MapTemplate? target = maps.Registry.Find(mapId);
        if (target is null)
        {
            if (log)
            {
                _logger.LogWarning("teleport of {Player} to unknown map {MapId}", player.Name, mapId);
            }

            return false;
        }

        // Player::TeleportTo (Player.cpp:1861-1864): "don't let enter battlegrounds without assigned battleground id" — only the
        // battleground system, through BattlegroundEntryAllowed, lets a player onto a battleground map.
        if (target.IsBattleground && !(BattlegroundEntryAllowed?.Invoke(player, mapId) ?? false))
        {
            return false;
        }

        Map? current = player.Map;
        if (current is null || IsBeingTeleportedFar(player))
        {
            return false;
        }

        // vmangos TeleportTo → MapManager::CanPlayerEnter: the instance rules may refuse a far
        // teleport (raid group, full or resetting instance) before anything changes.
        return current.MapId == mapId || _world.MapResolver is not { } resolver || resolver.CanEnter(player, mapId);
    }

    /// <summary>
    /// Whether a player may be teleported onto a battleground map: the battleground feature answers true for the map of the match the player
    /// is bound to (vmangos <c>InBattleGround()</c>). Null (no battleground system) refuses every battleground map. World thread.
    /// </summary>
    public Func<Player, uint, bool>? BattlegroundEntryAllowed { get; set; }

    /// <summary>
    /// Start a teleport (vmangos <c>Player::TeleportTo</c>). Fails — returns false and changes
    /// nothing — for invalid coordinates (<c>MapManager::IsValidMapCoord</c>), a map that is not
    /// in the registry, a battleground map the player is not bound to (<see cref="BattlegroundEntryAllowed"/>),
    /// a player in no map, or a far teleport already under way.
    /// </summary>
    public bool TeleportTo(Player player, uint mapId, float x, float y, float z, float orientation)
        => TeleportTo(player, mapId, x, y, z, orientation, TeleportOptions.None);

    /// <summary>
    /// <see cref="TeleportTo(Player, uint, float, float, float, float)"/> with vmangos <c>TeleportToOptions</c>. With
    /// <see cref="TeleportOptions.NotLeaveTransport"/> a passenger stays on its ship: the teleport is always a far one
    /// (vmangos only teleports near without a transport, Player.cpp:1896), SMSG_TRANSFER_PENDING names the ship and the
    /// old map, and SMSG_NEW_WORLD carries the offset on the ship instead of the world position (Player.cpp:2068-2072, 2113-2118).
    /// </summary>
    public bool TeleportTo(Player player, uint mapId, float x, float y, float z, float orientation, TeleportOptions options)
    {
        if (!Check(player, mapId, x, y, z, orientation, log: true) || player.Map is not { } current)
        {
            return false;
        }

        // vmangos revives a ghost that enters the map its corpse is in (Player.cpp:1953-1966; there before the entry check, here once it passed).
        current.Combat.ReviveForDungeonEntry(player, mapId);

        // vmangos TeleportTo (Player.cpp:1868-1872): leave the ship unless told to stay on it.
        bool staysAboard = (options & TeleportOptions.NotLeaveTransport) != 0 && player.Transport is not null;
        if (!staysAboard)
        {
            player.Transport?.RemovePassenger(player);
        }

        // vmangos TeleportTo: reset the client time stamp and stop movement, leave any transport.
        ResetMovementForTeleport(player, staysAboard);

        var destination = new TeleportDestination(mapId, x, y, z, orientation);
        if (current.MapId == mapId && !staysAboard)
        {
            bool leavesCombat = (options & TeleportOptions.NotLeaveCombat) == 0;
            _pending[player.Guid] = new Pending(destination, TeleportStage.Near, current, default)
            {
                HonorlessTarget = leavesCombat,
            };
            if (leavesCombat)
            {
                // vmangos Player::TeleportTo near branch (Player.cpp:1923-1927): CombatStop() unless TELE_TO_NOT_LEAVE_COMBAT.
                current.Combat.CombatStop(player);
            }

            NearTeleportStarting?.Invoke(player, destination);
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
        if (player.IsQuestSettlementPending)
        {
            return false;
        }

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
        ArriveNear(player, dest);
        SendTeleportToObservers(map, player, dest);
        UpdateZone(map, player);

        // vmangos TeleportPositionRelocation runs the zone (or area) update at once; the 1 s zone timer would leave the world states
        // and the zone listeners a second behind. ZoneAreaUpdater runs every listener and then rethrows the first failure (inside
        // Map.Update a throwing updater is logged); here that failure is logged the same way and the arrival still completes, so
        // one failing listener cannot leave the player without its visibility pass and the completion event.
        try
        {
            map.FindUpdater<ZoneAreaUpdater>()?.OnRelocated(player);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "map {MapId} zone update of {Player} after a near teleport failed", map.MapId, player.Name);
        }

        player.NeedsVisibilityUpdate = true;
        TeleportCompleted?.Invoke(player);
        if (pending.HonorlessTarget)
        {
            HonorlessTargetDue?.Invoke(player, false);
        }

        return true;
    }

    /// <summary>
    /// The client finished loading the new map (vmangos <c>HandleMoveWorldportAckOpcode</c>).
    /// Ignored unless SMSG_NEW_WORLD was sent; the player enters the new map at the start of
    /// the next world tick (maps cannot be created while the world iterates them).
    /// </summary>
    public bool HandleWorldportAck(Player player)
    {
        if (player.IsQuestSettlementPending)
        {
            return false;
        }

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

        if (player.IsQuestSettlementPending)
        {
            return;
        }

        Map source = pending.SourceMap;
        if (!ReferenceEquals(player.Map, source) || !ReferenceEquals(_world.FindOnlinePlayer(player.Guid), player))
        {
            _pending.Remove(player.Guid);
            return;
        }

        // vmangos Player::ExecuteTeleportFar (Player.cpp:2025): accepted far transfer clears
        // pending extra attacks before the player leaves the source map. Failed/superseded
        // transfers return above and preserve the queue.
        player.Combat.ResetExtraAttacks();
        FarTeleportExecuting?.Invoke(player);
        TeleportDestination dest = pending.Destination;
        player.Selection = ObjectGuid.Empty;
        Transports.ShipTransport? ship = player.Transport;
        player.Session.Send(WorldOpcode.SmsgTransferPending, ship is null
            ? TeleportPackets.BuildTransferPending(dest.MapId)
            : TeleportPackets.BuildTransferPending(dest.MapId, ship.Entry, source.MapId));
        source.RemovePlayer(player);
        source.BeginTransit(player);

        // The player is in no map now; a save while in transit stores the destination, as
        // vmangos SaveToDB does with m_teleportDest during a far teleport.
        MovementInfo aboard = player.Movement;
        player.MapId = dest.MapId;
        player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
        pending.Stage = TeleportStage.Far;
        if (ship is not null)
        {
            // Still aboard: the movement block keeps the ship and the offset, and the client loads the new map at its place on
            // the ship (vmangos SendNewWorld: m_movementInfo.GetTransportPos() with the destination map).
            player.SetTransportData(ship.Guid, aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation);
            player.Session.Send(WorldOpcode.SmsgNewWorld, TeleportPackets.BuildNewWorld(
                dest.MapId, aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation));
            return;
        }

        player.Session.Send(WorldOpcode.SmsgNewWorld, TeleportPackets.BuildNewWorld(dest.MapId, dest.X, dest.Y, dest.Z, dest.Orientation));
    }

    // vmangos HandleMoveWorldportAckOpcode after the semaphore check.
    private void CompleteTeleportFar(Player player, Pending pending)
    {
        if (!_pending.TryGetValue(player.Guid, out Pending? current) || !ReferenceEquals(current, pending))
        {
            return;
        }

        if (player.IsQuestSettlementPending)
        {
            return;
        }

        _pending.Remove(player.Guid);
        pending.SourceMap.EndTransit(player);
        if (!ReferenceEquals(_world.FindOnlinePlayer(player.Guid), player) || player.Map is not null)
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

        if (TryEnterMap(player, dest))
        {
            HonorlessTargetDue?.Invoke(player, true);
        }
        else
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
        // The map resolver picks (or creates) the instance and binds the player or its group
        // (vmangos MapManager::CreateMap → DungeonMap::Add); null refuses the entry.
        IMapResolver? resolver = _world.MapResolver;
        Map? map = resolver is null ? _world.GetMap(dest.MapId) : resolver.ResolveEntry(player, dest.MapId);
        if (map is null || map.FindObject(player.Guid) is not null)
        {
            return false;
        }

        player.MapId = dest.MapId;
        if (player.Transport is { } ship)
        {
            MovementInfo aboard = player.Movement;
            if (ReferenceEquals(ship.CurrentMap, map))
            {
                // vmangos HandleMoveWorldportAckOpcode: "Transport position may have changed while loading" - the player
                // enters at its offset from where the ship is now.
                (float x, float y, float z, float o) = (aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation);
                ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
                player.Relocate(x, y, z, o, _world.NowMs);
                player.SetTransportData(ship.Guid, aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation);
            }
            else
            {
                // The ship is not where the player arrives (a failed entry sent it back): it is left behind.
                ship.RemovePassenger(player);
                player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
            }
        }
        else
        {
            player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
        }

        UpdateZone(map, player);
        _beforeAddToMap(player);
        map.AddPlayer(player);
        _afterAddToMap(player);
        resolver?.OnEntered(player, map);
        TeleportCompleted?.Invoke(player);
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

    // vmangos ExecuteTeleportNear → TeleportPositionRelocation (Unit.cpp:9855-9856): m_movementInfo.ChangePosition only. TeleportTo
    // already stopped the motion; swimming, levitating and the other client state stay. The fall in progress ends (vmangos
    // SetFallInformation(0) in the near branch of TeleportTo, Player.cpp:1932).
    private void ArriveNear(Player player, TeleportDestination dest)
    {
        MovementInfo arrived = player.Movement;
        arrived.X = dest.X;
        arrived.Y = dest.Y;
        arrived.Z = dest.Z;
        arrived.Orientation = dest.Orientation;
        player.ApplyMovement(arrived, _world.NowMs);
        if (LocomotionStates.TryGet(player, out LocomotionState state))
        {
            state.ResetFall();
        }
    }

    // vmangos TeleportTo (Player.cpp:1884-1889): the moving and turning flags go; ONTRANSPORT and the ship data go too unless the
    // player stays aboard.
    private void ResetMovementForTeleport(Player player, bool staysAboard)
    {
        MovementInfo movement = player.Movement;
        movement.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.TurnLeft | MovementFlags.TurnRight);
        if (!staysAboard)
        {
            movement.Flags &= ~MovementFlags.OnTransport;
            movement.TransportGuid = 0;
            movement.TransportX = 0;
            movement.TransportY = 0;
            movement.TransportZ = 0;
            movement.TransportOrientation = 0;
        }

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

        /// <summary>vmangos DELAYED_CAST_HONORLESS_TARGET scheduled by a same-map teleport.</summary>
        public bool HonorlessTarget { get; init; }
    }
}

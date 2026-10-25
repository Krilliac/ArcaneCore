using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Transports;

/// <summary>
/// The world's ships and zeppelins (vmangos <c>TransportMgr</c> plus the transport parts of <c>Map</c>; re-implemented).
/// <list type="bullet">
/// <item>Spawning: a continent route gets one ship, created with the first map of the route it touches (vmangos
/// <c>SpawnTransportsOnMap</c>, Map.cpp:176); <see cref="Install"/> creates those maps, as vmangos has its continents loaded
/// from the start. A route inside one instanceable map gets a ship in every instance of it.</item>
/// <item>Sending: a ship is not in the grids. Every player of its map has it (vmangos <c>Map::SendInitTransports</c> at map
/// entry, <c>SendCreateUpdateToMap</c> when the ship arrives, <c>SendOutOfRangeUpdateToMap</c> when it leaves); its own
/// passengers are not told when it changes maps.</item>
/// <item>Map change at a dock (vmangos <c>ShipTransport::TeleportTransport</c>): creatures are left behind, players are
/// revived, freed of fear and confusion, taken out of combat and carried along: a far teleport to the new map that keeps
/// them on the ship (<see cref="TeleportPassenger"/>), or a relocation when the map stays the same.</item>
/// </list>
/// World thread only.
/// </summary>
public sealed partial class TransportSystem
{
    /// <summary>Maximum allowed passenger local offset coordinate magnitude (vmangos Player::LoadFromDB 250yd bound).</summary>
    public const float MaxPassengerOffset = 250.0f;

    private static readonly ConditionalWeakTable<WorldRuntime, TransportSystem> s_registered = new();

    private readonly WorldRuntime _world;
    private readonly ILogger _logger;
    private readonly TransportTemplate[] _templates;
    private readonly List<ShipTransport> _ships = [];
    private readonly Dictionary<Map, List<ShipTransport>> _byMap = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ObjectGuid> _justBoarded = [];
    private readonly HashSet<uint> _missingTransportReportedEntries = [];
    private bool _installed;

    public static bool IsOffsetWithinBounds(float x, float y, float z)
        => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            && MathF.Abs(x) <= MaxPassengerOffset
            && MathF.Abs(y) <= MaxPassengerOffset
            && MathF.Abs(z) <= MaxPassengerOffset;

    public TransportSystem(WorldRuntime world, IEnumerable<TransportTemplate> templates, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(templates);
        _world = world;
        _templates = [.. templates.OrderBy(t => t.Entry)];
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The routes, by entry.</summary>
    public IReadOnlyList<TransportTemplate> Templates => _templates;

    /// <summary>Every ship that exists, in creation order.</summary>
    public IReadOnlyList<ShipTransport> Ships => _ships;

    /// <summary>
    /// A far teleport that keeps the player aboard (vmangos <c>TeleportTo(.., TELE_TO_NOT_LEAVE_TRANSPORT)</c>): the player,
    /// the new map and the position. The world feature wires it to the teleport service. Without it, or when it refuses,
    /// a player whose ship changes maps is put off the ship where it stands.
    /// </summary>
    public Func<Player, uint, float, float, float, float, bool>? TeleportPassenger { get; set; }

    /// <summary>
    /// Run on a player before its ship changes maps, after a dead passenger was revived (vmangos TeleportTransport:
    /// <c>RemoveSpellsCausingAura(SPELL_AURA_MOD_CONFUSE / MOD_FEAR)</c> and <c>CombatStopWithPets(true)</c>). The world
    /// feature wires it to the spell system; without it only the combat stop runs.
    /// </summary>
    public Action<Player>? PreparePassengerForMapChange { get; set; }

    /// <summary>
    /// Send a player to its hearthstone bind point (vmangos <c>RelocateToHomebind</c>): used for a character saved on a ship that
    /// no longer exists or with an offset off the ship. The world feature wires it to the teleport service.
    /// </summary>
    public Func<Player, bool>? TeleportToHomebind { get; set; }

    /// <summary>Raised when a unit boards a ship.</summary>
    public event Action<ShipTransport, Unit>? PassengerBoarded;

    /// <summary>Raised when a unit leaves a ship.</summary>
    public event Action<ShipTransport, Unit>? PassengerLeft;

    /// <summary>Make <paramref name="system"/> the transport system of <paramref name="world"/> (<see cref="Of"/>).</summary>
    public static void Register(WorldRuntime world, TransportSystem system)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(system);
        s_registered.AddOrUpdate(world, system);
    }

    /// <summary>The registered system of <paramref name="world"/>, or null when transports are off.</summary>
    public static TransportSystem? Of(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_registered.TryGetValue(world, out TransportSystem? system) ? system : null;
    }

    /// <summary>
    /// Attach to every map, now and when created, and create the continent routes' ships (world thread, once).
    /// </summary>
    public void Install()
    {
        if (!_world.IsWorldThread)
        {
            throw new InvalidOperationException("transports are installed on the world thread");
        }

        if (_installed)
        {
            return;
        }

        _installed = true;
        _world.MapCreated += OnMapCreated;
        _world.MapUnloading += OnMapUnloading;
        _world.PlayerLoggingOut += OnPlayerLoggingOut;
        foreach (Map map in _world.Maps.ToArray())
        {
            OnMapCreated(map);
        }

        // vmangos loads the continents at startup and their ships with them; this base creates maps on first use.
        foreach (TransportTemplate template in _templates)
        {
            if (!template.InInstance && !template.Spawned && template.KeyFrames.Count > 0)
            {
                _world.GetMap(template.KeyFrames[0].Node.MapId);
            }

            if (!template.InInstance && (!template.Spawned || FindByEntry(template.Entry) is null))
            {
                ReportMissingOnMap(
                    template.Entry,
                    $"Continent transport entry {template.Entry} ({template.GameObject.Name}) failed to spawn during Install (KeyFrames={template.KeyFrames.Count}, Spawned={template.Spawned}).");
            }
        }

        _logger.LogInformation("Transports: {Routes} routes, {Ships} ships sailing", _templates.Length, _ships.Count);
    }

    private void ReportMissingOnMap(uint entry, string detail)
    {
        if (!_missingTransportReportedEntries.Add(entry))
        {
            return;
        }

        AnomalyLedger.Shared.Report(
            "transport",
            "missing-on-map",
            $"transport entry {entry}",
            detail,
            AnomalySeverity.Warning);
    }

    /// <summary>The ships on <paramref name="map"/> now.</summary>
    public IReadOnlyList<ShipTransport> ShipsOn(Map map)
        => _byMap.TryGetValue(map, out List<ShipTransport>? ships) ? ships : [];

    /// <summary>vmangos <c>Map::GetTransport</c>: the ship with this GUID on <paramref name="map"/>, or null.</summary>
    public ShipTransport? Find(Map map, ObjectGuid guid)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (guid.IsEmpty || !_byMap.TryGetValue(map, out List<ShipTransport>? ships))
        {
            return null;
        }

        foreach (ShipTransport ship in ships)
        {
            if (ship.Guid == guid)
            {
                return ship;
            }
        }

        return null;
    }

    /// <summary>The ship of a continent route, by template entry (its GUID counter), or null.</summary>
    public ShipTransport? FindByEntry(uint entry) => _ships.FirstOrDefault(s => s.Entry == entry);

    /// <summary>
    /// The 1.12 client loses a ship it just boarded until it is sent again; vmangos re-sends it at a CMSG_MOVE_TIME_SKIPPED
    /// that arrives before the player's next movement aboard (MovementHandler.cpp:1001-1010; HandleMoverRelocation sets the
    /// mark on boarding and clears it on every movement aboard, 1083 and 1089). True once after a player boarded (and clears the mark).
    /// </summary>
    public bool TakeJustBoarded(Player player) => _justBoarded.Remove(player.Guid);

    /// <summary>vmangos <c>SetJustBoarded(false)</c> in HandleMoverRelocation: a movement of a player already aboard.</summary>
    internal void ClearJustBoarded(Player player) => _justBoarded.Remove(player.Guid);

    /// <summary>Send <paramref name="ship"/> to <paramref name="player"/> again: out of range, then create (vmangos SendOutOfRange/CreateUpdateToPlayer).</summary>
    public void ResendTo(ShipTransport ship, Player player)
    {
        ArgumentNullException.ThrowIfNull(ship);
        ArgumentNullException.ThrowIfNull(player);
        TransportPackets.Send(player, TransportPackets.BuildOutOfRange([ship]), CompressionThreshold);
        TransportPackets.Send(player, TransportPackets.BuildCreate([ship], player, _world.NowMs), CompressionThreshold);
    }

    // --- per map ------------------------------------------------------------------------------------------------------

    internal void UpdateMap(Map map)
    {
        if (_installed)
        {
            foreach (TransportTemplate template in _templates)
            {
                if (!template.InInstance && template.MapsUsed.Contains(map.MapId))
                {
                    ShipTransport? activeShip = FindByEntry(template.Entry);
                    if (activeShip is null || activeShip.CurrentMap is null)
                    {
                        ReportMissingOnMap(
                            template.Entry,
                            $"Configured continent transport entry {template.Entry} ({template.GameObject.Name}) for map {map.MapId} is missing or detached from any active map.");
                    }
                    else if (!template.MapsUsed.Contains(activeShip.CurrentMap.MapId))
                    {
                        ReportMissingOnMap(
                            template.Entry,
                            $"Transport entry {template.Entry} ({template.GameObject.Name}) is on unexpected map {activeShip.CurrentMap.MapId} (route maps: {string.Join(",", template.MapsUsed)}).");
                    }
                }
            }
        }

        if (!_byMap.TryGetValue(map, out List<ShipTransport>? ships) || ships.Count == 0)
        {
            return;
        }

        uint now = _world.NowMs;
        foreach (ShipTransport ship in ships.ToArray())
        {
            if (ReferenceEquals(ship.CurrentMap, map))
            {
                try
                {
                    ship.Update(now);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("transport.update", ex);
                    AnomalyLedger.Shared.Report("transport", "lifecycle-fault", $"transport entry {ship.Entry}", ex.Message);
                    _logger.LogFault(
                        "map.transport",
                        ex,
                        "TransportSystem fault updating transport {Entry} (guid={Guid}) on map {MapId}",
                        ship.Entry,
                        ship.Guid,
                        map.MapId);
                }
            }
            else
            {
                ReportMissingOnMap(
                    ship.Entry,
                    $"Transport entry {ship.Entry} listed on map {map.MapId} has mismatched CurrentMap ({ship.CurrentMap?.MapId.ToString() ?? "null"}).");
            }
        }
    }

    /// <summary>
    /// vmangos <c>Map::SendInitTransports</c> (Map.cpp:1720-1734): every ship of the map but the player's own, before its own
    /// create block. The player's own ship goes into the self packet (<see cref="OnWritingSelf"/>).
    /// </summary>
    internal void OnPlayerAdding(Map map, Player player)
    {
        try
        {
            if (player.LoginTransportSeat is { } seat)
            {
                player.LoginTransportSeat = null;
                RestoreSeat(map, player, seat);
            }

            if (_byMap.TryGetValue(map, out List<ShipTransport>? ships) && ships.Count > 0)
            {
                TransportPackets.Send(player, TransportPackets.BuildCreate(ships.Where(s => !ReferenceEquals(s, player.Transport)), player, _world.NowMs),
                    CompressionThreshold);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("transport.player-added", ex);
            AnomalyLedger.Shared.Report("transport", "lifecycle-fault", TransportAnomalies.PlayerSubject(player), ex.Message);
            _logger.LogFault(
                "map.transport",
                ex,
                "TransportSystem fault adding player {Player} (guid={Guid}) to map {MapId}",
                player.Name,
                player.Guid.Low,
                map.MapId);
        }
    }

    /// <summary>
    /// vmangos <c>Map::SendInitSelf</c> (Map.cpp:1690-1718): a player aboard gets its ship first in its own create packet,
    /// which then carries the has-transport byte. The other passengers vmangos adds to that packet come with the ordinary
    /// visibility pass right after (has-transport 0).
    /// </summary>
    internal void OnWritingSelf(Map map, Player player, UpdateData selfPacket)
    {
        if (player.Transport is not { } ship || !ReferenceEquals(ship.CurrentMap, map))
        {
            return;
        }

        try
        {
            PacketWriter block = selfPacket.BeginBlock();
            UpdateBlockWriter.WriteCreateBlock(block, ship, player, isNewObject: false, _world.NowMs);
            selfPacket.EndBlock();
            selfPacket.HasTransport = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("transport.self-block", ex);
            AnomalyLedger.Shared.Report("transport", "lifecycle-fault", TransportAnomalies.PlayerSubject(player), ex.Message);
            _logger.LogFault(
                "map.transport",
                ex,
                "TransportSystem fault writing self transport block for player {Player} (guid={Guid}) on map {MapId}",
                player.Name,
                player.Guid.Low,
                map.MapId);
        }
    }

    /// <summary>vmangos <c>Map::SendRemoveTransports</c>: every ship of the map but the player's own.</summary>
    internal void OnPlayerRemoved(Map map, Player player)
    {
        try
        {
            if (_byMap.TryGetValue(map, out List<ShipTransport>? ships) && ships.Count > 0)
            {
                TransportPackets.Send(player, TransportPackets.BuildOutOfRange(ships.Where(s => !ReferenceEquals(s, player.Transport))), CompressionThreshold);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("transport.player-removed", ex);
            AnomalyLedger.Shared.Report("transport", "lifecycle-fault", TransportAnomalies.PlayerSubject(player), ex.Message);
            _logger.LogFault(
                "map.transport",
                ex,
                "TransportSystem fault removing player {Player} (guid={Guid}) from map {MapId}",
                player.Name,
                player.Guid.Low,
                map.MapId);
        }
    }

    internal void OnBoarded(ShipTransport ship, Unit passenger)
    {
        Raise(PassengerBoarded, ship, passenger);
    }

    internal void OnLeft(ShipTransport ship, Unit passenger)
    {
        if (passenger is Player player)
        {
            _justBoarded.Remove(player.Guid);
        }

        Raise(PassengerLeft, ship, passenger);
    }

    /// <summary>A player's client says it stands on <paramref name="ship"/>: board it and remember to re-send the ship.</summary>
    internal void BoardFromClient(ShipTransport ship, Player player)
    {
        if (ship.AddPassenger(player))
        {
            _justBoarded.Add(player.Guid);
        }
    }

    /// <summary>vmangos frame Update flag: destroy and create the ship again for every player of its map who is not aboard.</summary>
    internal void Refresh(ShipTransport ship)
    {
        SendOutOfRangeToMap(ship);
        SendCreateToMap(ship);
    }

    // --- map change (ShipTransport::TeleportTransport, Transport.cpp:120-200) ---------------------------------------

    internal void TeleportTransport(ShipTransport ship, uint newMapId, float x, float y, float z, float o)
    {
        Map? oldMap = ship.CurrentMap;
        Map newMap = oldMap is not null && oldMap.MapId == newMapId ? oldMap : _world.GetMap(newMapId);
        RemoveFromMap(ship);
        ship.CurrentMap = newMap;

        foreach (Unit passenger in ship.Passengers.ToArray())
        {
            try
            {
                float destX = passenger.Movement.TransportX;
                float destY = passenger.Movement.TransportY;
                float destZ = passenger.Movement.TransportZ;
                float destO = passenger.Movement.TransportOrientation;
                ShipTransport.CalculatePassengerPosition(ref destX, ref destY, ref destZ, ref destO, x, y, z, o);
                switch (passenger)
                {
                    case Creature creature:
                        LeaveCreatureBehind(ship, creature);
                        break;
                    case Player player:
                        CarryPlayer(ship, player, newMapId, destX, destY, destZ, destO);
                        break;
                    default:
                        ship.RemovePassenger(passenger);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("transport.teleport-passenger", ex);
                AnomalyLedger.Shared.Report("transport", "lifecycle-fault", $"transport entry {ship.Entry}", ex.Message);
                _logger.LogFault(
                    "map.transport",
                    ex,
                    "TransportSystem fault teleporting passenger {PassengerGuid} on transport {Entry} to map {NewMapId}",
                    passenger.Guid,
                    ship.Entry,
                    newMapId);
            }
        }

        ship.Relocate(newMapId, x, y, z, o);
        AddToMap(ship, newMap);
        if (oldMap?.MapId != newMapId)
        {
            // An operator's view of the ships sailing (World logging at Debug for ArcaneCore.World.Transports).
            _logger.LogDebug("Transport {Entry} sailed from map {OldMap} to map {NewMap} with {Passengers} passenger(s)", ship.Entry,
                oldMap?.MapId, newMapId, ship.Passengers.Count);
        }
    }

    // vmangos: "Units teleport on transport not implemented": a creature stays on the old map; one whose owner is not
    // riding along leaves combat (evades) or is put back at its owner or its spawn point.
    private void LeaveCreatureBehind(ShipTransport ship, Creature creature)
    {
        ship.RemovePassenger(creature);
        creature.RemoveMovementFlags(MovementFlags.OnTransport); // vmangos leaves the flag; a server-moved unit must not keep it
        Player? owner = creature.GetOwner() as Player;
        if (owner is not null && ReferenceEquals(owner.Transport, ship))
        {
            return; // the pet follows its owner through the far teleport (the pet system's job)
        }

        Map? map = creature.Map;
        if (creature.Combat.IsInCombat)
        {
            map?.FindUpdater<CreatureMapSystem>()?.EnterEvadeMode(creature);
            return;
        }

        if (owner is not null)
        {
            creature.Relocate(owner.X, owner.Y, owner.Z, 0f, _world.NowMs);
        }
        else
        {
            CreatureHome home = creature.Home;
            creature.Relocate(home.X, home.Y, home.Z, home.Orientation, _world.NowMs);
        }
    }

    private void CarryPlayer(ShipTransport ship, Player player, uint newMapId, float x, float y, float z, float o)
    {
        if (player.Map is not { } map)
        {
            if (!player.IsLoggingOut && !player.Locomotion.SuppressAnomalies)
            {
                TransportAnomalies.Report(
                    "passenger-left-behind",
                    player,
                    $"Passenger had null Map when transport entry {ship.Entry} transitioned to map {newMapId}; removed from transport.");
            }
            ship.RemovePassenger(player); // not in the world (in transit, logging in)
            return;
        }

        if (!player.IsAlive)
        {
            map.Combat.ResurrectPlayer(player, 1.0f, applySickness: false);
        }

        if (PreparePassengerForMapChange is { } prepare)
        {
            prepare(player);
        }
        else
        {
            map.Combat.CombatStop(player);
        }

        if (newMapId == player.MapId)
        {
            // No teleport packet without a map change (vmangos: SetAsServerSide + TeleportPositionRelocation).
            player.RelocateOnTransport(x, y, z, o);
            return;
        }

        if (TeleportPassenger is not { } teleport || !teleport(player, newMapId, x, y, z, o))
        {
            _logger.LogWarning("{Player} could not travel with transport {Entry} to map {MapId}; left at the dock", player.Name, ship.Entry, newMapId);
            if (!player.IsLoggingOut && !player.Locomotion.SuppressAnomalies)
            {
                TransportAnomalies.Report(
                    "passenger-left-behind",
                    player,
                    $"Passenger could not be carried with transport entry {ship.Entry} from map {player.MapId} to map {newMapId} at ({x:F2}, {y:F2}, {z:F2}); left behind at dock.");
            }
            ship.RemovePassenger(player);
            player.RemoveMovementFlags(MovementFlags.OnTransport);
        }
    }

    // --- spawning (TransportMgr::SpawnTransportsOnMap / CreateTransport, TransportMgr.cpp:374-427) ----------------------

    private void OnMapCreated(Map map)
    {
        if (_byMap.ContainsKey(map))
        {
            return;
        }

        _byMap[map] = [];
        map.AddUpdater(new TransportMapUpdater(this));
        foreach (TransportTemplate template in _templates)
        {
            // Continent ships exist once.
            if (template.Spawned && !template.InInstance)
            {
                continue;
            }

            if (template.MapsUsed.Contains(map.MapId))
            {
                if (CreateTransport(template, map) is not null)
                {
                    template.Spawned = true;
                }
                else
                {
                    bool instanceable = map.Template?.Instanceable ?? false;
                    if (instanceable || map.InstanceId == 0)
                    {
                        ReportMissingOnMap(
                            template.Entry,
                            $"Transport entry {template.Entry} ({template.GameObject.Name}) failed to spawn on map {map.MapId} (instanceId={map.InstanceId}, routeInInstance={template.InInstance}, mapInstanceable={instanceable}).");
                    }
                }
            }
        }
    }

    private ShipTransport? CreateTransport(TransportTemplate template, Map map)
    {
        int frame = -1;
        for (int i = 0; i < template.KeyFrames.Count; i++)
        {
            if (template.KeyFrames[i].Node.MapId == map.MapId)
            {
                frame = i;
                break;
            }
        }

        if (frame < 0)
        {
            return null;
        }

        // A continent ship sails the shared copy only (vmangos GetContinentInstanceId; this base has one continent instance).
        bool instanceable = map.Template?.Instanceable ?? false;
        if (!instanceable && map.InstanceId != 0)
        {
            return null;
        }

        if (instanceable != template.InInstance)
        {
            _logger.LogError("Transport {Entry} attempted creation in map {MapId} but its route is {Kind}", template.Entry, map.MapId,
                template.InInstance ? "instanced" : "a continent route");
            return null;
        }

        var ship = new ShipTransport(this, template, frame, _world.NowMs);
        _ships.Add(ship);
        AddToMap(ship, map);
        _logger.LogDebug("Created transport {Entry} on map {MapId}", template.Entry, map.MapId);
        return ship;
    }

    private void OnMapUnloading(Map map)
    {
        if (!_byMap.Remove(map, out List<ShipTransport>? ships))
        {
            return;
        }

        foreach (ShipTransport ship in ships)
        {
            foreach (Unit passenger in ship.Passengers.ToArray())
            {
                ship.RemovePassenger(passenger);
            }

            ship.CurrentMap = null;
            _ships.Remove(ship);
        }
    }

    // vmangos Unit::CleanupsBeforeDelete / Player::RemoveFromWorld: a player leaving the world leaves its ship.
    // The seat is kept for the final snapshot, which is taken after this handler (vmangos saves transport_guid and the offset).
    private void OnPlayerLoggingOut(Player player)
    {
        if (player.Transport is { } ship)
        {
            player.LogoutTransportSeat = new TransportSeat(ship.Guid.Low, player.Movement.TransportX, player.Movement.TransportY,
                player.Movement.TransportZ, player.Movement.TransportOrientation);
            ship.RemovePassenger(player);
        }

        _justBoarded.Remove(player.Guid);
    }

    // vmangos Player::LoadFromDB (Player.cpp:14794-14838): a character saved aboard is put back on its ship at its offset; the
    // ship may have sailed to the other continent meanwhile, then the character follows it there. A ship that is gone, or an
    // offset off the ship (more than 250 yards), sends the character to its bind point. vmangos decides before the map is
    // entered; here the player enters its saved map first (on land when the ship is elsewhere, so nothing unknown is named on
    // the wire) and boards plus far-teleports, or goes to the bind point, right after the map update.
    private void RestoreSeat(Map map, Player player, TransportSeat seat)
    {
        ShipTransport? ship = _ships.FirstOrDefault(s => s.Guid.Low == seat.Guid && s.CurrentMap is not null);
        float x = seat.X, y = seat.Y, z = seat.Z, o = seat.Orientation;
        bool offsetValid = IsOffsetWithinBounds(seat.X, seat.Y, seat.Z);
        if (ship is not null && offsetValid)
        {
            ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
            offsetValid = Maps.Grid.GridDefines.IsValidMapCoord(x, y, z, o);
        }

        if (ship is null || !offsetValid)
        {
            _logger.LogWarning("{Player} was saved on transport {Guid} which {Reason}; sending it to its bind point", player.Name, seat.Guid,
                ship is null ? "does not sail" : "it is not on");
            if (ship is null)
            {
                ReportMissingOnMap(
                    seat.Guid,
                    $"Player {player.Name} (guid {player.Guid.Low}) logged in with saved transport seat on transport entry {seat.Guid} on map {map.MapId}, but that transport is not active in the world.");
            }
            else if (!player.Locomotion.SuppressAnomalies)
            {
                TransportAnomalies.Report(
                    "passenger-left-behind",
                    player,
                    $"Player logged in with out-of-bounds saved offset ({seat.X:F2}, {seat.Y:F2}, {seat.Z:F2}) on transport entry {ship.Entry} on map {map.MapId}; relocating to homebind.");
            }
            map.RunAfterUpdate(() => TeleportToHomebind?.Invoke(player));
            return;
        }

        if (ReferenceEquals(ship.CurrentMap, map))
        {
            player.SetTransportData(ship.Guid, seat.X, seat.Y, seat.Z, seat.Orientation);
            ship.AddPassenger(player, adjustCoords: false);
            player.RelocateOnTransport(x, y, z, o);
            return;
        }

        // The ship is on the other map. The character enters its saved map on land: its self create must not name a ship
        // this map never sent (vmangos never has that state on the wire). It boards right before the far teleport; until then
        // a save or a logout keeps the stored seat (vmangos has no such window: LoadFromDB puts the character aboard at once).
        player.BoardingTransportSeat = seat;
        map.RunAfterUpdate(() => FollowShip(map, player, ship, seat));
    }

    // The deferred half of RestoreSeat for a ship on the other map: board at the saved offset and far-teleport to the ship
    // where it is now (it may have moved on, or even sailed back); the bind point when that fails.
    private void FollowShip(Map map, Player player, ShipTransport ship, TransportSeat seat)
    {
        if (player.BoardingTransportSeat != seat)
        {
            return; // a later login or seat restore owns the player now
        }

        player.BoardingTransportSeat = null;
        if (!ReferenceEquals(player.Map, map) || player.Transport is not null)
        {
            return; // logged out or moved on meanwhile
        }

        if (ship.CurrentMap is { } current && TeleportPassenger is { } teleport)
        {
            float x = seat.X, y = seat.Y, z = seat.Z, o = seat.Orientation;
            ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
            if (Maps.Grid.GridDefines.IsValidMapCoord(x, y, z, o))
            {
                player.SetTransportData(ship.Guid, seat.X, seat.Y, seat.Z, seat.Orientation);
                ship.AddPassenger(player, adjustCoords: false);
                if (teleport(player, current.MapId, x, y, z, o))
                {
                    return;
                }

                ship.RemovePassenger(player);
            }
        }

        if (!player.IsLoggingOut && !player.Locomotion.SuppressAnomalies)
        {
            TransportAnomalies.Report(
                "passenger-left-behind",
                player,
                $"Deferred login seat restore could not carry player to transport entry {ship.Entry} on map {ship.CurrentMap?.MapId.ToString() ?? "null"}; relocating to homebind.");
        }
        player.RemoveMovementFlags(MovementFlags.OnTransport);
        player.ClearTransportData();
        TeleportToHomebind?.Invoke(player);
    }

    private void AddToMap(ShipTransport ship, Map map)
    {
        ship.CurrentMap = map;
        ship.MapId = map.MapId;
        if (!_byMap.TryGetValue(map, out List<ShipTransport>? ships))
        {
            ships = [];
            _byMap[map] = ships;
        }

        if (!ships.Contains(ship))
        {
            ships.Add(ship);
        }

        SendCreateToMap(ship);
    }

    private void RemoveFromMap(ShipTransport ship)
    {
        if (ship.CurrentMap is not { } map)
        {
            return;
        }

        SendOutOfRangeToMap(ship);
        if (_byMap.TryGetValue(map, out List<ShipTransport>? ships))
        {
            ships.Remove(ship);
        }

        ship.CurrentMap = null;
    }

    private void SendCreateToMap(ShipTransport ship)
    {
        if (ship.CurrentMap is not { } map)
        {
            return;
        }

        foreach (Player player in map.Players.ToArray())
        {
            if (!ReferenceEquals(player.Transport, ship))
            {
                TransportPackets.Send(player, TransportPackets.BuildCreate([ship], player, _world.NowMs), CompressionThreshold);
            }
        }
    }

    private void SendOutOfRangeToMap(ShipTransport ship)
    {
        if (ship.CurrentMap is not { } map)
        {
            return;
        }

        PacketWriter? packet = null;
        foreach (Player player in map.Players.ToArray())
        {
            if (!ReferenceEquals(player.Transport, ship))
            {
                packet ??= TransportPackets.BuildOutOfRange([ship]);
                TransportPackets.Send(player, packet, CompressionThreshold);
            }
        }
    }

    private int CompressionThreshold => _world.Options.UpdateCompressionThreshold;

    private void Raise(Action<ShipTransport, Unit>? handlers, ShipTransport ship, Unit passenger)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<ShipTransport, Unit> handler in handlers.GetInvocationList().Cast<Action<ShipTransport, Unit>>())
        {
            try
            {
                handler(ship, passenger);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("transport.passenger-handler", ex);
                AnomalyLedger.Shared.Report("transport", "lifecycle-fault", $"transport entry {ship.Entry}", ex.Message);
                _logger.LogFault("map.transport", ex, "transport passenger handler failed");
            }
        }
    }

    /// <summary>The per-map hook: the ships of the map move with its update, and its players get them at entry and lose them at exit.</summary>
    private sealed class TransportMapUpdater(TransportSystem system) : IMapUpdater
    {
        public void Update(Map map, uint diffMs) => system.UpdateMap(map);

        public void OnPlayerRemoved(Map map, Player player) => system.OnPlayerRemoved(map, player);

        public void OnPlayerAdding(Map map, Player player) => system.OnPlayerAdding(map, player);

        public void OnWritingSelf(Map map, Player player, UpdateData selfPacket) => system.OnWritingSelf(map, player, selfPacket);
    }
}

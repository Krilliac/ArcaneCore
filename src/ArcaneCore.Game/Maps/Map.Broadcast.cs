using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

public sealed partial class Map
{
    /// <summary>
    /// Relay a movement packet (packed mover GUID + movement block) to every player whose client
    /// has the mover — vmangos sends through the mover's broadcaster, whose listeners are the
    /// players that have it in their visible set.
    /// </summary>
    public void BroadcastToObservers(WorldObject source, WorldOpcode opcode, ReadOnlySpan<byte> payload)
        => BroadcastToObservers(source, opcode, payload, except: null);

    /// <summary>
    /// <see cref="BroadcastToObservers(WorldObject, WorldOpcode, ReadOnlySpan{byte})"/> leaving out <paramref name="except"/> as well (vmangos
    /// SendMessageToSetExcept: the possessor of a unit does not get the movement its own client sent).
    /// </summary>
    public void BroadcastToObservers(WorldObject source, WorldOpcode opcode, ReadOnlySpan<byte> payload, Player? except)
    {
        if (!_observers.TryGetValue(source.Guid, out HashSet<Player>? observers))
        {
            return;
        }

        if (_world.IsParallelPhase && !_world.HoldsSerialGate)
        {
            // Map work outside the serial gate (an ungated creature pass): a session may also be sent to by another map's gated work
            // (a whisper, a group update), so the packets wait in this map's outbox until the map next takes the gate (FlushOutbox),
            // in the order they were made.
            _outbox.Add(new OutboxPacket(source, opcode, payload.ToArray(), except));
            return;
        }

        SendToObservers(observers, source, except, opcode, payload);
    }

    /// <summary>Send to every observer but the source and <paramref name="except"/>. Plain HashSet foreach (struct enumerator, no allocation; a recipient that throws is recorded and skipped); takes no gate and touches no outbox.</summary>
    private void SendToObservers(HashSet<Player> observers, WorldObject source, Player? except, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        foreach (Player other in observers)
        {
            if (!ReferenceEquals(other, source) && !ReferenceEquals(other, except))
            {
                try
                {
                    other.Session.Send(opcode, payload);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One recipient's fault must not drop the packet for the observers after it (nor, in a flush, the packets after this one).
                    _logger.LogFault("map.observer-send", ex, "map {MapId} instance {InstanceId} send to an observer failed; the other observers still get {Opcode}", MapId, InstanceId, opcode);
                }
            }
        }
    }

    private readonly record struct OutboxPacket(WorldObject Source, WorldOpcode Opcode, byte[] Payload, Player? Except);

    private readonly List<OutboxPacket> _outbox = [];

    /// <summary>Packets made outside the serial gate during the parallel maps phase and not sent yet (diagnostics, tests).</summary>
    internal int OutboxCount => _outbox.Count;

    /// <summary>
    /// Send the packets <see cref="BroadcastToObservers(WorldObject, WorldOpcode, ReadOnlySpan{byte}, Player?)"/> held back while this map
    /// ran outside the serial gate, in order, to the source's observers as they are now (nothing in between can change them: only this
    /// map's own update could, and it was making the packets). The caller holds the gate. Cheap when empty.
    /// </summary>
    internal void FlushOutbox()
    {
        if (_outbox.Count == 0)
        {
            return;
        }

        try
        {
            foreach (OutboxPacket packet in _outbox)
            {
                if (_observers.TryGetValue(packet.Source.Guid, out HashSet<Player>? observers))
                {
                    SendToObservers(observers, packet.Source, packet.Except, packet.Opcode, packet.Payload);
                }
            }
        }
        finally
        {
            _outbox.Clear(); // whatever threw, a packet must not fail every later flush as well
        }
    }

    /// <summary>
    /// Send a packet to every player within <paramref name="range"/> of <paramref name="source"/>,
    /// as vmangos Map::MessageDistBroadcast → MessageDistDeliverer does: a 3D distance check
    /// plus both bounding radii (WorldObject::IsWithinDist defaults), an optional same-team
    /// filter (no exemptions), and a range of 0 meaning the whole map. The distance is from the
    /// listener's camera (Camera::GetBody: far sight, mind vision, possession), not their feet.
    /// Only the cells within range (plus the largest bounding radius in the map) are visited.
    /// </summary>
    public void BroadcastInRange(
        WorldObject source, float range, WorldOpcode opcode, ReadOnlySpan<byte> payload,
        bool includeSelf, Team? onlyTeam = null)
    {
        if (range <= 0)
        {
            foreach (Player player in _players.Values)
            {
                DeliverInRange(player, source, range, opcode, payload, includeSelf, onlyTeam);
            }

            return;
        }

        // vmangos visits cameras in those cells (MessageDistDeliverer::Visit): a player whose view
        // point is here hears the message even when their body is in another cell.
        float searchRadius = range + source.BoundingRadius + _grid.MaxBoundingRadius;
        bool useScratch = !_broadcastScratchInUse;
        if (useScratch) _broadcastScratchInUse = true;
        List<Player> players = useScratch ? _broadcastPlayersScratch : [];
        List<WorldObject> eyes = useScratch ? _broadcastEyesScratch : [];
        try
        {
            players.Clear();
            eyes.Clear();
            _grid.CollectPlayers(source.X, source.Y, searchRadius, players);
            _grid.CollectObjects(source.X, source.Y, searchRadius, eyes);
            foreach (WorldObject eye in eyes)
            {
                if (UnitControl.ViewerOf(eye) is { } viewer && ReferenceEquals(viewer.Map, this))
                {
                    players.Add(viewer);
                }
            }

            SortByJoinOrderDistinct(players);
            foreach (Player player in players)
            {
                if (ReferenceEquals(player.Map, this))
                {
                    DeliverInRange(player, source, range, opcode, payload, includeSelf, onlyTeam);
                }
            }
        }
        finally
        {
            if (useScratch)
            {
                players.Clear();
                eyes.Clear();
                _broadcastScratchInUse = false;
            }
        }
    }

    private readonly List<Player> _broadcastPlayersScratch = [];
    private readonly List<WorldObject> _broadcastEyesScratch = [];
    private bool _broadcastScratchInUse;

    // Reused by CountPlayersWithin: world thread only, and the collect it holds the list for calls nothing back, so it is never re-entered.
    private readonly List<Player> _countScratch = [];

    /// <summary>
    /// The number of players of this map within <paramref name="radius"/> yards (2D, centre to centre) of a point. One grid query over the
    /// few cells the circle touches (<see cref="GridContainer.CollectPlayers"/>) plus an exact distance test on the players it returns:
    /// no walk over every player of the map, and no allocation once the scratch list has grown. A playerbot is skipped when
    /// <paramref name="includeBots"/> is false (<see cref="IPlayerSession.IsPlayerbot"/>), and so is a player in GM mode or hidden by <see cref="Player.GmInvisible"/> (<c>.gm visible off</c> and the invisibility spell do not turn GM mode on); ghosts count. World thread.
    /// </summary>
    public int CountPlayersWithin(float x, float y, float radius, bool includeBots)
    {
        List<Player> found = _countScratch;
        found.Clear();
        _grid.CollectPlayers(x, y, radius, found);
        float limit = radius * radius;
        int count = 0;
        foreach (Player player in found)
        {
            float dx = player.X - x;
            float dy = player.Y - y;
            // Written as a negation so a player at a non-finite position (which every grid query returns) is never counted.
            if (!ReferenceEquals(player.Map, this) || player.IsGameMaster || player.GmInvisible || !((dx * dx) + (dy * dy) <= limit) || (!includeBots && player.Session.IsPlayerbot))
            {
                continue;
            }

            count++;
        }

        found.Clear();
        return count;
    }

    private static void DeliverInRange(
        Player player, WorldObject source, float range, WorldOpcode opcode, ReadOnlySpan<byte> payload,
        bool includeSelf, Team? onlyTeam)
    {
        if (ReferenceEquals(player, source))
        {
            if (includeSelf)
            {
                player.Session.Send(opcode, payload);
            }

            return;
        }

        if (onlyTeam is { } team && player.Team != team)
        {
            return;
        }

        if (range > 0)
        {
            // vmangos MessageDistDeliverer: Camera::GetBody()->IsWithinDist, the view point (far sight,
            // mind vision, the possessed unit), not the player's feet. The view point is this player
            // when nothing else holds the camera.
            WorldObject ear = player.ViewPoint;
            float dx = ear.X - source.X;
            float dy = ear.Y - source.Y;
            float dz = ear.Z - source.Z;
            float max = range + ear.BoundingRadius + source.BoundingRadius;
            if ((dx * dx) + (dy * dy) + (dz * dz) >= max * max)
            {
                return;
            }
        }

        player.Session.Send(opcode, payload);
    }
}

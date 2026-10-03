using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Updates;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// One map instance: its players, their visibility and the per-tick update pipeline.
/// <para>
/// Thread affinity: world thread only. <see cref="Update"/> runs, in order:
/// (1) each player's queued in-world packets, (2) visibility for players that moved,
/// (3) values updates for objects whose fields changed, (4) a flush of every client's
/// queued update blocks. Players are only added/removed in phases outside (2)–(4), so a
/// client never receives blocks about an object it was just told to destroy.
/// </para>
/// <para>
/// Visibility is the distance form of vmangos' grid/cell visibility; a cell index can slot
/// in behind <see cref="UpdateVisibility"/> without changing behaviour.
/// </para>
/// </summary>
public sealed class Map
{
    /// <summary>
    /// Continent visibility distance. vmangos <c>DEFAULT_VISIBILITY_DISTANCE</c> = 100 yards
    /// (ObjectDefines.h), the default of <c>Visibility.Distance.Continents</c>.
    /// </summary>
    public const float VisibilityRange = 100.0f;

    /// <summary>
    /// Extra distance an already-visible unit may move away before it is removed, so a player
    /// standing on the boundary does not flicker. vmangos <c>Visibility.Distance.Grey.Unit</c>
    /// default = 1 yard (World.cpp), applied in <c>WorldObject::IsWithinVisibilityDistanceOf</c>.
    /// </summary>
    public const float VisibilityGreyDistance = 1.0f;

    private readonly WorldRuntime _world;
    private readonly ILogger _logger;
    private readonly Dictionary<ObjectGuid, Player> _players = [];
    private readonly List<WorldObject> _valuesQueue = [];
    private readonly List<IMapUpdater> _updaters = [];
    private bool _inUpdatePhase;

    internal Map(uint mapId, WorldRuntime world, ILogger logger)
    {
        MapId = mapId;
        _world = world;
        _logger = logger;
    }

    public uint MapId { get; }

    public int PlayerCount => _players.Count;

    public IReadOnlyCollection<Player> Players => _players.Values;

    public Player? FindPlayer(ObjectGuid guid) => _players.GetValueOrDefault(guid);

    /// <summary>The systems attached with <see cref="AddUpdater"/>, in attach order.</summary>
    public IReadOnlyList<IMapUpdater> Updaters => _updaters;

    /// <summary>Attach a per-map system, updated every tick after the timers (world thread).</summary>
    public void AddUpdater(IMapUpdater updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        _updaters.Add(updater);
    }

    /// <summary>The first attached system of type <typeparamref name="T"/>, if any.</summary>
    public T? FindUpdater<T>()
        where T : class, IMapUpdater
        => _updaters.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Put a player into the map: its own create block goes out at once as its own packet
    /// (vmangos Map::SendInitSelf), then it is exchanged with every player in range.
    /// </summary>
    public void AddPlayer(Player player)
    {
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        if (player.Map is not null)
        {
            throw new InvalidOperationException($"{player.Guid} is already in map {player.Map.MapId}");
        }

        player.MapId = MapId;
        player.Map = this;
        _players[player.Guid] = player;

        // The create block carries every current value, so pending changes are moot.
        player.ClearChangedFields();
        player.IsQueuedForUpdate = false;

        PacketWriter block = player.PendingUpdates.BeginBlock();
        UpdateBlockWriter.WriteCreateBlock(block, player, player, isNewObject: false, _world.NowMs);
        player.PendingUpdates.EndBlock();
        FlushPlayer(player);

        UpdateVisibility(player);
        player.NeedsVisibilityUpdate = false;
    }

    /// <summary>
    /// Take a player out of the map. Every client that sees it gets SMSG_DESTROY_OBJECT
    /// (vmangos WorldObject::DestroyForNearbyPlayers / Object::DestroyForPlayer).
    /// </summary>
    public void RemovePlayer(Player player)
    {
        EnsureWorldThread();
        EnsureNotInUpdatePhase();
        if (!_players.Remove(player.Guid))
        {
            return;
        }

        // SMSG_DESTROY_OBJECT (vanilla): the full 8-byte object GUID.
        Span<byte> destroy = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destroy, player.Guid.Value);
        foreach (Player other in _players.Values)
        {
            if (other.VisibleObjects.Remove(player.Guid))
            {
                other.Session.Send(WorldOpcode.SmsgDestroyObject, destroy);
            }
        }

        if (player.IsQueuedForUpdate)
        {
            _valuesQueue.Remove(player);
            player.IsQueuedForUpdate = false;
        }

        foreach (IMapUpdater updater in _updaters)
        {
            updater.OnPlayerRemoved(this, player);
        }

        player.ClearChangedFields();
        player.VisibleObjects.Clear();
        player.PendingUpdates.Clear();
        player.NeedsVisibilityUpdate = false;
        player.Map = null;
    }

    /// <summary>
    /// Relay a movement packet (packed mover GUID + movement block) to every player whose client
    /// has the mover — vmangos sends through the mover's broadcaster, whose listeners are the
    /// players that have it in their visible set.
    /// </summary>
    public void BroadcastToObservers(WorldObject source, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        foreach (Player other in _players.Values)
        {
            if (!ReferenceEquals(other, source) && other.VisibleObjects.Contains(source.Guid))
            {
                other.Session.Send(opcode, payload);
            }
        }
    }

    /// <summary>
    /// Send a packet to every player within <paramref name="range"/> of <paramref name="source"/>,
    /// as vmangos Map::MessageDistBroadcast → MessageDistDeliverer does: a 3D distance check
    /// plus both bounding radii (WorldObject::IsWithinDist defaults), an optional same-team
    /// filter (no exemptions), and a range of 0 meaning the whole map.
    /// </summary>
    public void BroadcastInRange(
        WorldObject source, float range, WorldOpcode opcode, ReadOnlySpan<byte> payload,
        bool includeSelf, Team? onlyTeam = null)
    {
        foreach (Player player in _players.Values)
        {
            if (ReferenceEquals(player, source))
            {
                if (includeSelf)
                {
                    player.Session.Send(opcode, payload);
                }

                continue;
            }

            if (onlyTeam is { } team && player.Team != team)
            {
                continue;
            }

            if (range > 0)
            {
                float dx = player.X - source.X;
                float dy = player.Y - source.Y;
                float dz = player.Z - source.Z;
                float max = range + player.BoundingRadius + source.BoundingRadius;
                if ((dx * dx) + (dy * dy) + (dz * dz) >= max * max)
                {
                    continue;
                }
            }

            player.Session.Send(opcode, payload);
        }
    }

    /// <summary>One simulation step (world thread).</summary>
    public void Update(uint diffMs)
    {
        // (1) in-world packets
        foreach (Player player in _players.Values.ToArray())
        {
            if (player.Map != this)
            {
                continue; // removed while processing an earlier player
            }

            try
            {
                player.Session.ProcessWorldPackets(player);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "packet handling failed for {Player}; disconnecting", player.Name);
                player.Session.Kick();
            }
        }

        // (1b) timers: logouts whose countdown is over (vmangos WorldSession::Update → LogoutPlayer)
        uint now = _world.NowMs;
        foreach (Player player in _players.Values.ToArray())
        {
            if (player.Map == this && player.IsLogoutDue(now, _world.Options.LogoutDelayMs))
            {
                _world.LogoutPlayer(player);
            }
        }

        // (1c) per-map systems (creatures, …) — see IMapUpdater
        foreach (IMapUpdater updater in _updaters)
        {
            try
            {
                updater.Update(this, diffMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "map {MapId} updater {Updater} failed", MapId, updater.GetType().Name);
            }
        }

        _inUpdatePhase = true;
        try
        {
            // (2) visibility
            foreach (Player player in _players.Values)
            {
                if (player.NeedsVisibilityUpdate)
                {
                    UpdateVisibility(player);
                    player.NeedsVisibilityUpdate = false;
                }
            }

            // (3) values updates
            foreach (WorldObject obj in _valuesQueue)
            {
                SendValuesUpdate(obj);
                obj.ClearChangedFields();
                obj.IsQueuedForUpdate = false;
            }

            _valuesQueue.Clear();

            // (4) flush
            foreach (Player player in _players.Values)
            {
                FlushPlayer(player);
            }
        }
        finally
        {
            _inUpdatePhase = false;
        }
    }

    /// <summary>
    /// vmangos <c>WorldObject::IsWithinVisibilityDistanceOf</c> for two players on the same
    /// map (no transport, not taxi-flying): a 2D distance check (<c>IsWithinDistInMap(...,
    /// is3D = false)</c>) against the map visibility distance, plus the grey distance when the
    /// target is already visible, plus both bounding radii (<c>SizeFactor::BoundingRadius</c>),
    /// with a strict <c>&lt;</c> comparison (<c>WorldObject::IsWithinDist</c>).
    /// </summary>
    public static bool IsWithinVisibilityDistance(WorldObject viewer, WorldObject target, bool alreadyVisible)
    {
        float dx = viewer.X - target.X;
        float dy = viewer.Y - target.Y;
        float maxDist = VisibilityRange
            + (alreadyVisible ? VisibilityGreyDistance : 0.0f)
            + viewer.BoundingRadius
            + target.BoundingRadius;
        return (dx * dx) + (dy * dy) < maxDist * maxDist;
    }

    internal void QueueValuesUpdate(WorldObject obj)
    {
        obj.IsQueuedForUpdate = true;
        _valuesQueue.Add(obj);
    }

    /// <summary>Re-evaluate visibility between a player and everyone else, in both directions.</summary>
    private void UpdateVisibility(Player player)
    {
        foreach (Player other in _players.Values)
        {
            if (ReferenceEquals(other, player))
            {
                continue;
            }

            UpdateVisibilityOf(viewer: player, target: other);
            UpdateVisibilityOf(viewer: other, target: player);
        }
    }

    /// <summary>vmangos Player::UpdateVisibilityOf&lt;Player&gt;: create on entering range, out-of-range on leaving.</summary>
    private void UpdateVisibilityOf(Player viewer, Player target)
    {
        bool inVisibleList = viewer.VisibleObjects.Contains(target.Guid);
        bool inRange = IsWithinVisibilityDistance(viewer, target, inVisibleList);

        if (inVisibleList && !inRange)
        {
            viewer.VisibleObjects.Remove(target.Guid);
            viewer.PendingUpdates.AddOutOfRange(target.Guid);
        }
        else if (!inVisibleList && inRange)
        {
            viewer.VisibleObjects.Add(target.Guid);
            PacketWriter block = viewer.PendingUpdates.BeginBlock();
            UpdateBlockWriter.WriteCreateBlock(block, target, viewer, isNewObject: false, _world.NowMs);
            viewer.PendingUpdates.EndBlock();
        }
    }

    private void SendValuesUpdate(WorldObject obj)
    {
        if (obj is Player self && ReferenceEquals(self.Map, this))
        {
            AppendValues(obj, self);
        }

        foreach (Player viewer in _players.Values)
        {
            if (!ReferenceEquals(viewer, obj) && viewer.VisibleObjects.Contains(obj.Guid))
            {
                AppendValues(obj, viewer);
            }
        }
    }

    private static void AppendValues(WorldObject obj, Player viewer)
    {
        PacketWriter block = viewer.PendingUpdates.BeginBlock();
        int before = block.Length;
        if (UpdateBlockWriter.TryWriteValuesBlock(block, obj, viewer))
        {
            viewer.PendingUpdates.EndBlock();
        }
        else if (block.Length == before)
        {
            viewer.PendingUpdates.CancelBlock();
        }
    }

    private void FlushPlayer(Player player)
        => player.PendingUpdates.Flush(
            (opcode, payload) => player.Session.Send(opcode, payload),
            _world.Options.UpdateCompressionThreshold);

    private void EnsureWorldThread()
    {
        if (!_world.IsWorldThread)
        {
            throw new InvalidOperationException("map state may only be changed on the world thread");
        }
    }

    private void EnsureNotInUpdatePhase()
    {
        if (_inUpdatePhase)
        {
            throw new InvalidOperationException("players cannot be added or removed during the visibility/values/flush phases");
        }
    }
}

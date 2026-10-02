using ArcaneCore.Protocol;

namespace ArcaneCore.Game;

/// <summary>
/// A single map instance and its visibility logic. Each player keeps a set of the other
/// players its client currently has an object for (vmangos <c>Player::m_visibleGUIDs</c>).
/// Entering the map, moving and leaving re-evaluate that set: a player coming into range is
/// sent a create-update, a player going out of range is sent an out-of-range block, and
/// movement is relayed only to players that currently see the mover.
/// <para>
/// This is the distance-based form of the grid/cell visibility from the charter — a per-cell
/// grid index is a later optimization that can slot in behind this same surface.
/// </para>
/// <para>
/// Thread affinity: every mutation, visibility decision and the resulting sends happen under
/// one per-map lock, so the per-viewer packet order (create → movement → out-of-range/destroy)
/// always matches the visible-set transitions, regardless of which session thread moves.
/// </para>
/// </summary>
public sealed class Map(uint mapId)
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

    // SMSG_UPDATE_OBJECT block type for out-of-range GUIDs: UPDATETYPE_OUT_OF_RANGE_OBJECTS = 4
    // in 1.12.1 (vmangos UpdateData.h — follows UPDATETYPE_CREATE_OBJECT2 = 3 for builds > 1.8.4).
    private const byte UpdateTypeOutOfRangeObjects = 4;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<uint, Participant> _players = [];

    public uint MapId { get; } = mapId;

    public int PlayerCount
    {
        get
        {
            _lock.Wait();
            try
            {
                return _players.Count;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>
    /// Add a player and exchange create-updates with every in-range player already present,
    /// so both clients render each other.
    /// </summary>
    public async Task EnterAsync(IWorldPlayer who, uint serverTimeMs)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var self = new Participant(who);
            _players[who.Player.Guid] = self;
            await UpdateVisibilityAsync(self, serverTimeMs).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Apply a movement update: store the new position, re-evaluate visibility in both
    /// directions (create for newly in-range players, out-of-range for players that left the
    /// range), then relay the movement packet (already prefixed with the mover's packed GUID)
    /// to every player that now sees the mover.
    /// </summary>
    public async Task MoveAsync(
        IWorldPlayer mover, float x, float y, float z, float orientation,
        WorldOpcode opcode, ReadOnlyMemory<byte> relayPacket, uint serverTimeMs)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_players.TryGetValue(mover.Player.Guid, out Participant? self))
            {
                return;
            }

            PlayerObject player = mover.Player;
            player.X = x;
            player.Y = y;
            player.Z = z;
            player.Orientation = orientation;

            await UpdateVisibilityAsync(self, serverTimeMs).ConfigureAwait(false);

            // vmangos relays movement through the mover's broadcaster, whose listeners are
            // exactly the players that have the mover in their visible set.
            foreach (Participant other in _players.Values)
            {
                if (other != self && other.Visible.Contains(player.Guid))
                {
                    await other.Session.SendToClientAsync(opcode, relayPacket).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Remove a player and tell every player that sees it to destroy its object.</summary>
    public async Task LeaveAsync(IWorldPlayer who)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            uint guid = who.Player.Guid;
            if (!_players.Remove(guid))
            {
                return;
            }

            // Leaving the world uses SMSG_DESTROY_OBJECT (vmangos Object::DestroyForPlayer via
            // WorldObject::DestroyForNearbyPlayers), not an out-of-range block.
            byte[] destroy = BuildDestroy(guid);
            foreach (Participant other in _players.Values)
            {
                if (other.Visible.Remove(guid))
                {
                    await other.Session.SendToClientAsync(WorldOpcode.SmsgDestroyObject, destroy).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// vmangos <c>WorldObject::IsWithinVisibilityDistanceOf</c> for two players on the same
    /// map (no transport, not taxi-flying): a 2D distance check (<c>IsWithinDistInMap(...,
    /// is3D = false)</c>) against the map visibility distance, plus the grey distance when the
    /// target is already visible, plus both bounding radii (<c>SizeFactor::BoundingRadius</c>),
    /// with a strict <c>&lt;</c> comparison (<c>WorldObject::IsWithinDist</c>).
    /// </summary>
    public static bool IsWithinVisibilityDistance(PlayerObject viewer, PlayerObject target, bool alreadyVisible)
    {
        float dx = viewer.X - target.X;
        float dy = viewer.Y - target.Y;
        float maxDist = VisibilityRange
            + (alreadyVisible ? VisibilityGreyDistance : 0.0f)
            + viewer.BoundingRadius + target.BoundingRadius;
        return (dx * dx) + (dy * dy) < maxDist * maxDist;
    }

    /// <summary>
    /// Re-evaluate visibility between <paramref name="self"/> and every other player, in both
    /// directions (vmangos <c>Player::UpdateVisibilityOf</c> run for the mover as viewer and
    /// for each nearby player as viewer). Caller holds <see cref="_lock"/>.
    /// </summary>
    private async Task UpdateVisibilityAsync(Participant self, uint serverTimeMs)
    {
        foreach (Participant other in _players.Values)
        {
            if (other == self)
            {
                continue;
            }

            await UpdateVisibilityOfAsync(viewer: self, target: other, serverTimeMs).ConfigureAwait(false);
            await UpdateVisibilityOfAsync(viewer: other, target: self, serverTimeMs).ConfigureAwait(false);
        }
    }

    private static async Task UpdateVisibilityOfAsync(Participant viewer, Participant target, uint serverTimeMs)
    {
        uint targetGuid = target.Session.Player.Guid;
        bool inVisibleList = viewer.Visible.Contains(targetGuid);
        bool inRange = IsWithinVisibilityDistance(viewer.Session.Player, target.Session.Player, inVisibleList);

        if (inVisibleList && !inRange)
        {
            // vmangos Player::UpdateVisibilityOf<Player>: BuildOutOfRangeUpdateBlock.
            viewer.Visible.Remove(targetGuid);
            await viewer.Session.SendToClientAsync(WorldOpcode.SmsgUpdateObject,
                BuildOutOfRange(target.Session.Player.ObjectGuid)).ConfigureAwait(false);
        }
        else if (!inVisibleList && inRange)
        {
            // vmangos Player::UpdateVisibilityOf<Player>: BuildCreateUpdateBlockForPlayer.
            viewer.Visible.Add(targetGuid);
            await viewer.Session.SendToClientAsync(WorldOpcode.SmsgUpdateObject,
                ObjectUpdateBuilder.BuildOtherCreate(target.Session.Player, serverTimeMs)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// SMSG_UPDATE_OBJECT carrying only an out-of-range block. Layout per vmangos
    /// <c>WorldPackets::ObjectUpdate::UpdateObject::AppendBodyTo</c> (build &gt; 1.8.4):
    /// uint32 blockCount (the out-of-range list counts as one block), uint8 hasTransport,
    /// uint8 UPDATETYPE_OUT_OF_RANGE_OBJECTS, uint32 guidCount, then each GUID packed.
    /// </summary>
    public static byte[] BuildOutOfRange(ObjectGuid guid)
    {
        byte[] packed = guid.ToPacked();
        var writer = new PacketWriter(10 + packed.Length);
        writer.WriteUInt32(1); // block count
        writer.WriteByte(0);   // has transport
        writer.WriteByte(UpdateTypeOutOfRangeObjects);
        writer.WriteUInt32(1); // guid count
        writer.WriteBytes(packed);
        return writer.AsMemory().ToArray();
    }

    private static byte[] BuildDestroy(uint guid)
    {
        // SMSG_DESTROY_OBJECT (vanilla): the full 8-byte object GUID.
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.AsMemory().ToArray();
    }

    /// <summary>A player on this map plus the GUIDs its client currently has objects for.</summary>
    private sealed class Participant(IWorldPlayer session)
    {
        public IWorldPlayer Session { get; } = session;

        public HashSet<uint> Visible { get; } = [];
    }
}

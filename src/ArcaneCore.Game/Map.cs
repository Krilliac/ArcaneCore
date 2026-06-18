using System.Collections.Concurrent;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game;

/// <summary>
/// A single map instance and its visibility logic. Players on the same map within
/// <see cref="VisibilityRange"/> see each other and each other's movement. This is the
/// distance-based form of the grid/cell visibility from the charter — a per-cell grid is a
/// later optimization that can slot in behind this same surface.
/// </summary>
public sealed class Map(uint mapId)
{
    /// <summary>Vanilla player visibility is roughly 100 yards.</summary>
    public const float VisibilityRange = 100.0f;

    private readonly ConcurrentDictionary<uint, IWorldPlayer> _players = new();

    public uint MapId { get; } = mapId;

    /// <summary>
    /// Add a player and exchange create-updates with every in-range player already present,
    /// so both clients render each other.
    /// </summary>
    public async Task EnterAsync(IWorldPlayer who, uint serverTimeMs)
    {
        IReadOnlyList<IWorldPlayer> existing = [.. _players.Values];
        _players[who.Player.Guid] = who;

        foreach (IWorldPlayer other in existing)
        {
            if (!InRange(who.Player, other.Player))
            {
                continue;
            }

            await who.SendToClientAsync(WorldOpcode.SmsgUpdateObject,
                ObjectUpdateBuilder.BuildOtherCreate(other.Player, serverTimeMs)).ConfigureAwait(false);
            await other.SendToClientAsync(WorldOpcode.SmsgUpdateObject,
                ObjectUpdateBuilder.BuildOtherCreate(who.Player, serverTimeMs)).ConfigureAwait(false);
        }
    }

    /// <summary>Remove a player and tell in-range players to destroy its object.</summary>
    public async Task LeaveAsync(IWorldPlayer who)
    {
        _players.TryRemove(who.Player.Guid, out _);
        byte[] destroy = BuildDestroy(who.Player.Guid);

        foreach (IWorldPlayer other in _players.Values)
        {
            if (InRange(who.Player, other.Player))
            {
                await other.SendToClientAsync(WorldOpcode.SmsgDestroyObject, destroy).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Relay a movement packet (already prefixed with the mover's packed GUID) to in-range players.</summary>
    public async Task RelayMovementAsync(IWorldPlayer mover, WorldOpcode opcode, ReadOnlyMemory<byte> packet)
    {
        foreach (IWorldPlayer other in _players.Values)
        {
            if (other.Player.Guid != mover.Player.Guid && InRange(mover.Player, other.Player))
            {
                await other.SendToClientAsync(opcode, packet).ConfigureAwait(false);
            }
        }
    }

    public int PlayerCount => _players.Count;

    private static bool InRange(PlayerObject a, PlayerObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= VisibilityRange * VisibilityRange;
    }

    private static byte[] BuildDestroy(uint guid)
    {
        // SMSG_DESTROY_OBJECT (vanilla): the full 8-byte object GUID.
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.AsMemory().ToArray();
    }
}

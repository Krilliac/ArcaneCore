using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Transports;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>SMSG_TRANSFER_PENDING: the target map and, for a passenger whose ship changes maps, the ship entry and the old map.</summary>
public sealed record TransferPendingView(uint MapId, uint? TransportEntry, uint? OldMapId);

/// <summary>SMSG_NEW_WORLD: the map and the position (a passenger's offset on its ship).</summary>
public sealed record NewWorldView(uint MapId, float X, float Y, float Z, float Orientation);

/// <summary>
/// Ship steps for scripted bots (docs/areas/transports.md). A client stands on a ship by sending movement that carries the
/// ONTRANSPORT flag, the ship GUID and its offset on the ship; it leaves by sending movement without them. These build that
/// movement from the bot's own server state, as a client would from what it sees, and run it through the real movement handler.
/// </summary>
public static class ScenarioTransports
{
    /// <summary>The ship of route <paramref name="entry"/> (world thread read), or a scenario failure when there is none.</summary>
    public static Task<ShipTransport> ShipAsync(this ScenarioContext context, uint entry) => context.World.InvokeAsync(() =>
        TransportSystem.Of(context.World)?.FindByEntry(entry)
            ?? throw new ScenarioAssertionException($"no ship of route {entry} (World:Transports:Enabled and content)"));

    /// <summary>MSG_MOVE_HEARTBEAT standing on <paramref name="ship"/> at the given offset; the world position is computed from the ship.</summary>
    public static async Task<bool> BoardAsync(this ScenarioBot bot, ShipTransport ship, float offsetX, float offsetY, float offsetZ, float offsetO = 0f)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(ship);
        byte[] payload = await bot.ReadAsync(player =>
        {
            float x = offsetX, y = offsetY, z = offsetZ, o = offsetO;
            ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
            MovementInfo movement = player.Movement;
            movement.Flags = (movement.Flags & ~MovementFlags.MaskMoving) | MovementFlags.OnTransport;
            movement.X = x;
            movement.Y = y;
            movement.Z = z;
            movement.Orientation = o;
            movement.TransportGuid = ship.Guid.Value;
            movement.TransportX = offsetX;
            movement.TransportY = offsetY;
            movement.TransportZ = offsetZ;
            movement.TransportOrientation = offsetO;
            return Write(movement, player);
        }).ConfigureAwait(false);
        return await bot.SendAsync(WorldOpcode.MsgMoveHeartbeat, payload).ConfigureAwait(false);
    }

    /// <summary>MSG_MOVE_HEARTBEAT at the bot's current world position without the ship: it steps off (or jumps overboard).</summary>
    public static async Task<bool> LeaveShipAsync(this ScenarioBot bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        byte[] payload = await bot.ReadAsync(player =>
        {
            MovementInfo movement = player.Movement;
            movement.Flags &= ~(MovementFlags.OnTransport | MovementFlags.MaskMoving);
            movement.TransportGuid = 0;
            movement.TransportX = movement.TransportY = movement.TransportZ = movement.TransportOrientation = 0;
            return Write(movement, player);
        }).ConfigureAwait(false);
        return await bot.SendAsync(WorldOpcode.MsgMoveHeartbeat, payload).ConfigureAwait(false);
    }

    /// <summary>CMSG_MOVE_TIME_SKIPPED for the bot itself (the first one after boarding makes the server send the ship again).</summary>
    public static Task<bool> TimeSkippedAsync(this ScenarioBot bot, uint milliseconds)
    {
        ArgumentNullException.ThrowIfNull(bot);
        var payload = new PacketWriter(12);
        payload.WriteUInt64(bot.Guid.Value);
        payload.WriteUInt32(milliseconds);
        return bot.SendAsync(WorldOpcode.CmsgMoveTimeSkipped, payload.ToArray());
    }

    /// <summary>SMSG_TRANSFER_PENDING (vmangos TransferPending::AppendBodyTo): u32 map [u32 transport entry, u32 old map].</summary>
    public static TransferPendingView TransferPending(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var reader = new PacketReader(payload);
        uint map = reader.ReadUInt32();
        return payload.Length >= 12 ? new TransferPendingView(map, reader.ReadUInt32(), reader.ReadUInt32()) : new TransferPendingView(map, null, null);
    }

    /// <summary>SMSG_NEW_WORLD: u32 map, f32 x, y, z, orientation.</summary>
    public static NewWorldView NewWorld(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var reader = new PacketReader(payload);
        return new NewWorldView(reader.ReadUInt32(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static byte[] Write(MovementInfo movement, Player player)
    {
        movement.Time = unchecked(player.Movement.Time + 100); // a client clock that only moves forward; the server stamps its own time
        var writer = new PacketWriter(64);
        movement.Write(writer);
        return writer.ToArray();
    }
}

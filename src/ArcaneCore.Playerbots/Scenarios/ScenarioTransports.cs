using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

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

/// <summary>
/// A bot takes a real boat across the sea (docs/areas/transports.md): the Ratchet - Booty Bay ship (<c>gameobject_template</c> 20808,
/// TaxiPath 241 of the client's TaxiPathNode.dbc). It waits until the ship lies at one of its ports, stands on the dock and boards it
/// through its movement, is carried through the ship's map change to the other continent still aboard (SMSG_TRANSFER_PENDING names the
/// ship, SMSG_NEW_WORLD carries the offset), rides into the other port and steps off there. Afterwards, and after a failure, the bot is
/// put back where it stood. Needs <c>World:Transports:Enabled</c> and the content (<c>tools/content/refresh-world-content.ps1</c>,
/// <c>NpcServices:TaxiPathNodeDbcPath</c>). With the real clock of a live world it takes up to one round trip (about six minutes), so
/// <c>World:Playerbots:Scenarios:MaxDurationSeconds</c> must allow 600.
/// </summary>
public sealed class ShipCrossingScenario : IPlayerbotScenario
{
    /// <summary>The Ratchet - Booty Bay boat ("TEST Ship" in the data).</summary>
    public const uint Ship = 20808;

    /// <summary>Where the bot stands on the deck, relative to the ship.</summary>
    public const float DeckX = 0f, DeckY = 0f, DeckZ = 6f;

    public string Name => "ship";

    public string Description => "a bot boards the Ratchet - Booty Bay boat at a port, crosses to the other continent aboard and steps off at the other port";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        (ShipTransport ship, TimeSpan period) = await context.StepAsync("the boat sails between two ports on two maps", async () =>
        {
            ShipTransport found = await context.ShipAsync(Ship).ConfigureAwait(false);
            uint[] portMaps = await context.ReadAsync(() => found.Template.KeyFrames.Where(f => f.IsStopFrame).Select(f => f.Node.MapId).ToArray()).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(2, portMaps.Length, "ports of the route");
            ScenarioContext.Expect(portMaps[0] != portMaps[1], $"both ports lie on map {portMaps[0]}");
            return (found, TimeSpan.FromMilliseconds(found.Period));
        }).ConfigureAwait(false);

        ScenarioBot bot = await context.StepAsync("login " + PlayerbotScenarioCatalog.BotA, () => context.LoginAsync(PlayerbotScenarioCatalog.BotA)).ConfigureAwait(false);
        (uint MapId, float X, float Y, float Z, float O) origin = await bot.ReadAsync(p => (p.MapId, p.X, p.Y, p.Z, p.Orientation)).ConfigureAwait(false);
        try
        {
            TransportKeyFrame from = await context.StepAsync("the boat lies at a port", async () =>
            {
                await context.WaitUntilAsync("the boat waits at a port", () => !ship.IsMoving && ship.CurrentFrame.IsStopFrame, period).ConfigureAwait(false);
                return await context.ReadAsync(() => ship.CurrentFrame).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await context.StepAsync($"{bot.Name} boards at the dock on map {from.Node.MapId}", async () =>
            {
                (float x, float y, float z) = await context.ReadAsync(() => (ship.X, ship.Y, ship.Z)).ConfigureAwait(false);
                await context.PlaceAsync(bot, from.Node.MapId, x, y, z + DeckZ).ConfigureAwait(false);
                ScenarioContext.Expect(await bot.BoardAsync(ship, DeckX, DeckY, DeckZ).ConfigureAwait(false), "heartbeat aboard refused");
                await context.WaitUntilAsync($"{bot.Name} is a passenger", () => ReferenceEquals(bot.RequirePlayer().Transport, ship)).ConfigureAwait(false);
            }).ConfigureAwait(false);

            long mark = bot.Mark();
            uint to = await context.StepAsync("the boat changes maps with the bot aboard", async () =>
            {
                TransferPendingView pending = await bot.WaitForPacketAsync(WorldOpcode.SmsgTransferPending, ScenarioTransports.TransferPending,
                    p => p.TransportEntry == Ship, since: mark, timeout: period).ConfigureAwait(false);
                ScenarioContext.ExpectEqual(from.Node.MapId, pending.OldMapId ?? uint.MaxValue, "SMSG_TRANSFER_PENDING old map");
                NewWorldView arrival = await bot.WaitForPacketAsync(WorldOpcode.SmsgNewWorld, ScenarioTransports.NewWorld, since: mark).ConfigureAwait(false);
                ScenarioContext.ExpectEqual((pending.MapId, DeckX, DeckY, DeckZ), (arrival.MapId, arrival.X, arrival.Y, arrival.Z), "SMSG_NEW_WORLD");
                await context.WaitUntilAsync($"{bot.Name} is on map {pending.MapId} aboard", () => bot.Session!.Player is { IsInWorld: true, Map: { } map } player
                    && map.MapId == pending.MapId && ReferenceEquals(player.Transport, ship) && player.Movement.HasFlag(MovementFlags.OnTransport)).ConfigureAwait(false);
                return pending.MapId;
            }).ConfigureAwait(false);

            TransportKeyFrame port = await context.StepAsync($"the boat lies at the other port with {bot.Name} aboard", async () =>
            {
                // A frame's Index counts within its spline stretch, so the ports are told apart by their TaxiPathNode row.
                await context.WaitUntilAsync("the boat waits at the other port", () => !ship.IsMoving && ship.CurrentFrame.IsStopFrame
                    && ship.CurrentFrame.Node.Id != from.Node.Id, period).ConfigureAwait(false);
                TransportKeyFrame reached = await context.ReadAsync(() => ship.CurrentFrame).ConfigureAwait(false);
                ScenarioContext.ExpectEqual(to, reached.Node.MapId, "the other port's map");
                await context.ExpectAsync(bot, "still aboard, at its place on the deck", p =>
                {
                    float x = DeckX, y = DeckY, z = DeckZ, o = 0f;
                    ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
                    return ReferenceEquals(p.Transport, ship) && MathF.Abs(p.X - x) < 0.01f && MathF.Abs(p.Y - y) < 0.01f && MathF.Abs(p.Z - z) < 0.01f;
                }).ConfigureAwait(false);
                return reached;
            }).ConfigureAwait(false);

            await context.StepAsync($"{bot.Name} steps off at the port", async () =>
            {
                ScenarioContext.Expect(await bot.LeaveShipAsync().ConfigureAwait(false), "heartbeat off the ship refused");
                await context.WaitUntilAsync($"{bot.Name} is off the boat", () => bot.RequirePlayer().Transport is null).ConfigureAwait(false);
                (uint map, float distance) = await bot.ReadAsync(p => (p.MapId, MathF.Sqrt(((p.X - port.Node.X) * (p.X - port.Node.X)) + ((p.Y - port.Node.Y) * (p.Y - port.Node.Y))))).ConfigureAwait(false);
                ScenarioContext.ExpectEqual(port.Node.MapId, map, "map after stepping off");
                ScenarioContext.Expect(distance < 30f, $"{distance:0.0} yards from the port");
            }).ConfigureAwait(false);
        }
        finally
        {
            await GoHomeAsync(context, bot, origin).ConfigureAwait(false);
        }
    }

    // The bot goes back to where it stood (off the boat first): the scenario bots are shared, and one left aboard would sail on.
    private static async Task GoHomeAsync(ScenarioContext context, ScenarioBot bot, (uint MapId, float X, float Y, float Z, float O) origin)
    {
        if (bot.Session is null)
        {
            return;
        }

        TeleportService teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
        await context.CleanupAsync($"bring {bot.Name} back", async () =>
        {
            await context.WaitUntilAsync($"{bot.Name} is not between maps", () => bot.Session?.Player is not { } player
                || (player.IsInWorld && teleports.StageOf(player) is null)).ConfigureAwait(false);
            if (await context.ReadAsync(() => bot.Session?.Player is not { IsInWorld: true }).ConfigureAwait(false))
            {
                return; // logged out: nothing to move
            }

            if (await context.ReadAsync(() => bot.Session?.Player?.Transport is not null).ConfigureAwait(false))
            {
                await bot.LeaveShipAsync().ConfigureAwait(false);
                await context.WaitUntilAsync($"{bot.Name} is off the boat", () => bot.Session?.Player is not { } player || player.Transport is null).ConfigureAwait(false);
            }

            await context.PlaceAsync(bot, origin.MapId, origin.X, origin.Y, origin.Z, origin.O).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

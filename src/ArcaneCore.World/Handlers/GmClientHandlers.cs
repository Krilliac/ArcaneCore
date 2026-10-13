using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// The GM client's built-in teleport requests (docs/reference/opcodes.md "unhandled"): CMSG_WORLD_TELEPORT
/// (vmangos MiscHandler.cpp HandleWorldTeleportOpcode: administrators only, ignored on a taxi flight,
/// otherwise a "no permission" notice) and CMSG_TELEPORT_TO_UNIT (HandleTeleportToUnitOpcode: runs
/// ".goname &lt;name&gt;" through the command table, which applies the command's own security). World thread.
/// </summary>
public sealed class GmClientHandlers : IOpcodeHandlerGroup
{
    /// <summary>vmangos MAX_PLAYER_NAME.</summary>
    internal const int MaxPlayerName = 12;

    internal const string NoPermission = "You do not have permission to perform that function";

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgWorldTeleport, HandleWorldTeleport);
        table.OnWorld(WorldOpcode.CmsgTeleportToUnit, HandleTeleportToUnit);
    }

    private static void HandleWorldTeleport(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 24)
        {
            return;
        }

        var reader = new PacketReader(payload);
        reader.ReadUInt32(); // client time
        uint map = reader.ReadUInt32();
        float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(), o = reader.ReadSingle();

        if ((player.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            return;
        }

        if (session.Security < AccountSecurity.Administrator)
        {
            session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(NoPermission));
            return;
        }

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(o))
        {
            return;
        }

        session.Services.GetRequiredService<TeleportFeature>().Teleports.TeleportTo(player, map, x, y, z, o);
    }

    private static void HandleTeleportToUnit(WorldSession session, Player player, byte[] payload)
    {
        string name;
        try
        {
            name = new PacketReader(payload).ReadCString();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        if (name.Length == 0 || name.Length > MaxPlayerName || name.Any(char.IsWhiteSpace))
        {
            return;
        }

        CommandTable commands = session.Services.GetRequiredService<CommandTableSource>().Current;
        var context = new CommandContext(session, player, commands);
        try
        {
            commands.Execute(context, "goname " + name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Logger.LogError(ex, "[{Endpoint}] teleport-to-unit '{Name}' by {Player} failed", session.RemoteEndpoint, name, player.Name);
        }
    }
}

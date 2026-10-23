using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
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
/// Also the client console's <c>setrawpos</c> (CMSG_MOVE_SET_RAW_POSITION and MSG_MOVE_TELEPORT_CHEAT, HandleMoveSetRawPosition: an
/// administrator's near teleport, the same refusals) and CMSG_WHOIS (HandleWhoisOpcode: an administrator reads a character's account
/// and last address; ArcaneCore keeps no e-mail, which vmangos shows as "Unknown" when empty).
/// </summary>
public sealed class GmClientHandlers : IOpcodeHandlerGroup
{
    /// <summary>vmangos MAX_PLAYER_NAME.</summary>
    internal const int MaxPlayerName = 12;

    internal const string NoPermission = "You do not have permission to perform that function";       // mangos_string 804
    internal const string NeedCharacterName = "Please provide character name";                          // 807
    internal const string PlayerNotFound = "Player {0} not found or offline";                            // 808
    internal const string AccountNotFound = "Account for character {0} not found";                       // 809

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgWorldTeleport, HandleWorldTeleport);
        table.OnWorld(WorldOpcode.CmsgTeleportToUnit, HandleTeleportToUnit);
        table.OnWorld(WorldOpcode.CmsgMoveSetRawPosition, HandleSetRawPosition);
        table.OnWorld(WorldOpcode.MsgMoveTeleportCheat, HandleSetRawPosition);
        table.OnWorld(WorldOpcode.CmsgWhois, HandleWhois);
    }

    private static void Notify(WorldSession session, string text)
        => session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(text));

    /// <summary>
    /// vmangos HandleMoveSetRawPosition (MiscHandler.cpp:1069-1087): x, y, z, o on the current map. Ignored on a taxi flight; a
    /// non-administrator only gets the permission notice, so the opcode cannot move a player the anticheat watches.
    /// </summary>
    private static void HandleSetRawPosition(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 16 || !player.IsInWorld || (player.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            return;
        }

        var reader = new PacketReader(payload);
        float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(), o = reader.ReadSingle();
        if (session.Security < AccountSecurity.Administrator)
        {
            Notify(session, NoPermission);
            return;
        }

        if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(o))
        {
            session.Services.GetRequiredService<TeleportFeature>().Teleports.TeleportTo(player, player.MapId, x, y, z, o);
        }
    }

    /// <summary>
    /// vmangos HandleWhoisOpcode (MiscHandler.cpp:1089-1136): administrators only; an online character's account name, e-mail and last
    /// address from the login database, answered with SMSG_WHOIS.
    /// </summary>
    private static void HandleWhois(WorldSession session, Player player, byte[] payload)
    {
        if (session.Security < AccountSecurity.Administrator)
        {
            Notify(session, NoPermission);
            return;
        }

        string raw;
        try
        {
            raw = new PacketReader(payload).ReadCString();
        }
        catch (ArgumentOutOfRangeException)
        {
            raw = string.Empty;
        }

        string name = CharacterNames.Normalize(raw);
        if (name.Length == 0 || name.Length > MaxPlayerName)
        {
            Notify(session, NeedCharacterName);
            return;
        }

        if (session.World.FindOnlinePlayer(name) is not { Session: WorldSession target })
        {
            Notify(session, string.Format(PlayerNotFound, name));
            return;
        }

        string username = target.AccountName;
        IServiceScopeFactory scopes = session.Services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                Account? account = await scope.ServiceProvider.GetRequiredService<IAccountStore>().FindByUsernameAsync(username).ConfigureAwait(false);
                if (account is null)
                {
                    Notify(session, string.Format(AccountNotFound, name));
                    return;
                }

                string acc = account.Username.Length == 0 ? "Unknown" : account.Username;
                string lastIp = account.LastIp.Length == 0 ? "Unknown" : account.LastIp;
                var writer = new PacketWriter(64);
                writer.WriteCString($"{name}'s account is {acc}, e-mail: Unknown, last ip: {lastIp}");
                session.Send(WorldOpcode.SmsgWhois, writer.ToArray());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("handler.whois", ex);
                session.Logger.LogError(ex, "[{Endpoint}] whois '{Name}' failed", session.RemoteEndpoint, name);
            }
        });
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
            FaultLedger.Shared.Record("handler.teleport-to-unit", ex);
            session.Logger.LogError(ex, "[{Endpoint}] teleport-to-unit '{Name}' by {Player} failed", session.RemoteEndpoint, name, player.Name);
        }
    }
}

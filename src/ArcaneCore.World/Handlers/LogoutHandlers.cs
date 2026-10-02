using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Logging out to the character screen (vmangos MiscHandler.cpp HandleLogoutRequestOpcode /
/// HandleLogoutCancelOpcode). The 20-second countdown runs in the map update, which completes
/// it through <see cref="Game.Maps.WorldRuntime.LogoutPlayer"/>. CMSG_PLAYER_LOGOUT is
/// deliberately unhandled: vmangos' handler is empty. World thread.
/// </summary>
public sealed class LogoutHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgLogoutRequest, HandleLogoutRequest);
        table.OnWorld(WorldOpcode.CmsgLogoutCancel, HandleLogoutCancel);
    }

    private static void HandleLogoutRequest(WorldSession session, Player player, byte[] payload)
    {
        if (player.IsLoggingOut)
        {
            return; // the countdown is already running
        }

        // Refusals, in vmangos order: combat, then jumping/falling. (The GM freeze aura check
        // arrives with auras in M12.)
        LogoutResult refusal = (player.UnitFlags & UnitFlags.InCombat) != 0 ? LogoutResult.InCombat
            : player.Movement.HasFlag(MovementFlags.Jumping | MovementFlags.FallingFar) ? LogoutResult.JumpingOrFalling
            : LogoutResult.Success;
        if (refusal != LogoutResult.Success)
        {
            session.Send(WorldOpcode.SmsgLogoutResponse, MiscPackets.BuildLogoutResponse(refusal, instant: false));
            return;
        }

        // Instant in a resting area, on a taxi, or for staff at or above InstantLogout.
        if ((player.Flags & PlayerFlags.Resting) != 0
            || (player.UnitFlags & UnitFlags.TaxiFlight) != 0
            || session.Security >= session.World.Options.InstantLogoutSecurity)
        {
            session.Send(WorldOpcode.SmsgLogoutResponse, MiscPackets.BuildLogoutResponse(LogoutResult.Success, instant: true));
            session.World.LogoutPlayer(player);
            return;
        }

        player.BeginLogout(session.World.NowMs);
        session.Send(WorldOpcode.SmsgLogoutResponse, MiscPackets.BuildLogoutResponse(LogoutResult.Success, instant: false));
    }

    private static void HandleLogoutCancel(WorldSession session, Player player, byte[] payload)
    {
        session.Send(WorldOpcode.SmsgLogoutCancelAck, []);
        if (player.IsLoggingOut)
        {
            player.CancelLogout();
        }
    }
}

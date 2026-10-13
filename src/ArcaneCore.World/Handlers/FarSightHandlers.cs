using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// CMSG_FAR_SIGHT (vmangos MiscHandler.cpp:1138-1155): one byte, 0 puts the camera back on the player and 1 back on the PLAYER_FARSIGHT
/// object (Eagle Eye, Mind Vision, a possessed unit); without such an object on the map nothing happens. World thread.
/// </summary>
public sealed class FarSightHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgFarSight, HandleFarSight);

    private static void HandleFarSight(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 1 || payload[0] > 1)
        {
            return;
        }

        UnitControl.SetFarSightActive(player, payload[0] == 1);
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Death;

/// <summary>
/// wow_messages world/resurrect/cmsg_self_res.wowm: opcode 0x02B3 has no payload.
/// vmangos SpellHandler.cpp:461-473 selects the spell from the server-owned field.
/// </summary>
public sealed class SelfResurrectionHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgSelfRes, Handle);

    private static void Handle(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length == 0)
        {
            session.Services.GetRequiredService<SpellFeature>().System.TrySelfResurrect(player);
        }
    }
}

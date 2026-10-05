using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Death;

/// <summary>Build 5875 CMSG_RESURRECT_RESPONSE: unpacked u64 caster GUID, u8 acceptance.</summary>
public sealed class PlayerResurrectionHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgResurrectResponse, HandleResponse);

    private static void HandleResponse(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var caster = new ObjectGuid(reader.ReadUInt64());
        bool accept = reader.ReadByte() != 0;
        session.Services.GetRequiredService<PlayerResurrectionFeature>().Respond(player, caster, accept);
    }
}

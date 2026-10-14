using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;

namespace ArcaneCore.World.Net.Handlers;

/// <summary>
/// Miscellaneous 1.12.1 client opcodes (<c>CMSG_GETDEATHBINDZONE</c> 0x0156 and <c>CMSG_GROUP_CANCEL</c> 0x0070).
/// </summary>
public sealed class MiscPacketHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgGetdeathbindzone, HandleGetDeathBindZone);
        table.OnWorld(WorldOpcode.CmsgGroupCancel, static (_, _, _) => { });
    }

    /// <summary>
    /// <c>CMSG_GETDEATHBINDZONE</c> → <c>SMSG_BINDZONEREPLY</c> (u32 homebind area id, u32 current zone id).
    /// </summary>
    private static void HandleGetDeathBindZone(WorldSession session, Player player, byte[] payload)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(player.Home.ZoneId);
        writer.WriteUInt32(player.ZoneId);
        session.Send(WorldOpcode.SmsgBindzonereply, writer.ToArray());
    }
}

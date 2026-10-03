using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Social;

/// <summary>
/// The tabard designer and guild emblem requests (vmangos NPCHandler.cpp:49-69 and GuildHandler.cpp:684-735; layouts from
/// vmangos Server/Packets/Npc.cpp:35-38, Guild.cpp:51-59 and gtker msg_save_guild_emblem_client). A short payload
/// disconnects the client, trailing bytes are ignored.
/// </summary>
public sealed class TabardHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.MsgTabardvendorActivate, (s, p, d) =>
            SocialHandlers.Social(s).Guilds.ActivateTabardVendor(p, new ObjectGuid(new PacketReader(d).ReadUInt64())));
        table.OnWorld(WorldOpcode.MsgSaveGuildEmblem, HandleSaveEmblem);
    }

    /// <summary>MSG_SAVE_GUILD_EMBLEM (client to server): u64 vendor, then five u32 read as signed values.</summary>
    private static void HandleSaveEmblem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var vendor = new ObjectGuid(reader.ReadUInt64());
        int style = reader.ReadInt32();
        int color = reader.ReadInt32();
        int borderStyle = reader.ReadInt32();
        int borderColor = reader.ReadInt32();
        int background = reader.ReadInt32();
        SocialHandlers.Social(session).Guilds.SaveEmblem(player, vendor, style, color, borderStyle, borderColor, background);
    }
}

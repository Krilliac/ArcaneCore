using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Guilds;

/// <summary>
/// Tabard and emblem packets (vmangos Guild.h GuildEmblem, Server/Packets/Guild.cpp:141-149 and Npc.cpp:143-153;
/// gtker msg_save_guild_emblem_server, msg_tabardvendor_activate).
/// </summary>
public static class GuildEmblemPackets
{
    /// <summary>MSG_SAVE_GUILD_EMBLEM (server to client): u32 result (vmangos ERR_GUILDEMBLEM_*).</summary>
    public static byte[] BuildSaveResult(GuildEmblemResult result)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32((uint)result);
        return writer.ToArray();
    }

    /// <summary>MSG_TABARDVENDOR_ACTIVATE (server to client): u64 tabard designer guid.</summary>
    public static byte[] BuildTabardVendorActivate(ObjectGuid npc)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(npc.Value);
        return writer.ToArray();
    }
}

/// <summary>MSG_SAVE_GUILD_EMBLEM result (vmangos Guild.h:187-194; gtker GuildEmblemResult for 1.12).</summary>
public enum GuildEmblemResult : uint
{
    Success = 0,

    /// <summary>Never sent: neither vmangos nor cmangos validates the colours.</summary>
    InvalidTabardColors = 1,
    NoGuild = 2,
    NotGuildMaster = 3,
    NotEnoughMoney = 4,

    /// <summary>"[-ZERO] fails silently" (GuildHandler.cpp:688): the packet is sent and the client prints nothing.</summary>
    NoMessage = 5,
}

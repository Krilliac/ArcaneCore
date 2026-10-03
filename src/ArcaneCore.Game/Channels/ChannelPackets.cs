using System.Text;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Channels;

/// <summary>Channel packets (vmangos Channel.cpp Make* and List; gtker smsg_channel_notify / smsg_channel_list).</summary>
public static class ChannelPackets
{
    /// <summary>SMSG_CHANNEL_NOTIFY with no payload after the channel name (vmangos Channel::MakeNotifyPacket).</summary>
    public static byte[] BuildNotify(ChatNotify type, string channel)
    {
        var writer = Start(type, channel, 0);
        return writer.ToArray();
    }

    /// <summary>SMSG_CHANNEL_NOTIFY followed by one guid (JOINED, LEFT, PASSWORD_CHANGED, OWNER_CHANGED, ANNOUNCEMENTS_*, MODERATION_*, PLAYER_ALREADY_MEMBER, INVITE).</summary>
    public static byte[] BuildNotify(ChatNotify type, string channel, ObjectGuid guid)
    {
        var writer = Start(type, channel, 8);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }

    /// <summary>SMSG_CHANNEL_NOTIFY followed by a name (PLAYER_NOT_FOUND, CHANNEL_OWNER, PLAYER_NOT_BANNED, PLAYER_INVITED, PLAYER_INVITE_BANNED).</summary>
    public static byte[] BuildNotify(ChatNotify type, string channel, string name)
    {
        var writer = Start(type, channel, name.Length + 1);
        writer.WriteCString(name);
        return writer.ToArray();
    }

    /// <summary>SMSG_CHANNEL_NOTIFY followed by target and source guids (PLAYER_KICKED, PLAYER_BANNED, PLAYER_UNBANNED).</summary>
    public static byte[] BuildNotify(ChatNotify type, string channel, ObjectGuid target, ObjectGuid source)
    {
        var writer = Start(type, channel, 16);
        writer.WriteUInt64(target.Value);
        writer.WriteUInt64(source.Value);
        return writer.ToArray();
    }

    /// <summary>YOU_JOINED: u32 channel flags, u32 0 (vmangos Channel::MakeYouJoined: a non-zero number would be appended to the name).</summary>
    public static byte[] BuildYouJoined(string channel, ChannelFlags flags)
    {
        var writer = Start(ChatNotify.YouJoined, channel, 8);
        writer.WriteUInt32((uint)flags);
        writer.WriteUInt32(0);
        return writer.ToArray();
    }

    /// <summary>MODE_CHANGE: u64 guid, u8 old flags, u8 new flags (vmangos Channel::MakeModeChange).</summary>
    public static byte[] BuildModeChange(string channel, ObjectGuid guid, ChannelMemberFlags oldFlags, ChannelMemberFlags newFlags)
    {
        var writer = Start(ChatNotify.ModeChange, channel, 10);
        writer.WriteUInt64(guid.Value);
        writer.WriteByte((byte)oldFlags);
        writer.WriteByte((byte)newFlags);
        return writer.ToArray();
    }

    /// <summary>SMSG_CHANNEL_LIST: CString name, u8 channel flags, u32 count, then u64 guid and u8 member flags each (vmangos Channel::List).</summary>
    public static byte[] BuildList(string channel, ChannelFlags flags, IReadOnlyList<(ObjectGuid Guid, ChannelMemberFlags Flags)> members)
    {
        var writer = new PacketWriter(channel.Length + 6 + (members.Count * 9));
        writer.WriteCString(channel);
        writer.WriteByte((byte)flags);
        writer.WriteUInt32((uint)members.Count);
        foreach ((ObjectGuid guid, ChannelMemberFlags memberFlags) in members)
        {
            writer.WriteUInt64(guid.Value);
            writer.WriteByte((byte)memberFlags);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_MESSAGECHAT for CHAT_MSG_CHANNEL: u8 type, u32 language, CString channel, u32
    /// player rank (honor rank), u64 sender, u32 text length + 1, CString text, u8 chat tag
    /// (vmangos ChatHandler::BuildChatPacket, CHAT_MSG_CHANNEL case; gtker smsg_messagechat).
    /// </summary>
    public static byte[] BuildChannelMessage(string channel, Language language, ObjectGuid sender, string text, ChatTag tag, uint playerRank = 0)
    {
        var writer = new PacketWriter(32 + channel.Length + text.Length);
        writer.WriteByte((byte)ChatType.Channel);
        writer.WriteUInt32((uint)language);
        writer.WriteCString(channel);
        writer.WriteUInt32(playerRank);
        writer.WriteUInt64(sender.Value);
        writer.WriteUInt32((uint)Encoding.UTF8.GetByteCount(text) + 1);
        writer.WriteCString(text);
        writer.WriteByte((byte)tag);
        return writer.ToArray();
    }

    private static PacketWriter Start(ChatNotify type, string channel, int extra)
    {
        var writer = new PacketWriter(2 + channel.Length + extra);
        writer.WriteByte((byte)type);
        writer.WriteCString(channel);
        return writer;
    }
}

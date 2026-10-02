using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Packets;

/// <summary>Chat and notification packets (layouts per vmangos ChatHandler::BuildChatPacket for 1.12).</summary>
public static class ChatPackets
{
    /// <summary>
    /// SMSG_MESSAGECHAT for a player-originated or system message: u8 type, u32 language, then
    /// for SAY/PARTY/YELL the sender GUID twice, otherwise once; u32 length (incl. NUL),
    /// the message, u8 chat tag. gtker smsg_messagechat.wowm (1.7–1.12) has the same layout.
    /// Channel and monster messages use other branches and are built elsewhere.
    /// </summary>
    public static byte[] BuildMessage(ChatType type, Language language, ObjectGuid sender, string message, ChatTag tag)
    {
        if (type is ChatType.Channel or ChatType.MonsterSay or ChatType.MonsterYell or ChatType.MonsterEmote
            or ChatType.MonsterWhisper or ChatType.RaidBossEmote or ChatType.RaidBossWhisper)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "channel/monster chat has its own layout");
        }

        var writer = new PacketWriter(32 + message.Length);
        writer.WriteByte((byte)type);
        writer.WriteUInt32((uint)language);
        writer.WriteUInt64(sender.Value);
        if (type is ChatType.Say or ChatType.Party or ChatType.Yell)
        {
            writer.WriteUInt64(sender.Value);
        }

        WriteSizedString(writer, message);
        writer.WriteByte((byte)tag);
        return writer.ToArray();
    }

    /// <summary>A CHAT_MSG_SYSTEM line (vmangos Player::SendSysMessage: empty sender, universal).</summary>
    public static byte[] BuildSystemMessage(string message)
        => BuildMessage(ChatType.System, Language.Universal, ObjectGuid.Empty, message, ChatTag.None);

    /// <summary>SMSG_CHAT_PLAYER_NOT_FOUND: the name that was not found (gtker smsg_chat_player_not_found).</summary>
    public static byte[] BuildPlayerNotFound(string name)
    {
        var writer = new PacketWriter(name.Length + 1);
        writer.WriteCString(name);
        return writer.ToArray();
    }

    /// <summary>SMSG_NOTIFICATION: a centre-screen message (gtker smsg_notification).</summary>
    public static byte[] BuildNotification(string text)
    {
        var writer = new PacketWriter(text.Length + 1);
        writer.WriteCString(text);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_TEXT_EMOTE: u64 emoter, u32 text emote, u32 emote number, u32 target-name length
    /// (incl. NUL), target name or a single NUL (vmangos EmoteChatBuilder).
    /// </summary>
    public static byte[] BuildTextEmote(ObjectGuid emoter, uint textEmote, uint emoteNumber, string? targetName)
    {
        var writer = new PacketWriter(32);
        writer.WriteUInt64(emoter.Value);
        writer.WriteUInt32(textEmote);
        writer.WriteUInt32(emoteNumber);
        if (string.IsNullOrEmpty(targetName))
        {
            writer.WriteUInt32(1);
            writer.WriteByte(0);
        }
        else
        {
            WriteSizedString(writer, targetName);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_EMOTE: u32 animation emote, u64 unit (gtker smsg_emote).</summary>
    public static byte[] BuildEmote(uint emote, ObjectGuid unit)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt32(emote);
        writer.WriteUInt64(unit.Value);
        return writer.ToArray();
    }

    /// <summary>u32 length including the terminating NUL, then the NUL-terminated string.</summary>
    private static void WriteSizedString(PacketWriter writer, string value)
    {
        writer.WriteUInt32((uint)System.Text.Encoding.UTF8.GetByteCount(value) + 1);
        writer.WriteCString(value);
    }
}

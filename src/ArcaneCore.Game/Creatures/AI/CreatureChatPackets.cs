using System.Text;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Creature chat for build 5875 (SMSG_MESSAGECHAT monster branches and SMSG_EMOTE).
/// Layouts from gtker/wow_messages smsg_messagechat.wowm (1.7–1.12) and vmangos
/// ChatHandler::BuildChatPacket:
/// <list type="bullet">
/// <item>MONSTER_SAY / MONSTER_YELL: u8 type, u32 language, u64 sender, u32 name length
/// (incl. NUL), name, u64 target, u32 message length (incl. NUL), message, u8 chat tag.</item>
/// <item>MONSTER_EMOTE / MONSTER_WHISPER: u8 type, u32 language, u32 name length, name,
/// u64 target, sized message, u8 tag.</item>
/// </list>
/// </summary>
public static class CreatureChatPackets
{
    /// <summary>vmangos CONFIG_FLOAT_LISTEN_RANGE_SAY default.</summary>
    public const float SayRange = 25.0f;

    /// <summary>vmangos CONFIG_FLOAT_LISTEN_RANGE_YELL default.</summary>
    public const float YellRange = 300.0f;

    /// <summary>vmangos CONFIG_FLOAT_LISTEN_RANGE_TEXTEMOTE default.</summary>
    public const float TextEmoteRange = 25.0f;

    public static byte[] BuildMonsterMessage(ChatType type, uint language, ObjectGuid sender, string senderName, ObjectGuid target, string message)
    {
        ArgumentNullException.ThrowIfNull(senderName);
        ArgumentNullException.ThrowIfNull(message);
        var w = new PacketWriter(32 + senderName.Length + message.Length);
        w.WriteByte((byte)type);
        w.WriteUInt32(language);
        switch (type)
        {
            case ChatType.MonsterSay:
            case ChatType.MonsterYell:
                w.WriteUInt64(sender.Value);
                WriteSized(w, senderName);
                w.WriteUInt64(target.Value);
                break;
            case ChatType.MonsterEmote:
            case ChatType.MonsterWhisper:
                WriteSized(w, senderName);
                w.WriteUInt64(target.Value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "not a monster chat type");
        }

        WriteSized(w, message);
        w.WriteByte(0); // chat tag: none
        return w.ToArray();
    }

    /// <summary>SMSG_EMOTE: u32 animation emote, u64 unit (gtker smsg_emote).</summary>
    public static byte[] BuildEmote(uint emote, ObjectGuid unit)
    {
        var w = new PacketWriter(12);
        w.WriteUInt32(emote);
        w.WriteUInt64(unit.Value);
        return w.ToArray();
    }

    private static void WriteSized(PacketWriter w, string text)
    {
        w.WriteUInt32((uint)Encoding.UTF8.GetByteCount(text) + 1);
        w.WriteCString(text);
    }
}

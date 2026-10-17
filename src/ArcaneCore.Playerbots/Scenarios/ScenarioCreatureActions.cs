using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// Scenario harness extensions for creature AI scenarios: the text emote a client aims at a unit, and decoders for monster chat and
/// SMSG_ATTACKSTART. Kept apart from <see cref="ScenarioPackets"/> and <see cref="ScenarioDecoders"/> so other harness work does not
/// collide with them.
/// </summary>
public static class ScenarioCreatureActions
{
    /// <summary>
    /// CMSG_TEXT_EMOTE: u32 text emote (EmotesText.dbc), u32 emote number, u64 target (the server's own parsing,
    /// ChatHandlers.HandleTextEmote; vmangos WorldPackets::Misc::TextEmote).
    /// </summary>
    public static Task<bool> TextEmoteAsync(this ScenarioBot bot, uint textEmote, ObjectGuid target, uint emoteNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(bot);
        var w = new PacketWriter(16);
        w.WriteUInt32(textEmote);
        w.WriteUInt32(emoteNumber);
        w.WriteUInt64(target.Value);
        return bot.SendAsync(WorldOpcode.CmsgTextEmote, w.ToArray());
    }
}

/// <summary>SMSG_MESSAGECHAT from a creature: chat type, language, speaker guid (say and yell only), speaker name, target, text.</summary>
public sealed record MonsterChatView(ChatType Type, uint Language, ulong Speaker, string SpeakerName, ulong Target, string Message);

/// <summary>SMSG_ATTACKSTART: attacker and victim guids (CombatPackets.AttackStart).</summary>
public sealed record AttackStartView(ulong Attacker, ulong Victim);

/// <summary>Decoders for creature packets, mirroring the server writers (CreatureChatPackets, CombatPackets).</summary>
public static class ScenarioCreatureDecoders
{
    /// <summary>
    /// SMSG_MESSAGECHAT in the monster layout (CreatureChatPackets; vmangos ChatHandler::BuildChatPacket for CHAT_MSG_MONSTER_*): u8 type,
    /// u32 language, u64 speaker for say and yell, u32 name length and name, u64 target, u32 text length and text, u8 tag.
    /// </summary>
    public static MonsterChatView MonsterChat(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var r = new PacketReader(payload);
        var type = (ChatType)r.ReadByte();
        if (type is not (ChatType.MonsterSay or ChatType.MonsterYell or ChatType.MonsterEmote or ChatType.MonsterWhisper
            or ChatType.RaidBossEmote or ChatType.RaidBossWhisper))
        {
            throw new FormatException($"MonsterChat: {type} is not monster chat");
        }

        uint language = r.ReadUInt32();
        ulong speaker = type is ChatType.MonsterSay or ChatType.MonsterYell ? r.ReadUInt64() : 0;
        string name = Sized(ref r);
        ulong target = r.ReadUInt64();
        string text = Sized(ref r);
        return new MonsterChatView(type, language, speaker, name, target, text);
    }

    /// <summary>SMSG_ATTACKSTART: u64 attacker, u64 victim.</summary>
    public static AttackStartView AttackStart(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var r = new PacketReader(payload);
        return new AttackStartView(r.ReadUInt64(), r.ReadUInt64());
    }

    private static string Sized(ref PacketReader reader)
    {
        uint length = reader.ReadUInt32();
        if (length > reader.Remaining)
        {
            throw new FormatException("MonsterChat: string length");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes((int)length)).TrimEnd('\0');
    }
}

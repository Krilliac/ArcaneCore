using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>
/// The master's commands (mangoszero ChatShortcutActions.cpp; vmangos partybot command table, Chat.cpp:94-116), one word each,
/// by whisper or party chat.
/// </summary>
public enum PlayerbotPartyCommand
{
    /// <summary>Follow the master and assist it (leaves stay and passive).</summary>
    Follow,

    /// <summary>Hold this place.</summary>
    Stay,

    /// <summary>Attack the master's current target.</summary>
    Attack,

    /// <summary>Stop fighting and stay passive (also 'passive').</summary>
    Stop,

    /// <summary>Walk to the master now, then hold there.</summary>
    Come,

    /// <summary>Whisper back level, health %, mana % and the current activity.</summary>
    Status,

    /// <summary>Leave the group and go back to the bot's own goals.</summary>
    Leave,
}

/// <summary>Who a chat line comes from, for a party bot.</summary>
internal enum PlayerbotChatSource
{
    /// <summary>Not for the bot: its own line, a channel or say, a line from another managed bot.</summary>
    Ignore,

    /// <summary>The bot's master, by whisper or party chat: a command.</summary>
    Master,

    /// <summary>Somebody else's whisper: one polite, rate-limited answer.</summary>
    Stranger,
}

/// <summary>One decoded player SMSG_MESSAGECHAT (vmangos ChatHandler::BuildChatPacket; ChatPackets.BuildMessage).</summary>
internal readonly record struct PlayerbotChatLine(ChatType Type, Language Language, ObjectGuid Sender, string Text);

/// <summary>The party bot's chat: decoding what it hears, parsing commands, and the whispers it sends back (CMSG_MESSAGECHAT).</summary>
internal static class PlayerbotChatCommands
{
    /// <summary>The answer to a whisper from somebody who is not the bot's master.</summary>
    internal const string PoliteReply = "Sorry, I am a server bot. I only take orders from the player I am grouped with.";

    /// <summary>The answer to a whispered word the master sent that is no command.</summary>
    internal const string Help = "Commands: follow, stay, attack, stop, come, status, leave.";

    /// <summary>The longest line a bot reads as a command (anything longer is talk, not an order).</summary>
    internal const int MaxCommandLength = 16;

    /// <summary>
    /// A command word: trimmed, case-insensitive, nothing else on the line (the master's party talk is not taken for orders).
    /// 'passive' is 'stop'. False for anything else.
    /// </summary>
    internal static bool TryParse(string? text, out PlayerbotPartyCommand command)
    {
        command = default;
        if (text is null) return false;
        string word = text.Trim();
        if (word.Length == 0 || word.Length > MaxCommandLength) return false;
        switch (word.ToLowerInvariant())
        {
            case "follow": command = PlayerbotPartyCommand.Follow; return true;
            case "stay": command = PlayerbotPartyCommand.Stay; return true;
            case "attack": command = PlayerbotPartyCommand.Attack; return true;
            case "stop":
            case "passive": command = PlayerbotPartyCommand.Stop; return true;
            case "come": command = PlayerbotPartyCommand.Come; return true;
            case "status": command = PlayerbotPartyCommand.Status; return true;
            case "leave": command = PlayerbotPartyCommand.Leave; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Whose line it is: the master's whisper or party (raid) line is a command; another player's whisper gets the polite answer;
    /// everything else, and every line from the bot itself or from another managed bot, is ignored.
    /// </summary>
    internal static PlayerbotChatSource Classify(PlayerbotChatLine line, ObjectGuid self, ObjectGuid master, bool senderIsBot)
    {
        if (line.Sender.IsEmpty || line.Sender == self || senderIsBot) return PlayerbotChatSource.Ignore;
        bool whisper = line.Type == ChatType.Whisper;
        bool group = line.Type is ChatType.Party or ChatType.Raid or ChatType.RaidLeader;
        if (!master.IsEmpty && line.Sender == master && (whisper || group)) return PlayerbotChatSource.Master;
        return whisper ? PlayerbotChatSource.Stranger : PlayerbotChatSource.Ignore;
    }

    /// <summary>
    /// Decode a player chat line: u8 type, u32 language, u64 sender, a second u64 for say/party/yell, u32 size, CString text, u8 tag.
    /// False for channel and monster lines (their layout differs) and for a body that does not fit.
    /// </summary>
    internal static bool TryRead(byte[] payload, out PlayerbotChatLine line)
    {
        line = default;
        if (payload.Length < 18) return false;
        var type = (ChatType)payload[0];
        if (type is ChatType.Channel or ChatType.MonsterSay or ChatType.MonsterYell or ChatType.MonsterEmote
            or ChatType.MonsterWhisper or ChatType.RaidBossEmote or ChatType.RaidBossWhisper) return false;
        var reader = new PacketReader(payload.AsSpan(1));
        if (!reader.TryReadUInt32(out uint language) || !reader.TryReadUInt64(out ulong sender)) return false;
        if ((type is ChatType.Say or ChatType.Party or ChatType.Yell) && !reader.TryReadUInt64(out _)) return false;
        if (!reader.TryReadUInt32(out uint size) || size == 0 || size > reader.Remaining) return false;
        try
        {
            line = new PlayerbotChatLine(type, (Language)language, new ObjectGuid(sender), reader.ReadCString());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // no terminator (PacketReader's malformed-packet exception)
        }
    }

    /// <summary>CMSG_MESSAGECHAT whisper body: u32 type, u32 language, CString target, CString text.</summary>
    internal static byte[] Whisper(Language language, string to, string text)
    {
        var writer = new PacketWriter(16 + to.Length + text.Length);
        writer.WriteUInt32((uint)ChatType.Whisper);
        writer.WriteUInt32((uint)language);
        writer.WriteCString(to);
        writer.WriteCString(text);
        return writer.ToArray();
    }

    /// <summary>CMSG_MESSAGECHAT say/party body: u32 type, u32 language, CString text.</summary>
    internal static byte[] Message(ChatType type, Language language, string text)
    {
        var writer = new PacketWriter(12 + text.Length);
        writer.WriteUInt32((uint)type);
        writer.WriteUInt32((uint)language);
        writer.WriteCString(text);
        return writer.ToArray();
    }
}

/// <summary>
/// The bot's polite answers to strangers: one per sender within <see cref="WindowMs"/> of world time, and at most
/// <see cref="MaxPerWindow"/> in all within it, so a crowd of whisperers cannot make the bot spam.
/// </summary>
internal sealed class PlayerbotReplyLimiter
{
    internal const long WindowMs = 60_000;
    internal const int MaxPerWindow = 8;
    private readonly Dictionary<ObjectGuid, long> _lastBySender = [];

    internal bool TryTake(ObjectGuid sender, long nowMs)
    {
        foreach (ObjectGuid expired in _lastBySender.Where(entry => nowMs - entry.Value >= WindowMs).Select(entry => entry.Key).ToArray())
            _lastBySender.Remove(expired);
        if (_lastBySender.ContainsKey(sender) || _lastBySender.Count >= MaxPerWindow) return false;
        _lastBySender[sender] = nowMs;
        return true;
    }
}

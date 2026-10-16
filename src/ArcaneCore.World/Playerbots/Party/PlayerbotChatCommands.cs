using System.Text.RegularExpressions;
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

    /// <summary>Whisper back equipped items across slots 0..18.</summary>
    Gear,

    /// <summary>Whisper back free and total bag slots across backpack and equipped containers.</summary>
    Bags,

    /// <summary>Whisper back current money formatted in gold/silver/copper.</summary>
    Gold,

    /// <summary>Whisper back active quests and their completion state.</summary>
    Quests,

    /// <summary>Whisper back known professions or weapon/class skills with current/max values.</summary>
    Skills,

    /// <summary>Whisper back active talent build name, spent points, and free talent points.</summary>
    Talents,

    /// <summary>Run out-of-combat buffing and equipment upkeep immediately.</summary>
    Upkeep,

    /// <summary>Share active quests in the bot's quest log with the party.</summary>
    ShareQuests,

    /// <summary>Whisper back the list of available party commands.</summary>
    Help,

    /// <summary>Whisper back internal bot brain/motion/route/stall/trace diagnostic state (GM or master).</summary>
    Debug,

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

    /// <summary>
    /// Another managed bot's line, let through as input (<see cref="Chat.PlayerbotSpeechOptions.BotToBot"/>): never an order and never a
    /// fixed reply; only the chat service answers it, within the bot-to-bot turns and budgets.
    /// </summary>
    Bot,
}

/// <summary>
/// One decoded player SMSG_MESSAGECHAT (vmangos ChatHandler::BuildChatPacket; ChatPackets.BuildMessage, ChannelPackets.BuildChannelMessage).
/// <see cref="ChannelName"/> is the world channel of a CHAT_MSG_CHANNEL line (null otherwise).
/// </summary>
internal readonly record struct PlayerbotChatLine(ChatType Type, Language Language, ObjectGuid Sender, string Text, string? ChannelName = null);

/// <summary>One decoded SMSG_TEXT_EMOTE: who emoted, the text emote id, and the name of the target (empty: none).</summary>
internal readonly record struct PlayerbotTextEmote(ObjectGuid Emoter, uint TextEmote, string TargetName);

/// <summary>The party bot's chat: decoding what it hears, parsing commands, and the whispers it sends back (CMSG_MESSAGECHAT).</summary>
internal static partial class PlayerbotChatCommands
{
    /// <summary>The answer to a whisper from somebody who is not the bot's master.</summary>
    internal const string PoliteReply = "Sorry, I am a server bot. I only take orders from the player I am grouped with.";

    /// <summary>The answer to a whispered word the master sent that is no command, or to the 'help' / 'commands' order.</summary>
    internal const string Help = "Commands: follow, stay, come, attack, stop, status, gear, bags, gold, quests, skills, talents, buff, share, leave.";

    /// <summary>The command list shown to GameMasters (includes debug/diagnostic inspection commands).</summary>
    internal const string GmHelp = "Commands: follow, stay, come, attack, stop, status, gear, bags, gold, quests, skills, talents, buff, share, leave | GM: debug, diag, state, trace, brain, motion (.bot info/inspect).";

    /// <summary>Returns <see cref="GmHelp"/> when the caller is a GM, otherwise <see cref="Help"/>.</summary>
    internal static string HelpFor(bool isGm) => isGm ? GmHelp : Help;

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
            case "gear":
            case "equip":
            case "equipment": command = PlayerbotPartyCommand.Gear; return true;
            case "bags":
            case "bag":
            case "inv":
            case "inventory": command = PlayerbotPartyCommand.Bags; return true;
            case "gold":
            case "money":
            case "cash": command = PlayerbotPartyCommand.Gold; return true;
            case "quests":
            case "quest": command = PlayerbotPartyCommand.Quests; return true;
            case "skills":
            case "professions":
            case "prof": command = PlayerbotPartyCommand.Skills; return true;
            case "talents":
            case "spec":
            case "talent": command = PlayerbotPartyCommand.Talents; return true;
            case "buff":
            case "buffs":
            case "upkeep": command = PlayerbotPartyCommand.Upkeep; return true;
            case "share":
            case "sharequests": command = PlayerbotPartyCommand.ShareQuests; return true;
            case "help":
            case "commands":
            case "command":
            case "cmd":
            case "cmds":
            case "?": command = PlayerbotPartyCommand.Help; return true;
            case "debug":
            case "diag":
            case "diagnostics":
            case "state":
            case "brain":
            case "trace":
            case "motion":
            case "route":
            case "stall":
            case "stalls": command = PlayerbotPartyCommand.Debug; return true;
            case "leave": command = PlayerbotPartyCommand.Leave; return true;
            default: return false;
        }
    }

    /// <summary>True when <paramref name="command"/> is a read-only inspection/help/debug query that a GM may run without owning the bot.</summary>
    internal static bool IsGmInspectCommand(PlayerbotPartyCommand command) => command is
        PlayerbotPartyCommand.Status or
        PlayerbotPartyCommand.Gear or
        PlayerbotPartyCommand.Bags or
        PlayerbotPartyCommand.Gold or
        PlayerbotPartyCommand.Quests or
        PlayerbotPartyCommand.Skills or
        PlayerbotPartyCommand.Talents or
        PlayerbotPartyCommand.Help or
        PlayerbotPartyCommand.Debug;

    /// <summary>True when <paramref name="text"/> is a help/commands keyword or a natural-language question revolving around commands/controls.</summary>
    internal static bool IsHelpQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (TryParse(text, out PlayerbotPartyCommand cmd) && cmd == PlayerbotPartyCommand.Help) return true;
        if (AttackHelpClause().IsMatch(text)) return false;
        return HelpQuestionRegex().IsMatch(text);
    }

    /// <summary>True when <paramref name="text"/> is a debug/diagnostic keyword or a question asking for internal bot debug state.</summary>
    internal static bool IsDebugQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (TryParse(text, out PlayerbotPartyCommand cmd) && cmd == PlayerbotPartyCommand.Debug) return true;
        return DebugQuestionRegex().IsMatch(text);
    }

    [GeneratedRegex(@"\bhelp me (kill|fight|attack)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttackHelpClause();

    [GeneratedRegex(
        @"^\s*(help|commands?|cmds?|\?)\s*[?!.]*\s*$|\b(what|which|list|show|tell me)\b.*\b(commands?|orders?|controls?|options)\b|\bhow (do|can) i (control|command|use|order|direct) (you|u|bots?)\b|\bwhat can (i|we) (say|tell|ask|whisper|order|command)\b|\bwhat (do|can) (you|u) (do|respond to|understand|accept)\b|\bcommand list\b|\bhelp with commands\b|\bneed help\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HelpQuestionRegex();

    [GeneratedRegex(
        @"\b(debug|diagnostics?|diag|stack ?trace|internal state|brain state|motion state|route state|stall state|home state|telemetry)\b|\b(what is|what's|show|dump|print|inspect)\b.*\b(debug|diag|diagnostics?|state|brain|trace|motion|route|stall)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DebugQuestionRegex();

    /// <summary>
    /// Whose line it is: the master's whisper or party (raid) line is a command; another player's whisper gets the polite answer;
    /// a managed bot's line is <see cref="PlayerbotChatSource.Bot"/> when <paramref name="botsHeard"/> lets bots hear bots, and ignored
    /// otherwise; the bot's own line and everything else are ignored.
    /// </summary>
    internal static PlayerbotChatSource Classify(PlayerbotChatLine line, ObjectGuid self, ObjectGuid master, bool senderIsBot, bool botsHeard = false)
    {
        if (line.Sender.IsEmpty || line.Sender == self) return PlayerbotChatSource.Ignore;
        if (senderIsBot) return botsHeard ? PlayerbotChatSource.Bot : PlayerbotChatSource.Ignore;
        bool whisper = line.Type == ChatType.Whisper;
        bool group = line.Type is ChatType.Party or ChatType.Raid or ChatType.RaidLeader;
        if (!master.IsEmpty && line.Sender == master && (whisper || group)) return PlayerbotChatSource.Master;
        return whisper ? PlayerbotChatSource.Stranger : PlayerbotChatSource.Ignore;
    }

    /// <summary>
    /// Decode a player chat line: u8 type, u32 language, u64 sender, a second u64 for say/party/yell, u32 size, CString text, u8 tag.
    /// A channel line (CHAT_MSG_CHANNEL) is u8 type, u32 language, CString channel, u32 player rank, u64 sender, u32 size, CString text,
    /// u8 tag (ChannelPackets.BuildChannelMessage). False for monster lines (their layout differs) and for a body that does not fit.
    /// </summary>
    internal static bool TryRead(byte[] payload, out PlayerbotChatLine line)
    {
        line = default;
        if (payload.Length < 18) return false;
        var type = (ChatType)payload[0];
        if (type is ChatType.MonsterSay or ChatType.MonsterYell or ChatType.MonsterEmote
            or ChatType.MonsterWhisper or ChatType.RaidBossEmote or ChatType.RaidBossWhisper) return false;
        var reader = new PacketReader(payload.AsSpan(1));
        try
        {
            if (!reader.TryReadUInt32(out uint language)) return false;
            string? channel = null;
            if (type == ChatType.Channel)
            {
                channel = reader.ReadCString();
                if (channel.Length == 0 || !reader.TryReadUInt32(out _)) return false;
            }

            if (!reader.TryReadUInt64(out ulong sender)) return false;
            if ((type is ChatType.Say or ChatType.Party or ChatType.Yell) && !reader.TryReadUInt64(out _)) return false;
            if (!reader.TryReadUInt32(out uint size) || size == 0 || size > reader.Remaining) return false;
            line = new PlayerbotChatLine(type, (Language)language, new ObjectGuid(sender), reader.ReadCString(), channel);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // no terminator (PacketReader's malformed-packet exception)
        }
    }

    /// <summary>
    /// Decode SMSG_TEXT_EMOTE (ChatPackets.BuildTextEmote, vmangos HandleTextEmoteOpcode): u64 emoter, u32 text emote, u32 emote number,
    /// u32 name size (with the terminator), CString target name (empty: no target). False for a body that does not fit.
    /// </summary>
    internal static bool TryReadTextEmote(byte[] payload, out PlayerbotTextEmote emote)
    {
        emote = default;
        if (payload.Length < 21) return false;
        var reader = new PacketReader(payload);
        try
        {
            if (!reader.TryReadUInt64(out ulong emoter) || !reader.TryReadUInt32(out uint textEmote) || !reader.TryReadUInt32(out _)
                || !reader.TryReadUInt32(out uint size) || size == 0 || size > reader.Remaining) return false;
            emote = new PlayerbotTextEmote(new ObjectGuid(emoter), textEmote, reader.ReadCString());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>CMSG_MESSAGECHAT channel body: u32 type (CHAT_MSG_CHANNEL), u32 language, CString channel, CString text.</summary>
    internal static byte[] ChannelMessage(Language language, string channel, string text)
    {
        var writer = new PacketWriter(16 + channel.Length + text.Length);
        writer.WriteUInt32((uint)ChatType.Channel);
        writer.WriteUInt32((uint)language);
        writer.WriteCString(channel);
        writer.WriteCString(text);
        return writer.ToArray();
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

    /// <summary>
    /// The channel a bot's group line goes out on: raid chat in a raid (party chat reaches only the speaker's own subgroup,
    /// GroupManager.BroadcastChat), party chat otherwise.
    /// </summary>
    internal static ChatType GroupChannel(bool raid) => raid ? ChatType.Raid : ChatType.Party;

    /// <summary>CMSG_MESSAGECHAT say/yell/party/raid/raid warning/battleground/guild body: u32 type, u32 language, CString text.</summary>
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

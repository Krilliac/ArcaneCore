using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>Where a bot says a line (<see cref="BotSpeech.Channel"/>). Each is sent the way a 1.12 client sends it, through the bot's own session.</summary>
internal enum BotSpeechChannel : byte
{
    /// <summary>/say (CMSG_MESSAGECHAT CHAT_MSG_SAY): heard within the say range.</summary>
    Say,

    /// <summary>/yell (CHAT_MSG_YELL): heard within the yell range.</summary>
    Yell,

    /// <summary>/p (CHAT_MSG_PARTY): needs a group.</summary>
    Party,

    /// <summary>/ra (CHAT_MSG_RAID): needs a raid group.</summary>
    Raid,

    /// <summary>/rw (CHAT_MSG_RAID_WARNING): only the raid leader (the server refuses anyone else; the bot never tries).</summary>
    RaidWarning,

    /// <summary>/bg (CHAT_MSG_BATTLEGROUND): needs a battleground.</summary>
    Battleground,

    /// <summary>/g (CHAT_MSG_GUILD): needs a guild; <see cref="BotSpeech.Guild"/> pins the guild it was written for.</summary>
    Guild,

    /// <summary>/w (CHAT_MSG_WHISPER) to <see cref="BotSpeech.Target"/>.</summary>
    Whisper,

    /// <summary>A world channel the bot is in (CHAT_MSG_CHANNEL to <see cref="BotSpeech.ChannelName"/>, e.g. "Trade - City").</summary>
    World,

    /// <summary>A text emote (CMSG_TEXT_EMOTE <see cref="BotSpeech.TextEmote"/>, aimed at <see cref="BotSpeech.TargetGuid"/>); <see cref="BotSpeech.Text"/> is unused.</summary>
    Emote,
}

/// <summary>How much a line matters when budgets are thin (<see cref="PlayerbotSpeechLimiter"/>).</summary>
internal enum BotSpeechPriority : byte
{
    /// <summary>Idle chatter: the first to go. Needs a real player who can hear it.</summary>
    Low,

    /// <summary>A remark on something that happened (a ding, a need, an LFG call).</summary>
    Normal,

    /// <summary>A reply to someone who spoke to the bot: past the per-bot gap, with one line of reserve over a channel budget.</summary>
    High,
}

/// <summary>
/// One line a bot says (<see cref="PlayerbotChat.TrySpeak"/>, or the envelope of an announcement <see cref="BotChatAsk.Speech"/>). The
/// line is screened by the chat safety, rate-limited by <see cref="PlayerbotSpeechLimiter"/> and said on the world thread by the bot's
/// session exactly as a client's CMSG_MESSAGECHAT / CMSG_TEXT_EMOTE would be. <see cref="Topic"/> is a short label for logs, meters and
/// dedupe ("reply", "lfg", "wts", "greet"); it is never said.
/// </summary>
internal sealed record BotSpeech(BotSpeechChannel Channel, string Text, string Topic, BotSpeechPriority Priority = BotSpeechPriority.Normal)
{
    /// <summary>Whisper: the recipient's character name.</summary>
    public string? Target { get; init; }

    /// <summary>Whisper, emote and reply: the recipient (the per-target limit and the emote's aim).</summary>
    public ObjectGuid TargetGuid { get; init; }

    /// <summary>World: the channel's full name as the client knows it ("General - Elwynn Forest", "Trade - City", "LookingForGroup").</summary>
    public string? ChannelName { get; init; }

    /// <summary>Emote: the text emote id (TEXTEMOTE_WAVE = 101, ...).</summary>
    public uint TextEmote { get; init; }

    /// <summary>Guild: the guild the line was written for; not said once the bot is in another one.</summary>
    public string? Guild { get; init; }

    /// <summary>
    /// The line answers <see cref="TargetGuid"/>, who spoke first: that speaker is its audience, so no other real listener is needed
    /// (<see cref="PlayerbotSpeechOptions.RequireRealListener"/>).
    /// </summary>
    public bool IsReply { get; init; }

    /// <summary>The line answers another bot: the bot-to-bot budget and turn cap apply (<see cref="PlayerbotSpeechOptions"/>).</summary>
    public bool ToBot { get; init; }

    /// <summary>Bot-to-bot: the line's turn in its exchange (1 = the first answer to a bot).</summary>
    public int Turn { get; init; }

    /// <summary>A whisper back to <paramref name="name"/> (<paramref name="guid"/>).</summary>
    internal static BotSpeech WhisperTo(string name, ObjectGuid guid, string text, string topic, BotSpeechPriority priority = BotSpeechPriority.Normal)
        => new(BotSpeechChannel.Whisper, text, topic, priority) { Target = name, TargetGuid = guid };

    /// <summary>A line in the world channel <paramref name="channelName"/>.</summary>
    internal static BotSpeech InChannel(string channelName, string text, string topic, BotSpeechPriority priority = BotSpeechPriority.Low)
        => new(BotSpeechChannel.World, text, topic, priority) { ChannelName = channelName };

    /// <summary>A text emote aimed at <paramref name="target"/> (empty: no target).</summary>
    internal static BotSpeech EmoteAt(uint textEmote, ObjectGuid target, string topic, BotSpeechPriority priority = BotSpeechPriority.Normal)
        => new(BotSpeechChannel.Emote, string.Empty, topic, priority) { TextEmote = textEmote, TargetGuid = target };

    /// <summary>The CMSG_MESSAGECHAT type of this channel (<see cref="ChatType.TextEmote"/> for an emote).</summary>
    internal ChatType ChatType => Channel switch
    {
        BotSpeechChannel.Say => ChatType.Say,
        BotSpeechChannel.Yell => ChatType.Yell,
        BotSpeechChannel.Party => ChatType.Party,
        BotSpeechChannel.Raid => ChatType.Raid,
        BotSpeechChannel.RaidWarning => ChatType.RaidWarning,
        BotSpeechChannel.Battleground => ChatType.Battleground,
        BotSpeechChannel.Guild => ChatType.Guild,
        BotSpeechChannel.Whisper => ChatType.Whisper,
        BotSpeechChannel.World => ChatType.Channel,
        _ => ChatType.TextEmote,
    };

    /// <summary>
    /// The channel a reply to a line heard on <paramref name="heard"/> goes back on: a whisper by whisper, raid (and raid warning) in
    /// raid chat, a battleground line in battleground chat, a channel line in that channel. Null for a type a bot never answers on.
    /// </summary>
    internal static BotSpeechChannel? ReplyChannelFor(ChatType heard) => heard switch
    {
        ChatType.Whisper => BotSpeechChannel.Whisper,
        ChatType.Say => BotSpeechChannel.Say,
        ChatType.Yell => BotSpeechChannel.Yell,
        ChatType.Party => BotSpeechChannel.Party,
        ChatType.Raid or ChatType.RaidLeader or ChatType.RaidWarning => BotSpeechChannel.Raid,
        ChatType.Battleground or ChatType.BattlegroundLeader => BotSpeechChannel.Battleground,
        ChatType.Guild => BotSpeechChannel.Guild,
        ChatType.Channel => BotSpeechChannel.World,
        _ => null,
    };
}

/// <summary>Why a line was or was not taken by <see cref="PlayerbotChat.TrySpeak"/>, or not said at delivery.</summary>
internal enum BotSpeechAdmission
{
    /// <summary>Queued: the world thread says it at the next drain (unless a delivery check stops it).</summary>
    Queued,

    /// <summary>Chat or speech is off.</summary>
    Disabled,

    /// <summary>No text (or no emote), or a whisper without a target, or a world line without a channel name.</summary>
    Empty,

    /// <summary>The safety screening flagged the line (or could not judge it in time).</summary>
    Flagged,

    /// <summary>The bot spoke too recently (its gap or its per-minute cap).</summary>
    Cooldown,

    /// <summary>The bot spoke to this target too recently.</summary>
    Target,

    /// <summary>The channel's budget (per channel and zone, or the say/yell area) is spent.</summary>
    Budget,

    /// <summary>The same line was said in the same place a moment ago.</summary>
    Duplicate,

    /// <summary>Bot-to-bot: the pair's turns, the reply depth or the global bot-to-bot budget is spent.</summary>
    BotToBot,

    /// <summary>No real player could hear it.</summary>
    NoListener,

    /// <summary>The bot cannot say it there now (no group, no guild, not in that channel, the target is offline, not raid leader).</summary>
    Unavailable,

    /// <summary>The delivery queue is full.</summary>
    QueueFull,
}

/// <summary>
/// One chat line a bot heard (world thread), as input for the chat service and the lanes built on it (<see cref="PlayerbotChat.LineHeard"/>):
/// any player or bot line the bot's session received, world channel lines included. <see cref="SenderIsBot"/> says whether another managed
/// bot said it; <see cref="FirstHearing"/> is true for the first bot that reports this exact line (one line reaches every bot in a channel).
/// </summary>
internal sealed record BotHeardLine(
    Guid BotId,
    ObjectGuid BotGuid,
    string BotName,
    ChatType Type,
    string? ChannelName,
    ObjectGuid SenderGuid,
    string SenderName,
    bool SenderIsBot,
    string Text,
    bool AddressesBot,
    bool FirstHearing);

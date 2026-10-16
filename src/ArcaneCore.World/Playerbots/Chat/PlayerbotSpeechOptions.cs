namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// <c>World:Playerbots:Chat:Speech</c>: how bots speak, on their own or in answer, and where (docs/areas/playbots.md, Bot speech). Every
/// line goes through <see cref="PlayerbotChat.TrySpeak"/> (or a reply of the chat service), the chat safety screening and
/// <see cref="PlayerbotSpeechLimiter"/>, and is said by the bot's own session as a client's chat packet. The defaults keep channels quiet:
/// a bot speaks at most once per <see cref="BotGapSeconds"/> on its own, a world channel hears at most <see cref="ChannelLinesPerMinute"/>
/// bot lines a minute (fewer when it is crowded with bots), and nothing a bot starts on its own is said where no real player can hear it.
/// Every key is live (<c>.reload config</c>).
/// </summary>
public sealed class PlayerbotSpeechOptions
{
    /// <summary>Let bots speak through the speech API (on by default). Off: only the answers of the chat service of before (whisper, party, say).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A line a bot starts on its own (not a reply) is said only where a real (non-bot) player can hear it: someone in say or yell range, a
    /// real member of the group or guild online, a real member of the world channel, an online whisper target (on by default).
    /// </summary>
    public bool RequireRealListener { get; set; } = true;

    /// <summary>The least seconds between two lines a bot starts on its own (0..3600; replies are not held by it).</summary>
    public int BotGapSeconds { get; set; } = 20;

    /// <summary>The most lines one bot says in a minute, replies included (1..60).</summary>
    public int BotLinesPerMinute { get; set; } = 6;

    /// <summary>The least seconds between two lines from one bot to the same player (whisper, emote or reply; 0..3600; a high-priority reply is not held).</summary>
    public int TargetGapSeconds { get; set; } = 10;

    /// <summary>
    /// The most bot lines one world channel hears in a minute, from all bots together (0..60; 0 keeps bots silent there). The channel is
    /// per zone ("General - Elwynn Forest") and per faction; when more than <see cref="ChannelCrowdBots"/> bots are in it the minute
    /// stretches in proportion, so a crowded channel hears fewer bot lines, never more.
    /// </summary>
    public int ChannelLinesPerMinute { get; set; } = 3;

    /// <summary>How many bots a world channel holds before its budget thins out (1..10000; see <see cref="ChannelLinesPerMinute"/>).</summary>
    public int ChannelCrowdBots { get; set; } = 10;

    /// <summary>The most /say and /yell lines bots say in a minute in one zone of one map, from all bots together (0..120).</summary>
    public int LocalLinesPerMinute { get; set; } = 6;

    /// <summary>The most lines the bots of one party, raid or battleground team say in a minute (0..120).</summary>
    public int GroupLinesPerMinute { get; set; } = 8;

    /// <summary>The most lines the bots of one guild say in guild chat in a minute (0..120; the guild announcements keep their own hourly cap too).</summary>
    public int GuildLinesPerMinute { get; set; } = 4;

    /// <summary>Seconds within which the same line is not said twice in the same place by any bot (0..3600).</summary>
    public int DedupeSeconds { get; set; } = 120;

    /// <summary>
    /// The zone channels bots join through their own CMSG_JOIN_CHANNEL on login and when they change zone, leaving the old zone's ones
    /// (<c>General</c>, <c>Trade</c>, <c>LocalDefense</c>, <c>LookingForGroup</c>; comma-separated; all by default; <c>None</c> joins none and
    /// leaves the ones joined). Names and rules are the 1.12 client's: General and LocalDefense in every open-world zone, Trade only in a
    /// capital, LookingForGroup everywhere; none of the zone ones inside an instance or battleground.
    /// </summary>
    public PlayerbotWorldChannels WorldChannels { get; set; } = PlayerbotWorldChannels.All;

    /// <summary>The most bots whose channels are changed in one social pass (1..1000), so a fleet logging in at once spreads its joins.</summary>
    public int ChannelChangesPerPass { get; set; } = 25;

    /// <summary>
    /// Let a bot's line reach other bots as input (on by default), within <see cref="BotToBotMaxTurns"/>, <see cref="BotToBotPairLinesPerHour"/>
    /// and <see cref="BotToBotLinesPerMinute"/>. Off: bots hear only real players, as before.
    /// </summary>
    public bool BotToBot { get; set; } = true;

    /// <summary>
    /// The most answers in one exchange between two bots (0..10; 0 = bots never answer bots). An exchange ends after
    /// <see cref="BotToBotQuietSeconds"/> of silence between them.
    /// </summary>
    public int BotToBotMaxTurns { get; set; } = 3;

    /// <summary>Seconds of silence after which an exchange between two bots is over and its turns start again (10..3600).</summary>
    public int BotToBotQuietSeconds { get; set; } = 120;

    /// <summary>The most lines two bots say to each other in an hour (0..600).</summary>
    public int BotToBotPairLinesPerHour { get; set; } = 12;

    /// <summary>The most lines all bots together say to other bots in a minute (0..600).</summary>
    public int BotToBotLinesPerMinute { get; set; } = 10;

    /// <summary>
    /// Answer a text emote a player (or bot) aims at a bot in kind: a wave with a wave, a bow with a bow, a hug with a blush, a rude gesture
    /// with one back or nothing (on by default), at most once per <see cref="EmoteReplyGapSeconds"/> per bot and emoter.
    /// </summary>
    public bool EmoteReplies { get; set; } = true;

    /// <summary>The least seconds between two emote answers from one bot to the same emoter (0..3600).</summary>
    public int EmoteReplyGapSeconds { get; set; } = 30;

    /// <summary>Lines waiting for the world thread at once (16..4096); a line that finds the queue full is not said.</summary>
    public int MaxQueuedLines { get; set; } = 512;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Chat:Speech";
        Check(BotGapSeconds, 0, 3600, nameof(BotGapSeconds));
        Check(BotLinesPerMinute, 1, 60, nameof(BotLinesPerMinute));
        Check(TargetGapSeconds, 0, 3600, nameof(TargetGapSeconds));
        Check(ChannelLinesPerMinute, 0, 60, nameof(ChannelLinesPerMinute));
        Check(ChannelCrowdBots, 1, 10_000, nameof(ChannelCrowdBots));
        Check(LocalLinesPerMinute, 0, 120, nameof(LocalLinesPerMinute));
        Check(GroupLinesPerMinute, 0, 120, nameof(GroupLinesPerMinute));
        Check(GuildLinesPerMinute, 0, 120, nameof(GuildLinesPerMinute));
        Check(DedupeSeconds, 0, 3600, nameof(DedupeSeconds));
        Check(ChannelChangesPerPass, 1, 1000, nameof(ChannelChangesPerPass));
        Check(BotToBotMaxTurns, 0, 10, nameof(BotToBotMaxTurns));
        Check(BotToBotQuietSeconds, 10, 3600, nameof(BotToBotQuietSeconds));
        Check(BotToBotPairLinesPerHour, 0, 600, nameof(BotToBotPairLinesPerHour));
        Check(BotToBotLinesPerMinute, 0, 600, nameof(BotToBotLinesPerMinute));
        Check(EmoteReplyGapSeconds, 0, 3600, nameof(EmoteReplyGapSeconds));
        Check(MaxQueuedLines, 16, 4096, nameof(MaxQueuedLines));
        if (CheckWorldChannels(WorldChannels) is { } channels) throw new InvalidOperationException($"{section}: WorldChannels {channels}");

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }

    internal static string? CheckWorldChannels(PlayerbotWorldChannels channels)
        => (channels & ~PlayerbotWorldChannels.All) == 0 ? null : "must be a combination of General, Trade, LocalDefense and LookingForGroup (or None)";
}

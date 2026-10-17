namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// <c>World:Playerbots:Chat:Conversation</c>: bots talking with each other and reacting to world channel chatter (docs/areas/playbots.md,
/// Bot conversations). The conversation director (<c>BotSocial.PlayerbotConversations</c>) runs on the social pass (once a second):
/// now and then it pairs two bots that share a group, stand near each other or share a guild, and lets them trade a few lines on a topic
/// taken from their real state (a need one has and the other can fill, a quest they share, a recent kill, a ding, loot, a death). The
/// world chatter reactions (<c>BotSocial.PlayerbotWorldChatter</c>) answer a real player's LFM or WTB line in a world channel with a
/// whisper when the bot fits it. Every line goes through the bot chat service (safety screening, the speech limits of
/// <see cref="PlayerbotSpeechOptions"/>, the bot-to-bot guard) and is said by the bot's own session. The defaults keep it rare and quiet;
/// every key is live (<c>.reload config</c>).
/// </summary>
public sealed class PlayerbotConversationOptions
{
    /// <summary>Let the director start bot-to-bot conversations (on by default; needs <see cref="PlayerbotSpeechOptions.BotToBot"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The most conversations the director starts in an hour, over all bots (0..600; 0 = none). Each pass rolls against this, so they
    /// spread over the hour instead of coming in bursts.
    /// </summary>
    public int ConversationsPerHour { get; set; } = 20;

    /// <summary>The most conversations running at once (1..64).</summary>
    public int MaxActive { get; set; } = 4;

    /// <summary>
    /// The most lines one conversation has, the opener included (2..6). The bot-to-bot turn cap (<see cref="PlayerbotSpeechOptions.BotToBotMaxTurns"/>)
    /// still applies to the answers, so with its default of 3 a conversation has at most 4 lines.
    /// </summary>
    public int MaxLines { get; set; } = 4;

    /// <summary>Minutes before the same two bots are paired again (1..1440).</summary>
    public int PairCooldownMinutes { get; set; } = 30;

    /// <summary>Minutes before one bot starts or joins another conversation (1..1440).</summary>
    public int BotCooldownMinutes { get; set; } = 10;

    /// <summary>The least seconds between two lines of a conversation (2..120): the time a player takes to read and type.</summary>
    public int LineGapSeconds { get; set; } = 7;

    /// <summary>The most extra random seconds added to <see cref="LineGapSeconds"/> (0..120).</summary>
    public int LineJitterSeconds { get; set; } = 6;

    /// <summary>How long a planned line may wait for the speech limits before the conversation is dropped (10..600 seconds).</summary>
    public int LineDeadlineSeconds { get; set; } = 60;

    /// <summary>
    /// How many bots the director looks at per pass when it picks a conversation (1..256): the work per pass is bounded by it, whatever
    /// the size of the fleet.
    /// </summary>
    public int CandidatesPerPass { get; set; } = 8;

    /// <summary>Two bots "near each other" are at most this many yards apart (5..100) for a /say conversation.</summary>
    public int NearYards { get; set; } = 20;

    /// <summary>
    /// Answer a real player's world channel line that fits the bot: an LFM for a dungeon at the bot's level (a whispered "inv" when the
    /// bot is free and its role is wanted), a WTB for an item the bot carries spare (a whispered offer) (on by default).
    /// </summary>
    public bool WorldReactions { get; set; } = true;

    /// <summary>The most reactions all bots together whisper in a minute (0..60; 0 = none).</summary>
    public int WorldReactionsPerMinute { get; set; } = 4;

    /// <summary>How many bots answer one world channel line at most (1..5).</summary>
    public int ReactionsPerLine { get; set; } = 1;

    /// <summary>Minutes before one bot reacts to the same player's world channel lines again (1..1440).</summary>
    public int ReactionPlayerCooldownMinutes { get; set; } = 10;

    /// <summary>Heard world channel lines waiting for the next social pass at most (8..1024); more are dropped.</summary>
    public int MaxHeardQueue { get; set; } = 64;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Chat:Conversation";
        Check(ConversationsPerHour, 0, 600, nameof(ConversationsPerHour));
        Check(MaxActive, 1, 64, nameof(MaxActive));
        Check(MaxLines, 2, 6, nameof(MaxLines));
        Check(PairCooldownMinutes, 1, 1440, nameof(PairCooldownMinutes));
        Check(BotCooldownMinutes, 1, 1440, nameof(BotCooldownMinutes));
        Check(LineGapSeconds, 2, 120, nameof(LineGapSeconds));
        Check(LineJitterSeconds, 0, 120, nameof(LineJitterSeconds));
        Check(LineDeadlineSeconds, 10, 600, nameof(LineDeadlineSeconds));
        Check(CandidatesPerPass, 1, 256, nameof(CandidatesPerPass));
        Check(NearYards, 5, 100, nameof(NearYards));
        Check(WorldReactionsPerMinute, 0, 60, nameof(WorldReactionsPerMinute));
        Check(ReactionsPerLine, 1, 5, nameof(ReactionsPerLine));
        Check(ReactionPlayerCooldownMinutes, 1, 1440, nameof(ReactionPlayerCooldownMinutes));
        Check(MaxHeardQueue, 8, 1024, nameof(MaxHeardQueue));

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }
}

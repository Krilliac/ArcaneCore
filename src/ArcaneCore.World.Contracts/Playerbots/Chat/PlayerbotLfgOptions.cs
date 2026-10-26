namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// <c>World:Playerbots:Chat:Lfg</c>: how bots answer a real player's group talk (docs/areas/playbots.md, Bot voice): "any groups for
/// &lt;dungeon&gt;?" in a world channel, a whispered or channelled "inv" to a bot that looks for more, and the lines bots say back. Every answer
/// states only what the coordinator knows (a waiting group, its missing roles, its level); the built-in templates or a configured model
/// only phrase it, and a player who chose "ai off" gets the built-in lines. Every key is live (<c>.reload config</c>); all on by default
/// except <see cref="WorldBossQueries"/>.
/// </summary>
public sealed class PlayerbotLfgOptions
{
    /// <summary>Let bots take part in group talk beyond answering a whisper to the poster (on by default). Off: only the whisper "inv" to a bot's LFG or LFM call is answered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Answer "any groups for &lt;dungeon&gt;?" with the groups bots really wait for (on by default).</summary>
    public bool AnswerQueries { get; set; } = true;

    /// <summary>Take an "inv" or "I can tank" said in a channel as an offer to join when it names exactly one live call (on by default).</summary>
    public bool ChannelAvailability { get; set; } = true;

    /// <summary>Answer a query in the channel it was asked in, not only by whisper (on by default).</summary>
    public bool PublicAnswers { get; set; } = true;

    /// <summary>The most groups one answer names (1..5).</summary>
    public int MaxGroupsPerAnswer { get; set; } = 2;

    /// <summary>The most queries answered in one minute, all channels together (0..60; 0 = none).</summary>
    public int QueriesPerMinute { get; set; } = 6;

    /// <summary>Seconds before the same player's next query is answered (0..3600).</summary>
    public int PlayerQueryCooldownSeconds { get; set; } = 60;

    /// <summary>Seconds a bot's referral ("ask Aldrin, he is forming it") stays good for the player's offer to join (30..600).</summary>
    public int ReferralSeconds { get; set; } = 120;

    /// <summary>Seconds after a call within which a player's channel "inv" is taken as an answer to it (15..600).</summary>
    public int ChannelReplyWindowSeconds { get; set; } = 120;

    /// <summary>Seconds within which a player's repeated offer to the same call is applied without a second reply (10..600).</summary>
    public int AnswerRepeatSeconds { get; set; } = 60;

    /// <summary>The most channel lines waiting to be read (1..256); a line that finds the queue full is not read.</summary>
    public int MaxQueuedLines { get; set; } = 32;

    /// <summary>The most queued lines read in one social pass (1..32).</summary>
    public int LinesPerPass { get; set; } = 4;

    /// <summary>The most players whose offer to join is followed until the group forms or falls through (8..512).</summary>
    public int MaxTrackedVolunteers { get; set; } = 64;

    /// <summary>Tell a player whose noted offer lapsed because the group ended ("the group fell through") once (on by default).</summary>
    public bool LapseNotice { get; set; } = true;

    /// <summary>Let a free bot ask "any groups for &lt;dungeon&gt;?" in a channel, only when a group for it forms on its team (on by default).</summary>
    public bool BotAsks { get; set; } = true;

    /// <summary>The most such questions bots ask in an hour, all together (0..60; 0 = none).</summary>
    public int BotAsksPerHour { get; set; } = 6;

    /// <summary>Answer a query about a world boss (off by default).</summary>
    public bool WorldBossQueries { get; set; }

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Chat:Lfg";
        Check(MaxGroupsPerAnswer, 1, 5, nameof(MaxGroupsPerAnswer));
        Check(QueriesPerMinute, 0, 60, nameof(QueriesPerMinute));
        Check(PlayerQueryCooldownSeconds, 0, 3600, nameof(PlayerQueryCooldownSeconds));
        Check(ReferralSeconds, 30, 600, nameof(ReferralSeconds));
        Check(ChannelReplyWindowSeconds, 15, 600, nameof(ChannelReplyWindowSeconds));
        Check(AnswerRepeatSeconds, 10, 600, nameof(AnswerRepeatSeconds));
        Check(MaxQueuedLines, 1, 256, nameof(MaxQueuedLines));
        Check(LinesPerPass, 1, 32, nameof(LinesPerPass));
        Check(MaxTrackedVolunteers, 8, 512, nameof(MaxTrackedVolunteers));
        Check(BotAsksPerHour, 0, 60, nameof(BotAsksPerHour));

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }
}

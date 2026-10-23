namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Initiative</c>: bots start conversations and comment on their surroundings (docs/areas/playbots-social.md, Initiative). On the
/// social pass a free bot opens with a character nearby only for a reason: the same quest objective (a positive reply forms a group), a familiar one it last talked to about
/// something, or a familiar one who has just arrived. Every opening goes through the announcement path (limiter, safety screen, opt-in rules). A bot whispers only a player
/// who opted in to AI chat or whom it likes (affinity 10 or more); anyone else gets a public /say. A player hears from bots at most once per
/// <see cref="OpeningCooldownMinutes"/>, and a player who ignores openings is approached less and less. A bot also, rarely, remarks on its surroundings
/// (a rare creature, a world event, an enemy raid, a friend's first mount). The bot's mood scales every chance. Every key is live.
/// </summary>
public sealed class PlayerbotSocialInitiativeOptions
{
    /// <summary>The most openings per social pass.</summary>
    public const int MaxMaxOpeningsPerPass = 64;

    /// <summary>The longest cooldown or gap in minutes (a day).</summary>
    public const int MaxMinutes = 1440;

    /// <summary>Let bots open conversations and comment on their surroundings (on by default).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most openings (all bots together) in one pass of the social feature (1..64, default 4).</summary>
    public int MaxOpeningsPerPass { get; set; } = 4;

    /// <summary>The minutes before any bot opens with the same real player again (1..1440, default 30); an opening they ignored doubles it each time.</summary>
    public int OpeningCooldownMinutes { get; set; } = 30;

    /// <summary>Let bots comment on their surroundings (on by default).</summary>
    public bool CommentsEnabled { get; set; } = true;

    /// <summary>The minutes between two comments of the same bot (1..1440, default 10).</summary>
    public int CommentBotGapMinutes { get; set; } = 10;

    /// <summary>The seconds between two comments in the same zone, whichever bot (1..86400, default 60).</summary>
    public int CommentZoneGapSeconds { get; set; } = 60;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Initiative";
        if (MaxOpeningsPerPass is < 1 or > MaxMaxOpeningsPerPass) throw new InvalidOperationException($"{section}: MaxOpeningsPerPass must be 1..{MaxMaxOpeningsPerPass}.");
        if (OpeningCooldownMinutes is < 1 or > MaxMinutes) throw new InvalidOperationException($"{section}: OpeningCooldownMinutes must be 1..{MaxMinutes}.");
        if (CommentBotGapMinutes is < 1 or > MaxMinutes) throw new InvalidOperationException($"{section}: CommentBotGapMinutes must be 1..{MaxMinutes}.");
        if (CommentZoneGapSeconds is < 1 or > 86_400) throw new InvalidOperationException($"{section}: CommentZoneGapSeconds must be 1..86400.");
    }
}

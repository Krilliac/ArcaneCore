namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Mood</c>: a short-term mood per bot (docs/areas/playbots-social.md, Mood). In memory only, -1 (miserable) to +1 (delighted),
/// nudged by events (up: a level, a better item, a group success, help received; down: a death, a wipe, a kick, a rude emote, a chat strike) and decaying toward 0
/// with a half-life. It scales the chance of the bot's own initiative and comments between 0.5x and 1.5x, picks cheerful or grumpy template variants and adds a mood
/// fact to the model prompt. Every key is live.
/// </summary>
public sealed class PlayerbotSocialMoodOptions
{
    /// <summary>The longest <see cref="HalfLifeMinutes"/> (a day).</summary>
    public const int MaxHalfLifeMinutes = 1440;

    /// <summary>Let bots have a mood (on by default). Off: every mood reads 0 and events change nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The minutes in which a mood decays to half of itself (1..1440, default 30).</summary>
    public int HalfLifeMinutes { get; set; } = 30;

    public void Validate()
    {
        if (HalfLifeMinutes is < 1 or > MaxHalfLifeMinutes)
            throw new InvalidOperationException($"{PlayerbotOptions.SectionName}:Social:Mood: HalfLifeMinutes must be 1..{MaxHalfLifeMinutes}.");
    }
}

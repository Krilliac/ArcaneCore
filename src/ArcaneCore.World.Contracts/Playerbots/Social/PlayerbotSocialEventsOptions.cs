namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Events</c>: bots and the world's events (docs/areas/playbots-social.md, World events). The master switch of
/// the section, and the holiday half (<see cref="Holidays"/>): while a festival runs, a bounded number of idle town bots gather near its
/// NPCs (outside their interaction range) instead of at the inn, celebrate there within per-listener limits and now and then say a line that
/// names the festival. Every key is live: <c>.reload config</c> changes this object in place and the feature reads it at every decision.
/// </summary>
public sealed class PlayerbotSocialEventsOptions
{
    /// <summary>
    /// Let bots take part in the world's events (on by default). Off: festival places are given up (the bots go back to the inn at their next
    /// idle think) and no event line or emote is made.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Festival gathering, celebration and talk (<c>World:Playerbots:Social:Events:Holidays</c>).</summary>
    public PlayerbotHolidayOptions Holidays { get; set; } = new();

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Events";
        if (Holidays is null) throw new InvalidOperationException($"{section}:Holidays is missing.");
        Holidays.Validate();
    }
}

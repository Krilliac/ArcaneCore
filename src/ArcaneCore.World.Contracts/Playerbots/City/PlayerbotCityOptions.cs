namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:City</c>: managed bots make trips to their faction's capital cities to use what the cities hold (docs/areas/playbots-city.md):
/// repair and vendors, the class trainer, the auction house and mailbox, the innkeeper. A trip starts for one of a few cheap reasons
/// (bags nearly full of goods, gold to spend, a spell to learn, a level milestone, or simply the visit interval) and ends back in the bot's
/// ordinary goals. Every key is live: <c>.reload config</c> changes this object in place and the capital goals read it at every
/// decision.
/// </summary>
public sealed class PlayerbotCityOptions
{
    /// <summary>The longest <see cref="VisitIntervalMinutes"/> accepted (one week).</summary>
    public const int MaxVisitIntervalMinutes = 10_080;

    /// <summary>The longest <see cref="MaxTripYards"/> accepted.</summary>
    public const float MaxTripYardsCeiling = 100_000f;

    /// <summary>The highest <see cref="MinGoldForAuctionTrip"/> accepted (copper).</summary>
    public const int MaxMinGoldForAuctionTrip = 2_000_000_000;

    /// <summary>The highest <see cref="MinLevel"/> accepted (the 1.12 level cap).</summary>
    public const int MaxMinLevel = 60;

    /// <summary>Let bots make capital city trips (on by default). Off: no trip starts; one under way is dropped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minutes between two capital city trips of one bot when nothing else sent it (1..10080). Each bot's own interval is this value
    /// plus or minus a quarter, fixed by its guid, and the first one is a random point of the first interval, so the bots do not all
    /// leave at once.
    /// </summary>
    public int VisitIntervalMinutes { get; set; } = 90;

    /// <summary>
    /// The copper a bot must hold (0..2000000000) for the gold to send it to the auction house on its own: with this much money and
    /// something to buy or sell there, and no trip for half a <see cref="VisitIntervalMinutes"/>, it leaves. 50000 is five gold.
    /// </summary>
    public int MinGoldForAuctionTrip { get; set; } = 50_000;

    /// <summary>
    /// Make the capital's inn the bot's home on a trip (off by default). Players bind where they quest, and a bot with its bind in a
    /// capital that is stranded or stalled is sent there with nothing of its level to fight; this makes the hearthstone lead to the
    /// capital's services instead. Only an innkeeper that likes the bot is used, and a bind already in the capital stays.
    /// </summary>
    public bool BindInCapital { get; set; }

    /// <summary>
    /// The longest straight line in yards (above 0, at most 100000) from the bot to a capital on its own map for a trip to start by
    /// walking. A capital beyond it is skipped. A bot whose home bind is in a capital may use its hearthstone for any distance.
    /// </summary>
    public float MaxTripYards { get; set; } = 6000f;

    /// <summary>The level a bot must have reached (1..60) before it makes trips of its own accord.</summary>
    public int MinLevel { get; set; } = 10;

    /// <summary>The most trips under way to one capital at the same moment (1..1000). A bot whose trigger fires while the city is at the cap waits and asks again.</summary>
    public int MaxTripsPerCapital { get; set; } = 6;

    /// <summary>The most trips that start for one capital in a minute (1..600), so a restart or a level-up wave does not send the whole population at once.</summary>
    public int MaxStartsPerMinute { get; set; } = 4;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":City";
        if (VisitIntervalMinutes is < 1 or > MaxVisitIntervalMinutes)
            throw new InvalidOperationException($"{section}: VisitIntervalMinutes must be 1..{MaxVisitIntervalMinutes}.");
        if (MinGoldForAuctionTrip is < 0 or > MaxMinGoldForAuctionTrip)
            throw new InvalidOperationException($"{section}: MinGoldForAuctionTrip must be 0..{MaxMinGoldForAuctionTrip}.");
        if (!float.IsFinite(MaxTripYards) || MaxTripYards <= 0f || MaxTripYards > MaxTripYardsCeiling)
            throw new InvalidOperationException($"{section}: MaxTripYards must be above 0 and at most {MaxTripYardsCeiling}.");
        if (MinLevel is < 1 or > MaxMinLevel)
            throw new InvalidOperationException($"{section}: MinLevel must be 1..{MaxMinLevel}.");
        if (MaxTripsPerCapital is < 1 or > 1000)
            throw new InvalidOperationException($"{section}: MaxTripsPerCapital must be 1..1000.");
        if (MaxStartsPerMinute is < 1 or > 600)
            throw new InvalidOperationException($"{section}: MaxStartsPerMinute must be 1..600.");
    }
}

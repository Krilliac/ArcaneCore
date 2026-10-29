namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Travel</c>: how autonomous bots get to a far place the way a 1.12 player does (docs/areas/playbots.md, "Travel").
/// The route planner (<c>PlayerbotTravelPlanner</c>) chains walking, flight paths (<see cref="PlayerbotOptions.Taxi"/>, whose
/// <c>Enabled</c> is the flight-path switch), the Deeprun Tram and lifts, boats, zeppelins, the hearthstone and mage teleports and portals,
/// choosing legs by time from what the bot really has. Every means has its own switch; a means switched off is never planned and a leg of
/// it under way ends at its next safe point. Every key is live: <c>.reload config</c> changes this object in place and a bot reads it at its
/// next decision. Not vmangos keys: autonomous travel is an ArcaneCore feature.
/// </summary>
public sealed class PlayerbotTravelOptions
{
    /// <summary>Let bots plan multi-leg trips to far places (on by default). Off: no trip is planned and a far goal stays where its caller leaves it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Let bots ride the boats (Menethil - Auberdine, Menethil - Theramore, Auberdine - Rut'theran, Booty Bay - Ratchet; on by default).</summary>
    public bool Boats { get; set; } = true;

    /// <summary>Let bots walk into same-map teleport triggers as a leg (Teldrassil's Darnassus and Rut'theran portals; on by default).</summary>
    public bool AreaTriggers { get; set; } = true;

    /// <summary>Let Horde bots ride the zeppelins (Orgrimmar - Undercity, Orgrimmar - Grom'gol, Undercity - Grom'gol; on by default).</summary>
    public bool Zeppelins { get; set; } = true;

    /// <summary>Let bots use their hearthstone (item 6948) as a leg when it is off cooldown and the bind point is on the way (on by default).</summary>
    public bool Hearthstone { get; set; } = true;

    /// <summary>
    /// Let mage bots offer their Teleport and Portal spells as a service (on by default): a mage teleports itself, and opens a portal for a
    /// bot of its faction that asks on the in-world request board. Off: no mage teleports for travel and no portal is asked for or opened.
    /// </summary>
    public bool MageService { get; set; } = true;

    /// <summary>A bot asks for a portal only from a mage within this many yards of it, on its map (10..1000; default 150).</summary>
    public float MageServiceRadiusYards { get; set; } = 150f;

    /// <summary>
    /// Copper a requester pays the mage for a portal, by a real trade of money (0..100000; default 0: the service is free). The portal is
    /// planned only when the bot can pay it inside <see cref="MaxTravelSpendCopper"/>.
    /// </summary>
    public uint MageServiceFeeCopper { get; set; }

    /// <summary>
    /// The most copper one trip may spend on fares, fees and reagents together (0..10000000; default 10000, one gold). 0: only free means
    /// (walking, boats, zeppelins, the tram, lifts, the hearthstone, a free portal, the bot's own teleport when it has the rune).
    /// </summary>
    public uint MaxTravelSpendCopper { get; set; } = 10_000;

    /// <summary>
    /// The first wait (seconds) before a bot tries a transit point again (one flight master's node, tram platform, lift, dock) that just
    /// failed it; each further failure of the same point doubles the wait up to <see cref="TransitBackoffMaxSeconds"/> (10..3600; default 60).
    /// </summary>
    public int TransitBackoffSeconds { get; set; } = 60;

    /// <summary>The longest wait (seconds) before a failed transit point is tried again (10..86400; default 300). Must not be below <see cref="TransitBackoffSeconds"/>.</summary>
    public int TransitBackoffMaxSeconds { get; set; } = 300;

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Travel";
        if (CheckRadius(MageServiceRadiusYards) is { } radius) throw new InvalidOperationException($"{section}: MageServiceRadiusYards {radius}.");
        if (CheckFee(MageServiceFeeCopper) is { } fee) throw new InvalidOperationException($"{section}: MageServiceFeeCopper {fee}.");
        if (CheckSpend(MaxTravelSpendCopper) is { } spend) throw new InvalidOperationException($"{section}: MaxTravelSpendCopper {spend}.");
        if (CheckBackoff(TransitBackoffSeconds) is { } backoff) throw new InvalidOperationException($"{section}: TransitBackoffSeconds {backoff}.");
        if (CheckBackoffMax(TransitBackoffMaxSeconds) is { } max) throw new InvalidOperationException($"{section}: TransitBackoffMaxSeconds {max}.");
        if (TransitBackoffMaxSeconds < TransitBackoffSeconds)
            throw new InvalidOperationException($"{section}: TransitBackoffMaxSeconds ({TransitBackoffMaxSeconds}) must not be below TransitBackoffSeconds ({TransitBackoffSeconds}).");
    }

    internal static string? CheckRadius(float value) => float.IsFinite(value) && value is >= 10f and <= 1000f ? null : "must be 10..1000";

    internal static string? CheckFee(uint value) => value <= 100_000 ? null : "must be 0..100000";

    internal static string? CheckSpend(uint value) => value <= 10_000_000 ? null : "must be 0..10000000";

    internal static string? CheckBackoff(int value) => value is >= 10 and <= 3600 ? null : "must be 10..3600";

    internal static string? CheckBackoffMax(int value) => value is >= 10 and <= 86_400 ? null : "must be 10..86400";

    /// <summary>The wait after the <paramref name="failures"/>-th failure in a row of one transit point (1 = the first): doubling from the first wait, capped.</summary>
    internal uint BackoffMs(int failures)
    {
        int first = Math.Clamp(TransitBackoffSeconds, 10, 3600);
        int max = Math.Max(first, Math.Clamp(TransitBackoffMaxSeconds, 10, 86_400));
        long seconds = first;
        for (int i = 1; i < failures && seconds < max; i++) seconds *= 2;
        return (uint)(Math.Min(seconds, max) * 1000L);
    }
}

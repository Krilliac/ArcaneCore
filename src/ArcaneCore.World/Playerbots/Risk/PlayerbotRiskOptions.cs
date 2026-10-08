namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Risk</c>: how a bot weighs a fight before it takes it and while it lasts (docs/areas/playbots-risk.md).
/// Before a pull it scores the risk (the predicted time to kill everything that would join against its time to die) and the
/// reward (experience by level difference, quest credit, loot) and engages only when the reward justifies the risk; in a fight
/// it compares the observed damage rates and retreats past the creatures' leash when it is losing. Every key is live:
/// <c>.reload config</c> changes the shared object in place and running bots use the new value at their next decision.
/// </summary>
public sealed class PlayerbotRiskOptions
{
    /// <summary>Weigh fights and retreat from lost ones (on by default). Off: the bot takes the nearest target and fights to the end.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The aggressiveness knob (0.25..4, 1 by default). It scales the risk a bot accepts for a given reward before a pull and
    /// how far behind it may fall in a fight before it retreats: 2 takes fights twice as dangerous, 0.5 only half.
    /// </summary>
    public float Tolerance { get; set; } = 1f;

    /// <summary>A losing bot retreats once its health falls to this percentage (5..95); one about to die retreats at any health.</summary>
    public float RetreatHealthPct { get; set; } = 35f;

    /// <summary>
    /// A fight against a single enemy at or below this health percentage is nearly won and is not abandoned (0..60), unless the bot
    /// would die in less than half the time it needs to finish it.
    /// </summary>
    public float NearlyWonHealthPct { get; set; } = 20f;

    /// <summary>After a retreat (or before a pull it is too hurt for) the bot recovers to this health and mana percentage (10..100).</summary>
    public float RecoverHealthPct { get; set; } = 80f;

    /// <summary>
    /// Seconds a bot remembers the creatures it retreated from or died to and the places that happened (0..86400): it does not
    /// pull a remembered creature again, and a remembered place raises the risk of fights there.
    /// </summary>
    public int DangerMemorySeconds { get; set; } = 300;

    /// <summary>
    /// A bot in a real player's group follows its master's lead and never retreats on its own judgement; with this on (default)
    /// it does retreat when the group is wiping (the master dead, or half the group or more).
    /// </summary>
    public bool PartyRetreatOnWipe { get; set; } = true;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Risk";
        if (!float.IsFinite(Tolerance) || Tolerance is < 0.25f or > 4f) throw new InvalidOperationException($"{section}: Tolerance must be 0.25..4.");
        if (Check(RetreatHealthPct, 5, 95) is { } retreat) throw new InvalidOperationException($"{section}: RetreatHealthPct {retreat}");
        if (Check(NearlyWonHealthPct, 0, 60) is { } won) throw new InvalidOperationException($"{section}: NearlyWonHealthPct {won}");
        if (Check(RecoverHealthPct, 10, 100) is { } recover) throw new InvalidOperationException($"{section}: RecoverHealthPct {recover}");
        if (DangerMemorySeconds is < 0 or > 86_400) throw new InvalidOperationException($"{section}: DangerMemorySeconds must be 0..86400.");
    }

    /// <summary>A reload's range check for a percentage key (null when valid).</summary>
    internal static string? Check(float value, float min, float max)
        => float.IsFinite(value) && value >= min && value <= max ? null : $"must be {min}..{max}";
}

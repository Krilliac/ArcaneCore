namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Taxi</c>: autonomous bots take flight paths (docs/areas/playbots.md, Flight paths). A bot that knows the taxi node
/// beside it and one near where a long trip ends walks to that flight master, activates the flight through the real taxi opcodes
/// (<see cref="PlayerbotTaxi"/>) and waits out the flight. Every key is live: <c>.reload config</c> changes this object in place and a
/// bot reads it at its next decision. Not vmangos keys: autonomous taxi use is an ArcaneCore feature.
/// </summary>
public sealed class PlayerbotTaxiOptions
{
    /// <summary>Let bots discover flight masters they pass and fly between known nodes (on by default). Off: bots never talk to a flight master.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A trip is flown only when the straight line from the bot to where it is going is at least this many yards (100..20000; default 600).
    /// Shorter trips are walked.
    /// </summary>
    public float MinRouteYards { get; set; } = 600f;

    /// <summary>
    /// The farthest, in yards, the bot's own position may lie from the flight path node it departs from, and the node it lands at from the
    /// trip's end, in a straight line (10..2000; default 300). The two walks together must also be at most half of the straight line.
    /// </summary>
    public float MaxWalkToMasterYards { get; set; } = 300f;

    /// <summary>
    /// The longest, in minutes, a flight node is backed off after trips from it failed (the server refused the flight, the flight master
    /// could not be reached, or none stood where the node is): the per-node cap on the schedule of <c>World:Playerbots:Travel</c>
    /// (<c>TransitBackoffSeconds</c> doubling to <c>TransitBackoffMaxSeconds</c>). Only the failing node waits; the bot walks, or flies from
    /// another known node, meanwhile (1..1440; default 10).
    /// </summary>
    public int BackoffMinutes { get; set; } = 10;

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Taxi";
        if (CheckMinRoute(MinRouteYards) is { } route) throw new InvalidOperationException($"{section}: MinRouteYards {route}.");
        if (CheckMaxWalk(MaxWalkToMasterYards) is { } walk) throw new InvalidOperationException($"{section}: MaxWalkToMasterYards {walk}.");
        if (CheckBackoff(BackoffMinutes) is { } backoff) throw new InvalidOperationException($"{section}: BackoffMinutes {backoff}.");
    }

    internal static string? CheckMinRoute(float value) => float.IsFinite(value) && value is >= 100f and <= 20000f ? null : "must be 100..20000";

    internal static string? CheckMaxWalk(float value) => float.IsFinite(value) && value is >= 10f and <= 2000f ? null : "must be 10..2000";

    internal static string? CheckBackoff(int value) => value is >= 1 and <= 1440 ? null : "must be 1..1440";
}

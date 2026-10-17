using System.Globalization;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// How the bots' retreats ended, counted for <c>.playerbot qa</c> and the QA pass's log line (w29 live: 66 of 85 death loops ended
/// with the bot retreating, and nothing counted how many retreats got away): "safe" (nothing threatened the bot any more),
/// "cornered" (the retreat gave up and the bot fought), "died" on the way, and the retreats refused as unable to succeed
/// (<see cref="PlayerbotRiskModel.CorneredReason"/>, counted once per fight). Counters are interlocked: the world thread writes, the QA
/// task and commands read. <see cref="Shared"/> is the fleet's; a <see cref="PlayerbotRisk"/> counts into its own
/// <see cref="PlayerbotRisk.RetreatStats"/> (the shared one unless a test replaces it).
/// </summary>
internal sealed class PlayerbotRetreatStats
{
    private long _safe, _cornered, _died, _refused;

    /// <summary>The fleet's counters.</summary>
    internal static PlayerbotRetreatStats Shared { get; } = new();

    internal long Safe => Interlocked.Read(ref _safe);

    internal long Cornered => Interlocked.Read(ref _cornered);

    internal long Died => Interlocked.Read(ref _died);

    /// <summary>Retreats not ordered because the bot could not have broken contact before it died.</summary>
    internal long Refused => Interlocked.Read(ref _refused);

    /// <summary>Retreats that ended.</summary>
    internal long Ended => Safe + Cornered + Died;

    /// <summary>Count a retreat's outcome ("safe", "cornered" or "died"; anything else is ignored).</summary>
    internal void Record(string? outcome)
    {
        switch (outcome)
        {
            case "safe": Interlocked.Increment(ref _safe); break;
            case "cornered": Interlocked.Increment(ref _cornered); break;
            case "died": Interlocked.Increment(ref _died); break;
        }
    }

    internal void RecordRefused() => Interlocked.Increment(ref _refused);

    /// <summary>The counters as one line, e.g. <c>Retreats: 12 ended (safe 3, cornered 2, died 7), 5 refused as cornered-no-escape.</c></summary>
    internal string Describe()
    {
        long safe = Safe, cornered = Cornered, died = Died, refused = Refused;
        return string.Create(CultureInfo.InvariantCulture,
            $"Retreats: {safe + cornered + died} ended (safe {safe}, cornered {cornered}, died {died}), {refused} refused as {PlayerbotRiskModel.CorneredReason}.");
    }
}

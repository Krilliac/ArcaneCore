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
    private long _safe, _cornered, _died, _refused, _refusedDied, _refusedSurvived;

    /// <summary>The upper bounds of the predicted-share bands (<see cref="PlayerbotRiskModel.RetreatDamage"/> over the bot's health).</summary>
    private static readonly float[] BandTops = [0.3f, 0.6f, float.PositiveInfinity];

    // [band, 0] ended, [band, 1] died; the last band is the retreats not weighed (lethal, a party wipe).
    private readonly long[] _bands = new long[(BandTops.Length + 1) * 2];

    /// <summary>The fleet's counters.</summary>
    internal static PlayerbotRetreatStats Shared { get; } = new();

    internal long Safe => Interlocked.Read(ref _safe);

    internal long Cornered => Interlocked.Read(ref _cornered);

    internal long Died => Interlocked.Read(ref _died);

    /// <summary>Retreats not ordered because the bot could not have broken contact before it died.</summary>
    internal long Refused => Interlocked.Read(ref _refused);

    /// <summary>Retreats that ended.</summary>
    internal long Ended => Safe + Cornered + Died;

    /// <summary>Fights in which a retreat was refused and no retreat followed, by how they ended.</summary>
    internal long RefusedDied => Interlocked.Read(ref _refusedDied);

    internal long RefusedSurvived => Interlocked.Read(ref _refusedSurvived);

    /// <summary>
    /// Count a retreat's outcome ("safe", "cornered" or "died"; anything else is ignored), and in the band of the damage share the
    /// estimate predicted for it (<paramref name="predictedShare"/>; null: not weighed), so the log shows whether the estimate is calibrated.
    /// </summary>
    internal void Record(string? outcome, float? predictedShare = null)
    {
        switch (outcome)
        {
            case "safe": Interlocked.Increment(ref _safe); break;
            case "cornered": Interlocked.Increment(ref _cornered); break;
            case "died": Interlocked.Increment(ref _died); break;
            default: return;
        }

        int band = BandTops.Length;
        if (predictedShare is { } share)
        {
            band = 0;
            while (band < BandTops.Length - 1 && !(share < BandTops[band])) band++;
        }

        Interlocked.Increment(ref _bands[band * 2]);
        if (outcome == "died") Interlocked.Increment(ref _bands[(band * 2) + 1]);
    }

    internal void RecordRefused() => Interlocked.Increment(ref _refused);

    /// <summary>A fight in which the retreat was refused (and none began) ended: the bot <paramref name="died"/> or got out of combat alive.</summary>
    internal void RecordRefusedEnd(bool died)
    {
        if (died) Interlocked.Increment(ref _refusedDied);
        else Interlocked.Increment(ref _refusedSurvived);
    }

    /// <summary>Retreats ended and died in a band (0: predicted share under 0.3, 1: under 0.6, 2: higher, 3: not weighed).</summary>
    internal (long Ended, long Died) Band(int band) => (Interlocked.Read(ref _bands[band * 2]), Interlocked.Read(ref _bands[(band * 2) + 1]));

    /// <summary>The counters as one line, e.g. <c>Retreats: 12 ended (safe 3, cornered 2, died 7), 5 refused as cornered-no-escape (fights fought on: ...). Died by predicted loss: ...</c></summary>
    internal string Describe()
    {
        long safe = Safe, cornered = Cornered, died = Died, refused = Refused;
        (long lowEnded, long lowDied) = Band(0);
        (long midEnded, long midDied) = Band(1);
        (long highEnded, long highDied) = Band(2);
        (long unweighedEnded, long unweighedDied) = Band(3);
        return string.Create(CultureInfo.InvariantCulture,
            $"Retreats: {safe + cornered + died} ended (safe {safe}, cornered {cornered}, died {died}), {refused} refused as {PlayerbotRiskModel.CorneredReason} "
            + $"(fights fought on: died {RefusedDied}, survived {RefusedSurvived}). Died by predicted loss: <0.3 {lowDied}/{lowEnded}, 0.3-0.6 {midDied}/{midEnded}, "
            + $">=0.6 {highDied}/{highEnded}, not weighed {unweighedDied}/{unweighedEnded}.");
    }
}

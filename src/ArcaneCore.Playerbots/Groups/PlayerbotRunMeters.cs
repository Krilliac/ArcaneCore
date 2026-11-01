using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// The dungeon and raid run instruments (docs/ops/metrics.md): <c>arcanecore.playerbot.runs{outcome}</c>, one count when a run's group disbands. The meter's
/// name starts with <see cref="ArcaneMeters.Prefix"/>, so the metrics store collects it when <c>Ops:Metrics:Enabled</c> is on; with no listener a count is a
/// single enabled check. The label set is closed: <see cref="Completed"/>, <see cref="GivenUp"/>, <see cref="Timeout"/>.
/// </summary>
internal static class PlayerbotRunMeters
{
    /// <summary>The run meter.</summary>
    internal static Meter Meter { get; } = new("ArcaneCore.Playerbots.Runs", "1.0");

    /// <summary>Runs that ended, by <c>outcome</c>.</summary>
    internal static Counter<long> Runs { get; } = Meter.CreateCounter<long>("arcanecore.playerbot.runs", "{run}", "Dungeon and raid runs that ended, by outcome.");

    /// <summary>Every boss of the plan died and the group left.</summary>
    internal const string Completed = "completed";

    /// <summary>The run ended without finishing, other than by a timeout (no path, too few members, the leader gone, out of wipes ...).</summary>
    internal const string GivenUp = "given_up";

    /// <summary>The run ended because a gather, travel, enter or run step timed out.</summary>
    internal const string Timeout = "timeout";

    /// <summary>The outcome label of a run that ended with <paramref name="reason"/> (null for a completed run).</summary>
    internal static string OutcomeOf(bool complete, string? reason)
        => complete ? Completed : reason is not null && reason.Contains("timeout", StringComparison.Ordinal) ? Timeout : GivenUp;

    /// <summary>The slot of <paramref name="outcome"/> in a per-coordinator tally (<see cref="Completed"/>, <see cref="GivenUp"/>, <see cref="Timeout"/>).</summary>
    internal static int IndexOf(string outcome) => outcome switch
    {
        Completed => 0,
        GivenUp => 1,
        Timeout => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "not a run outcome label"),
    };

    internal static void Count(string outcome)
    {
        if (Runs.Enabled) Runs.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }
}

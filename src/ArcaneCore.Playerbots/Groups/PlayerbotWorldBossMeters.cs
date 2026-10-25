using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// The outcome labels of <c>arcanecore.playerbot.worldboss_raids</c> (docs/ops/metrics.md): a closed set, so the metric never gains a label from a reason string.
/// <see cref="Of"/> maps the reason a group ended with, and whether it had reached the walk to the boss, to one of them.
/// </summary>
internal static class PlayerbotWorldBossOutcomes
{
    /// <summary>A raid formed (counted when its group forms).</summary>
    internal const string Formed = "formed";

    internal const string Killed = "killed";
    internal const string Wiped = "wiped";

    /// <summary>Somebody else tapped or killed the boss, or held it past <c>HoldSeconds</c>.</summary>
    internal const string BossTaken = "boss-taken";

    /// <summary>The boss vanished with no respawn pending.</summary>
    internal const string BossGone = "boss-gone";

    internal const string NotEnough = "not-enough";

    /// <summary>The raid ended before it left for the boss (invitations unanswered, the leader gone).</summary>
    internal const string FormingFailed = "forming-failed";

    /// <summary>The raid lost its leader or its invitations after it left for the boss.</summary>
    internal const string Abandoned = "abandoned";

    internal const string Timeout = "timeout";
    internal const string Stalled = "stalled";
    internal const string NoPath = "no-path";

    /// <summary>An open muster ran out of window without a raid.</summary>
    internal const string MusterFailed = "muster-failed";

    internal const string Other = "other";

    /// <summary>Every label.</summary>
    internal static readonly string[] All =
        [Formed, Killed, Wiped, BossTaken, BossGone, NotEnough, FormingFailed, Abandoned, Timeout, Stalled, NoPath, MusterFailed, Other];

    /// <summary>The label of a group that ended with <paramref name="reason"/> (its <c>Finish</c> reason; exact, case sensitive); a reason not listed is <see cref="Other"/>.</summary>
    internal static string Of(string? reason, bool reachedTravelling)
    {
        switch (reason)
        {
            case "killed": return Killed;
            case "wiped": return Wiped;
            case "boss-taken": return BossTaken;
            case "boss-gone" or "objective-missing": return BossGone;
            case "not-enough" or "too-few": return NotEnough;
            case "stalled": return Stalled;
            case "invites-unanswered" or "leader-gone" or "leader-stopped" or "leader-left": return reachedTravelling ? Abandoned : FormingFailed;
            case null: return Other;
        }

        if (reason.Contains("timeout", StringComparison.Ordinal)) return Timeout;
        if (reason.Contains("no-path", StringComparison.Ordinal)) return NoPath;
        return Other;
    }
}

/// <summary>
/// The world-boss instruments (docs/ops/metrics.md): <c>arcanecore.playerbot.worldboss_raids{outcome}</c>, one count per raid formed, per raid ended and per
/// muster that failed, and <c>arcanecore.playerbot.worldboss_active</c>, the raids under way. The meter's name starts with <see cref="ArcaneMeters.Prefix"/>, so
/// the metrics store collects it when <c>Ops:Metrics:Enabled</c> is on; with no listener a count is a single enabled check.
/// </summary>
internal static class PlayerbotWorldBossMeters
{
    private static int _active;

    /// <summary>The world-boss meter.</summary>
    internal static Meter Meter { get; } = new("ArcaneCore.Playerbots.WorldBosses", "1.0");

    /// <summary>Raids and musters, by <c>outcome</c> (<see cref="PlayerbotWorldBossOutcomes"/>).</summary>
    internal static Counter<long> Raids { get; } = Meter.CreateCounter<long>("arcanecore.playerbot.worldboss_raids", "{raid}", "World-boss raids formed and ended, and musters that failed, by outcome.");

    /// <summary>The raids under way now (the last value a coordinator set).</summary>
    internal static ObservableGauge<int> Active { get; } = Meter.CreateObservableGauge("arcanecore.playerbot.worldboss_active", static () => Volatile.Read(ref _active), "{raid}", "World-boss raids under way.");

    internal static void Count(string outcome)
    {
        if (Raids.Enabled) Raids.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    internal static void SetActive(int raids) => Volatile.Write(ref _active, raids);
}

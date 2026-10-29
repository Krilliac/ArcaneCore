using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Validation;

/// <summary>
/// Start-up checks for the realm list's liveness settings (docs/ops/realm-liveness.md): the world's beat interval
/// (<c>World:Realm:HeartbeatIntervalSeconds</c>), the logon daemon's stale threshold (<c>Realm:Liveness:HeartbeatStaleSeconds</c>) and how they
/// relate to each other and to the tick watchdog's hang threshold. Both daemons run it, each against the file it reads: a key the daemon does not
/// read is checked at its default, so a threshold that is only wrong against the other daemon's file is caught by that daemon.
/// </summary>
public sealed class RealmLivenessConfigChecks : IConfigCheck
{
    private const string IntervalKey = "World:Realm:HeartbeatIntervalSeconds";
    private const string StaleKey = "Realm:Liveness:HeartbeatStaleSeconds";
    private const string RestartingKey = "Realm:Liveness:RestartingAs";
    private const string HangKey = "Ops:Watchdog:TickMonitor:HangMs";

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigIssue>();

        int? interval = ConfigChecks.ReadRange(issues, configuration, IntervalKey, 1, 3600, "1..3600 seconds (default 10)");
        int? stale = ConfigChecks.ReadRange(issues, configuration, StaleKey, 0, 86_400, "0 to turn the heartbeat check off, or 1..86400 seconds (default 60)");
        int? hangMs = ConfigChecks.ReadRange(issues, configuration, HangKey, 0, int.MaxValue, "0 to disable hang detection, or a duration in milliseconds");

        string? restarting = configuration[RestartingKey];
        if (!string.IsNullOrWhiteSpace(restarting) && !(Enum.TryParse(restarting, ignoreCase: true, out RestartingRealmDisplay display) && Enum.IsDefined(display)))
        {
            issues.Add(ConfigChecks.Error(RestartingKey, $"'{restarting}' is not a display.", "use " + string.Join(", ", Enum.GetNames<RestartingRealmDisplay>())));
        }

        int effectiveInterval = interval ?? 10;
        int effectiveStale = stale ?? new RealmLivenessOptions().HeartbeatStaleSeconds;
        if (effectiveStale > 0 && effectiveStale < 3 * effectiveInterval)
        {
            issues.Add(ConfigChecks.Error(StaleKey,
                $"{effectiveStale} s is below three times the world's beat interval ({effectiveInterval} s), so one or two missed beats would show the realm offline.",
                $"use at least {3 * effectiveInterval} seconds, or lower {IntervalKey}"));
        }

        int effectiveHangMs = hangMs ?? new WatchdogOptions().TickMonitor.HangMs;
        if (effectiveStale > 0 && effectiveHangMs > 0 && effectiveStale * 1000L <= 5L * effectiveHangMs)
        {
            issues.Add(ConfigChecks.Error(StaleKey,
                $"{effectiveStale} s is not above five times the tick watchdog's hang threshold ({effectiveHangMs} ms), so a tick the watchdog calls a hang would nearly take the realm offline.",
                $"use more than {5L * effectiveHangMs / 1000.0:0.#} seconds, or lower {HangKey}"));
        }

        return issues;
    }
}

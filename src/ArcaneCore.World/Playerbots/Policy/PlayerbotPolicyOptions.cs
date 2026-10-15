namespace ArcaneCore.World.Playerbots.Policy;

/// <summary>
/// <c>World:Playerbots:Policy</c>: the bots' goal policy engine (<see cref="PlayerbotPolicyEngine"/>; docs/areas/playbots.md, Goal policy).
/// Off by default; on, with no <see cref="ModelPath"/>, it scores with the rule ladder itself, so it always agrees with the ladder and the
/// bots choose the goals they would without it (its snapshots still add the rungs' probes to a think every <see cref="IntervalMs"/>).
/// </summary>
public sealed class PlayerbotPolicyOptions
{
    /// <summary>Score each bot's goals off the world thread and let the scores re-rank the ladder's choice (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A goal model weights file (JSON, <c>arcanecore.playerbot-policy</c> v1; relative to the profile folder, or absolute). Empty scores with
    /// the rule ladder. A file that does not load is reported once (anomaly <c>bot-policy/model-load-failed</c>) and the ladder scores instead.
    /// </summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>World milliseconds between two snapshots of one bot's state for scoring (500..60000).</summary>
    public int IntervalMs { get; set; } = 3000;

    /// <summary>Scores taken from a snapshot older than this (world milliseconds) are not used: that think, the ladder decides (500..120000).</summary>
    public int StaleAfterMs { get; set; } = 4500;

    /// <summary>Wall-clock milliseconds one map's batch may score for in one tick; the rows past it are left to the ladder and counted (0.1..50).</summary>
    public double TickBudgetMs { get; set; } = 2.0;

    /// <summary>
    /// The chance (0..0.2) that a snapshot's choice is instead one of the ladder's eligible runner-up goals, picked at random, for gathering
    /// telemetry the ladder alone never produces. 0 (the default) never explores.
    /// </summary>
    public double ExplorationRate { get; set; }

    /// <summary>Decision telemetry for training (<c>World:Playerbots:Policy:Telemetry</c>; off by default).</summary>
    public PlayerbotPolicyTelemetryOptions Telemetry { get; set; } = new();

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Policy";
        if (ModelPath is null || ModelPath.Length > 260 || ModelPath.Any(char.IsControl))
            throw new InvalidOperationException($"{section}: ModelPath must be at most 260 characters and contain no control characters.");
        if (IntervalMs is < 500 or > 60_000) throw new InvalidOperationException($"{section}: IntervalMs must be 500..60000.");
        if (StaleAfterMs is < 500 or > 120_000) throw new InvalidOperationException($"{section}: StaleAfterMs must be 500..120000.");
        // Shorter, and every snapshot cycle would end in a gap where each think finds its snapshot stale.
        if (StaleAfterMs < IntervalMs) throw new InvalidOperationException($"{section}: StaleAfterMs must be at least IntervalMs.");
        if (!double.IsFinite(TickBudgetMs) || TickBudgetMs is < 0.1 or > 50)
            throw new InvalidOperationException($"{section}: TickBudgetMs must be 0.1..50.");
        if (!double.IsFinite(ExplorationRate) || ExplorationRate is < 0 or > 0.2)
            throw new InvalidOperationException($"{section}: ExplorationRate must be 0..0.2.");
        if (Telemetry is null) throw new InvalidOperationException($"{section}:Telemetry is missing.");
        Telemetry.Validate();
        // Telemetry and exploration are made by the engine: asked for with the engine off, they would silently do nothing.
        if (!Enabled && (Telemetry.Enabled || ExplorationRate > 0))
            throw new InvalidOperationException($"{section}: Telemetry and ExplorationRate need Enabled = true.");
    }
}

/// <summary><c>World:Playerbots:Policy:Telemetry</c>: one record per bot decision with its outcome, for training (<see cref="PlayerbotPolicyTelemetry"/>).</summary>
public sealed class PlayerbotPolicyTelemetryOptions
{
    /// <summary>Log each decision's state, choice and outcome (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>The folder of the telemetry files: relative to the profile folder, or absolute.</summary>
    public string Directory { get; set; } = "telemetry/playerbot-policy";

    /// <summary>Minutes of world time a decision's outcome is measured over (1..60).</summary>
    public int OutcomeMinutes { get; set; } = 5;

    /// <summary>The largest one telemetry file grows before the next one starts, in bytes (64 KiB..1 GiB).</summary>
    public long MaxFileBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>The most telemetry files kept; the oldest goes first (1..1000). The folder holds at most MaxFiles x MaxFileBytes.</summary>
    public int MaxFiles { get; set; } = 16;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Policy:Telemetry";
        if (string.IsNullOrWhiteSpace(Directory) || Directory.Length > 260 || Directory.Any(char.IsControl))
            throw new InvalidOperationException($"{section}: Directory must be non-empty, at most 260 characters, and contain no control characters.");
        if (OutcomeMinutes is < 1 or > 60) throw new InvalidOperationException($"{section}: OutcomeMinutes must be 1..60.");
        if (MaxFileBytes is < 64 * 1024 or > 1024L * 1024 * 1024) throw new InvalidOperationException($"{section}: MaxFileBytes must be 65536..1073741824.");
        if (MaxFiles is < 1 or > 1000) throw new InvalidOperationException($"{section}: MaxFiles must be 1..1000.");
    }
}

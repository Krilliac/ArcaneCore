using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Game.AntiCheat;

/// <summary>
/// The <c>AntiCheat</c> section (docs/areas/anticheat.md): the server-side movement and rejection-point checks ported from
/// the MaNGOS Zero anticheat fork (feature/anticheat-tooling, feature/timesync, feature/anticheat-detection-framework). On by
/// default with <see cref="Action"/> = <see cref="AntiCheatAction.Log"/>: findings are scored and logged, nothing is ever
/// done to a player until an operator raises the ceiling. Staff accounts, GM mode and the server's own managed playerbot
/// sessions are never checked. <c>.reload config</c> applies every key at once.
/// </summary>
public sealed class AntiCheatOptions
{
    public const string SectionName = "AntiCheat";

    /// <summary>Run the checks at all. Bound from AntiCheat:Enabled; default true (log only, see <see cref="Action"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The highest escalation ever applied: None (score only), Log, GmAlert, Rubberband, Kick. A score past a threshold
    /// whose action is above this ceiling gets the ceiling instead. Bound from AntiCheat:Action; default Log.
    /// </summary>
    public AntiCheatAction Action { get; set; } = AntiCheatAction.Log;

    /// <summary>
    /// Accounts at or above this security level are not checked; Player checks everyone (a GM in GM mode is still exempt).
    /// Bound from AntiCheat:ExemptSecurity; default Moderator (every staff account).
    /// </summary>
    public AccountSecurity ExemptSecurity { get; set; } = AccountSecurity.Moderator;

    /// <summary>The server's own managed playerbot sessions are not checked (they are trusted server code). Bound from AntiCheat:ExemptManagedBots; default true.</summary>
    public bool ExemptManagedBots { get; set; } = true;

    /// <summary>Score at which the GM alert applies (fork AntiCheat.Score.Warn). Default 30.</summary>
    public float ScoreGmAlert { get; set; } = 30;

    /// <summary>Score at which the player is moved back to the last validated position (fork AntiCheat.Score.Rubberband). Default 60.</summary>
    public float ScoreRubberband { get; set; } = 60;

    /// <summary>Score at which the session is disconnected (fork AntiCheat.Score.Kick). Default 120.</summary>
    public float ScoreKick { get; set; } = 120;

    /// <summary>Score points forgotten per second, so occasional noise never adds up (fork AntiCheat.Score.DecayPerSec). Default 2.</summary>
    public float DecayPerSecond { get; set; } = 2;

    /// <summary>Tolerance above the allowed speed, in percent, before the speed check scores. Default 10.</summary>
    public float SpeedTolerancePercent { get; set; } = 10;

    /// <summary>Distance in yards that one packet may always cover on top of the speed budget (rounding, collision push). Default 2.</summary>
    public float SpeedSlackYards { get; set; } = 2;

    /// <summary>One packet moving farther than this, plus what the speed covers in the latency slack, is a teleport (fork AntiCheat.Teleport.Distance). Default 50.</summary>
    public float TeleportDistance { get; set; } = 50;

    /// <summary>
    /// The base of the latency slack in milliseconds: the time budget of a movement step is the client's own elapsed time,
    /// capped by the server's receive interval plus this plus twice the ping latency average. Default 1000.
    /// </summary>
    public int LatencySlackMs { get; set; } = 1000;

    /// <summary>The most the latency slack may grow to, in milliseconds. Default 3000.</summary>
    public int MaxLatencySlackMs { get; set; } = 3000;

    /// <summary>
    /// After a speed change is acknowledged the previous (higher) speed still counts for this long, in milliseconds, plus the
    /// latency slack, so packets already in flight are never judged by the new speed. Default 2000.
    /// </summary>
    public int SpeedChangeGraceMs { get; set; } = 2000;

    /// <summary>A gap this long (milliseconds) between two movement packets starts a new baseline (loading screen, AFK). Default 3000.</summary>
    public int BaselineGapMs { get; set; } = 3000;

    /// <summary>Movement packets per second, by the receive clock AND the client's own clock, that count as a burst. Default 50.</summary>
    public int BurstPacketsPerSecond { get; set; } = 50;

    /// <summary>A client timestamp that goes back by more than this many milliseconds is scored. Default 500.</summary>
    public int ClientTimeRegressionMs { get; set; } = 500;

    /// <summary>A drop of this many yards that ends without MSG_MOVE_FALL_LAND is fall-damage suppression. Default 20.</summary>
    public float FallSuppressYards { get; set; } = 20;

    /// <summary>A CMSG_MOVE_TIME_SKIPPED reporting more than this many milliseconds is scored (a client freeze rarely exceeds it). Default 10000.</summary>
    public int MaxTimeSkipMs { get; set; } = 10000;

    /// <summary>More CMSG_MOVE_TIME_SKIPPED than this in ten seconds is scored as spam. Default 10.</summary>
    public int MaxTimeSkipsPer10Seconds { get; set; } = 10;

    /// <summary>
    /// Run the checks that need world geometry (swimming out of water, climbing into the air, walking through a wall). Each
    /// runs only where the terrain or vmap data for that spot is actually loaded; without data it never scores. Default true.
    /// </summary>
    public bool TerrainChecks { get; set; } = true;

    /// <summary>The independent-clock speed-hack detector (the fork's gateway SpeedHackDetector).</summary>
    public SpeedClockOptions SpeedClock { get; set; } = new();

    /// <summary>At most one GM alert per offender per this many seconds. Default 10.</summary>
    public int GmAlertIntervalSeconds { get; set; } = 10;

    /// <summary>At most one rubberband per offender per this many milliseconds. Default 2000.</summary>
    public int RubberbandIntervalMs { get; set; } = 2000;

    /// <summary>The violation log (characters table character_anticheat_log).</summary>
    public AntiCheatLogOptions Log { get; set; } = new();

    /// <summary>The account autoban on repeated kicks.</summary>
    public AntiCheatAutobanOptions Autoban { get; set; } = new();

    /// <summary>A copy (the live options are replaced, never edited in place, so a check never sees half a change).</summary>
    public AntiCheatOptions Clone()
    {
        var copy = (AntiCheatOptions)MemberwiseClone();
        copy.SpeedClock = (SpeedClockOptions)SpeedClock.Clone();
        copy.Log = (AntiCheatLogOptions)Log.Clone();
        copy.Autoban = Autoban.Clone();
        return copy;
    }

    /// <summary>Every problem of these values (empty when they are usable); shared by the startup check and <c>.reload config</c>.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Enum.IsDefined(Action))
        {
            problems.Add($"AntiCheat:Action ({Action}) must be None, Log, GmAlert, Rubberband or Kick.");
        }

        if (!Enum.IsDefined(ExemptSecurity))
        {
            problems.Add($"AntiCheat:ExemptSecurity ({ExemptSecurity}) must be Player, Moderator, GameMaster or Administrator.");
        }

        Positive(problems, "ScoreGmAlert", ScoreGmAlert);
        Positive(problems, "ScoreRubberband", ScoreRubberband);
        Positive(problems, "ScoreKick", ScoreKick);
        if (ScoreGmAlert > ScoreRubberband || ScoreRubberband > ScoreKick)
        {
            problems.Add("AntiCheat:ScoreGmAlert, ScoreRubberband and ScoreKick must not decrease.");
        }

        NotNegative(problems, "DecayPerSecond", DecayPerSecond);
        NotNegative(problems, "SpeedTolerancePercent", SpeedTolerancePercent);
        NotNegative(problems, "SpeedSlackYards", SpeedSlackYards);
        Positive(problems, "TeleportDistance", TeleportDistance);
        NotNegative(problems, "LatencySlackMs", LatencySlackMs);
        if (MaxLatencySlackMs < LatencySlackMs)
        {
            problems.Add("AntiCheat:MaxLatencySlackMs must be at least LatencySlackMs.");
        }

        NotNegative(problems, "SpeedChangeGraceMs", SpeedChangeGraceMs);
        Positive(problems, "BaselineGapMs", BaselineGapMs);
        Positive(problems, "BurstPacketsPerSecond", BurstPacketsPerSecond);
        NotNegative(problems, "ClientTimeRegressionMs", ClientTimeRegressionMs);
        Positive(problems, "FallSuppressYards", FallSuppressYards);
        Positive(problems, "MaxTimeSkipMs", MaxTimeSkipMs);
        Positive(problems, "MaxTimeSkipsPer10Seconds", MaxTimeSkipsPer10Seconds);
        NotNegative(problems, "GmAlertIntervalSeconds", GmAlertIntervalSeconds);
        NotNegative(problems, "RubberbandIntervalMs", RubberbandIntervalMs);
        problems.AddRange(SpeedClock.Validate());
        problems.AddRange(Log.Validate());
        problems.AddRange(Autoban.Validate());
        return problems;
    }

    private static void Positive(List<string> problems, string key, double value)
    {
        if (!(value > 0) || !double.IsFinite(value))
        {
            problems.Add($"AntiCheat:{key} ({value}) must be a positive number.");
        }
    }

    private static void NotNegative(List<string> problems, string key, double value)
    {
        if (!(value >= 0) || !double.IsFinite(value))
        {
            problems.Add($"AntiCheat:{key} ({value}) must not be negative.");
        }
    }
}

/// <summary>
/// <c>AntiCheat:SpeedClock</c>: the independent-clock detector (the fork's src/gateway/SpeedHackDetector). Over a sliding
/// window it compares the client's movement timestamps with the server's receive times; a sustained ratio above
/// 1 + <see cref="TolerancePercent"/>/100 is a fast client clock (a speed hack that speeds the whole game up).
/// </summary>
public sealed class SpeedClockOptions : ICloneable
{
    /// <summary>Run it. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pairs of consecutive packets in the window. Default 20.</summary>
    public int Window { get; set; } = 20;

    /// <summary>Usable pairs needed before the window is judged. Default 12.</summary>
    public int MinSamples { get; set; } = 12;

    /// <summary>How much faster than real time, in percent, the client clock may run. Default 30.</summary>
    public int TolerancePercent { get; set; } = 30;

    /// <summary>Consecutive hot evaluations before it fires. Default 3.</summary>
    public int SustainWindows { get; set; } = 3;

    /// <summary>A pair further apart than this (milliseconds) is ignored (the player stood still or the client froze). Default 3000.</summary>
    public int MaxGapMs { get; set; } = 3000;

    /// <summary>After firing it stays silent for this long (milliseconds). Default 10000.</summary>
    public int CooldownMs { get; set; } = 10000;

    public object Clone() => MemberwiseClone();

    public IEnumerable<string> Validate()
    {
        if (Window < 2 || Window > 1000)
        {
            yield return $"AntiCheat:SpeedClock:Window ({Window}) must be 2 to 1000.";
        }

        if (MinSamples < 1 || MinSamples > Window)
        {
            yield return $"AntiCheat:SpeedClock:MinSamples ({MinSamples}) must be 1 to Window.";
        }

        if (TolerancePercent <= 0)
        {
            yield return $"AntiCheat:SpeedClock:TolerancePercent ({TolerancePercent}) must be positive.";
        }

        if (SustainWindows < 1)
        {
            yield return $"AntiCheat:SpeedClock:SustainWindows ({SustainWindows}) must be at least 1.";
        }

        if (MaxGapMs <= 0 || CooldownMs < 0)
        {
            yield return "AntiCheat:SpeedClock:MaxGapMs must be positive and CooldownMs not negative.";
        }
    }
}

/// <summary><c>AntiCheat:Log</c>: the batched violation log (never one INSERT per violation).</summary>
public sealed class AntiCheatLogOptions : ICloneable
{
    /// <summary>Write violations to character_anticheat_log. Default true.</summary>
    public bool Persist { get; set; } = true;

    /// <summary>Seconds between two flushes of the queued rows. Default 10.</summary>
    public int FlushIntervalSeconds { get; set; } = 10;

    /// <summary>Most rows written by one flush (the rest wait for the next). Default 500.</summary>
    public int MaxRowsPerFlush { get; set; } = 500;

    /// <summary>Most rows waiting in memory; beyond it the newest are dropped and counted. Default 10000.</summary>
    public int MaxQueuedRows { get; set; } = 10000;

    /// <summary>
    /// Repeats of one violation type by one character within this many milliseconds are folded into the row already queued
    /// (its count goes up) instead of adding rows. Default 5000.
    /// </summary>
    public int CoalesceMs { get; set; } = 5000;

    public object Clone() => MemberwiseClone();

    public IEnumerable<string> Validate()
    {
        if (FlushIntervalSeconds <= 0 || MaxRowsPerFlush <= 0 || MaxQueuedRows <= 0 || CoalesceMs < 0)
        {
            yield return "AntiCheat:Log: FlushIntervalSeconds, MaxRowsPerFlush and MaxQueuedRows must be positive and CoalesceMs not negative.";
        }
    }
}

/// <summary>
/// <c>AntiCheat:Autoban</c> (the fork's AntiCheat.Autoban.*): every anticheat kick adds <see cref="KickPoints"/> to the
/// account, the points decay by <see cref="DecayPerHour"/>, and at <see cref="Threshold"/> the account is banned through the
/// ban store for the next duration of the ladder (<see cref="FirstBanSeconds"/>, <see cref="SecondBanSeconds"/>,
/// <see cref="LaterBanSeconds"/>: 1 day, 7 days, then permanent). The step is the number of earlier
/// AntiCheat bans of the account within <see cref="HistoryDays"/>, read from the ban history, so it survives restarts. Off by default.
/// </summary>
public sealed class AntiCheatAutobanOptions
{
    /// <summary>Ban accounts on repeated kicks. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Points one kick adds. Default 10.</summary>
    public float KickPoints { get; set; } = 10;

    /// <summary>
    /// Points at which the account is banned. Default 25: the third kick within five hours of the first (the fork's 30 with
    /// any decay at all needs a fourth kick).
    /// </summary>
    public float Threshold { get; set; } = 25;

    /// <summary>Points forgotten per hour. Default 1.</summary>
    public float DecayPerHour { get; set; } = 1;

    /// <summary>Seconds of the first ban (0 is permanent). Default 86400 (one day).</summary>
    public long FirstBanSeconds { get; set; } = 86400;

    /// <summary>Seconds of the second ban (0 is permanent). Default 604800 (seven days).</summary>
    public long SecondBanSeconds { get; set; } = 604800;

    /// <summary>Seconds of the third and every later ban (0 is permanent). Default 0 (permanent).</summary>
    public long LaterBanSeconds { get; set; }

    /// <summary>Earlier AntiCheat bans older than this many days do not move the account up the ladder. Default 180.</summary>
    public int HistoryDays { get; set; } = 180;

    public AntiCheatAutobanOptions Clone() => (AntiCheatAutobanOptions)MemberwiseClone();

    /// <summary>The ban duration (seconds, 0 permanent) after <paramref name="earlierBans"/> earlier AntiCheat bans: the last step is sticky.</summary>
    public long DurationFor(int earlierBans) => earlierBans switch
    {
        <= 0 => FirstBanSeconds,
        1 => SecondBanSeconds,
        _ => LaterBanSeconds,
    };

    public IEnumerable<string> Validate()
    {
        if (!(KickPoints > 0) || !(Threshold > 0) || !(DecayPerHour >= 0))
        {
            yield return "AntiCheat:Autoban: KickPoints and Threshold must be positive and DecayPerHour not negative.";
        }

        if (FirstBanSeconds < 0 || SecondBanSeconds < 0 || LaterBanSeconds < 0)
        {
            yield return "AntiCheat:Autoban: FirstBanSeconds, SecondBanSeconds and LaterBanSeconds must not be negative (0 is permanent).";
        }

        if (HistoryDays < 0)
        {
            yield return "AntiCheat:Autoban:HistoryDays must not be negative.";
        }
    }
}

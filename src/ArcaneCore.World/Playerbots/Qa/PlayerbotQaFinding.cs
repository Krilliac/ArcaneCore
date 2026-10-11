using System.Globalization;
using System.Text.Json.Serialization;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The likely cause class of a finding (<see cref="PlayerbotQaClassifier"/>).</summary>
public enum QaCause
{
    Unknown,

    /// <summary>The content lacks something the bots need (a spawn, a template, a waypoint path, a script).</summary>
    MissingData,

    /// <summary>The bot code misbehaved (a recovery exception, a quarantine, a death with no attacker).</summary>
    BehaviourBug,

    /// <summary>The content is simply harder than the bots (elite attackers, a death loop against higher levels).</summary>
    ContentDifficulty,

    /// <summary>
    /// A homed bot stalled outside its home area: on its way back, or on a turn-in or capital errand that took it out. Expected for a homed
    /// bot, set nothing aside for other bots, and is not reported to GitHub (the report still lists it).
    /// </summary>
    OutsideHome,
}

/// <summary>Where a finding stands with GitHub.</summary>
internal enum QaPublicationState
{
    /// <summary>Not reported (below the thresholds, reporter off, or waiting for a cap).</summary>
    Pending,

    /// <summary>The would-be issue was written to <c>qa/pending-issues/</c>; nothing was sent.</summary>
    DryRun,

    /// <summary>An issue exists (<see cref="QaPublication.Issue"/>), created or found.</summary>
    Published,

    /// <summary>The finding's issue was closed by a person; it is left alone.</summary>
    KnownClosed,

    /// <summary>The last publish attempt failed (<see cref="QaPublication.Reason"/>); it is retried on the next pass.</summary>
    Failed,
}

/// <summary>A finding's publication state.</summary>
internal sealed record QaPublication(QaPublicationState State, int Issue = 0, string? Reason = null)
{
    public static QaPublication Pending { get; } = new(QaPublicationState.Pending);

    /// <summary>Published and known-closed findings are never evicted: their number is the only local memory of the issue.</summary>
    [JsonIgnore]
    public bool Pinned => State is QaPublicationState.Published or QaPublicationState.KnownClosed;

    public override string ToString() => State switch
    {
        QaPublicationState.Published => string.Create(CultureInfo.InvariantCulture, $"Published#{Issue}"),
        QaPublicationState.KnownClosed => string.Create(CultureInfo.InvariantCulture, $"KnownClosed#{Issue}"),
        QaPublicationState.Failed => "Failed(" + Reason + ")",
        _ => State.ToString(),
    };
}

/// <summary>One piece of evidence: where and when, who attacked, and the redacted log line.</summary>
internal sealed record QaSample(DateTime AtUtc, bool HasPosition, uint Map, float X, float Y, float Z, uint[] Attackers, string Message);

/// <summary>
/// An immutable snapshot of a finding (commands, reports and the publisher read these; the aggregator keeps the mutable state). Also the
/// persisted form. It holds no bot name and no bot id.
/// </summary>
internal sealed record QaFindingView
{
    /// <summary>The fingerprint: 16 hex characters, the marker of the GitHub issue.</summary>
    public required string Id { get; init; }

    public required string Key { get; init; }

    public required QaSignalKind Kind { get; init; }

    /// <summary>What it is about, in words (the issue title after the kind).</summary>
    public string Subject { get; init; } = string.Empty;

    public long Count { get; init; }

    public int DistinctBots { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public DateTime LastSeenUtc { get; init; }

    public IReadOnlyList<QaSample> Samples { get; init; } = [];

    public uint Quest { get; init; }

    public uint Target { get; init; }

    public uint Map { get; init; }

    public string? QuestTitle { get; init; }

    public QaCause Cause { get; init; }

    public string CauseDetail { get; init; } = string.Empty;

    public double Severity { get; init; }

    public QaPublication Publication { get; init; } = QaPublication.Pending;

    public DateTime? LastCommentUtc { get; init; }

    /// <summary>The occurrences already told to GitHub (the issue body, comments) or written to a dry-run file.</summary>
    public long OccurrencesReported { get; init; }

    [JsonIgnore]
    public string ShortId => PlayerbotQaFingerprint.ShortId(Id);

    /// <summary>Whether the finding comes from creature data rather than from bots (it has no bot count to threshold on).</summary>
    [JsonIgnore]
    public bool BotIndependent => Kind is QaSignalKind.RelayDataMissing or QaSignalKind.RelayScriptMissing;

    [JsonIgnore]
    public string KindLabel => LabelOf(Kind);

    internal static string LabelOf(QaSignalKind kind) => kind switch
    {
        QaSignalKind.Stall => "stall",
        QaSignalKind.SharedSetAside => "shared set-aside",
        QaSignalKind.DeathLoop => "death loop",
        QaSignalKind.ActionFault => "action fault",
        QaSignalKind.Quarantine => "quarantine",
        QaSignalKind.Disabled => "disabled",
        QaSignalKind.RelayDataMissing => "relay data",
        QaSignalKind.RelayScriptMissing => "relay script",
        _ => kind.ToString(),
    };
}

/// <summary>What a publish attempt changed about a finding.</summary>
internal sealed record QaPublicationUpdate(QaPublication Publication, DateTime? LastCommentUtc = null, long? OccurrencesReported = null);

/// <summary>The mutable aggregate behind a <see cref="QaFindingView"/>; owned by <see cref="PlayerbotQaAggregator"/> and touched only under its lock.</summary>
internal sealed class QaFinding
{
    internal const int MaxTrackedBots = 512;

    public required string Id { get; init; }

    public required string Key { get; init; }

    public required QaSignalKind Kind { get; init; }

    public string Subject { get; set; } = string.Empty;

    public long Count { get; set; }

    public HashSet<ulong> Bots { get; } = [];

    /// <summary>The bots a signal without bot ids said stalled (a shared set-aside).</summary>
    public int AnonymousBots { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public List<QaSample> Samples { get; } = [];

    public uint Quest { get; set; }

    public uint Target { get; set; }

    public uint Map { get; set; }

    public string? QuestTitle { get; set; }

    public QaCause Cause { get; set; }

    public string CauseDetail { get; set; } = string.Empty;

    public double Severity { get; set; }

    public QaPublication Publication { get; set; } = QaPublication.Pending;

    public DateTime? LastCommentUtc { get; set; }

    public long OccurrencesReported { get; set; }

    /// <summary>Different bots seen (saturates at <see cref="MaxTrackedBots"/> tracked ids; a bot-less signal's own count counts too).</summary>
    public int DistinctBots => Math.Max(Bots.Count, AnonymousBots);

    public QaFindingView ToView() => new()
    {
        Id = Id, Key = Key, Kind = Kind, Subject = Subject, Count = Count, DistinctBots = DistinctBots, FirstSeenUtc = FirstSeenUtc,
        LastSeenUtc = LastSeenUtc, Samples = [.. Samples], Quest = Quest, Target = Target, Map = Map, QuestTitle = QuestTitle, Cause = Cause,
        CauseDetail = CauseDetail, Severity = Severity, Publication = Publication, LastCommentUtc = LastCommentUtc,
        OccurrencesReported = OccurrencesReported,
    };
}

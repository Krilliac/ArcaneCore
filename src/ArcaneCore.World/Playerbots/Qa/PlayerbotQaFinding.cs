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

    /// <summary>The server does not implement what the bots used: a spell effect, aura type, script or implicit target with no handler.</summary>
    NotImplemented,

    /// <summary>An exception the server caught and survived at one of its catch-and-log sites (a server bug, not a bot behaviour).</summary>
    ServerFault,
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

    /// <summary>The finding has a GitHub issue, in any state: a failed attempt keeps the number of the issue it was working on, and that issue exists.</summary>
    [JsonIgnore]
    public bool HoldsIssue => Issue > 0;

    public override string ToString() => State switch
    {
        QaPublicationState.Published => string.Create(CultureInfo.InvariantCulture, $"Published#{Issue}"),
        QaPublicationState.KnownClosed => string.Create(CultureInfo.InvariantCulture, $"KnownClosed#{Issue}"),
        QaPublicationState.Failed => "Failed(" + Reason + ")",
        _ => State.ToString(),
    };
}

/// <summary>How many occurrences of a finding the server saw on one build (<see cref="QaFindingView.BuildCounts"/>).</summary>
internal readonly record struct QaBuildCount(string Build, long Count);

/// <summary>A build this store has run and when it first saw it (UTC): what makes one build "newer" than another, since build strings do not order.</summary>
internal sealed record QaBuildSeen(string Build, DateTime FirstSeenUtc);

/// <summary>One piece of evidence: where and when, who attacked, and the redacted log line.</summary>
internal sealed record QaSample(DateTime AtUtc, bool HasPosition, uint Map, float X, float Y, float Z, uint[] Attackers, string Message)
{
    public QaBotObservation? Bot { get; init; }
}

/// <summary>
/// Where a finding began: the position of its first sample. Fixed for the life of the finding (it never follows later signals), so a signal
/// folds into the finding only when it lies within <see cref="PlayerbotQaAggregator.AnchorYards"/> of this point. Persisted with the finding.
/// </summary>
internal sealed record QaAnchor(uint Map, float X, float Y);

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

    /// <summary>
    /// The goal the bot held (<c>goal=</c> of a stall; <c>Quest</c> or <c>Entry</c> of a set-aside). Empty in files written before 2026-10-11:
    /// <see cref="PlayerbotQaAggregator.Restore"/> takes it from the key then.
    /// </summary>
    public string Goal { get; init; } = string.Empty;

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

    /// <summary>The spell a spell finding is about (0 for other kinds).</summary>
    public uint Spell { get; init; }

    public string SpellName { get; init; } = string.Empty;

    public string SpellRank { get; init; } = string.Empty;

    /// <summary>The effect index (0..2), or -1 when the finding is about the whole spell.</summary>
    public int EffectIndex { get; init; } = -1;

    public string EffectName { get; init; } = string.Empty;

    public int EffectValue { get; init; }

    /// <summary>The aura type number of an aura effect (0 when none).</summary>
    public uint Aura { get; init; }

    /// <summary>The gap code of an unsupported spell, the cast result of a refusal (<see cref="QaSignal.Reason"/>).</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>What the reason names, in words (the effect or aura name, the target type).</summary>
    public string ReasonDetail { get; init; } = string.Empty;

    public uint Item { get; init; }

    public uint Npc { get; init; }

    public uint GameObject { get; init; }

    /// <summary>The classes of the bots that met it: bit c = class c (<c>ArcaneCore.Game.Class</c>).</summary>
    public uint ClassMask { get; init; }

    /// <summary>The start-up spell coverage lists it (<see cref="QaSignal.FromCoverage"/>): it is a gap whether or not a bot met it yet.</summary>
    public bool Coverage { get; init; }

    /// <summary>
    /// The position of the finding's first sample (null for a finding without a position, and in files written before 2026-10-12:
    /// <see cref="PlayerbotQaAggregator.Restore"/> then takes the first sample's position, else the centre of the key's location cell).
    /// </summary>
    public QaAnchor? Anchor { get; init; }

    /// <summary>
    /// A death loop's identity: the attacker entries of its first signal (at most <see cref="QaFinding.MaxAttackers"/>); empty for other kinds
    /// and in older files (the oldest saved sample's attackers, else the subject's, stand in then).
    /// </summary>
    public IReadOnlyList<uint> Attackers { get; init; } = [];

    /// <summary>A death loop without attackers: the death source of its first signal (<c>Environment</c>, <c>Unobserved</c>...); empty otherwise.</summary>
    public string DeathSource { get; init; } = string.Empty;

    /// <summary>The build (<see cref="PlayerbotQaIssueComposer.BuildVersion"/>) that met the finding first. Null in files written before format 2: the build is unknown.</summary>
    public string? FirstBuild { get; init; }

    /// <summary>The build that met the finding last. Null in files written before format 2.</summary>
    public string? LastBuild { get; init; }

    /// <summary>
    /// Occurrences per build, most recent build first, at most <see cref="QaFinding.MaxTrackedBuilds"/> entries (the build now running and the one before it);
    /// the counts of older builds are dropped. Empty in files written before format 2.
    /// </summary>
    public IReadOnlyList<QaBuildCount> BuildCounts { get; init; } = [];

    /// <summary>
    /// The build that was running when the finding was first observed KnownClosed (its issue closed by a person); null while it is not KnownClosed.
    /// A recurrence on any other build is a regression (<see cref="PlayerbotQaGitHubOptions.ReopenOnRegression"/>).
    /// </summary>
    public string? ClosedBuild { get; init; }

    /// <summary>The build that was running when the publisher last created, reopened or commented on the finding's issue (the issue was open then).</summary>
    public string? ReportedBuild { get; init; }

    /// <summary>Occurrences seen on <paramref name="build"/>, among the builds still tracked (0 when unknown).</summary>
    public long CountOnBuild(string build)
    {
        foreach (QaBuildCount entry in BuildCounts ?? [])
        {
            if (string.Equals(entry.Build, build, StringComparison.Ordinal)) return entry.Count;
        }

        return 0;
    }

    [JsonIgnore]
    public string ShortId => PlayerbotQaFingerprint.ShortId(Id);

    /// <summary>Whether the finding comes from data rather than from bots (it has no bot count to threshold on; <see cref="QaKinds"/>).</summary>
    [JsonIgnore]
    public bool BotIndependent => QaKinds.Of(Kind).BotIndependent;

    [JsonIgnore]
    public string KindLabel => LabelOf(Kind);

    /// <summary>
    /// Whether <see cref="Quest"/> is what the finding is about. A stall's <c>quest=</c> is the last quest the bot's brain remembers; it is the
    /// subject only when the goal is Quest (a goal-less stall of an old file keeps the old reading). For every other goal it is shown as the last
    /// quest, not the goal.
    /// </summary>
    [JsonIgnore]
    public bool QuestIsGoal => Goal.Length == 0 || QuestIsGoalOf(Kind, Goal);

    internal static bool QuestIsGoalOf(QaSignalKind kind, string goal)
        => kind is not (QaSignalKind.Stall or QaSignalKind.DeathLoop) || string.Equals(goal, "Quest", StringComparison.Ordinal);

    internal static string LabelOf(QaSignalKind kind) => Enum.IsDefined(kind) ? QaKinds.Of(kind).Label : kind.ToString();

    /// <summary>The class names of <see cref="ClassMask"/> (<c>Mage, Warlock</c>), empty when none is known.</summary>
    [JsonIgnore]
    public string ClassNames => ClassNamesOf(ClassMask);

    internal static string ClassNamesOf(uint mask)
    {
        if (mask == 0) return string.Empty;
        var names = new List<string>();
        for (int c = 1; c < 32; c++)
        {
            if ((mask & (1u << c)) != 0) names.Add(((ArcaneCore.Game.Class)c).ToString());
        }

        return string.Join(", ", names);
    }
}

/// <summary>What a publish attempt changed about a finding.</summary>
/// <param name="Publication">The new state.</param>
/// <param name="LastCommentUtc">When a comment was added (also stamps the finding's reported build).</param>
/// <param name="OccurrencesReported">The occurrences now told to GitHub.</param>
/// <param name="Reported">The issue was created or reopened without a comment: stamps the finding's reported build all the same.</param>
internal sealed record QaPublicationUpdate(QaPublication Publication, DateTime? LastCommentUtc = null, long? OccurrencesReported = null, bool Reported = false);

/// <summary>The mutable aggregate behind a <see cref="QaFindingView"/>; owned by <see cref="PlayerbotQaAggregator"/> and touched only under its lock.</summary>
internal sealed class QaFinding
{
    internal const int MaxTrackedBots = 512;

    internal const int MaxAttackers = 32;

    /// <summary>The builds a finding keeps a count for: the current one and the previous one (older counts are dropped, so the list never grows).</summary>
    internal const int MaxTrackedBuilds = 2;

    /// <summary>The longest build string kept (a longer one, from an edited file, is cut).</summary>
    internal const int MaxBuildChars = 128;

    public string? FirstBuild { get; set; }

    public string? LastBuild { get; set; }

    /// <summary>Most recent build first; at most <see cref="MaxTrackedBuilds"/> entries.</summary>
    public List<QaBuildCount> BuildCounts { get; } = [];

    public string? ClosedBuild { get; set; }

    public string? ReportedBuild { get; set; }

    /// <summary>Count <paramref name="occurrences"/> on <paramref name="build"/>: it becomes the most recent build, and only the newest <see cref="MaxTrackedBuilds"/> stay.</summary>
    public void AddBuild(string build, long occurrences)
    {
        FirstBuild ??= build;
        LastBuild = build;
        if (BuildCounts.Count > 0 && string.Equals(BuildCounts[0].Build, build, StringComparison.Ordinal))
        {
            // The common case: the current build is already first. A struct replaced in place, no allocation.
            BuildCounts[0] = new QaBuildCount(build, Math.Max(0, BuildCounts[0].Count) + occurrences);
            return;
        }

        long total = occurrences;
        int at = BuildCounts.FindIndex(entry => string.Equals(entry.Build, build, StringComparison.Ordinal));
        if (at >= 0)
        {
            total = Math.Max(0, BuildCounts[at].Count) + occurrences;
            BuildCounts.RemoveAt(at);
        }

        BuildCounts.Insert(0, new QaBuildCount(build, total));
        if (BuildCounts.Count > MaxTrackedBuilds) BuildCounts.RemoveRange(MaxTrackedBuilds, BuildCounts.Count - MaxTrackedBuilds);
    }

    /// <summary>Fold the build stamps of a finding merged into this one (<paramref name="fromNewer"/>: it was seen more recently). Call before the seen times are merged.</summary>
    public void AbsorbBuilds(QaFinding from, bool fromNewer)
    {
        if (from.FirstBuild is not null && (FirstBuild is null || from.FirstSeenUtc < FirstSeenUtc)) FirstBuild = from.FirstBuild;
        if (from.LastBuild is not null && (LastBuild is null || fromNewer)) LastBuild = from.LastBuild;
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (QaBuildCount entry in BuildCounts.Concat(from.BuildCounts)) sums[entry.Build] = sums.GetValueOrDefault(entry.Build) + entry.Count;
        QaBuildCount[] merged = [.. sums.Select(p => new QaBuildCount(p.Key, p.Value))
            .OrderBy(e => string.Equals(e.Build, LastBuild, StringComparison.Ordinal) ? 0 : 1).ThenByDescending(e => e.Count).ThenBy(e => e.Build, StringComparer.Ordinal)
            .Take(MaxTrackedBuilds)];
        BuildCounts.Clear();
        BuildCounts.AddRange(merged);
    }

    /// <summary>A build string as kept: null when blank, cut at <see cref="MaxBuildChars"/>.</summary>
    internal static string? CleanBuild(string? build)
        => string.IsNullOrWhiteSpace(build) ? null : build.Length <= MaxBuildChars ? build : build[..MaxBuildChars];

    /// <summary>
    /// The most attacker entries a death-loop finding collects from its signals. A real loop has one to three; a finding that reaches this many is an
    /// area-wide death spot, and a bound keeps one common entry from letting it swallow every loop of a zone cell through a long chain of unions.
    /// </summary>
    internal const int MaxLoopAttackers = 8;

    /// <summary>The key of the aggregator's loop index this finding sits under (its anchor's map, or none); set and read by the aggregator under its lock.</summary>
    public uint IndexedMap { get; set; }

    public required string Id { get; init; }

    public required string Key { get; init; }

    public required QaSignalKind Kind { get; init; }

    public string Subject { get; set; } = string.Empty;

    public string Goal { get; set; } = string.Empty;

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

    public uint Spell { get; set; }

    public string SpellName { get; set; } = string.Empty;

    public string SpellRank { get; set; } = string.Empty;

    public int EffectIndex { get; set; } = -1;

    public string EffectName { get; set; } = string.Empty;

    public int EffectValue { get; set; }

    public uint Aura { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string ReasonDetail { get; set; } = string.Empty;

    public uint Item { get; set; }

    public uint Npc { get; set; }

    public uint GameObject { get; set; }

    public uint ClassMask { get; set; }

    public bool Coverage { get; set; }

    /// <summary>Where the first sample was; never changes once set.</summary>
    public QaAnchor? Anchor { get; set; }

    /// <summary>A death loop's attackers: the non-zero entries of every signal folded in (at most <see cref="MaxAttackers"/>); a loop that shares one of them is this loop.</summary>
    public SortedSet<uint> Attackers { get; } = [];

    /// <summary>A stall's coarse identity (<see cref="PlayerbotQaFingerprint.StallGroup"/>); empty for other kinds. In memory only: recomputed on load.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>The group without its zone cell, or null (<see cref="PlayerbotQaFingerprint.StallGroup"/>).</summary>
    public string? GroupBase { get; set; }

    public string DeathSource { get; set; } = string.Empty;

    /// <summary>Different bots seen (saturates at <see cref="MaxTrackedBots"/> tracked ids; a bot-less signal's own count counts too).</summary>
    public int DistinctBots => Math.Max(Bots.Count, AnonymousBots);

    public QaFindingView ToView() => new()
    {
        Id = Id, Key = Key, Kind = Kind, Subject = Subject, Goal = Goal, Count = Count, DistinctBots = DistinctBots, FirstSeenUtc = FirstSeenUtc,
        LastSeenUtc = LastSeenUtc, Samples = [.. Samples], Quest = Quest, Target = Target, Map = Map, QuestTitle = QuestTitle, Cause = Cause,
        CauseDetail = CauseDetail, Severity = Severity, Publication = Publication, LastCommentUtc = LastCommentUtc,
        OccurrencesReported = OccurrencesReported, Spell = Spell, SpellName = SpellName, SpellRank = SpellRank, EffectIndex = EffectIndex,
        EffectName = EffectName, EffectValue = EffectValue, Aura = Aura, Reason = Reason, ReasonDetail = ReasonDetail, Item = Item, Npc = Npc,
        GameObject = GameObject, ClassMask = ClassMask, Coverage = Coverage, Anchor = Anchor, Attackers = [.. Attackers], DeathSource = DeathSource,
        FirstBuild = FirstBuild, LastBuild = LastBuild, BuildCounts = [.. BuildCounts], ClosedBuild = ClosedBuild, ReportedBuild = ReportedBuild,
    };
}

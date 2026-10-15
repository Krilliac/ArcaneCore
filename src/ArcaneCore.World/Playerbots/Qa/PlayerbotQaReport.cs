using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// One finding as the JSON export shows it (no bot hashes, redacted text). The build fields tell "a fix landed and the problem stopped" from "nothing ran there":
/// <see cref="SeenOnCurrentBuild"/> is whether the running build (the file's <c>Build</c>) met the finding, <see cref="OccurrencesOnCurrentBuild"/> how often, and the file's
/// <c>BuildSinceUtc</c> is how long it has been running (the server's start); <see cref="Builds"/> holds the counts of the current and the previous build only.
/// <see cref="RatePerHour"/> is <see cref="Count"/> divided by <see cref="ObservedHours"/>, the time from <see cref="FirstSeenUtc"/> to the report (at least one minute, so a first sighting is not an absurd rate);
/// <see cref="DistinctBots"/> is the number of bots affected.
/// </summary>
internal sealed record QaReportEntry(int Rank, string Id, string ShortId, string Kind, string Cause, string CauseDetail, double Severity, long Count,
    int DistinctBots, DateTime FirstSeenUtc, DateTime LastSeenUtc, string Subject, string? QuestTitle, string Publication, IReadOnlyList<QaReportSample> Samples,
    string Goal = "", bool QuestIsGoal = true, bool Stale = false, string? FirstBuild = null, string? LastBuild = null, IReadOnlyList<QaBuildCount>? Builds = null,
    bool SeenOnCurrentBuild = false, long OccurrencesOnCurrentBuild = 0, string? ClosedBuild = null, string? ReportedBuild = null, double RatePerHour = 0, double ObservedHours = 0);

/// <summary>One evidence sample of the JSON export.</summary>
internal sealed record QaReportSample(DateTime AtUtc, uint Map, float? X, float? Y, float? Z, IReadOnlyList<uint> Attackers, string Message);

/// <summary><c>qa/report.json</c>.</summary>
internal sealed record QaReportFile(DateTime GeneratedUtc, string Build, int FindingCount, IReadOnlyList<QaReportEntry> Findings,
    QaSpellCoverageReport? SpellCoverage = null, DateTime? BuildSinceUtc = null);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(QaReportFile))]
internal sealed partial class QaReportJsonContext : JsonSerializerContext;

/// <summary>
/// Ranks findings and renders them: chat lines for <c>.playerbot qa</c>, and the <c>report.md</c> / <c>report.json</c> exports. Ranking is
/// severity, then count, then last seen (newest first), then id, so equal findings always list in the same order. Pure; all text is redacted.
/// </summary>
internal static class PlayerbotQaReport
{
    /// <summary>The most findings one chat listing shows.</summary>
    internal const int MaxChatFindings = 25;

    /// <summary>The shortest id prefix <see cref="TryResolve"/> accepts.</summary>
    internal const int MinPrefix = 4;

    private const int MaxChatLine = 220;

    /// <summary>The most stale findings the markdown report lists.</summary>
    private const int MaxStaleRows = 200;

    internal static IReadOnlyList<QaFindingView> Rank(IEnumerable<QaFindingView> findings)
        => [.. findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Count).ThenByDescending(f => f.LastSeenUtc).ThenBy(f => f.Id, StringComparer.Ordinal)];

    /// <summary>
    /// A finding last seen before <paramref name="since"/> (when this process started) was not met by the running build: it may well be fixed already.
    /// Null <paramref name="since"/> means nothing is stale.
    /// </summary>
    internal static bool IsStale(QaFindingView f, DateTime? since) => since is { } start && f.LastSeenUtc < start;

    /// <summary>Stale and not worth the default list: a stale finding whose issue is open (Published) is still shown.</summary>
    internal static bool IsHidden(QaFindingView f, DateTime? since) => IsStale(f, since) && f.Publication.State != QaPublicationState.Published;

    /// <summary>The shortest span a rate is computed over: a finding seen once a second ago is 60 per hour, not 3.6 million.</summary>
    private static readonly TimeSpan MinRateSpan = TimeSpan.FromMinutes(1);

    /// <summary>The hours <see cref="RatePerHour"/> divides by: first seen to <paramref name="nowUtc"/>, at least a minute.</summary>
    internal static double ObservedHours(QaFindingView f, DateTime nowUtc) => Math.Max((nowUtc - f.FirstSeenUtc).TotalHours, MinRateSpan.TotalHours);

    /// <summary>Occurrences per hour since the finding was first seen (<see cref="QaFindingView.Count"/> over <see cref="ObservedHours"/>).</summary>
    internal static double RatePerHour(QaFindingView f, DateTime nowUtc) => f.Count / ObservedHours(f, nowUtc);

    /// <summary><c>4.5/h</c>, or <c>&lt;0.1/h</c> for a slower finding (never a misleading 0.0).</summary>
    internal static string RateText(double perHour)
        => perHour > 0 && perHour < 0.1 ? "<0.1/h" : string.Create(CultureInfo.InvariantCulture, $"{perHour:F1}/h");

    /// <summary><c>2h</c> for the age of a first sighting (the same words as <see cref="Age"/> without "ago").</summary>
    private static string Since(DateTime firstSeenUtc, DateTime nowUtc) => Age(firstSeenUtc, nowUtc)[..^4];

    /// <summary>How long ago, as a short word: <c>&lt;1m ago</c>, <c>5m ago</c>, <c>2h ago</c>, <c>1d ago</c>.</summary>
    internal static string Age(DateTime lastSeenUtc, DateTime nowUtc)
    {
        TimeSpan span = nowUtc - lastSeenUtc;
        if (span < TimeSpan.FromMinutes(1)) return "<1m ago";
        if (span < TimeSpan.FromHours(1)) return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m ago");
        if (span < TimeSpan.FromDays(1)) return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h ago");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Min(span.TotalDays, 99999)}d ago");
    }

    /// <summary>
    /// The top <paramref name="n"/> (1..<see cref="MaxChatFindings"/>) as bounded chat lines. With <paramref name="since"/> the findings not seen since then
    /// (stale) are hidden behind one hint line, unless <paramref name="all"/>, which lists them after every current one. With <paramref name="now"/> each line shows its last-seen age.
    /// </summary>
    internal static IReadOnlyList<string> ChatTop(IEnumerable<QaFindingView> findings, int n, PlayerbotQaRedactor redactor, DateTime? now = null, DateTime? since = null, bool all = false)
    {
        n = Math.Clamp(n, 1, MaxChatFindings);
        // A spell gap no bot met yet (Count 0, listed by the start-up coverage) is not a seen problem: `.playerbot qa spells` lists those.
        IReadOnlyList<QaFindingView> ranked = Rank(findings.Where(f => f.Count > 0));
        if (ranked.Count == 0) return ["Bot QA: no findings."];
        // A stale finding with an open issue stays in the list (marked): its issue is live work, whatever this build has met.
        QaFindingView[] current = [.. ranked.Where(f => !IsHidden(f, since))];
        QaFindingView[] stale = [.. ranked.Where(f => IsHidden(f, since))];
        QaFindingView[] shown = all ? [.. current, .. stale] : current;
        const string HintTail = "hidden; .playerbot qa top all lists them. .playerbot qa prune [#hours] [closed] [dry] forgets old ones; a finding with a GitHub issue is always kept.";
        string hint = !all && stale.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{stale.Length} stale findings (not seen since this server started) {HintTail}")
            : string.Empty;
        if (shown.Length == 0) return [string.Create(CultureInfo.InvariantCulture, $"Bot QA: no findings seen since start; {stale.Length} stale {HintTail}")];
        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"Bot QA: top {Math.Min(n, shown.Length)} of {shown.Length} findings (.playerbot qa show <id> for the evidence)") };
        for (int i = 0; i < Math.Min(n, shown.Length); i++)
        {
            QaFindingView f = shown[i];
            string seen = now is { } at ? " " + Age(f.LastSeenUtc, at) + (IsStale(f, since) ? " (stale)" : string.Empty) + ", first " + Since(f.FirstSeenUtc, at) : string.Empty;
            string rate = " " + RateText(RatePerHour(f, now ?? f.LastSeenUtc));
            lines.Add(Cut(string.Create(CultureInfo.InvariantCulture,
                $"#{i + 1} {f.ShortId} sev {f.Severity:F0} x{f.Count}{rate} bots {f.DistinctBots}{seen} {f.Cause} {f.KindLabel}: {redactor.Redact(f.Subject)}")));
        }

        if (hint.Length != 0) lines.Add(Cut(hint));
        return lines;
    }

    /// <summary>
    /// <c>.playerbot qa spells</c>: the coverage summary and its five most shared gaps, then the top <paramref name="n"/> spell findings bots met (a gap
    /// no bot met yet is counted in the summary, not listed).
    /// </summary>
    internal static IReadOnlyList<string> ChatSpells(IEnumerable<QaFindingView> findings, QaSpellCoverageReport? coverage, int n, PlayerbotQaRedactor redactor)
    {
        n = Math.Clamp(n, 1, MaxChatFindings);
        var lines = new List<string>
        {
            Cut(coverage?.Describe() ?? "Spell coverage: not computed yet (the first QA pass after start-up computes it, once the spell table is loaded)."),
        };
        if (coverage is not null)
        {
            foreach (QaCoverageOffender o in coverage.TopOffenders(5))
            {
                lines.Add(Cut(string.Create(CultureInfo.InvariantCulture, $"  {QaSpellGaps.Describe(o.Code, o.Detail)}: {o.Spells} spells ({o.RotationSpells} rotation)")));
            }
        }

        IReadOnlyList<QaFindingView> met = Rank(findings.Where(f => QaKinds.Of(f.Kind).SpellKind && f.Count > 0));
        if (met.Count == 0)
        {
            lines.Add("No spell finding met by a bot yet.");
            return lines;
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture, $"Spell findings bots met: top {Math.Min(n, met.Count)} of {met.Count}"));
        for (int i = 0; i < Math.Min(n, met.Count); i++)
        {
            QaFindingView f = met[i];
            lines.Add(Cut(string.Create(CultureInfo.InvariantCulture,
                $"#{i + 1} {f.ShortId} x{f.Count} bots {f.DistinctBots} {f.KindLabel}: {redactor.Redact(f.Subject)}{(f.ClassMask != 0 ? " [" + f.ClassNames + "]" : string.Empty)}")));
        }

        return lines;
    }

    /// <summary>The finding whose id starts with <paramref name="prefix"/> (at least <see cref="MinPrefix"/> hex characters, unique); otherwise the reason.</summary>
    internal static bool TryResolve(IEnumerable<QaFindingView> findings, string prefix, out QaFindingView? finding, out string error)
    {
        finding = null;
        error = string.Empty;
        prefix = prefix.Trim().ToLowerInvariant();
        if (prefix.Length < MinPrefix || prefix.Length > PlayerbotQaFingerprint.FullChars || !prefix.All(Uri.IsHexDigit))
        {
            error = $"Give at least {MinPrefix} hex characters of a finding id.";
            return false;
        }

        QaFindingView[] matches = [.. findings.Where(f => f.Id.StartsWith(prefix, StringComparison.Ordinal)).Take(2)];
        if (matches.Length == 0)
        {
            error = $"No finding starts with {prefix}.";
            return false;
        }

        if (matches.Length > 1)
        {
            error = $"More than one finding starts with {prefix}; give more characters.";
            return false;
        }

        finding = matches[0];
        return true;
    }

    /// <summary>The evidence of one finding as chat lines.</summary>
    internal static IReadOnlyList<string> ChatShow(QaFindingView f, PlayerbotQaRedactor redactor, string? build = null)
    {
        var lines = new List<string>
        {
            Cut(string.Create(CultureInfo.InvariantCulture, $"{f.Id} {f.KindLabel}: {redactor.Redact(f.Subject)}")),
            Cut(string.Create(CultureInfo.InvariantCulture, $"cause {f.Cause}: {redactor.Redact(f.CauseDetail)}")),
            string.Create(CultureInfo.InvariantCulture, $"seen {f.Count} times by {f.DistinctBots} bots, {RateText(RatePerHour(f, f.LastSeenUtc))}, sev {f.Severity:F1}, first {f.FirstSeenUtc:u}, last {f.LastSeenUtc:u}, github {f.Publication}"),
        };
        lines.Add(Cut(BuildLine(f, build, redactor)));
        if (f.QuestTitle is not null) lines.Add(Cut($"{QuestLabel(f, "quest")}: {redactor.Redact(f.QuestTitle)}"));
        foreach (QaSample s in f.Samples.TakeLast(3))
        {
            string where = s.HasPosition ? string.Create(CultureInfo.InvariantCulture, $"map {s.Map} at {s.X:F1},{s.Y:F1},{s.Z:F1}") : "no position";
            string attackers = s.Attackers.Length > 0 ? " attackers " + string.Join('/', s.Attackers) : string.Empty;
            lines.Add(Cut($"{s.AtUtc:HH:mm:ss} {where}{attackers}: {redactor.Redact(s.Message)}"));
        }

        if (GoHint(f) is { } go) lines.Add("go there: " + go);
        return lines;
    }

    /// <summary><c>builds first A, last B; per build B=3, A=9; on this build (B): 3</c>; a build a file from before format 2 never stamped reads as unknown.</summary>
    private static string BuildLine(QaFindingView f, string? build, PlayerbotQaRedactor redactor)
    {
        string Name(string? b) => b is null ? "unknown" : redactor.Redact(b);
        string perBuild = f.BuildCounts.Count == 0 ? "none recorded" : string.Join(", ", f.BuildCounts.Select(e => Name(e.Build) + "=" + e.Count.ToString(CultureInfo.InvariantCulture)));
        string onThis = build is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"; on this build ({Name(build)}): {f.CountOnBuild(build)}");
        return $"builds first {Name(f.FirstBuild)}, last {Name(f.LastBuild)}; per build {perBuild}{onThis}";
    }

    /// <summary>The <c>.go xyz</c> command to the middle sample (null without a position).</summary>
    internal static string? GoHint(QaFindingView f)
    {
        QaSample[] withPosition = [.. f.Samples.Where(s => s.HasPosition)];
        if (withPosition.Length == 0) return null;
        QaSample s = withPosition[withPosition.Length / 2];
        return string.Create(CultureInfo.InvariantCulture, $".go xyz {s.X:F1} {s.Y:F1} {s.Z:F1} {s.Map}");
    }

    internal static string RenderMarkdown(IEnumerable<QaFindingView> findings, DateTime generatedUtc, string build, PlayerbotQaRedactor redactor,
        QaSpellCoverageReport? coverage = null, DateTime? since = null)
    {
        QaFindingView[] all = [.. findings];
        IReadOnlyList<QaFindingView> seen = Rank(all.Where(f => f.Count > 0));
        // The same split as the chat list: a stale finding whose issue is open stays in the main table.
        QaFindingView[] ranked = [.. seen.Where(f => !IsHidden(f, since))];
        QaFindingView[] stale = [.. seen.Where(f => IsHidden(f, since))];
        int listedOnly = all.Length - seen.Count;
        var sb = new StringBuilder();
        sb.Append("# Playerbot QA report\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Generated {generatedUtc:u} by ArcaneCore {Escape(build)}. {ranked.Length} findings, ranked by severity, count and last seen.");
        if (stale.Length > 0) sb.Append(CultureInfo.InvariantCulture, $" {stale.Length} more were not seen since {since:u} (this server's start) and are listed last under Stale.");
        if (listedOnly > 0) sb.Append(CultureInfo.InvariantCulture, $" {listedOnly} spell gaps no bot met yet are listed under Spell coverage.");
        sb.Append("\n\n");
        if (seen.Count == 0)
        {
            sb.Append("No findings.\n");
            if (coverage is not null) sb.Append('\n').Append(coverage.RenderMarkdown());
            return sb.ToString();
        }

        if (ranked.Length == 0)
        {
            sb.Append("No finding was seen since this server started.\n\n");
        }
        else
        {
            sb.Append("| # | Id | Severity | Seen | Rate/h | First seen | Last seen | Bots | Cause | Kind | Subject | GitHub |\n|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            for (int i = 0; i < ranked.Length; i++)
            {
                QaFindingView f = ranked[i];
                sb.Append(CultureInfo.InvariantCulture, $"| {i + 1} | `{f.ShortId}` | {f.Severity:F1} | {f.Count} | {RatePerHour(f, generatedUtc):F1} | {f.FirstSeenUtc:u} | {Age(f.LastSeenUtc, generatedUtc)} | {f.DistinctBots} | {f.Cause} | {f.KindLabel} | {Escape(redactor.Redact(f.Subject))} | {f.Publication} |\n");
            }

            sb.Append('\n');
        }

        for (int i = 0; i < ranked.Length; i++)
        {
            QaFindingView f = ranked[i];
            sb.Append(CultureInfo.InvariantCulture, $"## {i + 1}. {f.KindLabel}: {Escape(redactor.Redact(f.Subject))}\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"- Id `{f.Id}`, severity {f.Severity:F1}, seen {f.Count} times by {f.DistinctBots} bots affected ({RateText(RatePerHour(f, generatedUtc))} over {ObservedHours(f, generatedUtc):F1} h), first {f.FirstSeenUtc:u}, last {f.LastSeenUtc:u}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- Likely cause: {f.Cause}. {Escape(redactor.Redact(f.CauseDetail))}\n");
            if (f.QuestTitle is not null) sb.Append(CultureInfo.InvariantCulture, $"- {QuestLabel(f, "Quest")}: {Escape(redactor.Redact(f.QuestTitle))}\n");
            if (GoHint(f) is { } go) sb.Append(CultureInfo.InvariantCulture, $"- Go there: `{go}`\n");
            foreach (QaSample s in f.Samples)
            {
                string where = s.HasPosition ? string.Create(CultureInfo.InvariantCulture, $"map {s.Map} {s.X:F1},{s.Y:F1},{s.Z:F1}") : "no position";
                string attackers = s.Attackers.Length > 0 ? " attackers " + string.Join('/', s.Attackers) : string.Empty;
                sb.Append(CultureInfo.InvariantCulture, $"  - {s.AtUtc:u} {where}{attackers}: `{Escape(redactor.Redact(s.Message)).Replace('`', '\'')}`\n");
            }

            sb.Append('\n');
        }

        if (stale.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"## Stale (not seen since {since:u})\n\n");
            sb.Append("Last seen before this server started: possibly fixed by the running build. `.playerbot qa prune` forgets them (the evidence stays in report.json until then).\n\n");
            sb.Append("| Id | Severity | Seen | Rate/h | First seen | Last seen | Bots | Kind | Subject | GitHub |\n|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (QaFindingView f in stale.Take(MaxStaleRows))
            {
                sb.Append(CultureInfo.InvariantCulture, $"| `{f.ShortId}` | {f.Severity:F1} | {f.Count} | {RatePerHour(f, generatedUtc):F1} | {f.FirstSeenUtc:u} | {Age(f.LastSeenUtc, generatedUtc)} | {f.DistinctBots} | {f.KindLabel} | {Escape(redactor.Redact(f.Subject))} | {f.Publication} |\n");
            }

            if (stale.Length > MaxStaleRows) sb.Append(CultureInfo.InvariantCulture, $"\n{stale.Length - MaxStaleRows} more stale findings are not listed.\n");
            sb.Append('\n');
        }

        if (coverage is not null) sb.Append(coverage.RenderMarkdown());
        return sb.ToString();
    }

    internal static string RenderJson(IEnumerable<QaFindingView> findings, DateTime generatedUtc, string build, PlayerbotQaRedactor redactor,
        QaSpellCoverageReport? coverage = null, DateTime? since = null)
    {
        IReadOnlyList<QaFindingView> ranked = Rank(findings);
        var entries = new List<QaReportEntry>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++)
        {
            QaFindingView f = ranked[i];
            entries.Add(new QaReportEntry(i + 1, f.Id, f.ShortId, f.KindLabel, f.Cause.ToString(), redactor.Redact(f.CauseDetail), f.Severity, f.Count, f.DistinctBots,
                f.FirstSeenUtc, f.LastSeenUtc, redactor.Redact(f.Subject), f.QuestTitle is null ? null : redactor.Redact(f.QuestTitle), f.Publication.ToString(),
                [.. f.Samples.Select(s => new QaReportSample(s.AtUtc, s.Map, s.HasPosition ? s.X : null, s.HasPosition ? s.Y : null, s.HasPosition ? s.Z : null,
                    s.Attackers, redactor.Redact(s.Message)))], f.Goal, f.QuestIsGoal, IsStale(f, since), f.FirstBuild, f.LastBuild, [.. f.BuildCounts],
                f.CountOnBuild(build) > 0, f.CountOnBuild(build), f.ClosedBuild, f.ReportedBuild, RatePerHour(f, generatedUtc), ObservedHours(f, generatedUtc)));
        }

        // The build has been running since this server started (a lower bound: an earlier run of the same build is not tracked). Null when the caller gave no start.
        DateTime? running = since is { } start && start != DateTime.MaxValue ? start : null;
        return JsonSerializer.Serialize(new QaReportFile(generatedUtc, build, entries.Count, entries, coverage, running), QaReportJsonContext.Default.QaReportFile);
    }

    /// <summary><c>quest Q</c> when the quest is the finding's subject, <c>last quest Q (not the goal)</c> for a stall of another goal.</summary>
    private static string QuestLabel(QaFindingView f, string quest)
        => f.QuestIsGoal
            ? string.Create(CultureInfo.InvariantCulture, $"{quest} {f.Quest}")
            : string.Create(CultureInfo.InvariantCulture, $"{(quest[0] == 'Q' ? "Last" : "last")} quest {f.Quest} (not the goal)");

    private static string Cut(string line) => line.Length <= MaxChatLine ? line : line[..(MaxChatLine - 3)] + "...";

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

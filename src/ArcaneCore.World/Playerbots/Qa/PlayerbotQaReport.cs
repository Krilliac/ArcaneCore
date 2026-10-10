using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>One finding as the JSON export shows it (no bot hashes, redacted text).</summary>
internal sealed record QaReportEntry(int Rank, string Id, string ShortId, string Kind, string Cause, string CauseDetail, double Severity, long Count,
    int DistinctBots, DateTime FirstSeenUtc, DateTime LastSeenUtc, string Subject, string? QuestTitle, string Publication, IReadOnlyList<QaReportSample> Samples);

/// <summary>One evidence sample of the JSON export.</summary>
internal sealed record QaReportSample(DateTime AtUtc, uint Map, float? X, float? Y, float? Z, IReadOnlyList<uint> Attackers, string Message);

/// <summary><c>qa/report.json</c>.</summary>
internal sealed record QaReportFile(DateTime GeneratedUtc, string Build, int FindingCount, IReadOnlyList<QaReportEntry> Findings);

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

    internal static IReadOnlyList<QaFindingView> Rank(IEnumerable<QaFindingView> findings)
        => [.. findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Count).ThenByDescending(f => f.LastSeenUtc).ThenBy(f => f.Id, StringComparer.Ordinal)];

    /// <summary>The top <paramref name="n"/> (1..<see cref="MaxChatFindings"/>) as bounded chat lines.</summary>
    internal static IReadOnlyList<string> ChatTop(IEnumerable<QaFindingView> findings, int n, PlayerbotQaRedactor redactor)
    {
        n = Math.Clamp(n, 1, MaxChatFindings);
        IReadOnlyList<QaFindingView> ranked = Rank(findings);
        if (ranked.Count == 0) return ["Bot QA: no findings."];
        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"Bot QA: top {Math.Min(n, ranked.Count)} of {ranked.Count} findings (.playerbot qa show <id> for the evidence)") };
        for (int i = 0; i < Math.Min(n, ranked.Count); i++)
        {
            QaFindingView f = ranked[i];
            lines.Add(Cut(string.Create(CultureInfo.InvariantCulture,
                $"#{i + 1} {f.ShortId} sev {f.Severity:F0} x{f.Count} bots {f.DistinctBots} {f.Cause} {f.KindLabel}: {redactor.Redact(f.Subject)}")));
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
    internal static IReadOnlyList<string> ChatShow(QaFindingView f, PlayerbotQaRedactor redactor)
    {
        var lines = new List<string>
        {
            Cut(string.Create(CultureInfo.InvariantCulture, $"{f.Id} {f.KindLabel}: {redactor.Redact(f.Subject)}")),
            Cut(string.Create(CultureInfo.InvariantCulture, $"cause {f.Cause}: {redactor.Redact(f.CauseDetail)}")),
            string.Create(CultureInfo.InvariantCulture, $"seen {f.Count} times by {f.DistinctBots} bots, sev {f.Severity:F1}, first {f.FirstSeenUtc:u}, last {f.LastSeenUtc:u}, github {f.Publication}"),
        };
        if (f.QuestTitle is not null) lines.Add(Cut($"quest {f.Quest}: {redactor.Redact(f.QuestTitle)}"));
        foreach (QaSample s in f.Samples.TakeLast(3))
        {
            string where = s.HasPosition ? string.Create(CultureInfo.InvariantCulture, $"map {s.Map} at {s.X:F1},{s.Y:F1},{s.Z:F1}") : "no position";
            string attackers = s.Attackers.Length > 0 ? " attackers " + string.Join('/', s.Attackers) : string.Empty;
            lines.Add(Cut($"{s.AtUtc:HH:mm:ss} {where}{attackers}: {redactor.Redact(s.Message)}"));
        }

        if (GoHint(f) is { } go) lines.Add("go there: " + go);
        return lines;
    }

    /// <summary>The <c>.go xyz</c> command to the middle sample (null without a position).</summary>
    internal static string? GoHint(QaFindingView f)
    {
        QaSample[] withPosition = [.. f.Samples.Where(s => s.HasPosition)];
        if (withPosition.Length == 0) return null;
        QaSample s = withPosition[withPosition.Length / 2];
        return string.Create(CultureInfo.InvariantCulture, $".go xyz {s.X:F1} {s.Y:F1} {s.Z:F1} {s.Map}");
    }

    internal static string RenderMarkdown(IEnumerable<QaFindingView> findings, DateTime generatedUtc, string build, PlayerbotQaRedactor redactor)
    {
        IReadOnlyList<QaFindingView> ranked = Rank(findings);
        var sb = new StringBuilder();
        sb.Append("# Playerbot QA report\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Generated {generatedUtc:u} by ArcaneCore {Escape(build)}. {ranked.Count} findings, ranked by severity, count and last seen.\n\n");
        if (ranked.Count == 0)
        {
            sb.Append("No findings.\n");
            return sb.ToString();
        }

        sb.Append("| # | Id | Severity | Seen | Bots | Cause | Kind | Subject | GitHub |\n|---|---|---|---|---|---|---|---|---|\n");
        for (int i = 0; i < ranked.Count; i++)
        {
            QaFindingView f = ranked[i];
            sb.Append(CultureInfo.InvariantCulture, $"| {i + 1} | `{f.ShortId}` | {f.Severity:F1} | {f.Count} | {f.DistinctBots} | {f.Cause} | {f.KindLabel} | {Escape(redactor.Redact(f.Subject))} | {f.Publication} |\n");
        }

        sb.Append('\n');
        for (int i = 0; i < ranked.Count; i++)
        {
            QaFindingView f = ranked[i];
            sb.Append(CultureInfo.InvariantCulture, $"## {i + 1}. {f.KindLabel}: {Escape(redactor.Redact(f.Subject))}\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"- Id `{f.Id}`, severity {f.Severity:F1}, seen {f.Count} times by {f.DistinctBots} bots, first {f.FirstSeenUtc:u}, last {f.LastSeenUtc:u}\n");
            sb.Append(CultureInfo.InvariantCulture, $"- Likely cause: {f.Cause}. {Escape(redactor.Redact(f.CauseDetail))}\n");
            if (f.QuestTitle is not null) sb.Append(CultureInfo.InvariantCulture, $"- Quest {f.Quest}: {Escape(redactor.Redact(f.QuestTitle))}\n");
            if (GoHint(f) is { } go) sb.Append(CultureInfo.InvariantCulture, $"- Go there: `{go}`\n");
            foreach (QaSample s in f.Samples)
            {
                string where = s.HasPosition ? string.Create(CultureInfo.InvariantCulture, $"map {s.Map} {s.X:F1},{s.Y:F1},{s.Z:F1}") : "no position";
                string attackers = s.Attackers.Length > 0 ? " attackers " + string.Join('/', s.Attackers) : string.Empty;
                sb.Append(CultureInfo.InvariantCulture, $"  - {s.AtUtc:u} {where}{attackers}: `{Escape(redactor.Redact(s.Message)).Replace('`', '\'')}`\n");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    internal static string RenderJson(IEnumerable<QaFindingView> findings, DateTime generatedUtc, string build, PlayerbotQaRedactor redactor)
    {
        IReadOnlyList<QaFindingView> ranked = Rank(findings);
        var entries = new List<QaReportEntry>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++)
        {
            QaFindingView f = ranked[i];
            entries.Add(new QaReportEntry(i + 1, f.Id, f.ShortId, f.KindLabel, f.Cause.ToString(), redactor.Redact(f.CauseDetail), f.Severity, f.Count, f.DistinctBots,
                f.FirstSeenUtc, f.LastSeenUtc, redactor.Redact(f.Subject), f.QuestTitle is null ? null : redactor.Redact(f.QuestTitle), f.Publication.ToString(),
                [.. f.Samples.Select(s => new QaReportSample(s.AtUtc, s.Map, s.HasPosition ? s.X : null, s.HasPosition ? s.Y : null, s.HasPosition ? s.Z : null,
                    s.Attackers, redactor.Redact(s.Message)))]));
        }

        return JsonSerializer.Serialize(new QaReportFile(generatedUtc, build, entries.Count, entries), QaReportJsonContext.Default.QaReportFile);
    }

    private static string Cut(string line) => line.Length <= MaxChatLine ? line : line[..(MaxChatLine - 3)] + "...";

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

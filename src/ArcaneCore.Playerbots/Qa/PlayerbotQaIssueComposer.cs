using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The text of an issue ready to send (or to write to <c>qa/pending-issues/</c>).</summary>
internal sealed record QaIssueDraft(string Id, string Marker, string Title, string Body, IReadOnlyList<string> Labels);

/// <summary>
/// Builds the title, body and comment of a finding's GitHub issue. The body starts with the hidden marker
/// <c>&lt;!-- arcanecore-bot-qa:&lt;fingerprint&gt; --&gt;</c> that dedup searches for; every piece of evidence goes through the redactor. Pure.
/// </summary>
internal static partial class PlayerbotQaIssueComposer
{
    internal const string Footer = "Reported automatically by ArcaneCore playerbot QA";

    /// <summary>Opens the live status block of an issue body (<see cref="StatusBlock"/>); the block ends at <see cref="StatusEnd"/>.</summary>
    internal const string StatusBegin = "<!-- arcanecore-bot-qa-status:begin -->";

    internal const string StatusEnd = "<!-- arcanecore-bot-qa-status:end -->";

    private const int MaxEvidence = 5;

    internal static string MarkerOf(string id) => "<!-- arcanecore-bot-qa:" + id + " -->";

    /// <summary>The informational version of the ArcaneCore.World assembly (version and commit), as <c>.server info</c> shows it.</summary>
    internal static string BuildVersion()
        => typeof(PlayerbotQaIssueComposer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    internal static QaIssueDraft Compose(QaFindingView f, IReadOnlyList<string> labels, string build, PlayerbotQaRedactor redactor)
    {
        string marker = MarkerOf(f.Id);
        var sb = new StringBuilder();
        sb.Append(marker).Append('\n');
        sb.Append(StatusBlock(f, build)).Append("\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Playerbot QA finding `{f.ShortId}`: **{f.KindLabel}** - {Cell(redactor.Redact(f.Subject))}\n\n");
        sb.Append("| Field | Value |\n|---|---|\n");
        Row(sb, "Kind", f.KindLabel);
        Row(sb, "Likely cause", f.Cause.ToString());
        Row(sb, "Severity", f.Severity.ToString("F1", CultureInfo.InvariantCulture));
        Row(sb, "Occurrences", f.Count.ToString(CultureInfo.InvariantCulture));
        if (f.Kind != QaSignalKind.ServerFault) Row(sb, "Distinct bots", f.DistinctBots.ToString(CultureInfo.InvariantCulture));
        Row(sb, "First seen (UTC)", f.FirstSeenUtc.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "Last seen (UTC)", f.LastSeenUtc.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "Build", PlayerbotQaRedactor.RedactSecrets(build));
        if (f.Kind is QaSignalKind.Stall or QaSignalKind.DeathLoop && f.Goal.Length != 0) Row(sb, "Goal", f.Goal);
        // A stall's quest is the subject only for a Quest goal; for any other goal it is the last quest the bot remembers (GitHub #154).
        if (f.Quest != 0)
        {
            Row(sb, f.QuestIsGoal ? "Quest" : "Last quest (not the goal)", string.Create(CultureInfo.InvariantCulture,
                $"{f.Quest}{(f.QuestTitle is null ? string.Empty : " " + redactor.Redact(f.QuestTitle))}"));
        }
        sb.Append("\n## Likely cause\n\n").Append(Cell(redactor.Redact(f.CauseDetail))).Append("\n\n");
        if (QaKinds.Of(f.Kind).SpellKind) AppendSpell(sb, f, redactor);
        if (f.Kind == QaSignalKind.ServerFault) AppendServerFault(sb, f, redactor);

        sb.Append("## Evidence\n\n");
        foreach (QaSample s in f.Samples.TakeLast(MaxEvidence))
        {
            string where = s.HasPosition ? string.Create(CultureInfo.InvariantCulture, $"map {s.Map} at {s.X:F1}, {s.Y:F1}, {s.Z:F1}") : "no position";
            string attackers = s.Attackers.Length > 0 ? ", attackers " + string.Join('/', s.Attackers) : string.Empty;
            sb.Append(CultureInfo.InvariantCulture, $"- {s.AtUtc:u} {where}{attackers}\n");
            if (s.Bot is { } bot)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"  - level {bot.Level} {bot.Race} {bot.Team} {bot.Class}; goal {bot.Goal}, quest {bot.Quest}; ");
                sb.Append(bot.Graveyard == 0 ? "graveyard unknown" : string.Create(CultureInfo.InvariantCulture,
                    $"graveyard {bot.Graveyard}, {(bot.GraveyardDistance is { } distance ? distance.ToString("F1", CultureInfo.InvariantCulture) + " yd" : "distance unknown")}"));
                if (f.Kind == QaSignalKind.DeathLoop)
                {
                    sb.Append(bot.DeathSources is { Count: > 0 } sources
                        ? "; death sources " + string.Join('/', sources)
                        : "; last death source " + bot.Source);
                }
                sb.Append('\n');
                foreach (QaAttackerFact attacker in bot.AttackerFacts)
                {
                    string rank = attacker.Rank switch { 0 => "normal", 1 => "elite", 2 => "rare elite", 3 => "world boss", 4 => "rare", _ => attacker.Rank.ToString(CultureInfo.InvariantCulture) };
                    sb.Append(CultureInfo.InvariantCulture,
                        $"  - {attacker.Entry}: level {attacker.Level} (template {attacker.MinLevel}-{attacker.MaxLevel}), rank {rank}, guard {(attacker.Guard ? "yes" : "no")}, hostile {(attacker.Hostile ? "yes" : "no")}\n");
                }
            }
            if (f.Kind != QaSignalKind.ServerFault) sb.Append("  `").Append(Code(redactor.Redact(s.Message))).Append("`\n"); // a server fault never publishes its message: it may name a player
            if (s.Diag is { Length: > 0 } diag)
            {
                string cleanDiag = f.Kind == QaSignalKind.ServerFault ? redactor.RedactFault(diag) : redactor.Redact(diag);
                sb.Append("  - Diagnostic: `").Append(Code(cleanDiag)).Append("`\n");
            }
        }

        sb.Append("\n## Reproduction\n\n");
        if (QaKinds.Of(f.Kind).SpellKind && f.Spell != 0)
        {
            string who = f.ClassMask != 0 ? "a character of a class that met it (" + f.ClassNames + ")" : "a character of the spell's class";
            sb.Append(CultureInfo.InvariantCulture, $"- On {who}: `.learn {f.Spell}`, select a hostile creature (or a friend for a helpful spell), then `.cast {f.Spell}` (a triggered cast: it skips the cast checks) or cast it from the spellbook.\n");
            sb.Append(CultureInfo.InvariantCulture, $"- On a bot: select it, `.learn {f.Spell}` and `.cast {f.Spell}` (both act on the selected player); a bot of that class also casts it in its rotation.\n");
        }

        if (PlayerbotQaReport.GoHint(f) is { } go) sb.Append("- Teleport to the place: `").Append(go).Append("`\n");
        if (f.Quest != 0 && f.QuestIsGoal)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- Find the quest: `.lookup quest {Code(redactor.Redact(f.QuestTitle ?? f.Quest.ToString(CultureInfo.InvariantCulture)))}` (id {f.Quest})\n");
        }

        if (f.Target != 0) sb.Append(CultureInfo.InvariantCulture, $"- Find the creature: `.lookup creature` for entry {f.Target}\n");
        if (f.Kind == QaSignalKind.ServerFault)
        {
            sb.Append("- On the server console: `.server faults show ").Append(LedgerId(f)).Append("` shows the live count, frames and message.\n");
        }
        else if (f.Kind == QaSignalKind.Anomaly)
        {
            sb.Append("- On the server console: `.server anomalies show ").Append(Code(redactor.Redact(f.Goal + "/" + f.Reason))).Append("` shows the live count, distinct subjects and last detail.\n");
        }
        else
        {
            sb.Append("- Start a few bots (`.playerbot start`), let them play, and watch `.playerbot qa`.\n");
        }

        sb.Append("\n---\n").Append(Footer).Append('\n');

        string title = redactor.Title(f.KindLabel, f.Subject);
        return new QaIssueDraft(f.Id, marker, title, redactor.Body(sb.ToString(), marker), labels);
    }

    /// <summary>The fault ledger id of a server-fault finding (the last part of its key, <c>v1|serverfault|&lt;id&gt;</c>).</summary>
    internal static string LedgerId(QaFindingView f)
    {
        int bar = f.Key.LastIndexOf('|');
        string id = bar >= 0 ? f.Key[(bar + 1)..] : f.Key;
        return id.Length <= 16 && id.All(char.IsAsciiHexDigit) ? id : "unknown";
    }

    /// <summary>
    /// The server-fault table: the catch site, the exception type, the ledger id and the top frames (type and method names inside ArcaneCore
    /// only; no paths, no line numbers); never the exception message, which can carry player names. Everything goes through
    /// <see cref="PlayerbotQaRedactor.RedactFault"/>: the issue is public.
    /// </summary>
    private static void AppendServerFault(StringBuilder sb, QaFindingView f, PlayerbotQaRedactor redactor)
    {
        sb.Append("## Server fault\n\n| Field | Value |\n|---|---|\n");
        Row(sb, "Site", redactor.RedactFault(f.Goal));
        Row(sb, "Exception type", redactor.RedactFault(f.Reason));
        Row(sb, "Ledger id", LedgerId(f));
        if (f.Samples.LastOrDefault(s => !string.IsNullOrEmpty(s.Diag))?.Diag is { Length: > 0 } stack)
        {
            Row(sb, "Condensed stack", "`" + Code(redactor.RedactFault(stack)) + "`");
        }

        sb.Append("\n**Top frames**\n\n");
        string[] frames = f.ReasonDetail.Length == 0 ? [] : f.ReasonDetail.Split(" > ", StringSplitOptions.RemoveEmptyEntries);
        if (frames.Length == 0)
        {
            sb.Append("- (no ArcaneCore frame)\n");
        }

        foreach (string frame in frames)
        {
            sb.Append("- `").Append(Code(redactor.RedactFault(frame))).Append("`\n");
        }

        sb.Append('\n');
    }

    /// <summary>The spell table of a spell finding: the spell, the effect, the aura, the bots and classes, and an id reference.</summary>
    private static void AppendSpell(StringBuilder sb, QaFindingView f, PlayerbotQaRedactor redactor)
    {
        sb.Append("## Spell\n\n| Field | Value |\n|---|---|\n");
        string rank = f.SpellRank.Length != 0 ? " (" + f.SpellRank + ")" : string.Empty;
        Row(sb, "Spell", (string.Create(CultureInfo.InvariantCulture, $"{f.Spell} ") + redactor.Redact(f.SpellName) + rank).TrimEnd());
        if (f.EffectIndex >= 0)
        {
            Row(sb, "Effect", string.Create(CultureInfo.InvariantCulture, $"#{f.EffectIndex} {f.EffectName}, value {f.EffectValue}"));
        }

        if (f.Aura != 0)
        {
            var aura = (ArcaneCore.Game.Spells.AuraType)f.Aura;
            Row(sb, "Aura type", string.Create(CultureInfo.InvariantCulture, $"{f.Aura} {(Enum.IsDefined(aura) ? aura.ToString() : "unknown")}"));
        }

        if (f.Reason.Length != 0)
        {
            bool refusal = f.Kind == QaSignalKind.SpellRefused;
            Row(sb, refusal ? "Cast result" : "Gap", f.Reason + (!refusal && f.ReasonDetail.Length != 0 ? " " + redactor.Redact(f.ReasonDetail) : string.Empty));
        }

        Row(sb, "Bots that met it", f.DistinctBots.ToString(CultureInfo.InvariantCulture)
            + (f.Count == 0 ? " (found by the start-up spell coverage; no bot cast it yet)" : string.Empty));
        Row(sb, "Classes", f.ClassMask != 0 ? f.ClassNames : "unknown");
        if (f.Coverage) Row(sb, "Spell coverage", "listed: a spell bots can cast (a rotation ability, an escape or a class trainer spell)");
        // An id reference in the Wowhead classic style, as text only: nothing is fetched.
        Row(sb, "Reference", string.Create(CultureInfo.InvariantCulture, $"Wowhead classic spell={f.Spell} (https://classic.wowhead.com/spell={f.Spell})"));
        sb.Append('\n');
    }

    /// <summary>The comment for new occurrences of a finding whose issue is open.</summary>
    internal static string Comment(QaFindingView f, long newOccurrences, PlayerbotQaRedactor redactor)
    {
        QaSample? latest = f.Samples.Count > 0 ? f.Samples[^1] : null;
        string at = latest is { HasPosition: true }
            ? string.Create(CultureInfo.InvariantCulture, $"map {latest.Map} at {latest.X:F1}, {latest.Y:F1}, {latest.Z:F1}")
            : "no position";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{newOccurrences} new occurrences (total {f.Count}, bots {f.DistinctBots}) since {f.LastCommentUtc ?? f.FirstSeenUtc:u}, latest at {at} ({f.LastSeenUtc:u}).\n\n_{Footer}_");
        return redactor.Body(text, string.Empty);
    }

    /// <summary>
    /// The one comment that comes with reopening an issue whose finding recurred on a newer build (<paramref name="build"/>): that build, the occurrences on it, the total,
    /// the distinct bots and the latest sample. <paramref name="closedBuild"/> is the build the issue was closed on (or last known open on), when known.
    /// </summary>
    internal static string RegressionComment(QaFindingView f, string build, string? closedBuild, PlayerbotQaRedactor redactor)
    {
        QaSample? latest = f.Samples.Count > 0 ? f.Samples[^1] : null;
        string at = latest is { HasPosition: true }
            ? string.Create(CultureInfo.InvariantCulture, $"map {latest.Map} at {latest.X:F1}, {latest.Y:F1}, {latest.Z:F1}")
            : "no position";
        string was = closedBuild is null ? string.Empty : $" The issue was closed while build `{PlayerbotQaRedactor.RedactSecrets(closedBuild)}` was running.";
        string sample = latest is null || f.Kind == QaSignalKind.ServerFault ? string.Empty : $"\n\nSample: `{Code(redactor.Redact(latest.Message))}`";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"Regression: this finding occurred again on build `{PlayerbotQaRedactor.RedactSecrets(build)}`, a different build from the one it was closed on.{was} {f.CountOnBuild(build)} occurrences on this build (total {f.Count}){(f.Kind == QaSignalKind.ServerFault ? string.Empty : $", distinct bots {f.DistinctBots}")}, latest at {at} ({f.LastSeenUtc:u}). Reopening.{sample}\n\n_{Footer}_");
        return redactor.Body(text, string.Empty);
    }

    /// <summary>
    /// The live status block: a machine-readable comment line (<c>last-seen=</c> ISO 8601 UTC, <c>count=</c>, <c>build-count=</c>, <c>bots=</c>) that stale-issue sweeps can parse,
    /// and the same in words. The publisher rewrites it in place (<see cref="WithStatus"/>) as the finding keeps happening, so GitHub knows when it was last seen
    /// even between the daily comments. Numbers and the build only: nothing of any bot.
    /// </summary>
    internal static string StatusBlock(QaFindingView f, string build)
    {
        string seen = f.LastSeenUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        long onBuild = f.CountOnBuild(build);
        string bots = f.Kind == QaSignalKind.ServerFault ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", {f.DistinctBots} distinct bots");
        return string.Create(CultureInfo.InvariantCulture,
            $"{StatusBegin}\n<!-- arcanecore-bot-qa-status last-seen={seen} count={f.Count} build-count={onBuild} bots={f.DistinctBots} -->\n")
            + string.Create(CultureInfo.InvariantCulture,
                $"**Live status** (kept current by bot QA): last seen **{f.LastSeenUtc:u}**, {f.Count} occurrences in all, {onBuild} on build `{PlayerbotQaRedactor.RedactSecrets(build)}`{bots}.\n{StatusEnd}");
    }

    /// <summary>
    /// <paramref name="body"/> with its live status block replaced by <paramref name="block"/>; a body without one gets it after its first bot QA marker line (at the top
    /// when it has none). Everything else of the body is kept as it is.
    /// </summary>
    internal static string WithStatus(string body, string block)
    {
        body ??= string.Empty;
        int begin = body.IndexOf(StatusBegin, StringComparison.Ordinal);
        int end = begin < 0 ? -1 : body.IndexOf(StatusEnd, begin, StringComparison.Ordinal);
        if (begin >= 0 && end >= 0) return string.Concat(body.AsSpan(0, begin), block, body.AsSpan(end + StatusEnd.Length));
        Match marker = AnyMarker().Match(body);
        if (!marker.Success) return block + "\n\n" + body;
        int lineEnd = body.IndexOf('\n', marker.Index + marker.Length);
        return lineEnd < 0
            ? body + "\n" + block + "\n"
            : string.Concat(body.AsSpan(0, lineEnd + 1), block + "\n\n", body.AsSpan(lineEnd + 1));
    }

    /// <summary>The last-seen time the body's live status block shows; null when it has none (or it does not parse).</summary>
    internal static DateTime? StatusLastSeen(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        Match m = StatusLine().Match(body);
        return m.Success && DateTime.TryParseExact(m.Groups["seen"].Value, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime seen) ? seen : null;
    }

    /// <summary>
    /// The one comment that comes with closing an issue whose finding stopped (<see cref="PlayerbotQaGitHubOptions.AutoCloseQuietMultiple"/>): when it was last seen and on which
    /// build, how long the running build has gone without it against its usual gap, and that a recurrence reopens it.
    /// </summary>
    internal static string QuietCloseComment(QaFindingView f, string build, TimeSpan gap, TimeSpan quiet)
    {
        string last = f.LastBuild is null ? string.Empty : $" on build `{PlayerbotQaRedactor.RedactSecrets(f.LastBuild)}`";
        return string.Create(CultureInfo.InvariantCulture,
            $"Closing as completed: this finding was last seen {f.LastSeenUtc:u}{last} ({f.Count} occurrences in all). The newer build `{PlayerbotQaRedactor.RedactSecrets(build)}` has gone {quiet.TotalHours:F1} h without it, against a usual gap of {Span(gap)} between occurrences. If it happens again bot QA reopens this issue as a regression.\n\n_{Footer}_");
    }

    private static string Span(TimeSpan span) => span.TotalHours >= 1
        ? span.TotalHours.ToString("F1", CultureInfo.InvariantCulture) + " h"
        : Math.Max(1, (int)Math.Round(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min";

    [GeneratedRegex(@"<!-- arcanecore-bot-qa:[A-Za-z0-9]+ -->", RegexOptions.CultureInvariant)]
    private static partial Regex AnyMarker();

    [GeneratedRegex(@"<!-- arcanecore-bot-qa-status last-seen=(?<seen>[0-9TZ:\-]{20}) ", RegexOptions.CultureInvariant)]
    private static partial Regex StatusLine();

    /// <summary>The comment on an issue whose finding was merged into another: the surviving issue's number, nothing of any bot.</summary>
    internal static string DuplicateComment(int survivorIssue)
        => string.Create(CultureInfo.InvariantCulture,
            $"Duplicate of #{survivorIssue}: bot QA found these reports are one problem and now tracks them there. Closing this issue as a duplicate.\n\n_{Footer}_");

    private static void Row(StringBuilder sb, string name, string value) => sb.Append("| ").Append(name).Append(" | ").Append(Cell(value)).Append(" |\n");

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Code(string text) => text.Replace('`', '\'').Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

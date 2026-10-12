using System.Globalization;
using System.Reflection;
using System.Text;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The text of an issue ready to send (or to write to <c>qa/pending-issues/</c>).</summary>
internal sealed record QaIssueDraft(string Id, string Marker, string Title, string Body, IReadOnlyList<string> Labels);

/// <summary>
/// Builds the title, body and comment of a finding's GitHub issue. The body starts with the hidden marker
/// <c>&lt;!-- arcanecore-bot-qa:&lt;fingerprint&gt; --&gt;</c> that dedup searches for; every piece of evidence goes through the redactor. Pure.
/// </summary>
internal static class PlayerbotQaIssueComposer
{
    internal const string Footer = "Reported automatically by ArcaneCore playerbot QA";

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

    private static void Row(StringBuilder sb, string name, string value) => sb.Append("| ").Append(name).Append(" | ").Append(Cell(value)).Append(" |\n");

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Code(string text) => text.Replace('`', '\'').Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

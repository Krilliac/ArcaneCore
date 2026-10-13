using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// <c>.playerbot qa</c> (GameMaster): the bot QA findings, ranked (docs/areas/playbots-qa.md). Added to the <c>playerbot</c> root as an extension, so
/// the bot commands themselves are untouched. Read-only except <c>reset</c>, which forgets local findings and never touches GitHub. Every reply is bounded.
/// </summary>
public sealed class PlayerbotQaCommands : ICommandExtension
{
    /// <summary>The findings a bare <c>.playerbot qa</c> lists.</summary>
    internal const int DefaultTop = 10;

    /// <summary>The most queued signals a viewing command folds in on the world thread before it lists (the background pass takes the rest).</summary>
    internal const int MaxViewDrain = 512;

    public string Path => "playerbot";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("qa", AccountSecurity.GameMaster,
            "Syntax: .playerbot qa [top [#n] [all]|show $id|spells [#n]|reset [$id]|prune [#hours] [closed] [dry]]\nThe playerbot QA findings: stalls, death loops, faults, missing data and spells that do not work, ranked, and the GitHub reporter's state.",
            Root,
            Children:
            [
                new ChatCommand("top", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa top [#n] [all]\nList the n (default 10, at most 25) highest-ranked findings with their short ids and when each was last seen. Findings not seen since this server started (stale: possibly fixed by this build) are hidden behind one hint line; 'all' lists them too, after every current one.", Top),
                new ChatCommand("show", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa show $id\nShow one finding's cause, counts, evidence samples and a .go xyz command to the place (the id is at least 4 characters of the short id).", Show),
                new ChatCommand("spells", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa spells [#n]\nThe spell coverage of the bots (how many of the spells they can cast are fully handled, the gaps shared by the most spells) and the n (default 10, at most 25) highest-ranked spell findings bots met.", Spells),
                new ChatCommand("reset", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa reset [$id]\nForget one finding, or all of them. Local only: GitHub issues are not touched.", Reset),
                new ChatCommand("prune", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa prune [#hours] [closed] [dry]\nForget the findings not seen for #hours (default 12, 1..720). 'closed' also forgets those whose GitHub issue is known closed, whatever their age; 'dry' only lists what would go. Findings that hold a GitHub issue are never forgotten by age; 'closed' forgets the closed ones and remembers their issue number, so a recurrence stays closed. Local only: GitHub is not touched. Takes effect on disk at the next save.", Prune),
            ]),
    ];

    private static PlayerbotQaFeature? FeatureOf(CommandContext context)
    {
        PlayerbotQaFeature? feature = context.Session.Services.GetService<PlayerbotQaFeature>();
        if (feature?.Aggregator is null)
        {
            context.Reply("Bot QA is not running (World:Playerbots:Qa:Enabled).");
            return null;
        }

        return feature;
    }

    private static bool Root(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (FeatureOf(context) is not { } feature) return true;
        feature.Drain(MaxViewDrain);
        QaStatus status = feature.Status();
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Bot QA: {status.Findings} findings, {status.Received} warnings seen, {status.Dropped} dropped. GitHub: {status.GitHubMode}{(status.GitHubMode == "off" || status.TokenVariable.Length == 0 ? string.Empty : $", {status.TokenVariable} {(status.TokenSet ? "set" : "not set")}")}. {PlayerbotRetreatStats.Shared.Describe()}"));
        if (status.PersistenceDisabled is { } reason) context.Reply("Persistence is off: " + reason);
        foreach (string line in PlayerbotQaReport.ChatTop(feature.Aggregator!.Snapshot(), DefaultTop, feature.Redactor, feature.UtcNow, feature.StartedUtc)) context.Reply(line);
        return true;
    }

    private static bool Top(CommandContext context, string args)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int alls = words.Count(w => w.Equals("all", StringComparison.OrdinalIgnoreCase));
        if (alls > 1) return false;
        bool all = alls == 1;
        string[] numbers = [.. words.Where(w => !w.Equals("all", StringComparison.OrdinalIgnoreCase))];
        if (numbers.Length > 1) return false;
        int n = DefaultTop;
        if (numbers.Length == 1 && !int.TryParse(numbers[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false;
        if (FeatureOf(context) is not { } feature) return true;
        feature.Drain(MaxViewDrain); // thread-safe; without it a finding the bots just met would still look stale for up to one export interval
        foreach (string line in PlayerbotQaReport.ChatTop(feature.Aggregator!.Snapshot(), n, feature.Redactor, feature.UtcNow, feature.StartedUtc, all)) context.Reply(line);
        return true;
    }

    private static bool Show(CommandContext context, string args)
    {
        string id = args.Trim();
        if (id.Length == 0 || id.Contains(' ', StringComparison.Ordinal)) return false;
        if (FeatureOf(context) is not { } feature) return true;
        if (!PlayerbotQaReport.TryResolve(feature.Aggregator!.Snapshot(), id, out QaFindingView? finding, out string error))
        {
            context.Reply(error);
            return true;
        }

        foreach (string line in PlayerbotQaReport.ChatShow(finding!, feature.Redactor, feature.Build)) context.Reply(line);
        return true;
    }

    private static bool Spells(CommandContext context, string args)
    {
        string text = args.Trim().TrimStart('#');
        int n = DefaultTop;
        if (text.Length != 0 && !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false;
        if (FeatureOf(context) is not { } feature) return true;
        foreach (string line in PlayerbotQaReport.ChatSpells(feature.Aggregator!.Snapshot(), feature.SpellCoverage, n, feature.Redactor)) context.Reply(line);
        return true;
    }

    private static bool Reset(CommandContext context, string args)
    {
        string id = args.Trim();
        if (id.Contains(' ', StringComparison.Ordinal)) return false;
        if (FeatureOf(context) is not { } feature) return true;
        if (id.Length == 0)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Bot QA: {feature.Reset(null)} findings forgotten (GitHub is untouched)."));
            return true;
        }

        if (!PlayerbotQaReport.TryResolve(feature.Aggregator!.Snapshot(), id, out QaFindingView? finding, out string error))
        {
            context.Reply(error);
            return true;
        }

        feature.Reset(finding!.Id);
        context.Reply($"Bot QA: finding {finding.ShortId} forgotten (GitHub is untouched).");
        return true;
    }

    /// <summary>The hours <c>prune</c> uses without an argument.</summary>
    internal const int DefaultPruneHours = 12;

    /// <summary>The bounds of the hours argument of <c>prune</c>.</summary>
    internal const int MinPruneHours = 1, MaxPruneHours = 720;

    /// <summary>The most kinds the prune reply names (the rest are counted together).</summary>
    private const int MaxPruneKinds = 8;

    private static bool Prune(CommandContext context, string args)
    {
        int hours = DefaultPruneHours;
        bool closed = false, dry = false, hoursGiven = false;
        foreach (string word in args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (word.Equals("closed", StringComparison.OrdinalIgnoreCase) && !closed) closed = true;
            else if (word.Equals("dry", StringComparison.OrdinalIgnoreCase) && !dry) dry = true;
            else if (!hoursGiven && int.TryParse(word.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out hours)) hoursGiven = true;
            else return false;
        }

        if (hours is < MinPruneHours or > MaxPruneHours)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Hours must be {MinPruneHours}..{MaxPruneHours}."));
            return true;
        }

        if (FeatureOf(context) is not { } feature) return true;
        QaPruneResult result = feature.Prune(hours, closed, dry);
        string rule = string.Create(CultureInfo.InvariantCulture, $"not seen for {hours}h{(closed ? " or GitHub-closed" : string.Empty)}");
        string kept = result.Kept > 0 ? string.Create(CultureInfo.InvariantCulture, $" {result.Kept} old ones with a GitHub issue are kept.") : string.Empty;
        if (result.Count == 0)
        {
            context.Reply(Cut(string.Create(CultureInfo.InvariantCulture, $"Bot QA: nothing {rule}; {result.Remaining} findings remain.{kept}")));
            return true;
        }

        string breakdown = string.Join(", ", result.Kinds.Take(MaxPruneKinds).Select(k => string.Create(CultureInfo.InvariantCulture, $"{k.Key} {k.Value}")));
        if (result.Kinds.Count > MaxPruneKinds) breakdown += string.Create(CultureInfo.InvariantCulture, $", {result.Kinds.Count - MaxPruneKinds} more kinds");
        context.Reply(Cut(string.Create(CultureInfo.InvariantCulture,
            $"Bot QA: {(dry ? "dry run, would forget" : "forgot")} {result.Count} findings {rule} ({breakdown}); {result.Remaining} {(dry ? "would remain" : "remain")}.{kept} GitHub is untouched.")));
        if (!dry) return true;
        DateTime now = feature.UtcNow;
        foreach (QaFindingView f in result.Listed)
        {
            context.Reply(Cut(string.Create(CultureInfo.InvariantCulture, $"  {f.ShortId} x{f.Count} {PlayerbotQaReport.Age(f.LastSeenUtc, now)} {f.Publication} {f.KindLabel}: {feature.Redactor.Redact(f.Subject)}")));
        }

        if (result.Count > result.Listed.Count) context.Reply(string.Create(CultureInfo.InvariantCulture, $"  ... and {result.Count - result.Listed.Count} more."));
        return true;
    }

    private static string Cut(string line) => line.Length <= 220 ? line : line[..217] + "...";
}

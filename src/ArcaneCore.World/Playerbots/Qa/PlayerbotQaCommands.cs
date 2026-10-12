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

    public string Path => "playerbot";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("qa", AccountSecurity.GameMaster,
            "Syntax: .playerbot qa [top [#n]|show $id|spells [#n]|reset [$id]]\nThe playerbot QA findings: stalls, death loops, faults, missing data and spells that do not work, ranked, and the GitHub reporter's state.",
            Root,
            Children:
            [
                new ChatCommand("top", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa top [#n]\nList the n (default 10, at most 25) highest-ranked findings with their short ids.", Top),
                new ChatCommand("show", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa show $id\nShow one finding's cause, counts, evidence samples and a .go xyz command to the place (the id is at least 4 characters of the short id).", Show),
                new ChatCommand("spells", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa spells [#n]\nThe spell coverage of the bots (how many of the spells they can cast are fully handled, the gaps shared by the most spells) and the n (default 10, at most 25) highest-ranked spell findings bots met.", Spells),
                new ChatCommand("reset", AccountSecurity.GameMaster,
                    "Syntax: .playerbot qa reset [$id]\nForget one finding, or all of them. Local only: GitHub issues are not touched.", Reset),
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
        QaStatus status = feature.Status();
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Bot QA: {status.Findings} findings, {status.Received} warnings seen, {status.Dropped} dropped. GitHub: {status.GitHubMode}{(status.GitHubMode == "off" || status.TokenVariable.Length == 0 ? string.Empty : $", {status.TokenVariable} {(status.TokenSet ? "set" : "not set")}")}. {PlayerbotRetreatStats.Shared.Describe()}"));
        if (status.PersistenceDisabled is { } reason) context.Reply("Persistence is off: " + reason);
        foreach (string line in PlayerbotQaReport.ChatTop(feature.Aggregator!.Snapshot(), DefaultTop, feature.Redactor)) context.Reply(line);
        return true;
    }

    private static bool Top(CommandContext context, string args)
    {
        string text = args.Trim().TrimStart('#');
        int n = DefaultTop;
        if (text.Length != 0 && !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false;
        if (FeatureOf(context) is not { } feature) return true;
        foreach (string line in PlayerbotQaReport.ChatTop(feature.Aggregator!.Snapshot(), n, feature.Redactor)) context.Reply(line);
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

        foreach (string line in PlayerbotQaReport.ChatShow(finding!, feature.Redactor)) context.Reply(line);
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
}

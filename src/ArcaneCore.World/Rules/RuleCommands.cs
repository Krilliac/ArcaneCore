using System.Globalization;
using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Rules;

/// <summary>
/// The <c>.rules</c> tools for rule authors (design 5.1-5.3; an ArcaneCore addition): <c>check</c> lists every rejected rule row,
/// <c>vocab</c> prints the vocabulary, <c>trace</c> traces one subject's rule evaluations. Read-only towards game state.
/// </summary>
public sealed class RuleCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand(
            "rules",
            AccountSecurity.GameMaster,
            "Syntax: .rules check [host] [owner_id] | vocab [host] | trace [guid] — rule engine tools for rule authors.",
            null,
            [
                new ChatCommand("check", AccountSecurity.GameMaster,
                    "Syntax: .rules check [host] [owner_id] — what is loaded and every rule row that was rejected, with its reason (narrowed to a host and owner).", Check),
                new ChatCommand("vocab", AccountSecurity.GameMaster,
                    "Syntax: .rules vocab [host] — every event, action and target id with its parameters (a host's own plus the shared ones).", Vocab),
                new ChatCommand("trace", AccountSecurity.GameMaster,
                    "Syntax: .rules trace [guid] — start or stop tracing every rule evaluation of a subject (decimal or 0x guid); without a guid, show who is traced and the latest traced evaluations.", Trace),
            ]),
    ];

    private static RuleFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<RuleFeature>();

    private static bool Check(CommandContext context, string args)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        RuleHostKind? host = null;
        int? owner = null;
        if (words.Length > 0)
        {
            if (RuleHostNames.Parse(words[0]) is not { } parsed)
            {
                context.Reply($"Unknown rule host '{words[0]}'. Hosts: {HostList()}.");
                return true;
            }

            host = parsed;
        }

        if (words.Length > 1)
        {
            if (!int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                return false;
            }

            owner = id;
        }

        if (words.Length > 2)
        {
            return false;
        }

        RuleCatalog catalog = Feature(context).Catalog;
        context.Reply($"Rules: {catalog.Summary}.");
        RuleRejection[] rows = [.. catalog.Rejected.Where(r => (host is null || r.Host == host) && (owner is null || r.Owner?.Id == owner))];
        if (rows.Length == 0)
        {
            context.Reply(host is null && owner is null ? "No rule row was rejected." : "No rejected rule row matches.");
            return true;
        }

        foreach (RuleRejection row in rows)
        {
            context.Reply(row.Owner is { } o ? $"{row} [{RuleHostNames.Of(row.Host ?? RuleHostKind.Any)} {o}]" : row.ToString());
        }

        context.Reply($"{rows.Length} rejected row(s).");
        return true;
    }

    private static bool Vocab(CommandContext context, string args)
    {
        string word = args.Trim();
        RuleHostKind? host = null;
        if (word.Length > 0)
        {
            if (RuleHostNames.Parse(word) is not { } parsed)
            {
                context.Reply($"Unknown rule host '{word}'. Hosts: {HostList()}.");
                return true;
            }

            host = parsed;
        }

        RuleVocabulary vocabulary = Feature(context).Catalog.Vocabulary;
        if (host is { } h && vocabulary.FindHost(h) is null)
        {
            context.Reply($"Host {RuleHostNames.Of(h)} is not registered in this build; its rules are rejected. The shared primitives follow.");
        }

        IReadOnlyList<string> lines = RuleReference.VocabularyLines(vocabulary, host);
        foreach (string line in lines)
        {
            context.Reply(line);
        }

        context.Reply($"{lines.Count} primitive(s).");
        return true;
    }

    private static bool Trace(CommandContext context, string args)
    {
        RuleFeature feature = Feature(context);
        string word = args.Trim();
        if (word.Length == 0)
        {
            context.Reply(feature.Tracer.Count == 0
                ? "No subject is traced."
                : $"Traced: {string.Join(", ", feature.Tracer.Guids.Order().Select(g => $"0x{g:X}"))}.");
            IReadOnlyList<string> recent = feature.RecentTrace;
            foreach (string line in recent.Skip(Math.Max(0, recent.Count - 10)))
            {
                context.Reply(line);
            }

            return true;
        }

        bool hex = word.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ulong.TryParse(hex ? word[2..] : word, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out ulong guid) || guid == 0)
        {
            return false;
        }

        context.Reply(feature.Tracer.Toggle(guid)
            ? $"Tracing the rules of 0x{guid:X} (every evaluation is logged and kept for .rules trace)."
            : $"Stopped tracing 0x{guid:X}.");
        return true;
    }

    private static string HostList()
        => string.Join(", ", Enum.GetValues<RuleHostKind>().Where(k => k != RuleHostKind.Any).Select(RuleHostNames.Of));
}

using System.Globalization;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Commands;

/// <summary>
/// The paged command listings of <c>.commands</c> and <c>.help</c>: the commands (or sub-commands) the caller may use,
/// packed several per line and split into pages. Pure functions over a <see cref="CommandTable"/> and a security level
/// (no session, no I/O), so they are testable without a world. The console keeps the
/// full vertical list for a bare <c>.commands</c>/<c>.help</c> (it has no scrollback problem); with a page or filter argument it uses these.
/// </summary>
public static class CommandListing
{
    /// <summary>
    /// Reply lines per page, header and footer included. The 1.12 client's chat frame keeps only a short scrollback,
    /// and the earlier one-command-per-line dump (about 300 lines for an Administrator) pushed almost all of itself out
    /// of it, so a page must stay small enough to be read without scrolling.
    /// </summary>
    public const int PageLines = 15;

    /// <summary>Longest packed body line, in characters: well under the chat message limit and about two wrapped rows in the default chat frame.</summary>
    public const int LineWidth = 120;

    /// <summary>The longest filter text honoured (and echoed); keeps the header inside <see cref="LineWidth"/> (the footer drops its hints when it would not fit).</summary>
    public const int MaxFilterLength = 48;

    private const int BodyLines = PageLines - 2;

    /// <summary>One page of the top-level commands and groups the caller may use ("name" or "group (subcommand count)").</summary>
    public static IReadOnlyList<string> Overview(CommandTable table, AccountSecurity security, int page)
        => Compose("Commands", Pack(Tokens(table, table.Roots, security)), page, "commands");

    /// <summary>
    /// One page of the sub-commands (<paramref name="children"/>) of the group at <paramref name="groupPath"/> ("npc", "playerbot x")
    /// the caller may use; the path, not just the last word, goes into the "Next" footer so nested groups page correctly.
    /// </summary>
    public static IReadOnlyList<string> SubCommands(CommandTable table, IReadOnlyList<ChatCommand> children, string groupPath, AccountSecurity security, int page)
        => Compose(groupPath.Length == 0 ? "Subcommands" : $"Command {groupPath} subcommands", Pack(Tokens(table, children, security)), page, groupPath.Length == 0 ? "commands" : "help " + groupPath);

    /// <summary>
    /// One page of the usable commands whose full path ("npc add") contains <paramref name="filter"/>, case-insensitively;
    /// paths that start with it come first, otherwise table order.
    /// </summary>
    public static IReadOnlyList<string> Filtered(CommandTable table, AccountSecurity security, string filter, int page)
    {
        string text = Clip(filter);
        var paths = new List<string>();
        Collect(table, table.Roots, string.Empty, security, paths);
        string[] matches = [.. paths
            .Where(p => p.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
        if (matches.Length == 0)
        {
            return [$"No commands match '{text}'."];
        }

        return Compose($"Commands matching '{text}'", Pack(matches), page, "commands " + text);
    }

    /// <summary>The argument of <c>.commands</c>/<c>.help</c> split into filter text and page: a trailing number is the page ("npc 2", "2"); the text may be empty.</summary>
    public static (string Text, int Page) ParseArguments(string args)
    {
        string trimmed = args.Trim();
        int split = trimmed.LastIndexOf(' ');
        string last = split < 0 ? trimmed : trimmed[(split + 1)..];
        if (last.Length > 0 && last.All(char.IsAsciiDigit))
        {
            int page = int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : int.MaxValue;
            return (split < 0 ? string.Empty : trimmed[..split].TrimEnd(), page);
        }

        return (trimmed, 1);
    }

    /// <summary>Greedy-pack <paramref name="tokens"/> into comma-separated lines of at most <see cref="LineWidth"/> characters (a longer token gets a line of its own).</summary>
    public static List<string> Pack(IEnumerable<string> tokens)
    {
        var lines = new List<string>();
        string current = string.Empty;
        foreach (string token in tokens)
        {
            if (current.Length == 0)
            {
                current = token;
            }
            else if (current.Length + 2 + token.Length + 1 <= LineWidth)
            {
                current = current + ", " + token;
            }
            else
            {
                lines.Add(current + ",");
                current = token;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static List<string> Compose(string title, List<string> lines, int page, string pageCommand)
    {
        int pages = Math.Max(1, (lines.Count + BodyLines - 1) / BodyLines);
        int current = Math.Clamp(page, 1, pages);
        var reply = new List<string>(PageLines) { $"{title} (page {current}/{pages})" };
        reply.AddRange(lines.Skip((current - 1) * BodyLines).Take(BodyLines));
        // Longest footer that fits LineWidth: a long group path or filter text drops the trailing hints, never the "Next" part.
        string next = $"Next: .{pageCommand} {current + 1}";
        string[] candidates = current < pages
            ? [$"{next} | Search: .commands <text> | Details: .help <command>", $"{next} | Search: .commands <text>", next]
            : ["Search: .commands <text> [page] | Details: .help <command>"];
        reply.Add(candidates.FirstOrDefault(c => c.Length <= LineWidth) ?? candidates[^1]);
        return reply;
    }

    private static IEnumerable<string> Tokens(CommandTable table, IReadOnlyList<ChatCommand> level, AccountSecurity security)
    {
        foreach (ChatCommand command in level)
        {
            if (!table.IsListed(command, security))
            {
                continue;
            }

            int children = command.SubCommands.Count(c => table.IsListed(c, security));
            yield return children > 0 ? $"{command.Name} ({children})" : command.Name;
        }
    }

    private static void Collect(CommandTable table, IReadOnlyList<ChatCommand> level, string prefix, AccountSecurity security, List<string> paths)
    {
        foreach (ChatCommand command in level)
        {
            if (!table.IsListed(command, security))
            {
                continue;
            }

            string path = prefix.Length == 0 ? command.Name : prefix + " " + command.Name;
            if (command.Handler is not null && table.IsAvailable(command, security))
            {
                paths.Add(path);
            }

            Collect(table, command.SubCommands, path, security, paths);
        }
    }

    private static string Clip(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length > MaxFilterLength ? trimmed[..MaxFilterLength] : trimmed;
    }
}

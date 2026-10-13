using System.Text.RegularExpressions;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Xunit;

namespace ArcaneCore.World.Tests.Commands;

/// <summary>
/// The paged <c>.commands</c> / <c>.help</c> listings: a page fits the client's chat scrollback, every command shows up
/// exactly once across the pages, filters and permission filtering hold.
/// </summary>
public sealed partial class CommandListingTests
{
    [GeneratedRegex(@"\(page (\d+)/(\d+)\)$")]
    private static partial Regex PageHeader();

    private static CommandTable Table() => ChatCommands.CreateTable(new GmOptions());

    /// <summary>Every page of a listing, found by walking page 1..M from the header's "(page p/M)".</summary>
    private static List<IReadOnlyList<string>> AllPages(Func<int, IReadOnlyList<string>> page)
    {
        var pages = new List<IReadOnlyList<string>>();
        int total = 1;
        for (int p = 1; p <= total; p++)
        {
            IReadOnlyList<string> lines = page(p);
            Match header = PageHeader().Match(lines[0]);
            Assert.True(header.Success, lines[0]);
            Assert.Equal(p, int.Parse(header.Groups[1].Value));
            total = int.Parse(header.Groups[2].Value);
            pages.Add(lines);
        }

        return pages;
    }

    private static IEnumerable<string> Tokens(IEnumerable<IReadOnlyList<string>> pages)
        => pages.SelectMany(p => p.Skip(1).Take(p.Count - 2))
            .SelectMany(line => line.TrimEnd(',').Split(", "));

    [Theory]
    [InlineData(AccountSecurity.Player)]
    [InlineData(AccountSecurity.Moderator)]
    [InlineData(AccountSecurity.GameMaster)]
    [InlineData(AccountSecurity.Administrator)]
    public void Overview_EveryPageFitsThePageSizeAndLineWidth(AccountSecurity security)
    {
        CommandTable table = Table();
        foreach (IReadOnlyList<string> page in AllPages(p => CommandListing.Overview(table, security, p)))
        {
            Assert.InRange(page.Count, 3, CommandListing.PageLines);
            Assert.All(page, line => Assert.True(line.Length <= CommandListing.LineWidth, line.Length + ": " + line));
        }
    }

    [Fact]
    public void Overview_PagesCoverEveryListedRootExactlyOnce_WithGroupCounts()
    {
        CommandTable table = Table();
        foreach (AccountSecurity security in new[] { AccountSecurity.Player, AccountSecurity.GameMaster, AccountSecurity.Administrator })
        {
            string[] expected = [.. table.Roots.Where(c => table.IsListed(c, security)).Select(c =>
            {
                int n = c.SubCommands.Count(s => table.IsListed(s, security));
                return n > 0 ? $"{c.Name} ({n})" : c.Name;
            })];
            string[] actual = [.. Tokens(AllPages(p => CommandListing.Overview(table, security, p)))];

            Assert.Equal(expected, actual);                        // none lost, none duplicated, table order kept
            Assert.Equal(actual.Length, actual.Distinct().Count());
        }
    }

    [Fact]
    public void Overview_OutOfRangePageIsClamped_ToTheFirstAndLastPage()
    {
        CommandTable table = Table();
        var pages = AllPages(p => CommandListing.Overview(table, AccountSecurity.Administrator, p));
        Assert.Equal(pages[^1], CommandListing.Overview(table, AccountSecurity.Administrator, int.MaxValue));
        Assert.Equal(pages[0], CommandListing.Overview(table, AccountSecurity.Administrator, 0));
    }

    private static CommandTable Synthetic(int roots)
        => ChatCommands.Build(Enumerable.Range(0, roots).Select(i => new ChatCommand($"cmd{i:D3}", AccountSecurity.Player, "x", (_, _) => true)), [], [], new GmOptions());

    [Fact]
    public void Overview_OfThreeHundredRoots_SpansPages_WithTheNextFooter_AndEveryRootOnce()
    {
        CommandTable table = Synthetic(300);
        var pages = AllPages(p => CommandListing.Overview(table, AccountSecurity.Player, p));

        Assert.True(pages.Count > 1, "300 roots must span several pages");
        Assert.All(pages, page => Assert.InRange(page.Count, 3, CommandListing.PageLines));
        Assert.StartsWith("Next: .commands 2 | ", pages[0][^1], StringComparison.Ordinal);
        Assert.StartsWith($"Next: .commands {pages.Count} | ", pages[^2][^1], StringComparison.Ordinal);
        Assert.StartsWith("Search: .commands <text> [page]", pages[^1][^1], StringComparison.Ordinal);   // the last page has no "Next"
        Assert.Equal(pages[^1], CommandListing.Overview(table, AccountSecurity.Player, 9999));            // clamps to the real last page
        Assert.Equal(table.Roots.Select(r => r.Name), Tokens(pages));                                      // each once, in order
    }

    private static List<string> UsablePathsContaining(CommandTable table, AccountSecurity security, string text)
    {
        var expected = new List<string>();
        void Walk(IReadOnlyList<ChatCommand> level, string prefix)
        {
            foreach (ChatCommand c in level)
            {
                string path = prefix.Length == 0 ? c.Name : prefix + " " + c.Name;
                if (c.Handler is not null && table.IsAvailable(c, security) && path.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    expected.Add(path);
                }

                Walk(c.SubCommands, path);
            }
        }

        Walk(table.Roots, string.Empty);
        return expected;
    }

    [Fact]
    public void Filter_ForAShortText_SpansPages_WithTheExactNextFooter_Clamps_AndListsEveryMatchOnce()
    {
        CommandTable table = Table();
        var pages = AllPages(p => CommandListing.Filtered(table, AccountSecurity.Administrator, "e", p));

        Assert.True(pages.Count > 1, "the filter 'e' matches far more than one page");
        Assert.StartsWith("Next: .commands e 2 | Search: .commands <text>", pages[0][^1], StringComparison.Ordinal);
        Assert.StartsWith($"Next: .commands e {pages.Count} | ", pages[^2][^1], StringComparison.Ordinal);
        Assert.StartsWith($"Commands matching 'e' (page {pages.Count}/{pages.Count})", pages[^1][0], StringComparison.Ordinal);
        Assert.Equal(pages[^1], CommandListing.Filtered(table, AccountSecurity.Administrator, "e", int.MaxValue));
        Assert.Equal(pages[0], CommandListing.Filtered(table, AccountSecurity.Administrator, "e", -5));
        Assert.All(pages, page => Assert.InRange(page.Count, 3, CommandListing.PageLines));

        string[] paths = [.. Tokens(pages)];
        Assert.Equal(paths.Length, paths.Distinct().Count());
        Assert.Equal(UsablePathsContaining(table, AccountSecurity.Administrator, "e").OrderBy(p => p, StringComparer.Ordinal), paths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void SubCommands_OfANestedGroup_PageWithTheFullPathInTheFooter_AndFooterRespectsTheLineWidth()
    {
        ChatCommand[] leaves = [.. Enumerable.Range(0, 400).Select(i => new ChatCommand($"leaf{i:D3}", AccountSecurity.Player, "x", (_, _) => true))];
        var inner = new ChatCommand("inner", AccountSecurity.Player, "x", Children: leaves);
        var outer = new ChatCommand("outer", AccountSecurity.Player, "x", Children: [inner]);
        CommandTable table = ChatCommands.Build([outer], [], [], new GmOptions());

        var pages = AllPages(p => CommandListing.SubCommands(table, inner.SubCommands, "outer inner", AccountSecurity.Player, p));
        Assert.True(pages.Count > 1);
        Assert.Equal("Command outer inner subcommands (page 1/" + pages.Count + ")", pages[0][0]);
        Assert.StartsWith("Next: .help outer inner 2 | ", pages[0][^1], StringComparison.Ordinal);
        Assert.Equal(leaves.Select(l => l.Name), Tokens(pages));

        // A very long path makes the footer drop its hints, never exceed the line width.
        string longPath = string.Join(' ', Enumerable.Repeat("verylonggroupname", 5));
        IReadOnlyList<string> footerPage = CommandListing.SubCommands(table, inner.SubCommands, longPath, AccountSecurity.Player, 1);
        Assert.True(footerPage[^1].Length <= CommandListing.LineWidth, footerPage[^1]);
        Assert.StartsWith("Next: .help " + longPath + " 2", footerPage[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_ForAPlayerListsNoGmCommand()
    {
        CommandTable table = Table();
        string[] player = [.. Tokens(AllPages(p => CommandListing.Overview(table, AccountSecurity.Player, p)))];
        string[] admin = [.. Tokens(AllPages(p => CommandListing.Overview(table, AccountSecurity.Administrator, p)))];

        Assert.True(admin.Length > player.Length);
        Assert.Contains("help", player);
        Assert.Contains("commands", player);
        Assert.DoesNotContain(player, t => t.StartsWith("kick", StringComparison.Ordinal) || t.StartsWith("levelup", StringComparison.Ordinal) || t.StartsWith("account", StringComparison.Ordinal));
        Assert.Contains(admin, t => t.StartsWith("levelup", StringComparison.Ordinal));

        // Filtering cannot reveal them either.
        Assert.Equal(["No commands match 'levelup'."], CommandListing.Filtered(table, AccountSecurity.Player, "levelup", 1));
        Assert.Equal(["No commands match 'kick'."], CommandListing.Filtered(table, AccountSecurity.Player, "kick", 1));
    }

    [Fact]
    public void Filter_ListsFullPathsContainingTheText_CaseInsensitive_PrefixFirst_AndPagesCoverThemOnce()
    {
        CommandTable table = Table();
        var pages = AllPages(p => CommandListing.Filtered(table, AccountSecurity.Administrator, "NPC", p));
        string[] paths = [.. Tokens(pages)];

        Assert.NotEmpty(paths);
        Assert.Contains("npc add", paths);
        Assert.All(paths, path => Assert.Contains("npc", path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(paths.Length, paths.Distinct().Count());
        Assert.All(pages, page => Assert.InRange(page.Count, 3, CommandListing.PageLines));
        Assert.StartsWith("Commands matching 'NPC' (page 1/", pages[0][0], StringComparison.Ordinal);

        // Prefix matches ("npc ...") sort before substring matches.
        int lastPrefix = Array.FindLastIndex(paths, p => p.StartsWith("npc", StringComparison.OrdinalIgnoreCase));
        int firstOther = Array.FindIndex(paths, p => !p.StartsWith("npc", StringComparison.OrdinalIgnoreCase));
        Assert.True(firstOther < 0 || lastPrefix < firstOther);

        // Every usable command whose path contains the text is present (cross-checked with an independent walk).
        var expected = new List<string>();
        void Walk(IReadOnlyList<ChatCommand> level, string prefix)
        {
            foreach (ChatCommand c in level)
            {
                string path = prefix.Length == 0 ? c.Name : prefix + " " + c.Name;
                if (c.Handler is not null && table.IsAvailable(c, AccountSecurity.Administrator) && path.Contains("npc", StringComparison.OrdinalIgnoreCase))
                {
                    expected.Add(path);
                }

                Walk(c.SubCommands, path);
            }
        }

        Walk(table.Roots, string.Empty);
        Assert.Equal(expected.OrderBy(p => p, StringComparer.Ordinal), paths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Filter_WithNoMatchSaysSo_AndAnOverlongTextIsClipped()
    {
        CommandTable table = Table();
        Assert.Equal(["No commands match 'zzzznotacommand'."], CommandListing.Filtered(table, AccountSecurity.Administrator, "zzzznotacommand", 1));
        IReadOnlyList<string> clipped = CommandListing.Filtered(table, AccountSecurity.Administrator, new string('x', 500), 1);
        Assert.Single(clipped);
        Assert.True(clipped[0].Length <= CommandListing.LineWidth);
    }

    [Fact]
    public void SubCommands_ListAGroupsUsableChildren_PackedAndPaged()
    {
        CommandTable table = Table();
        ChatCommand server = table.Find("server")!;

        IReadOnlyList<string> player = CommandListing.SubCommands(table, server.SubCommands, "server", AccountSecurity.Player, 1);
        Assert.Equal("Command server subcommands (page 1/1)", player[0]);
        Assert.Equal("info, motd", player[1]);
        Assert.Equal(3, player.Count);

        string[] admin = [.. Tokens(AllPages(p => CommandListing.SubCommands(table, server.SubCommands, "server", AccountSecurity.Administrator, p)))];
        Assert.Contains("info", admin);
        Assert.Contains("motd", admin);
        Assert.True(admin.Length >= 2);

        // A big group pages and still covers every child once.
        ChatCommand biggest = table.Roots.OrderByDescending(c => c.SubCommands.Count).First();
        string[] children = [.. Tokens(AllPages(p => CommandListing.SubCommands(table, biggest.SubCommands, biggest.Name, AccountSecurity.Administrator, p)))
            .Select(t => Regex.Replace(t, @" \(\d+\)$", string.Empty))];
        Assert.Equal(
            biggest.SubCommands.Where(c => table.IsListed(c, AccountSecurity.Administrator)).Select(c => c.Name),
            children);
    }

    [Theory]
    [InlineData("", "", 1)]
    [InlineData("  ", "", 1)]
    [InlineData("3", "", 3)]
    [InlineData("npc", "npc", 1)]
    [InlineData("npc 2", "npc", 2)]
    [InlineData("npc add 12", "npc add", 12)]
    [InlineData("npc add", "npc add", 1)]
    [InlineData("99999999999999999999", "", int.MaxValue)]
    public void ParseArguments_SplitsTextAndTrailingPage(string args, string text, int page)
        => Assert.Equal((text, page), CommandListing.ParseArguments(args));

    [Fact]
    public void Pack_KeepsLinesWithinTheWidth_AndLosesNothing()
    {
        string[] tokens = [.. Enumerable.Range(0, 200).Select(i => "token" + i)];
        List<string> lines = CommandListing.Pack(tokens);

        Assert.All(lines, line => Assert.True(line.Length <= CommandListing.LineWidth));
        Assert.Equal(tokens, lines.SelectMany(l => l.TrimEnd(',').Split(", ")));
        Assert.All(lines.Take(lines.Count - 1), line => Assert.EndsWith(",", line, StringComparison.Ordinal));
        Assert.DoesNotContain(",", lines[^1][^1..], StringComparison.Ordinal);
    }
}

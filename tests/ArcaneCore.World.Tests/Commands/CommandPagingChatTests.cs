using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests.HotCode;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Commands;

/// <summary>End to end: <c>.commands &lt;text&gt; &lt;page&gt;</c> and <c>.help &lt;group&gt; &lt;page&gt;</c> reach the paged listings through chat.</summary>
public sealed class CommandPagingChatTests
{
    private static ChatCommand Leaf(string name) => new(name, AccountSecurity.Player, "Syntax: ." + name, (_, _) => true);

    private static async Task<string[]> RunAsync(WorldTestClient client, string command, int atLeast)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync(atLeast);
    }

    [Fact]
    public async Task CommandsWithTextAndPage_ShowThatPageOfTheFilter()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("PAGEADM", "Pageadm", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string[] first = await RunAsync(admin, ".commands e", 15);
        Assert.StartsWith("Commands matching 'e' (page 1/", first[0], StringComparison.Ordinal);
        Assert.StartsWith("Next: .commands e 2 | ", first[^1], StringComparison.Ordinal);

        string[] second = await RunAsync(admin, ".commands e 2", 15);
        Assert.StartsWith("Commands matching 'e' (page 2/", second[0], StringComparison.Ordinal);
        Assert.NotEqual(first[1], second[1]);

        string[] overview = await RunAsync(admin, ".commands 1", 3);
        Assert.StartsWith("Commands (page 1/", overview[0], StringComparison.Ordinal);
        Assert.Equal("Commands (page 1/1)", (await RunAsync(admin, ".help", 3))[0]);
    }

    [Fact]
    public async Task HelpWithAGroupAndPage_ShowsThatPageOfTheGroupsSubcommands_AlsoNested_AndOnTheUnknownSubcommandPath()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PAGEPLR", "Pagepl");
        await player.CollectAsync();

        ChatCommand[] leaves = [.. Enumerable.Range(0, 400).Select(i => Leaf($"leaf{i:D3}"))];
        var inner = new ChatCommand("inner", AccountSecurity.Player, "Syntax: .pagegrp inner", Children: leaves);
        var group = new ChatCommand("pagegrp", AccountSecurity.Player, "Syntax: .pagegrp", Children: [.. leaves, inner]);
        Assert.True(host.WorldServices.GetRequiredService<CommandTableSource>().TryAdd([group]).Applied);

        // The group's own help text first, then page 1 of its subcommands.
        string[] page1 = await RunAsync(player, ".help pagegrp", 16);
        Assert.Equal("Syntax: .pagegrp", page1[0]);
        Assert.StartsWith("Command pagegrp subcommands (page 1/", page1[1], StringComparison.Ordinal);
        Assert.StartsWith("Next: .help pagegrp 2 | ", page1[^1], StringComparison.Ordinal);
        Assert.StartsWith("leaf000, leaf001", page1[2], StringComparison.Ordinal);

        string[] page2 = await RunAsync(player, ".help pagegrp 2", 15);
        Assert.StartsWith("Command pagegrp subcommands (page 2/", page2[1], StringComparison.Ordinal);
        Assert.DoesNotContain("leaf000", string.Join(' ', page2));

        // A nested group pages by its full path.
        string[] nested = await RunAsync(player, ".help pagegrp inner 2", 15);
        Assert.StartsWith("Command pagegrp inner subcommands (page 2/", nested[1], StringComparison.Ordinal);
        Assert.Contains(nested, l => l.StartsWith("Next: .help pagegrp inner 3", StringComparison.Ordinal));

        // The unknown-subcommand listing (a handler-less group) pages by the group's path too.
        string[] unknown = await RunAsync(player, ".pagegrp", 16);
        Assert.Equal("There is no such subcommand", unknown[0]);
        Assert.StartsWith("Command pagegrp subcommands (page 1/", unknown[1], StringComparison.Ordinal);
        Assert.StartsWith("Next: .help pagegrp 2 | ", unknown[^1], StringComparison.Ordinal);
    }
}

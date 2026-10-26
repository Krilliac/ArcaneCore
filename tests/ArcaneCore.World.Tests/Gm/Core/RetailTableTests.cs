using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>
/// The vmangos command-table semantics (vmangos@0e3ff01 src/game/Chat/Chat.cpp): table order and
/// abbreviation (1185-1366, 1566-1600, 1767-1860), resolve-then-authorise and the reply texts
/// (1873-1970, 2081-2163), and the retail levels of the pre-existing commands.
/// </summary>
public sealed class RetailTableTests
{
    private static ChatCommand Leaf(string name, byte level = 0)
        => new(name, AccountSecurity.Player, "Syntax: ." + name, (_, _) => true, RetailLevel: level);

    [Fact]
    public void RootsFollowTheRetailTableOrder_UnknownRootsStayAfterThemInRegistrationOrder()
    {
        CommandTable table = ChatCommands.Build(
            [Leaf("zzfirst"), Leaf("save"), Leaf("help"), Leaf("gm"), Leaf("aaplugin")], [], [], new GmOptions());

        Assert.Equal(["gm", "help", "save", "zzfirst", "aaplugin"], table.Roots.Select(r => r.Name));
        Assert.True(RetailCommandOrder.IndexOf("gm") < RetailCommandOrder.IndexOf("go"));
        Assert.True(RetailCommandOrder.IndexOf("go") < RetailCommandOrder.IndexOf("guild"));
        Assert.Equal(-1, RetailCommandOrder.IndexOf("nosuchroot"));
    }

    [Fact]
    public void Abbreviations_TakeTheFirstPrefixMatchInTableOrder()
    {
        CommandTable table = ChatCommands.Build([Leaf("guild"), Leaf("go"), Leaf("gm"), Leaf("gps"), Leaf("save"), Leaf("saveall")], [], [], new GmOptions());

        Assert.Equal("gm", table.Lookup("g", AccountSecurity.Administrator).Command?.Name);
        Assert.Equal("go", table.Lookup("go", AccountSecurity.Administrator).Command?.Name);   // "go" before "gobject"-like names
        Assert.Equal("save", table.Lookup("sav", AccountSecurity.Administrator).Command?.Name);
        Assert.Equal("save", table.Lookup("save", AccountSecurity.Administrator).Command?.Name);   // retail: "save" precedes "saveall"
        Assert.Equal("saveall", table.Lookup("savea", AccountSecurity.Administrator).Command?.Name);

        // The pre-retail rule (exact name first) is still available.
        CommandTable exact = ChatCommands.Build([Leaf("saveall"), Leaf("save")], [], [], new GmOptions { ExactNameFirst = true });
        Assert.Equal("save", exact.Lookup("save", AccountSecurity.Administrator).Command?.Name);
    }

    [Fact]
    public void ResolveThenAuthorise_AKnownCommandAboveTheCallerIsUnavailable_NotUnknown()
    {
        CommandTable table = ChatCommands.Build([Leaf("kick", 2), Leaf("help")], [], [], new GmOptions());

        CommandLookup denied = table.Lookup("kick Bob", AccountSecurity.Player);
        Assert.Equal(CommandLookupResult.Ok, denied.Result);
        Assert.False(denied.Available);
        Assert.Equal("Bob", denied.Rest);

        Assert.True(table.Lookup("kick Bob", AccountSecurity.GameMaster).Available);
        Assert.Equal(CommandLookupResult.Unknown, table.Lookup("nonsense", AccountSecurity.Player).Result);
    }

    [Fact]
    public void HideUnavailable_RestoresTheOldBehaviour_AsUnknown()
    {
        CommandTable table = ChatCommands.Build([Leaf("kick", 2)], [], [], new GmOptions { HideUnavailable = true });

        Assert.Equal(CommandLookupResult.Unknown, table.Lookup("kick Bob", AccountSecurity.Player).Result);
        Assert.Equal(CommandLookupResult.Ok, table.Lookup("kick Bob", AccountSecurity.GameMaster).Result);
    }

    [Fact]
    public void Groups_ResolveToAnUnknownSubcommand_UnlessAHandlerServesTheArguments()
    {
        var group = new ChatCommand("server", AccountSecurity.Player, "Server.", Children: [Leaf("info"), Leaf("motd")]);
        var withHandler = new ChatCommand("gm", AccountSecurity.Player, "Syntax: .gm", (_, _) => true, [Leaf("chat")]);
        CommandTable table = ChatCommands.Build([group, withHandler], [], [], new GmOptions());

        CommandLookup nothing = table.Lookup("server", AccountSecurity.Player);
        Assert.Equal(CommandLookupResult.UnknownSubcommand, nothing.Result);
        Assert.Equal("server", nothing.Command?.Name);
        Assert.Equal(CommandLookupResult.UnknownSubcommand, table.Lookup("server nonsense", AccountSecurity.Player).Result);

        CommandLookup child = table.Lookup("server mot", AccountSecurity.Player);
        Assert.Equal("motd", child.Command?.Name);
        Assert.Equal("server motd", child.Path);

        CommandLookup args = table.Lookup("gm on", AccountSecurity.Player);   // not a sub-command: the handler gets "on"
        Assert.Equal(CommandLookupResult.Ok, args.Result);
        Assert.Equal("gm", args.Command?.Name);
        Assert.Equal("on", args.Rest);
    }

    // First-prefix resolution makes an entry that comes after a longer sibling it prefixes unreachable, even by its exact
    // name: .debug gccollect (Gm.Extra, discovered before Ops.Diagnostics) once took every .debug gc that way.
    [Fact]
    public void EveryShippedCommand_ResolvesByItsOwnPath()
    {
        CommandTable table = ChatCommands.CreateTable();
        var shadowed = new List<string>();
        Collect(table, table.Roots, string.Empty, shadowed);
        Assert.Empty(shadowed);
    }

    private static void Collect(CommandTable table, IReadOnlyList<ChatCommand> level, string prefix, List<string> shadowed)
    {
        foreach (ChatCommand command in level)
        {
            string path = prefix.Length == 0 ? command.Name : prefix + " " + command.Name;
            CommandLookup found = table.Lookup(path, AccountSecurity.Administrator);
            if (!ReferenceEquals(found.Command, command))
            {
                shadowed.Add($".{path} resolves to .{found.Path}");
            }

            Collect(table, command.SubCommands, path, shadowed);
        }
    }

    [Fact]
    public void RetailLevels_AreAppliedToTheExistingCommands()
    {
        CommandTable table = ChatCommands.CreateTable();
        (string Path, int Level)[] expected =
        [
            ("saveall", 6), ("announce", 4), ("notify", 4), ("gm", 2), ("gm chat", 1), ("kick", 2), ("tele", 2), ("go xyz", 2),
            ("learn", 5), ("unlearn", 3), ("cast", 5), ("unaura", 3), ("cooldown", 3), ("modify money", 4),
            ("instance listbinds", 3), ("instance unbind", 3), ("instance stats", 4),
            ("guild create", 3), ("guild delete", 4), ("guild invite", 3), ("guild uninvite", 3), ("guild rank", 3),
            ("save", 0), ("help", 0), ("commands", 0), ("gps", 1), ("server info", 0), ("server motd", 0),
        ];
        foreach ((string path, int level) in expected)
        {
            ChatCommand? command = table.Find(path);
            Assert.True(command is not null, path);
            Assert.True(level == command.RequiredLevel(table.Gm), $"{path}: expected {level}, got {command.RequiredLevel(table.Gm)}");
        }
    }

    [Fact]
    public void RetailLevels_CanBeSwitchedOff()
    {
        CommandTable table = ChatCommands.CreateTable(new GmOptions { RetailLevels = false });
        Assert.Equal(3, table.Find("kick")!.RequiredLevel(table.Gm));   // the legacy GameMaster declaration, not retail TICKETMASTER (2)
        Assert.Equal(1, table.Find("saveall")!.RequiredLevel(table.Gm));   // legacy Moderator, not retail ADMINISTRATOR (6)
    }

    [Fact]
    public async Task UnavailableUnknownAndSubcommandReplies_UseTheRetailTexts()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("RTPLAIN", "Rtplain");
        await player.CollectAsync();

        Assert.Equal("This command is not available to you.", await Say(player, ".kick Someone"));
        Assert.Equal("There is no such command", await Say(player, ".nonsense"));

        // UNKNOWN_SUBCOMMAND: the message, then the packed sub-command list of the group (Chat.cpp:1958, 2113-2163 order).
        await player.SendChatAsync(ChatType.Say, Language.Common, ".server nonsense");
        Assert.Equal("There is no such subcommand", (await player.ReadChatAsync()).Text);
        Assert.Equal("Command server subcommands (page 1/1)", (await player.ReadChatAsync()).Text);   // the group's path (vmangos prints no name here)
        Assert.Equal("info, motd", (await player.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task CommandsAndHelp_ListTheAvailableRootsInRetailOrder()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("RTLIST", "Rtlist");
        await player.CollectAsync();

        await player.SendChatAsync(ChatType.Say, Language.Common, ".commands");
        // A packed overview in retail table order ("modify" is listed for its player-level "xprate", vmangos Chat.cpp:630).
        Assert.Equal("Commands (page 1/1)", (await player.ReadChatAsync()).Text);
        Assert.Equal("modify (1), server (2), commands, dismount, help, save, unstuck", (await player.ReadChatAsync()).Text);
        Assert.StartsWith("Search: .commands <text>", (await player.ReadChatAsync()).Text);

        // ".help" alone is the same overview.
        await player.SendChatAsync(ChatType.Say, Language.Common, ".help");
        Assert.Equal("Commands (page 1/1)", (await player.ReadChatAsync()).Text);
        await player.CollectAsync();

        // ".help nosuch" -> the no-such-command text; ".help kick" shows even a command the caller may not run.
        Assert.Equal("There is no such command", await Say(player, ".help nosuchcommand"));
        Assert.StartsWith("Syntax: .kick", await Say(player, ".help kick"));
    }

    [Fact]
    public async Task AFailedHandler_PrintsTheCommandHelp_OrTheGenericSyntaxLine()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("RTSYN", "Rtsyn", AccountSecurity.Administrator);
        await admin.CollectAsync();

        // Chat.cpp:1941-1945: the help text replaces "Incorrect syntax."; sub-commands follow (ShowHelpForSubCommands).
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".modify money lots");
        Assert.StartsWith("Syntax: .modify money", (await admin.ReadChatAsync()).Text);
    }

    private static async Task<string> Say(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }
}

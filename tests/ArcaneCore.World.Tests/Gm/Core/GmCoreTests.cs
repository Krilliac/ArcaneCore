using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>
/// The additive GM-command plumbing: retail command levels with a configurable
/// AccountSecurity map (vmangos AccountTypes, D:\refs\vmangos\src\shared\Common.h:136-146),
/// extension merging into shared roots, the GM command log and the HasLowerSecurity port
/// (Chat.cpp:1521-1563). None of it changes how a command declared with only an
/// <see cref="AccountSecurity"/> behaves.
/// </summary>
public sealed class GmCoreTests
{
    private static readonly CommandHandler Ok = (_, _) => true;

    [Fact]
    public void DefaultSecurityMap_MapsTheFourStoredLevelsOntoTheRetailScale()
    {
        var options = new GmOptions();
        Assert.Equal(0, options.LevelOf(AccountSecurity.Player));
        Assert.Equal(1, options.LevelOf(AccountSecurity.Moderator));
        Assert.Equal(3, options.LevelOf(AccountSecurity.GameMaster));
        Assert.Equal(6, options.LevelOf(AccountSecurity.Administrator));
    }

    [Fact]
    public void LegacyDeclaredCommands_NeedTheRetailLevelOfTheirAccountSecurity()
    {
        var options = new GmOptions();
        Assert.Equal(3, new ChatCommand("x", AccountSecurity.GameMaster, "h", Ok).RequiredLevel(options));
        Assert.Equal(6, new ChatCommand("x", AccountSecurity.Administrator, "h", Ok).RequiredLevel(options));
        Assert.Equal(0, new ChatCommand("x", AccountSecurity.Player, "h", Ok).RequiredLevel(options));
    }

    [Fact]
    public void RetailLevel_GatesAgainstTheMappedLevel()
    {
        // BASIC_ADMIN (4): a stored GameMaster maps to 3 and is refused, an Administrator maps to 6.
        CommandTable table = ChatCommands.Build(
            [new ChatCommand("announce", AccountSecurity.Moderator, "h", Ok, RetailLevel: 4)], [], [], new GmOptions());
        Assert.Null(table.Resolve("announce", AccountSecurity.Moderator));
        Assert.Null(table.Resolve("announce", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("announce", AccountSecurity.Administrator));
    }

    [Fact]
    public void RetailLevel_RespectsAnOperatorOverriddenMap()
    {
        var options = new GmOptions();
        options.SecurityMap[AccountSecurity.GameMaster] = 4;
        CommandTable table = ChatCommands.Build(
            [new ChatCommand("announce", AccountSecurity.Moderator, "h", Ok, RetailLevel: 4)], [], [], options);
        Assert.NotNull(table.Resolve("announce", AccountSecurity.GameMaster));
    }

    [Fact]
    public void OptionsBind_FromTheWorldGmCommandsSection()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:GmCommands:LogCommands"] = "false",
            ["World:GmCommands:LowerSecurity"] = "false",
            ["World:GmCommands:SecurityMap:GameMaster"] = "2",
        }).Build();

        GmOptions options = GmOptions.Bind(configuration);
        Assert.False(options.LogCommands);
        Assert.False(options.LowerSecurity);
        Assert.Equal(2, options.LevelOf(AccountSecurity.GameMaster));
        Assert.Equal(6, options.LevelOf(AccountSecurity.Administrator)); // untouched keys keep the retail default
    }

    [Fact]
    public void TheShippedAppSettings_SpellOutTheDefaults()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "ArcaneCore.World", "appsettings.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(dir, "src", "ArcaneCore.World", "appsettings.json")).Build();

        GmOptions shipped = GmOptions.Bind(configuration);
        var defaults = new GmOptions();
        Assert.Equal(defaults.SecurityMap.OrderBy(p => p.Key), shipped.SecurityMap.OrderBy(p => p.Key));
        Assert.Equal(
            (defaults.HideUnavailable, defaults.ExactNameFirst, defaults.RetailLevels, defaults.LogCommands, defaults.LowerSecurity, defaults.LookupMaxResults),
            (shipped.HideUnavailable, shipped.ExactNameFirst, shipped.RetailLevels, shipped.LogCommands, shipped.LowerSecurity, shipped.LookupMaxResults));
    }

    [Fact]
    public void Defaults_AreLoggingOnAndStrictLowerSecurity()
    {
        var options = new GmOptions();
        Assert.True(options.LogCommands);
        Assert.True(options.LowerSecurity);
    }

    [Fact]
    public void Extensions_AddChildrenUnderAnExistingRoot()
    {
        var root = new ChatCommand("modify", AccountSecurity.Moderator, "h", Children: [new ChatCommand("money", AccountSecurity.Moderator, "h", Ok)]);
        var extension = new TestExtension("modify", new ChatCommand("hp", AccountSecurity.GameMaster, "h", Ok));

        CommandTable table = ChatCommands.Build([root], [], [extension], new GmOptions());
        Assert.NotNull(table.Resolve("modify money", AccountSecurity.Administrator));
        Assert.NotNull(table.Resolve("modify hp", AccountSecurity.Administrator));
        Assert.Single(table.Roots);
    }

    [Fact]
    public void Extensions_ReachNestedPaths()
    {
        var root = new ChatCommand("lookup", AccountSecurity.Moderator, "h", Children:
            [new ChatCommand("player", AccountSecurity.GameMaster, "h", Children: [new ChatCommand("name", AccountSecurity.GameMaster, "h", Ok)])]);
        var extension = new TestExtension("lookup player", new ChatCommand("account", AccountSecurity.GameMaster, "h", Ok));

        CommandTable table = ChatCommands.Build([root], [], [extension], new GmOptions());
        Assert.NotNull(table.Resolve("lookup player account", AccountSecurity.Administrator));
        Assert.NotNull(table.Resolve("lookup player name", AccountSecurity.Administrator));
    }

    [Fact]
    public void Extensions_ForAMissingRoot_FailAtStartup()
    {
        var extension = new TestExtension("nobody", new ChatCommand("x", AccountSecurity.Player, "h", Ok));
        var ex = Assert.Throws<InvalidOperationException>(() => ChatCommands.Build([], [], [extension], new GmOptions()));
        Assert.Contains("nobody", ex.Message);
    }

    [Fact]
    public void Extensions_WithADuplicateChildName_FailAtStartup()
    {
        var root = new ChatCommand("modify", AccountSecurity.Moderator, "h", Children: [new ChatCommand("money", AccountSecurity.Moderator, "h", Ok)]);
        var extension = new TestExtension("modify", new ChatCommand("Money", AccountSecurity.GameMaster, "h", Ok));
        var ex = Assert.Throws<InvalidOperationException>(() => ChatCommands.Build([root], [], [extension], new GmOptions()));
        Assert.Contains("money", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoGroupsWithTheSameRoot_StillFailAtStartup()
    {
        var a = new TestGroup(new ChatCommand("tele", AccountSecurity.Moderator, "h", Ok));
        var b = new TestGroup(new ChatCommand("tele", AccountSecurity.Moderator, "h", Ok));
        Assert.Throws<InvalidOperationException>(() => ChatCommands.Build([], [a, b], [], new GmOptions()));
    }

    [Fact]
    public void TheDiscoveredTable_BuildsWithTheAssemblysExtensions()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Equal(table.Roots.Count, table.Roots.Select(c => c.Name.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void HasLowerSecurity_PortOfTheVmangosRule()
    {
        var strict = new GmOptions { LowerSecurity = true };
        var lax = new GmOptions { LowerSecurity = false };

        // A lower or equal account never trips the check.
        Assert.False(GmSecurity.HasLowerSecurity(AccountSecurity.Administrator, AccountSecurity.GameMaster, strong: false, strict));
        // A higher target does when LowerSecurity is on ...
        Assert.True(GmSecurity.HasLowerSecurity(AccountSecurity.GameMaster, AccountSecurity.Administrator, strong: false, strict));
        // ... and passes when it is off (Chat.cpp:1546-1547: staff callers on non-strong checks).
        Assert.False(GmSecurity.HasLowerSecurity(AccountSecurity.GameMaster, AccountSecurity.Administrator, strong: false, lax));
        // A strong check (mute/unmute) refuses an equal level too and ignores the option.
        Assert.True(GmSecurity.HasLowerSecurity(AccountSecurity.GameMaster, AccountSecurity.GameMaster, strong: true, lax));
        Assert.False(GmSecurity.HasLowerSecurity(AccountSecurity.Administrator, AccountSecurity.GameMaster, strong: true, lax));
        // A plain player caller is always bound by the comparison.
        Assert.True(GmSecurity.HasLowerSecurity(AccountSecurity.Player, AccountSecurity.Moderator, strong: false, lax));
    }

    [Fact]
    public void CommandLog_NamesAccountPositionSelectionAndCommand()
    {
        string line = GmCommandLog.Describe("kick Victim", accountId: 7, playerName: "Gm", mapId: 1, x: 10.5f, y: -3f, z: 2f, selection: "Victim");
        // Chat.cpp:1912 "Command: %s [Player: %s (... Account: %u) X: %f Y: %f Z: %f Map: %u Selected: %s]"
        Assert.Equal("Command: kick Victim [Player: Gm (Account: 7) X: 10.5 Y: -3 Z: 2 Map: 1 Selected: Victim]", line);
    }

    [Fact]
    public void CommandLog_OnlyLevelAbovePlayerIsLogged()
    {
        var options = new GmOptions();
        var gm = new ChatCommand("kick", AccountSecurity.GameMaster, "h", Ok);
        var player = new ChatCommand("save", AccountSecurity.Player, "h", Ok);
        var retailPlayer = new ChatCommand("gm ingame", AccountSecurity.Moderator, "h", Ok, RetailLevel: 0);
        Assert.True(GmCommandLog.ShouldLog(gm, options));
        Assert.False(GmCommandLog.ShouldLog(player, options));
        Assert.False(GmCommandLog.ShouldLog(retailPlayer, options));
        Assert.False(GmCommandLog.ShouldLog(gm, new GmOptions { LogCommands = false }));
    }

    private sealed class TestExtension(string path, params ChatCommand[] children) : ICommandExtension
    {
        public string Path { get; } = path;

        public IReadOnlyList<ChatCommand> Children { get; } = children;
    }

    private sealed class TestGroup(params ChatCommand[] commands) : ICommandGroup
    {
        public IReadOnlyList<ChatCommand> Commands { get; } = commands;
    }
}

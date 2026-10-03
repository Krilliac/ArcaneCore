using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>docs/reference/gm-commands.md is generated from the live command table; these tests check the generator and the facts the page states.</summary>
public sealed class CommandReferenceTests
{
    private static readonly GmOptions Defaults = new();

    [Fact]
    public void ReloadRoot_IsListedOnlyWhenHotReloadCommandsIsOn_AndIsMarkedDevelopmentOnly()
    {
        IReadOnlyList<CommandNode> off = CommandReferenceRenderer.Nodes(reloadEnabled: false);
        IReadOnlyList<CommandNode> on = CommandReferenceRenderer.Nodes(reloadEnabled: true);
        Assert.DoesNotContain(off, n => n.Path.StartsWith("reload", StringComparison.Ordinal));
        Assert.DoesNotContain(off, n => n.Path.StartsWith(HotCodeCommands.RootName, StringComparison.Ordinal));
        CommandNode status = Assert.Single(on, n => n.Path == "reload status");
        Assert.True(status.DevOnly);
        Assert.Contains(on, n => n.Path == HotCodeCommands.RootName && n.DevOnly && n.RuntimeNote is not null);
        Assert.Contains(on, n => n.Path == HotModuleCommands.RootName && n.DevOnly && n.RuntimeNote is not null);
        Assert.Contains(on, n => n.Path.StartsWith(HotModuleCommands.RootName + " ", StringComparison.Ordinal) && n.HasHandler);
    }

    [Fact]
    public void EveryNodeWithAHandler_HasHelpText_AndNoPathIsListedTwice()
    {
        IReadOnlyList<CommandNode> nodes = CommandReferenceRenderer.Nodes(reloadEnabled: true);
        Assert.True(nodes.Count > 40, "the table must be enumerated; found " + nodes.Count);
        Assert.True(nodes.Where(n => n.HasHandler && string.IsNullOrWhiteSpace(n.Help)).Count() == 0,
            "commands with a handler but no help: " + string.Join(", ", nodes.Where(n => n.HasHandler && string.IsNullOrWhiteSpace(n.Help)).Select(n => n.Path)));
        string[] duplicates = [.. nodes.GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)];
        Assert.Empty(duplicates);
    }

    [Fact]
    public void ReachableCommands_FollowTheRetailLevelCompare_ForEveryStoredAccount()
    {
        IReadOnlyList<CommandNode> nodes = CommandReferenceRenderer.Nodes(reloadEnabled: true);
        string[] Reachable(AccountSecurity s) => [.. nodes.Where(n => n.HasHandler && Defaults.LevelOf(s) >= n.RequiredLevel).Select(n => n.Path)];

        // The default map is 0, 1, 3, 6: a player reaches only level-0 commands, an administrator everything.
        Assert.Equal(nodes.Where(n => n.HasHandler).Select(n => n.Path).Order(), Reachable(AccountSecurity.Administrator).Order());
        Assert.All(nodes.Where(n => n.HasHandler && n.RequiredLevel == 0), n => Assert.Contains(n.Path, Reachable(AccountSecurity.Player)));
        Assert.All(Reachable(AccountSecurity.Player), p => Assert.Equal(0, nodes.Single(n => n.Path == p).RequiredLevel));

        // Retail levels 4 and 5 are between the stored levels 3 and 6, so a stored GameMaster does not reach them.
        CommandNode announce = nodes.Single(n => n.Path == "announce");
        Assert.Equal(4, announce.RequiredLevel);
        Assert.DoesNotContain("announce", Reachable(AccountSecurity.GameMaster));
        Assert.Equal(AccountSecurity.Administrator, CommandReferenceRenderer.MinimumAccount(4, Defaults));
        Assert.Equal(AccountSecurity.GameMaster, CommandReferenceRenderer.MinimumAccount(2, Defaults));
        Assert.Equal(AccountSecurity.Administrator, CommandReferenceRenderer.MinimumAccount(5, Defaults));
        Assert.Null(CommandReferenceRenderer.MinimumAccount(7, Defaults));
    }

    [Fact]
    public void EveryRetailLevelRecorded_IsTheLevelOfItsNode_WithTheirSource()
    {
        IReadOnlyList<CommandNode> nodes = CommandReferenceRenderer.Nodes(reloadEnabled: true);
        int checkedNodes = 0;
        foreach (CommandNode node in nodes)
        {
            if (RetailCommandLevels.Of(node.Path) is { } level)
            {
                Assert.Equal(level, node.RequiredLevel);
                Assert.StartsWith("retail table", node.LevelSource, StringComparison.Ordinal);
                checkedNodes++;
            }
        }

        Assert.True(checkedNodes >= 15, "RetailCommandLevels should cover many nodes of the table; matched " + checkedNodes);
    }

    [Fact]
    public void RemappedSecurityMap_ChangesTheMinimumAccount()
    {
        var remapped = new GmOptions();
        remapped.SecurityMap[AccountSecurity.GameMaster] = 4;
        Assert.Equal(AccountSecurity.GameMaster, CommandReferenceRenderer.MinimumAccount(4, remapped));
        Assert.Equal(AccountSecurity.Administrator, CommandReferenceRenderer.MinimumAccount(5, remapped));
    }

    [Fact]
    public void RuntimeRootNames_AreTheOnesTheCommandSourcesDeclare()
    {
        Assert.Contains("RootName = \"hotcode\"", RepoRoot.ReadText("src/ArcaneCore.World/HotCode/HotCodeCommands.cs"), StringComparison.Ordinal);
        Assert.Contains("RootName = \"hotmodule\"", RepoRoot.ReadText("src/ArcaneCore.World/HotCode/Modules/HotModuleCommands.cs"), StringComparison.Ordinal);
        Assert.Contains("TryAdd", RepoRoot.ReadText("src/ArcaneCore.World/HotCode/HotCodeServiceCollectionExtensions.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void GmCommandReference_MatchesTheCommittedPage() =>
        DocsGolden.Verify("docs/reference/gm-commands.md", CommandReferenceRenderer.Render(CommandReferenceRenderer.Nodes(reloadEnabled: true), Defaults));
}

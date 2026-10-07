using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests;

public sealed class PlayerbotCommandsTests
{
    [Fact]
    public void Options_DefaultsAreDisabledAndResourceBounded()
    {
        var options = new PlayerbotOptions();
        Assert.False(options.Enabled);
        Assert.False(options.RestoreOnStartup);
        Assert.Equal(8, options.MaxBots);
        Assert.Equal(500, options.ThinkIntervalMs);
        Assert.Equal(4, options.MaxActionsPerTick);
        Assert.Equal(128, options.MaxPathPoints);
        Assert.Equal(2000f, options.MaxRouteYards);
        Assert.Equal(7f, options.MoveSpeed);
        Assert.Equal<uint[]>([0, 1], options.AllowedMaps);
        Assert.False(options.AllowLocalLlm);
        options.Validate();
    }

    [Fact]
    public void OptionsRejectInvalidBoundsAndDuplicateMaps()
    {
        var options = new PlayerbotOptions { MaxBots = 65 };
        Assert.Throws<InvalidOperationException>(() => options.Validate());
        options = new PlayerbotOptions { AllowedMaps = [0, 0] };
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void CommandTreeSeparatesMutationFromStatusRanks()
    {
        PlayerbotCommands group = new();
        ChatCommand root = Assert.Single(group.Commands);
        Assert.Equal(AccountSecurity.GameMaster, root.Security);
        Assert.Equal(AccountSecurity.Administrator, root.SubCommands.Single(c => c.Name == "create").Security);
        Assert.Equal(AccountSecurity.Administrator, root.SubCommands.Single(c => c.Name == "start").Security);
        Assert.Equal(AccountSecurity.Administrator, root.SubCommands.Single(c => c.Name == "stop").Security);
        Assert.Equal(AccountSecurity.GameMaster, root.SubCommands.Single(c => c.Name == "status").Security);
        Assert.Equal(AccountSecurity.GameMaster, root.SubCommands.Single(c => c.Name == "list").Security);
        Assert.Equal(AccountSecurity.GameMaster, root.SubCommands.Single(c => c.Name == "inspect").Security);
    }
}

using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Commands;

/// <summary>The production registration of the Console-only command paths (design 1.7): the action channel and the remote executor refuse them for every remote context.</summary>
public sealed class ConsoleOnlyRegistryTests
{
    [Fact]
    public async Task ConsoleOnlyRegistry_HasTheProductionPathsAfterStart()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        _ = host.WorldServices.GetRequiredService<CommandTable>(); // the daemon builds the table at start-up; the paths are registered with it

        Assert.True(ConsoleOnlyCommands.IsConsoleOnly("server remote token rotate"));
        Assert.True(ConsoleOnlyCommands.IsConsoleOnly(".Server Remote Token Rotate"), "paths are case-insensitive");
        Assert.False(ConsoleOnlyCommands.IsConsoleOnly("server remote token"), "a path covers the commands below it, not the group above");
        Assert.False(ConsoleOnlyCommands.IsConsoleOnly("server info"));
    }

    [Fact]
    public void Unregister_RemovesOnlyThePathsGiven()
    {
        ConsoleOnlyCommands.Register("zunregister one", "zunregister two");
        try
        {
            ConsoleOnlyCommands.Unregister("zunregister one", "znever registered");

            Assert.False(ConsoleOnlyCommands.IsConsoleOnly("zunregister one"));
            Assert.True(ConsoleOnlyCommands.IsConsoleOnly("zunregister two"));
        }
        finally
        {
            ConsoleOnlyCommands.Unregister("zunregister one", "zunregister two");
        }
    }
}

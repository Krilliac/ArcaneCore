using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The <c>.playerbot</c> commands that reply from background work hold their <see cref="CommandContext"/> open
/// (<see cref="CommandContext.Defer"/>): remote administration and the control pipe report the command done at
/// <see cref="CommandContext.WhenSettled"/>, so a command that replied later would be closed with "#done ok" before its result line.
/// </summary>
public sealed class PlayerbotCommandSettleTests
{
    private const string Admin = "Settleadmin";

    /// <summary>
    /// Run the handler of <c>.playerbot &lt;sub&gt; &lt;args&gt;</c> on the world thread as a console command, complete it as the executor does, and
    /// return whether it was already settled when the handler returned and the reply lines present at the moment it settled.
    /// </summary>
    private static async Task<(bool SettledAtReturn, string[] AtSettle)> RunAsync(PopulationTestHost world, string sub, string args)
    {
        var output = new ConcurrentQueue<string>();
        ChatCommand command = new PlayerbotCommands().Commands.Single().SubCommands.Single(c => c.Name == sub);
        CommandContext? context = null;
        bool settledAtReturn = await world.Host.OnWorldAsync(() =>
        {
            Player any = world.Host.World.FindOnlinePlayer(Admin)!;
            context = CommandContext.ForConsole((WorldSession)any.Session, ChatCommands.CreateTable(), output.Enqueue);
            Assert.True(command.Handler!(context, args));
            context.Complete();
            return context.WhenSettled.IsCompleted;
        });
        await context!.WhenSettled.WaitAsync(TimeSpan.FromSeconds(20));
        return (settledAtReturn, [.. output]);
    }

    private static Task<PopulationTestHost> StartAsync() => PopulationTestHost.StartAsync(o => { o.Population.Enabled = false; o.MaxBots = 4; });

    [Fact]
    public async Task Inspect_IsNotSettled_UntilTheBackgroundReplyIsOut()
    {
        await using PopulationTestHost world = await StartAsync();
        await using WorldTestClient admin = await world.Host.EnterWorldAsync(Admin, Admin, AccountSecurity.Administrator);
        (bool settledAtReturn, string[] atSettle) = await RunAsync(world, "inspect", "Nobotnamed");

        // The inspection runs on a later world tick, so the handler cannot have replied by the time it returns.
        Assert.False(settledAtReturn);
        Assert.Contains("Playerbot is not running or was not found.", atSettle);
    }

    [Fact]
    public async Task SplitBrainClear_IsNotSettled_UntilTheBackgroundReplyIsOut()
    {
        await using PopulationTestHost world = await StartAsync();
        await using WorldTestClient admin = await world.Host.EnterWorldAsync(Admin, Admin, AccountSecurity.Administrator);
        await world.Bots.ForceSplitBrainForTests();
        (_, string[] atSettle) = await RunAsync(world, "splitbrain", "clear");

        Assert.Contains(atSettle, line => line.StartsWith("Split brain cleared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_IsNotSettled_UntilTheBackgroundReplyIsOut()
    {
        await using PopulationTestHost world = await StartAsync();
        await using WorldTestClient admin = await world.Host.EnterWorldAsync(Admin, Admin, AccountSecurity.Administrator);
        (_, string[] atSettle) = await RunAsync(world, "create", "Settlebot 1 1");

        Assert.Contains(atSettle, line => line.StartsWith("Playerbot ok:", StringComparison.Ordinal) || line.StartsWith("Playerbot failed:", StringComparison.Ordinal));
    }
}

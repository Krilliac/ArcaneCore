using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Population;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary><c>.playerbot splitbrain [clear]</c> and the status warning of a world that stopped writing playerbot rows because another world changed them.</summary>
public sealed class PlayerbotSplitBrainCommandTests
{
    [Fact]
    public void TheCommandIsForAdministrators_AndStatusStaysForGameMasters()
    {
        ChatCommand root = Assert.Single(new PlayerbotCommands().Commands);
        Assert.Equal(AccountSecurity.Administrator, root.SubCommands.Single(c => c.Name == "splitbrain").Security);
        Assert.Equal(AccountSecurity.GameMaster, root.SubCommands.Single(c => c.Name == "status").Security);
    }

    [Fact]
    public async Task ShowStatusAndClear_AWorldThatStoppedWriting_ResumesItsWritesAfterTheClear()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => { o.Population.Enabled = false; o.MaxBots = 4; });
        await using WorldTestClient admin = await world.Host.EnterWorldAsync("SPLITADMIN", "Splitadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        Assert.StartsWith("No split brain", await SayAsync(admin, ".playerbot splitbrain", l => l.Contains("split brain", StringComparison.OrdinalIgnoreCase)));

        await world.Bots.ForceSplitBrainForTests();
        Assert.StartsWith("Split brain:", await SayAsync(admin, ".playerbot splitbrain", l => l.StartsWith("Split brain:", StringComparison.Ordinal)));
        await SayAsync(admin, ".playerbot status", l => l.StartsWith("WARNING: split brain", StringComparison.Ordinal));
        Assert.Equal("playerbot-split-brain", (await world.Bots.CreateAsync("Refusedbot", 1, 1)).Code);
        Assert.True(world.Bots.SplitBrain);

        await SayAsync(admin, ".playerbot splitbrain clear", l => l.StartsWith("Split brain clear accepted", StringComparison.Ordinal));
        await ReadLineAsync(admin, l => l.StartsWith("Split brain cleared", StringComparison.Ordinal));

        Assert.False(world.Bots.SplitBrain);
        int rows = world.Owners.RowsWritten;
        PlayerbotOperationResult created = await world.Bots.CreateAsync("Resumedbot", 1, 1);
        Assert.True(created.Success, created.Code);
        Assert.True((await world.Bots.StartAsync(created.BotId!.Value.ToString())).Success);
        Assert.True(world.Owners.RowsWritten > rows); // writes resume
    }

    private static async Task<string> SayAsync(WorldTestClient admin, string command, Func<string, bool> reply)
    {
        await admin.SendChatAsync(ChatType.Say, Language.Common, command);
        return await ReadLineAsync(admin, reply);
    }

    private static async Task<string> ReadLineAsync(WorldTestClient admin, Func<string, bool> reply)
    {
        var seen = new List<string>();
        try
        {
            while (true)
            {
                string line = (await admin.ReadChatAsync()).Text;
                seen.Add(line);
                if (reply(line)) return line;
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            throw new InvalidOperationException("no matching reply; saw: " + string.Join(" | ", seen), ex);
        }
    }
}

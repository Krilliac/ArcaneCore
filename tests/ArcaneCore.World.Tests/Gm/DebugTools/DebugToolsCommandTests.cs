using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.DebugTools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.DebugTools;

public sealed class DebugToolsCommandTests
{
    private static readonly string[] Names = ["tick", "slowticks", "features", "maps", "latency", "sqlite", "threads", "mt"];

    [Fact]
    public void DebugToolsAreGameMasterCommandsUnderDebug()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string name in Names)
        {
            Assert.Null(table.Resolve("debug " + name, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve("debug " + name, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task EveryReadoutAnswersOnARunningWorld()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("DBGTOOLS", "Dbgtools", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        await Task.Delay(300); // a few real ticks, so the tick and map readouts have samples

        string[] tick = await RunAsync(gm, ".debug tick");
        Assert.Contains(tick, l => l.StartsWith("World tick: interval", StringComparison.Ordinal));
        Assert.Contains(tick, l => l.StartsWith("World thread now:", StringComparison.Ordinal));

        Assert.NotEmpty(await RunAsync(gm, ".debug slowticks"));
        Assert.NotEmpty(await RunAsync(gm, ".debug features 5"));

        string[] maps = await RunAsync(gm, ".debug maps");
        Assert.Contains(maps, l => l.Contains("map(s) running", StringComparison.Ordinal));
        Assert.Contains(maps, l => l.Contains("map 0/0", StringComparison.Ordinal));
        Assert.Contains("Map update maxima cleared.", await RunAsync(gm, ".debug maps reset"));

        Assert.NotEmpty(await RunAsync(gm, ".debug latency"));
        Assert.NotEmpty(await RunAsync(gm, ".debug latency packet. 3"));
        Assert.NotEmpty(await RunAsync(gm, ".debug sqlite"));

        string[] threads = await RunAsync(gm, ".debug threads");
        Assert.Contains(threads, l => l.StartsWith("Thread pool:", StringComparison.Ordinal));

        string[] mt = await RunAsync(gm, ".debug mt");
        Assert.Contains(mt, l => l.StartsWith("Map updates:", StringComparison.Ordinal));
        Assert.Contains(mt, l => l.StartsWith("Path jobs:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BadArgumentsShowTheSyntax()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("DBGBAD", "Dbgbad", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        foreach (string command in new[] { ".debug tick now", ".debug slowticks 0", ".debug maps nonsense", ".debug features x y", ".debug sqlite x" })
        {
            string[] lines = await RunAsync(gm, command);
            Assert.Contains(lines, l => l.StartsWith("Syntax:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MapCostTrackerKeepsMeanLastAndMax()
    {
        var tracker = new MapCostTracker();
        Assert.Empty(tracker.Snapshot());
        // The tracker never dereferences the map beyond using it as a key, so the test needs no world.
        using var world = new ArcaneCore.Game.Maps.WorldRuntime(new ArcaneCore.Game.Maps.WorldRuntimeOptions(), new NullSaves(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);
        ArcaneCore.Game.Maps.Map map = world.GetMap(0);
        tracker.OnMapUpdated(map, 1000);
        tracker.OnMapUpdated(map, 5000);
        MapCostSample sample = Assert.Single(tracker.Snapshot());
        Assert.Equal(2, sample.Updates);
        Assert.Equal(5000, sample.LastMicros);
        Assert.Equal(5000, sample.MaxMicros);
        Assert.InRange(sample.MeanMicros, 1000, 5000);
        tracker.ResetMaxima();
        Assert.Equal(0, Assert.Single(tracker.Snapshot()).MaxMicros);
    }

    private sealed class NullSaves : ArcaneCore.Game.Maps.ICharacterSaveQueue
    {
        public void Enqueue(ArcaneCore.Kernel.Characters.CharacterState state) { }
    }

    private static async Task<string[]> RunAsync(WorldTestClient gm, string command)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        return await gm.CollectChatLinesAsync();
    }
}

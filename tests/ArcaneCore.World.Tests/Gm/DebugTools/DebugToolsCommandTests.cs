using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
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
    public async Task SlowTicks_ShowsAnEntryOlderThanSixtySeconds_WhenTheWindowReachesIt_AndExplainsTheRing()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("DBGSLOW", "Dbgslow", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        // A hitch recorded 4 minutes ago on a quiet server: the ring never evicts by age, so a 300 s window must still list it.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long fakeNow = now - (4 * 60 * System.Diagnostics.Stopwatch.Frequency);
        await host.OnWorldAsync(() =>
        {
            host.World.Stats.Clock = () => fakeNow;
            host.World.Stats.RecordSlowTick(default, new string?[] { "Old" }, new long[] { 1 }, 1, 987_000);
            host.World.Stats.Clock = () => now;
        });

        string[] wide = await RunAsync(gm, ".debug slowticks 300");
        Assert.Contains(wide, l => l.Contains("1 slow tick(s) within the last 300 s", StringComparison.Ordinal) && l.Contains("replaced first", StringComparison.Ordinal));
        Assert.Contains(wide, l => l.Contains("987 ms", StringComparison.Ordinal) && l.Contains("240 s ago", StringComparison.Ordinal));
        Assert.DoesNotContain(wide, l => l.Contains("requested", StringComparison.Ordinal));

        string[] narrow = await RunAsync(gm, ".debug slowticks 60");
        Assert.DoesNotContain(narrow, l => l.Contains("987 ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowTicks_WarnsWhenTheRingIsFull()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("DBGSLOW2", "Dbgslowtwo", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            for (int i = 0; i < ArcaneCore.Game.Maps.TickStats.SlowTickCapacity; i++)
            {
                host.World.Stats.RecordSlowTick(default, new string?[] { "F" }, new long[] { 1 }, 1, 500_000 + i);
            }
        });

        string[] lines = await RunAsync(gm, ".debug slowticks");
        Assert.Contains(lines, l => l.Contains("the ring is full (8)", StringComparison.Ordinal));
    }

    [Fact]
    public void MapCostTrackerKeepsMeanLastAndMax()
    {
        var tracker = new MapCostTracker();
        Assert.Empty(tracker.Snapshot());
        // The tracker never dereferences the map beyond using it as a key, so the test needs no world.
        using var world = new ArcaneCore.Game.Maps.WorldRuntime(new ArcaneCore.Game.Maps.WorldRuntimeOptions(), new NullSaveQueue(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);
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

    private static async Task<string[]> RunAsync(WorldTestClient gm, string command)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        return await gm.CollectChatLinesAsync();
    }
}

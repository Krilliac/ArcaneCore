using System.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary><c>.playerbot population rush</c>: the pacing, the brake, the caps and the commands.</summary>
public sealed class PopulationRushTests
{
    [Fact]
    public void ThePacer_HandsOutTheRequestedRate_AndNeverMoreThanTwoSecondsOfBurst()
    {
        var pacer = new PopulationRushPacer();
        int total = 0;
        for (int i = 0; i < 200; i++) // ten seconds of 50 ms updates at 5 per second
        {
            pacer.Add(50, 5);
            total += pacer.Take(int.MaxValue);
        }

        Assert.InRange(total, 49, 50);

        pacer.Add(60_000, 5); // a stall: the burst is capped at two seconds' worth
        Assert.Equal(10, pacer.Take(int.MaxValue));
        Assert.Equal(0, pacer.Take(int.MaxValue));
    }

    [Fact]
    public void TheBrake_PausesAfterNConsecutiveWindowsOverTheCeiling_AndResumesAfterNUnderTheResumeLine()
    {
        var options = new PlayerbotPopulationOptions { RushBrakeP95Ms = 500, RushBrakeWindows = 3, ResumeBelowPct = 80, WindowTicks = 100 };
        var brake = new PopulationRushBrake();
        long total = 0;
        PopulationRushBrakeChange Window(double p95)
        {
            total += 100;
            Assert.True(brake.WindowDue(100, total, options.WindowTicks));
            return brake.Observe(total, p95, options);
        }

        Assert.False(brake.WindowDue(99, 99, 100)); // not a full window
        Assert.Equal(PopulationRushBrakeChange.None, Window(600));
        Assert.Equal(PopulationRushBrakeChange.None, Window(499)); // under the ceiling: the count starts again
        Assert.Equal(PopulationRushBrakeChange.None, Window(600));
        Assert.Equal(PopulationRushBrakeChange.None, Window(600));
        Assert.Equal(PopulationRushBrakeChange.Paused, Window(501));
        Assert.True(brake.Paused);

        Assert.Equal(PopulationRushBrakeChange.None, Window(450)); // between the resume line (400) and the ceiling: still paused
        Assert.Equal(PopulationRushBrakeChange.None, Window(100));
        Assert.Equal(PopulationRushBrakeChange.None, Window(100));
        Assert.Equal(PopulationRushBrakeChange.Resumed, Window(100));
        Assert.False(brake.Paused);

        // A window is not counted twice: no new ticks, no new window.
        Assert.False(brake.WindowDue(100, total + 99, 100));
    }

    private static Task<PopulationTestHost> StartAsync(Action<PlayerbotOptions>? configure = null, Func<ArcaneCore.Kernel.WorldData.IWorldDataStore>? store = null)
        => PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 6;
            o.Population.Enabled = false;      // the rush needs no running population
            o.Population.TargetOnline = 0;
            o.Population.MaxCreatePerMinute = 1; // the rush bypasses it
            o.Population.WindowTicks = 20;
            o.Population.RushBrakeWindows = 3;
            configure?.Invoke(o);
        }, dataStore: store);

    private static async Task<PopulationRushStatus> WaitRushAsync(PopulationTestHost world, Func<PopulationRushStatus, bool> done, string what, int seconds = 20)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done(world.Feature.RushStatus))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for: {what}; rush is {world.Feature.RushStatus}");
            await Task.Delay(20);
        }

        return world.Feature.RushStatus;
    }

    [Fact]
    public async Task Rush_CreatesAndStartsTheBots_AtTheRequestedRate_BypassingTheCreationBudget()
    {
        await using PopulationTestHost world = await StartAsync();
        var clock = Stopwatch.StartNew();

        Assert.Null(world.Feature.StartRush(6, 2));
        await Task.Delay(1000);
        Assert.InRange(world.Feature.RushStatus.Created, 0, 3); // 2 per second: about 2 after a second, never the whole batch

        PopulationRushStatus done = await WaitRushAsync(world, r => r.State != "running", "the rush to finish");
        clock.Stop();

        Assert.Equal("done", done.State);
        Assert.Equal(6, done.Created);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2), $"6 bots at 2 per second take at least 2 s, took {clock.Elapsed}");
        Assert.InRange(done.AchievedPerSecond, 0.5, 2.6);
        Assert.Equal(6, (await world.Owners.LoadAllAsync()).Count);
        Assert.Equal(6, world.Members.Snapshot.Count); // population members, so the ordinary population owns them afterwards
        await world.WaitForSettledAsync(6);
    }

    [Fact]
    public async Task Rush_InFreshLaunch_MakesLevelOneBotsAtTheirRaceStart_OfBothFactions()
    {
        await using PopulationTestHost world = await StartAsync(o =>
        {
            o.Population.FreshLaunch = true;
            o.Population.Factions = PopulationFactions.Both;
        }, () => new FreshLaunchTests.RaceStartDataStore());

        Assert.Null(world.Feature.StartRush(6, 50));
        PopulationRushStatus done = await WaitRushAsync(world, r => r.State != "running", "the rush to finish");

        Assert.Equal(("done", 6), (done.State, done.Created));
        await world.WaitForSettledAsync(6);
        Assert.All(world.Bots.Snapshot(), bot => Assert.Equal((byte)1,
            world.Host.World.InvokeAsync(() => world.Bots.FindSession(bot.BotId)!.Player!.Level).GetAwaiter().GetResult()));
        var teams = new List<ArcaneCore.Game.Entities.Team>();
        foreach (PlayerbotStatus bot in world.Bots.Snapshot())
            teams.Add(await world.Host.World.InvokeAsync(() => world.Bots.FindSession(bot.BotId)!.Player!.Team));
        Assert.Equal(3, teams.Count(t => t == ArcaneCore.Game.Entities.Team.Alliance));
    }

    [Fact]
    public async Task TheBrake_PausesTheRush_WhileTheTickP95StaysOverTheCeiling_AndLiftsItWhenTheWorldRecovers()
    {
        await using PopulationTestHost world = await StartAsync(o => o.Population.RushBrakeP95Ms = 500);
        world.Ticks.Set(20, 900);
        Assert.Null(world.Feature.StartRush(6, 3));

        // Three fresh windows over the ceiling pause it (one window per Set: each adds WindowTicks ticks to the running total).
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(150);
            world.Ticks.Set(20, 900);
        }

        PopulationRushStatus paused = await WaitRushAsync(world, r => r.State == "paused", "the brake to pause the rush");
        Assert.Contains("paused by the brake", paused.Reason);
        int createdWhilePaused = paused.Created;
        await Task.Delay(1500);
        Assert.Equal(createdWhilePaused, world.Feature.RushStatus.Created); // nothing is made while paused

        for (int i = 0; i < 4; i++)
        {
            world.Ticks.Set(20, 10);
            await Task.Delay(150);
        }

        PopulationRushStatus resumed = await WaitRushAsync(world, r => r.State is "running" or "done", "the rush to resume");
        Assert.NotEqual("paused", resumed.State);
        PopulationRushStatus done = await WaitRushAsync(world, r => r.State == "done", "the resumed rush to finish");
        Assert.Equal(6, done.Created);
    }

    [Fact]
    public async Task TheBrake_TripsOnASlowWorld_WithinBoundedWallClockTime_AtTwiceTheTickBudget()
    {
        await using PopulationTestHost world = await StartAsync(o => o.Population.TickP95BudgetMs = 60);
        using var stop = new CancellationTokenSource();
        Task feeder = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) { world.Ticks.Set(5, 120); await Task.Delay(20); }
        });
        var clock = Stopwatch.StartNew();
        Assert.Null(world.Feature.StartRush(6, 2));

        PopulationRushStatus paused = await WaitRushAsync(world, r => r.State == "paused", "the brake to pause the rush", seconds: 25);
        clock.Stop();
        await stop.CancelAsync();
        await feeder;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(25), $"tripped after {clock.Elapsed}");
        Assert.True(paused.Created < 6);
    }

    [Fact]
    public async Task TheExistingCaps_StopARush_MaxBotsAndTheRegistrySize()
    {
        await using (PopulationTestHost world = await StartAsync(o => o.MaxBots = 2))
        {
            Assert.Null(world.Feature.StartRush(6, 50));
            PopulationRushStatus ended = await WaitRushAsync(world, r => r.State != "running", "the cap to end the rush");
            Assert.Equal("halted", ended.State);
            Assert.Equal(2, ended.Created);
            Assert.Contains("MaxBots", ended.Reason);
            Assert.Equal(2, world.RunningCount);
        }

        await using (PopulationTestHost world = await StartAsync(o => o.MaxRegisteredBots = 3))
        {
            Assert.Null(world.Feature.StartRush(6, 50));
            PopulationRushStatus ended = await WaitRushAsync(world, r => r.State != "running", "the registry cap to end the rush");
            Assert.Equal(("halted", 3), (ended.State, ended.Created));
            Assert.Contains("MaxRegisteredBots", ended.Reason);
            Assert.Equal(3, (await world.Owners.LoadAllAsync()).Count);
        }
    }

    [Fact]
    public async Task Rush_CanBeStopped_AndOnlyOneRunsAtATime()
    {
        await using PopulationTestHost world = await StartAsync();
        Assert.Null(world.Feature.StartRush(6, 1));
        Assert.Equal("a rush is already running; stop it first", world.Feature.StartRush(2, 1));

        Assert.True(world.Feature.StopRush());

        Assert.Equal("stopped", world.Feature.RushStatus.State);
        Assert.False(world.Feature.StopRush());
        await Task.Delay(1500);
        Assert.InRange(world.Feature.RushStatus.Created, 0, 2);
        int made = (await world.Owners.LoadAllAsync()).Count;
        await Task.Delay(1200);
        Assert.Equal(made, (await world.Owners.LoadAllAsync()).Count); // nothing more after the stop
    }

    [Fact]
    public async Task TheCommands_StartTheRush_ReportItInTheStatus_AndRefuseBadArguments_ForAdministratorsOnly()
    {
        await using PopulationTestHost world = await StartAsync();
        await using WorldTestClient admin = await world.Host.EnterWorldAsync("RUSHADMIN", "Rushadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string line = await SayAsync(admin, ".playerbot population rush 0", l => l.Contains("refused", StringComparison.Ordinal));
        Assert.StartsWith("Playerbot population rush refused: the count must be 1..", line);
        await SayAsync(admin, ".playerbot population rush 3 51", l => l.Contains("refused", StringComparison.Ordinal));
        await SayAsync(admin, ".playerbot population rush stop", l => l.StartsWith("No playerbot population rush is running", StringComparison.Ordinal));

        line = await SayAsync(admin, ".playerbot population rush 3 50", l => l.StartsWith("Playerbot population rush started:", StringComparison.Ordinal));
        Assert.StartsWith("Playerbot population rush started: 3 new bots at 50 per second", line);
        await WaitRushAsync(world, r => r.State == "done", "the commanded rush to finish");
        line = await SayAsync(admin, ".playerbot population status", l => l.StartsWith("Rush done:", StringComparison.Ordinal));
        Assert.Contains("3 of 3 created (0 failed)", line);
        Assert.Contains("Tick p95", line);
        Assert.Contains("brake at 500 ms", line);

        await using WorldTestClient player = await world.Host.EnterWorldAsync("RUSHPLAYER", "Rushplayer");
        await player.CollectAsync();
        await player.SendChatAsync(ChatType.Say, Language.Common, ".playerbot population rush 5");
        await player.ReadChatAsync();
        Assert.Equal("done", world.Feature.RushStatus.State);
        Assert.Equal(3, world.Feature.RushStatus.Requested);
    }

    private static async Task<string> SayAsync(WorldTestClient admin, string command, Func<string, bool> reply)
    {
        await admin.SendChatAsync(ChatType.Say, Language.Common, command);
        while (true)
        {
            string line = (await admin.ReadChatAsync()).Text;
            if (reply(line)) return line;
        }
    }
}

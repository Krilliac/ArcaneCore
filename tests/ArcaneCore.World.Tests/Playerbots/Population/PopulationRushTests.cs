using System.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Xunit;
using ArcaneCore.World.Bots;

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
        var options = new PlayerbotPopulationOptions { RushBrakeP95Ms = 500, RushBrakeWindows = 3, ResumeBelowPct = 80, RushBrakeWindowSeconds = 5 };
        var brake = new PopulationRushBrake();
        long total = 0, now = 1000;
        PopulationRushBrakeChange Window(double p95)
        {
            total += 100;
            now += 5000;
            Assert.True(brake.WindowDue(now, total, 5000));
            return brake.Observe(now, total, p95, options.RushCeilingMs(), options);
        }

        Assert.False(brake.WindowDue(now, total, 5000)); // the first call only starts the window
        Assert.False(brake.WindowDue(now + 4999, total + 100, 5000)); // not five seconds yet
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
        Assert.False(brake.WindowDue(now + 5000, total, 5000)); // five seconds but no ticks: no evidence
    }

    private static async Task<PopulationTestHost> StartAsync(Action<PlayerbotOptions>? configure = null, Func<ArcaneCore.Kernel.WorldData.IWorldDataStore>? store = null)
    {
        PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 6;
            o.Population.Enabled = false;      // the rush needs no running population
            o.Population.TargetOnline = 0;
            o.Population.MaxCreatePerMinute = 1; // the rush bypasses it
            o.Population.WindowTicks = 20;
            o.Population.RushBrakeWindows = 3;
            o.Population.RushBrakeWindowSeconds = 1;
            configure?.Invoke(o);
        }, dataStore: store);
        return world;
    }

    [Fact]
    public async Task Rush_StartedOnAColdWorld_WaitsForThePlacementSafetyData_InsteadOfHalting_AndThenCompletes()
    {
        await using PopulationTestHost world = await StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Feature.Provisioner.WarmGate = ct => gate.Task.WaitAsync(ct); // the facts build is held open: the world is cold

        Assert.Null(world.Feature.StartRush(2, 50));
        PopulationRushStatus held = await WaitRushAsync(world, _ => world.Feature.PassesStarted >= 3, "passes to run against the cold world");
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(20);
            held = world.Feature.RushStatus;
            Assert.NotEqual("halted", held.State);
        }

        Assert.Equal("running", held.State);
        Assert.Equal(0, held.Created);
        Assert.Empty(world.Members.Snapshot);

        gate.SetResult();
        PopulationRushStatus done = await WaitRushAsync(world, r => r.State != "running", "the rush to finish once the data is ready");
        Assert.Equal(("done", 2), (done.State, done.Created));
        await world.WaitForSettledAsync(2);
    }

    [Fact]
    public void Rush_HaltsOnAFailedPlacementDataBuild_ButHoldsWhileTheBuildRuns()
    {
        var rush = new PopulationRush();
        rush.Begin(5, 10, 0);

        // The build is running: a hold, however many passes it takes.
        for (int i = 0; i < 10; i++) Assert.False(rush.Report(5, 0, PopulationProvisioner.WaitingReason, null, i));
        Assert.Equal("running", rush.Snapshot(100).State);

        // The build failed and waits for its retry: the rush ends instead of waiting for good.
        Assert.True(rush.Report(5, 0, PopulationProvisioner.WaitingReason + PopulationProvisioner.BuildFailedSuffix, null, 200));
        PopulationRushStatus status = rush.Snapshot(200);
        Assert.Equal("halted", status.State);
        Assert.Contains("the last build failed", status.Reason);
    }

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

    // A world that keeps ticking at the p95 a test sets (volatile): 20 ticks every 20 ms.
    private sealed class SlowWorld : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _feeder;
        private double _p95;

        public SlowWorld(PopulationTestHost world, double p95)
        {
            _p95 = p95;
            _feeder = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    world.Ticks.Set(20, Volatile.Read(ref _p95));
                    try { await Task.Delay(20, _stop.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        public void Set(double p95) => Volatile.Write(ref _p95, p95);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _feeder;
            _stop.Dispose();
        }
    }

    // The brake's real-time clock, moved by the test.
    private sealed class StepClock : TimeProvider
    {
        private long _ms = 1_000_000;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _ms);

        public void Advance(int ms) => Interlocked.Add(ref _ms, ms);
    }

    // One brake window (1 s) of fake time per step; true real time is only the polling.
    private static async Task<int> StepUntilAsync(PopulationTestHost world, StepClock clock, Func<PopulationRushStatus, bool> done, int maxSteps = 200)
    {
        for (int steps = 1; steps <= maxSteps; steps++)
        {
            clock.Advance(1100);
            await Task.Delay(60);
            if (done(world.Feature.RushStatus)) return steps;
        }

        throw new TimeoutException($"not reached after {maxSteps} window steps; rush is {world.Feature.RushStatus}");
    }

    [Fact]
    public async Task TheBrake_PausesTheRush_WhileTheTickP95StaysOverTheCeiling_AndLiftsItWhenTheWorldRecovers()
    {
        await using PopulationTestHost world = await StartAsync();
        var clock = new StepClock();
        world.Feature.RushClock = clock;
        await using var slow = new SlowWorld(world, 120); // twice the 60 ms budget, over the default 90 ms ceiling
        Assert.Null(world.Feature.StartRush(6, 1));

        int steps = await StepUntilAsync(world, clock, r => r.State == "paused");
        PopulationRushStatus paused = world.Feature.RushStatus;
        // Three windows over the ceiling are needed (the first step only starts a window): not fewer, and not many more.
        Assert.InRange(steps, 3, 15);
        Assert.Contains("paused by the brake", paused.Reason);
        await Task.Delay(1000); // a bot already being created when the brake tripped may still finish
        int createdWhilePaused = world.Feature.RushStatus.Created;
        await Task.Delay(1500);
        Assert.Equal(createdWhilePaused, world.Feature.RushStatus.Created); // nothing is made while paused

        slow.Set(10);
        await StepUntilAsync(world, clock, r => r.State is "running" or "done");
        PopulationRushStatus resumed = world.Feature.RushStatus;
        Assert.NotEqual("paused", resumed.State);
        PopulationRushStatus done = await WaitRushAsync(world, r => r.State == "done", "the resumed rush to finish");
        Assert.Equal(6, done.Created);
    }

    [Fact]
    public async Task TheBrake_StopsCreationInTheMiddleOfAPass()
    {
        await using PopulationTestHost world = await StartAsync();
        var clock = new StepClock();
        world.Feature.RushClock = clock;
        await using var slow = new SlowWorld(world, 120);
        PopulationRushStatus? pausedInPass = null;
        var tripped = Stopwatch.StartNew();
        world.Owners.AfterCreate = async created =>
        {
            if (created != 2) return;
            // The pass holds the 3rd..6th bot back until the brake has tripped.
            await StepUntilAsync(world, clock, r => r.State == "paused");
            pausedInPass = world.Feature.RushStatus;
            tripped.Stop();
        };

        await world.Host.World.InvokeAsync(() =>
        {
            Assert.Null(world.Feature.StartRush(6, 50));
            world.Feature.Rush.Pacer.Add(2000, 50); // one pass of all 6 bots
            return true;
        });

        await WaitRushAsync(world, r => r.State == "paused" && r.Created == 2, "the pass to end after the pause");
        await Task.Delay(500);
        Assert.NotNull(pausedInPass);
        Assert.InRange(tripped.Elapsed.TotalSeconds, 0, 12);
        Assert.Equal(2, (await world.Owners.LoadAllAsync()).Count); // bots 3 to 6 were never created
        Assert.Equal("paused", world.Feature.RushStatus.State);
    }

    [Fact]
    public async Task WhileTheRushIsPausedOrRunning_TheBudgetStillParksOwnedBots()
    {
        await using PopulationTestHost world = await StartAsync(o =>
        {
            o.Population.IntervalSeconds = 2;
            o.Population.ConfirmWindows = 1;
            o.Population.MinRunSeconds = 0;
            o.MaxBots = 8;
        });
        Assert.Null(world.Feature.StartRush(6, 1));

        // Slow the world only once the rush has made a bot: a brake that trips first leaves nothing to shed, and a paused rush never resumes.
        await WaitRushAsync(world, r => r.State == "running" && r.Created >= 1, "the rush to create its first bot");
        await using var slow = new SlowWorld(world, 120);

        await WaitRushAsync(world, r => r.State == "paused" && r.Created >= 1, "the brake to pause the rush");
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (world.RunningCount >= world.Feature.RushStatus.Created)
        {
            Assert.True(DateTime.UtcNow < deadline, $"no bot was shed: {world.RunningCount} running of {world.Feature.RushStatus.Created} created");
            await Task.Delay(100);
        }

        Assert.True(world.Feature.RushStatus.Created > world.RunningCount);
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
            // The rush counts a bot when it is created; it is Running once its login has gone through, which on a loaded machine can
            // trail the rush's end. The cap holds whatever the state: never more than two bots at all, and both of them reach Running.
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (world.RunningCount < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Equal(2, world.Bots.Snapshot().Count());
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
        Assert.Contains("brake at 90 ms", line);

        await using WorldTestClient player = await world.Host.EnterWorldAsync("RUSHPLAYER", "Rushplayer");
        await player.CollectAsync();
        await player.SendChatAsync(ChatType.Say, Language.Common, ".playerbot population rush 5");
        await player.ReadSystemChatAsync();
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

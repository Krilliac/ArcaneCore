using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The population manager end to end: a real world thread, real managed playerbots and a fake tick source that decides the p95 the control
/// loop sees. A pass runs only when the test starts it (the interval is 600 s), so every step is deterministic.
/// </summary>
public sealed class PlayerbotPopulationFeatureTests
{
    [Fact]
    public async Task TheControlLoop_Adds_ThenHolds_ThenParks_ThenRestarts_AndResetsTheWindowAfterEachChange()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();

        // Too few ticks since the start: settle, change nothing.
        world.Ticks.Set(5, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("settling", status.State);
        Assert.Equal(0, world.RunningCount);
        Assert.Empty(world.Members.Snapshot);
        Assert.Equal(0, world.Ticks.Resets);

        // A full window well under the budget (10 ms against 80% of 60 ms): add up to the target, creating the bots.
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Equal("under-budget", status.State);
        Assert.Contains("started 0, created 2", status.Reason);
        Assert.Equal(2, status.CreatesLastMinute);
        Assert.Equal(2, world.Members.Snapshot.Count);
        Assert.Equal(2, (await world.Owners.LoadAllAsync()).Count);
        Assert.All(world.Members.Snapshot, m => Assert.Equal(PopulationTestHost.Elwynn, m.ZoneId));
        Assert.Equal(1, world.Ticks.Resets);
        Assert.Equal(0, world.Ticks.Samples); // the window starts again after the change
        await world.WaitForSettledAsync(2);

        // The very next pass sees no fresh ticks: it holds and creates nothing.
        status = await world.PassAsync();
        Assert.Equal("settling", status.State);
        Assert.Equal(2, world.Members.Snapshot.Count);
        Assert.Equal(1, world.Ticks.Resets);

        // Between the resume line (48 ms) and the budget (60 ms): hold, whatever the target says.
        world.Population.TargetOnline = 4;
        world.Ticks.Set(200, 55);
        status = await world.PassAsync();
        Assert.Equal("hysteresis", status.State);
        Assert.Equal(2, world.Members.Snapshot.Count);
        Assert.Equal(2, world.RunningCount);
        Assert.Equal(1, world.Ticks.Resets);
        world.Population.TargetOnline = 2;

        // Over the budget: park one (MaxParksPerInterval), then reset the window.
        world.Ticks.Set(200, 80);
        status = await world.PassAsync();
        Assert.Equal("over-budget", status.State);
        Assert.Contains("parked 1", status.Reason);
        Assert.Equal(2, world.Ticks.Resets);
        await world.Host.WaitForWorldAsync(() => world.RunningCount == 1, "one bot parked");
        Assert.Equal(1, status.OwnedParked);
        PlayerbotStatus parked = Assert.Single(world.Bots.Snapshot(), s => s.State != ManagedPlayerbotState.Running);
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer(parked.Name) is null, "the parked bot logged out");

        // Back under the budget with room in the target: the parked bot is started again, nothing new is created.
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Equal("under-budget", status.State);
        Assert.Contains("started 1, created 0", status.Reason);
        Assert.Equal(3, world.Ticks.Resets);
        await world.WaitForSettledAsync(2);
        Assert.Equal(2, world.Members.Snapshot.Count);

        // At the target and under the budget: nothing to do, so no reset either.
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Equal("at-target", status.State);
        Assert.Equal(3, world.Ticks.Resets);
        PopulationBandStatus band = Assert.Single(status.Bands);
        Assert.Equal((2, 2), (band.Want, band.Have));
    }

    [Fact]
    public async Task TheWorldDataStore_IsResolvedFromThePassScope_AndOnlyTheRaceClassCacheIsKept()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("created 1", status.Reason);
        Assert.Null(status.LastError);
        // The scoped store was created for the pass and ended with its scope (a store taken from the root provider would live on).
        ScopedWorldDataStore store = Assert.Single(world.CacheBuilders);
        Assert.True(store.Disposed);
        await world.WaitForSettledAsync(1);

        // The valid (race, class) pairs were kept: the next creating pass does not ask the store again.
        world.Population.TargetOnline = 2;
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Contains("created 1", status.Reason);
        Assert.Single(world.CacheBuilders);

        // A different class list is a different key: the pairs are asked for again, in a new scope.
        await world.WaitForSettledAsync(2);
        world.Population.Classes = [1, 2];
        world.Population.TargetOnline = 3;
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        Assert.Equal(2, world.CacheBuilders.Count);
        Assert.All(world.CacheBuilders, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task MaxCreatePerMinute_IsEnforcedOnCreation_OverAMinuteOfWorldTime()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 4;
            o.Population.MaxCreatePerMinute = 2;
        });

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("created 2", status.Reason);
        Assert.Contains("held back: creation is limited to 2 per minute", status.Reason);
        Assert.Equal(2, world.Members.Snapshot.Count);
        await world.WaitForSettledAsync(2);

        // Still inside the minute: the next pass is refused creations, not the loop.
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Equal("under-budget", status.State);
        Assert.Contains("created 0", status.Reason);
        Assert.Contains("creation is limited to 2 per minute", status.Reason);
        Assert.Equal(2, world.Members.Snapshot.Count);
        Assert.Equal(2, status.CreatesLastMinute);

        // A minute of world time later the window has emptied.
        await world.Host.World.InvokeAsync(() => { world.Feature.ClockMs += 61_000; return true; });
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Contains("created 2", status.Reason);
        Assert.Equal(4, world.Members.Snapshot.Count);
        Assert.Equal(2, status.CreatesLastMinute);
    }

    [Fact]
    public async Task MaxCreatePerMinuteZero_CreatesNothing_ButStillRestartsAParkedBot()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(1);
        PlayerbotStatus bot = Assert.Single(world.Bots.Snapshot());
        Assert.True((await world.Bots.StopAsync(bot.BotId.ToString())).Success);
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer(bot.Name) is null, "the bot logged out");

        world.Population.MaxCreatePerMinute = 0;
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("started 1, created 0", status.Reason);
        await world.WaitForSettledAsync(1);
        Assert.Equal(bot.BotId, Assert.Single(world.Members.Snapshot).BotId);

        // A second bot would have to be created: refused.
        world.Population.TargetOnline = 2;
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Contains("created 0", status.Reason);
        Assert.Single(world.Members.Snapshot);
    }

    [Fact]
    public async Task MaxRegisteredBots_StopsCreation()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxRegisteredBots = 1;
            o.Population.TargetOnline = 2;
        });

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("created 1", status.Reason);
        Assert.Contains("the bot registry is full (MaxRegisteredBots)", status.Reason);
        Assert.Single(world.Members.Snapshot);
        Assert.Single(await world.Owners.LoadAllAsync());
        await world.WaitForSettledAsync(1);

        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Contains("created 0", status.Reason);
        Assert.Contains("the bot registry is full (MaxRegisteredBots)", status.Reason);
        Assert.Single(await world.Owners.LoadAllAsync());
    }

    [Fact]
    public async Task MaxBots_CapsTheEffectiveTarget_AndTheRunningBots()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 1;
            o.Population.TargetOnline = 3;
        });

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal(3, status.Target);
        Assert.Equal(1, status.EffectiveTarget);
        Assert.Contains("created 1", status.Reason);
        await world.WaitForSettledAsync(1);

        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Equal("at-target", status.State);
        Assert.Single(await world.Owners.LoadAllAsync());
        Assert.Equal(1, world.RunningCount);
    }

    [Fact]
    public async Task AboveTheTarget_OnlyBotsThePopulationOwns_AreParked_NeverOneAnOperatorCreated()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.MaxParksPerInterval = 5);
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(2);

        // An operator's bot: running, but no population row.
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Operatorone", 1, 1);
        Guid operatorBot = Assert.IsType<Guid>(made.BotId);
        Assert.True((await world.Bots.StartAsync(operatorBot.ToString())).Success);
        await world.WaitForSettledAsync(3);
        Assert.DoesNotContain(world.Members.Snapshot, m => m.BotId == operatorBot);

        world.Population.TargetOnline = 1;
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("over-target", status.State);
        Assert.Contains("parked 2", status.Reason);
        await world.Host.WaitForWorldAsync(() => world.RunningCount == 1, "both owned bots parked");
        Assert.Equal(operatorBot, Assert.Single(world.Bots.Snapshot(), s => s.State == ManagedPlayerbotState.Running).BotId);

        // The operator's bot alone is over a target of 0, and the population owns nothing running: it does not touch it.
        world.Population.TargetOnline = 0;
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.NotEqual("over-target", status.State);
        Assert.DoesNotContain("parked", status.Reason);
        Assert.Equal(1, world.RunningCount);
        Assert.Equal(ManagedPlayerbotState.Running, Assert.Single(world.Bots.Snapshot(), s => s.BotId == operatorBot).State);
    }

    [Fact]
    public async Task AScriptedOwnedBot_IsNotParked_EvenOverTheBudget()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(1);
        Guid bot = Assert.Single(world.Members.Snapshot).BotId;
        Assert.True(await world.Bots.SetControllerAsync(bot, new IdleController()));
        await world.Host.WaitForWorldAsync(() => world.Bots.IsScripted(bot), "the bot is scripted");

        world.Ticks.Set(200, 80);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("over-budget", status.State);
        Assert.Contains("parked 0", status.Reason);
        Assert.Equal(1, world.RunningCount);
        Assert.Equal(1, world.Ticks.Resets); // nothing changed, so the window was not reset again
    }

    [Fact]
    public async Task ADisabledPopulation_DoesNothing_AndEnablingItLetsTheTimerRunPasses()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.Enabled = false;
            o.Population.IntervalSeconds = 2;
        }, manualClock: true);
        world.Ticks.Set(200, 10);

        await world.Host.World.AdvanceClockAsync(5000);
        PopulationStatus status = world.Feature.Status;
        Assert.False(status.Enabled);
        Assert.Equal("disabled", status.State);
        Assert.Equal(0, status.Passes);
        Assert.Empty(world.Members.Snapshot);
        Assert.Equal(0, world.RunningCount);
        Assert.Equal(0, world.Ticks.Resets);

        // Enabled, the timer starts a pass; too few ticks make it settle, so the world is left alone and the test stays on the clock it controls.
        world.Ticks.Set(5, 10);
        world.Population.Enabled = true;
        await world.Host.World.AdvanceClockAsync(2500);
        await PollAsync(() => world.Feature.Status.Passes >= 1, "a pass after the population was enabled");
        Assert.True(world.Feature.Status.Enabled);
        Assert.Equal("settling", world.Feature.Status.State);

        // Off again: the status goes back to disabled and no pass runs.
        world.Population.Enabled = false;
        await world.Host.World.AdvanceClockAsync(5000);
        status = world.Feature.Status;
        Assert.False(status.Enabled);
        Assert.Equal("disabled", status.State);
        Assert.Equal(0, status.Passes);
    }

    [Fact]
    public async Task APopulationRowWhoseBotIsGone_IsRemovedAtThePass()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        var orphan = new PlayerbotPopulationMember(Guid.NewGuid(), 4242, 12, 0, 100, 100);
        world.Members.Seed(orphan);

        world.Ticks.Set(200, 10);
        await world.PassAsync();

        Assert.Empty(world.Members.Snapshot);
    }

    [Fact]
    public async Task Restart_ChoosesStartableMembersBeforeTheCap_SoAParkedBotAmongMoreThan128IsStartedAndNothingNewIsCreated()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);

        // 128 owned bots that cannot be started (faulted in the registry), all older than the one parked bot.
        for (int i = 0; i < 128; i++)
        {
            var id = Guid.NewGuid();
            await world.Owners.CreateAsync(new ManagedPlayerbot(id, 9000 + i, 90_000 + i, "faulty" + i, false, ManagedPlayerbotState.Faulted,
                PlayerbotGoalKind.Explore, 0, 0, 1, 100, 100));
            world.Members.Seed(new PlayerbotPopulationMember(id, 90_000 + i, PopulationTestHost.Elwynn, 0, 100, 100 + i));
        }

        // The parked bot: a real, stopped, owned bot, the newest member.
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Parkedone", 1, 1);
        Guid parked = Assert.IsType<Guid>(made.BotId);
        ManagedPlayerbot record = Assert.IsType<ManagedPlayerbot>(world.Owners.Peek(parked));
        world.Members.Seed(new PlayerbotPopulationMember(parked, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        Assert.Equal(129, (await world.Owners.LoadAllAsync()).Count);

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("started 1, created 0", status.Reason);
        Assert.Equal(129, (await world.Owners.LoadAllAsync()).Count);
        await world.WaitForSettledAsync(1);
        Assert.Equal(parked, Assert.Single(world.Bots.Snapshot(), s => s.State == ManagedPlayerbotState.Running).BotId);
    }

    [Fact]
    public async Task AnOutLevelledBot_IsRelocatedToAZoneThatFitsItsLevel()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);

        await SetBotAsync(world, bot, 20, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Westfall, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task ABotWithinTheLevelsOfItsZone_StaysWhereItIs()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);

        // Level 12 is the zone maximum (10) plus the margin (2): not out-levelled.
        await SetBotAsync(world, bot, 12, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task AnUnderLevelledBot_IsRelocatedToAZoneThatFitsItsLevel()
    {
        // Issue #177: a level 9 bot homed in Duskwood (whose graveyards stand in level 24-26 areas) died in a loop at Raven Hill, and the
        // relocation only ever moved bots that were above their zone. Westfall's lowest place is 12; minus the slack (4) a level 5 bot is
        // below anywhere it could have been put there.
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);

        await SetBotAsync(world, bot, 5, PopulationTestHost.Westfall);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task ABotAtTheSlackBelowItsZone_StaysWhereItIs()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);

        // Level 8 is Westfall's lowest (12) minus the slack (4): it could have been placed there, so it is not moved.
        await SetBotAsync(world, bot, 8, PopulationTestHost.Westfall);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Westfall, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task ABotInACapital_IsNeverRelocated_WhateverItsLevel()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);

        // Stormwind is not in the atlas on purpose: a bot there is on a town or trainer goal and must not be sent back out.
        await SetBotAsync(world, bot, 20, PopulationTestHost.Stormwind);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
        Assert.Equal(PopulationTestHost.Stormwind, await world.Host.World.InvokeAsync(() => world.Bots.FindSession(bot)!.Player!.ZoneId));
    }

    [Fact]
    public async Task APlacementVeto_KeepsAnOutLevelledBotInPlace_UntilItLetsGo()
    {
        var veto = new TestPlacementVeto();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true,
            configureServices: services => services.AddSingleton<IPopulationPlacementVeto>(veto));
        Guid bot = await StartOneBotInElwynnAsync(world);

        await SetBotAsync(world, bot, 20, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        veto.Owned.Add(bot);
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);

        veto.Owned.Clear();
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Westfall, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    /// <summary>Create the one owned bot (Westfall is excluded, so it is placed in Elwynn) and wait until it stands in the world.</summary>
    private static async Task<Guid> StartOneBotInElwynnAsync(PopulationTestHost world)
    {
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("created 1", status.Reason);
        await world.WaitForSettledAsync(1);
        PlayerbotPopulationMember member = Assert.Single(world.Members.Snapshot);
        Assert.Equal(PopulationTestHost.Elwynn, member.ZoneId);
        return member.BotId;
    }

    private static Task SetBotAsync(PopulationTestHost world, Guid bot, byte level, uint zone) => world.Host.World.InvokeAsync(() =>
    {
        Player player = world.Bots.FindSession(bot)!.Player!;
        player.Level = level;
        player.ZoneId = zone;
        return true;
    });

    private sealed class TestPlacementVeto : IPopulationPlacementVeto
    {
        public HashSet<Guid> Owned { get; } = [];

        public bool OwnsPlacement(Guid botId) => Owned.Contains(botId);
    }

    private static async Task PollAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    private sealed class IdleController : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) { }
    }
}

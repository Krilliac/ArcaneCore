using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The population manager end to end: a real world thread, real managed playerbots and a fake tick source that decides the p95 the control
/// loop sees. A pass runs only when the test starts it (the interval is 600 s), so every step is deterministic.
/// </summary>
public sealed class PlayerbotPopulationFeatureTests
{
    private const uint SafeStarterZone = 9001;

    [Fact]
    public async Task UnsafeLeastAssignedZone_IsSkippedForAnotherZoneInTheSamePass()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 2,
            configureServices: UnsafeStarterServices);
        await AddSafeStarterZoneAsync(world);

        // One operator bot assigned to the safe zone makes the unsafe Elwynn zone strictly least assigned.
        PlayerbotOperationResult operatorResult = await world.Bots.CreateAsync("Safeseed", 1, 1);
        Guid operatorId = Assert.IsType<Guid>(operatorResult.BotId);
        Assert.True((await world.Bots.StartAsync(operatorId.ToString())).Success);
        await world.Host.World.InvokeAsync(() =>
        {
            world.Bots.FindSession(operatorId)!.Player!.ZoneId = SafeStarterZone;
            return true;
        });
        world.Ticks.Set(200, 10);

        // The creature survey of the graveyards runs in the background; creation waits for it (the terrain hold).
        PopulationStatus status = await world.PassAsync();
        for (DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            (status.Reason.Contains("waiting for the graveyard", StringComparison.Ordinal) || status.Reason.Contains(PopulationProvisioner.WaitingReason, StringComparison.Ordinal)) && DateTime.UtcNow < deadline;)
        {
            await Task.Delay(20);
            world.Ticks.Set(200, 10);
            status = await world.PassAsync();
        }

        Assert.Contains("created 1", status.Reason);
        Assert.Single(world.Members.Snapshot);
        Assert.Equal(SafeStarterZone, world.Members.Snapshot[0].ZoneId);
        await world.WaitForSettledAsync(2);
        Assert.Equal(2, world.RunningCount);
        Assert.Equal(2, (await world.Owners.LoadAllAsync()).Count); // no throwaway bot in a starting area
    }

    [Fact]
    public async Task AllUnsafeZones_CreateNoCharacterAndLeaveNoStoppedBandMember()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1,
            configureServices: UnsafeStarterServices);
        world.Ticks.Set(200, 10);

        // The creature survey runs in the background and holds creation until it is in; only a pass after the hold can show that
        // every zone was refused as unsafe.
        PopulationStatus status = await world.PassAsync();
        for (DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            (status.Reason.Contains("waiting for the graveyard", StringComparison.Ordinal) || status.Reason.Contains(PopulationProvisioner.WaitingReason, StringComparison.Ordinal)) && DateTime.UtcNow < deadline;)
        {
            await Task.Delay(20);
            world.Ticks.Set(200, 10);
            status = await world.PassAsync();
        }

        Assert.DoesNotContain("waiting for the graveyard terrain lookup", status.Reason);
        Assert.Contains("no safe zone, level and race-class placement is available", status.Reason);
        Assert.Contains("created 0", status.Reason);
        Assert.Empty(world.Members.Snapshot);
        Assert.Empty(await world.Owners.LoadAllAsync());
    }

    [Fact]
    public async Task PlacementRefusedAfterCreation_LeavesNoPopulationMember_AndRecordsTheRefusal()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        // Safe when the plan is made; unsafe once the new member row exists (a content reload between plan and teleport).
        world.Feature.Provisioner.AreaLevelSource = _ => world.Members.Snapshot.Count == 0 ? 0 : 60;
        world.Ticks.Set(200, 10);

        PopulationStatus status = await world.PassAsync();
        Assert.Contains("placement-unsafe", world.Feature.Status.LastError ?? string.Empty);
        Assert.Empty(world.Members.Snapshot);
        Assert.Equal(0, world.RunningCount);
        Assert.True(status.Reason.Contains("created 1"), status.Reason + " / " + status.LastError);
        // The unplaced level-1 bot is deleted, not parked: no registry row (so no MaxRegisteredBots slot) and no status entry.
        Assert.Empty(await world.Owners.LoadAllAsync());
        Assert.Empty(world.Bots.Snapshot());
    }

    [Fact]
    public async Task PlacementRefusedRepeatedly_PausesCreation_AndNextPassPlansAgain()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 1;
            o.Population.MaxCreatePerMinute = 60;
        });
        bool placeable = false;
        world.Feature.Provisioner.AreaLevelSource = _ => placeable || world.Members.Snapshot.Count == 0 ? 0 : 60;

        for (int pass = 1; pass <= 3; pass++)
        {
            PopulationStatus refused = await PassWithTicksAsync(world);
            Assert.Contains("created 1", refused.Reason);
            Assert.Empty(await world.Owners.LoadAllAsync());
        }

        for (int pass = 4; pass <= 13; pass++)
        {
            PopulationStatus paused = await PassWithTicksAsync(world);
            Assert.Contains("creation paused", paused.Reason);
            Assert.Contains("created 0", paused.Reason);
            Assert.Empty(await world.Owners.LoadAllAsync());
        }

        // Ten paused passes later the pause lifts by itself and the next pass plans (and is refused) again.
        PopulationStatus resumed = await PassWithTicksAsync(world);
        Assert.Contains("created 1", resumed.Reason);
        Assert.DoesNotContain("creation paused", resumed.Reason);

        // The pause lifts on its own after a number of passes, and the next plan places the bot.
        placeable = true;
        for (int pass = 0; pass < 12 && world.Members.Snapshot.Count == 0; pass++) await PassWithTicksAsync(world);
        Assert.Single(world.Members.Snapshot);
        Assert.Single(await world.Owners.LoadAllAsync());
    }

    [Fact]
    public async Task PlacementRefused_AndBotDeleteRefused_KeepsThePendingMember_AndALaterPassDeletesIt()
    {
        var hook = new SwitchableDeleteHook { Refuse = true };
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1,
            configureServices: services => services.AddSingleton<ICharacterDeleteHook>(hook));
        world.Feature.Provisioner.AreaLevelSource = _ => world.Members.Snapshot.Any(m => m.Band != PlayerbotPopulationFeature.PendingDeleteBand) ? 60 : 0;

        PopulationStatus first = await PassWithTicksAsync(world);
        Assert.Contains("created 1", first.Reason);
        // The character delete was refused: the registry row stays and so does the member, marked pending; nothing restarts it.
        Assert.Single(await world.Owners.LoadAllAsync());
        PlayerbotPopulationMember pending = Assert.Single(world.Members.Snapshot);
        Assert.Equal(PlayerbotPopulationFeature.PendingDeleteBand, pending.Band);
        Assert.Equal(0, world.RunningCount);

        hook.Refuse = false;
        await PassWithTicksAsync(world);
        // The pending bot is deleted at the start of the next pass; that pass's own new bot is refused and deleted too.
        Assert.Empty(await world.Owners.LoadAllAsync());
        Assert.Empty(world.Members.Snapshot);
    }

    [Fact]
    public async Task PlacementRefused_AndCancelledDuringTheDelete_KeepsThePendingMember_AndALaterPassDeletesIt()
    {
        System.Reflection.FieldInfo stop = typeof(PlayerbotPopulationFeature).GetField("_stop",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        PlayerbotPopulationFeature? feature = null;
        var shutdown = new CancelOnRefusalLogger(() => ((CancellationTokenSource)stop.GetValue(feature)!).Cancel());
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1,
            configureServices: services => services.AddSingleton<Microsoft.Extensions.Logging.ILogger<PlayerbotPopulationFeature>>(shutdown));
        feature = world.Feature;
        world.Feature.Provisioner.AreaLevelSource = _ => world.Members.Snapshot.Any(m => m.Band != PlayerbotPopulationFeature.PendingDeleteBand) ? 60 : 0;

        await PassWithTicksAsync(world);
        // The shutdown arrived right after the refusal, before the bot could be stopped and deleted.
        Assert.True(shutdown.Fired);
        Assert.Single(await world.Owners.LoadAllAsync());
        Assert.Equal(PlayerbotPopulationFeature.PendingDeleteBand, Assert.Single(world.Members.Snapshot).Band);

        shutdown.Armed = false;
        stop.SetValue(world.Feature, new CancellationTokenSource());
        // A failed deletion is retried on a growing interval: the first retry is within a pass or two.
        for (int pass = 0; pass < 3 && world.Members.Snapshot.Count > 0; pass++) await PassWithTicksAsync(world);
        Assert.Empty(await world.Owners.LoadAllAsync());
        Assert.Empty(world.Members.Snapshot);
    }

    private sealed class CancelOnRefusalLogger(Action cancel) : Microsoft.Extensions.Logging.ILogger<PlayerbotPopulationFeature>
    {
        public bool Armed { get; set; } = true;
        public bool Fired { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!Armed || !formatter(state, exception).Contains("placement of", StringComparison.Ordinal)) return;
            Fired = true;
            cancel();
        }
    }

    [Fact]
    public async Task APendingMember_WhoseBotWasRestoredRunning_IsStoppedAndDeleted_WithoutCountingInABand()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        // The state after a crash between the mark and the stop: marked pending, but the bot is desired and running again.
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Restoredone", 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        Assert.True((await world.Bots.StartAsync(id.ToString())).Success);
        // The pass counts only bots whose player is in the world; without this the count is 0 whatever the code does.
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Restoredone") is { IsInWorld: true }, "the restored bot in the world");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        world.Members.Seed(new PlayerbotPopulationMember(id, (await world.Owners.LoadAllAsync()).Single().CharacterId, 0,
            PlayerbotPopulationFeature.PendingDeleteBand, now, now));
        world.Feature.Provisioner.AreaLevelSource = _ => 60; // nothing else can be placed in this pass

        PopulationStatus status = await PassWithTicksAsync(world);
        Assert.Equal(0, status.Bands.Sum(b => b.Have)); // never counted towards a band, not even for the pass it was running in
        Assert.Equal(0, status.OwnedRunning);
        Assert.Equal(0, status.OperatorRunning); // not an operator bot either: the target (1) is not lowered by it
        Assert.DoesNotContain(await world.Owners.LoadAllAsync(), b => b.BotId == id);
        Assert.DoesNotContain(world.Members.Snapshot, m => m.BotId == id);
    }

    [Fact]
    public async Task ARefusedPendingDelete_IsRetriedWithGrowingGaps_CappedAtEight()
    {
        var hook = new SwitchableDeleteHook { Refuse = true };
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1,
            configureServices: services => services.AddSingleton<ICharacterDeleteHook>(hook));
        bool firstPass = true;
        // The first pass places and is refused; afterwards nothing is safe, so only the one pending bot is ever retried.
        world.Feature.Provisioner.AreaLevelSource = _ => firstPass && world.Members.Snapshot.Count == 0 ? 0 : 60;

        var attemptPasses = new List<int>();
        int calls = 0;
        for (int pass = 1; pass <= 46; pass++)
        {
            await PassWithTicksAsync(world);
            firstPass = false;
            if (hook.Calls != calls) { attemptPasses.Add(pass); calls = hook.Calls; }
        }

        Assert.Single(await world.Owners.LoadAllAsync());
        Assert.Equal(PlayerbotPopulationFeature.PendingDeleteBand, Assert.Single(world.Members.Snapshot).Band);
        int[] gaps = [.. attemptPasses.Zip(attemptPasses.Skip(1), (a, b) => b - a)];
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 8], gaps);
    }

    private sealed class SwitchableDeleteHook : ICharacterDeleteHook
    {
        public volatile bool Refuse;

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> CanDeleteCharacterAsync(WorldSession session, CharacterRecord character)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(!Refuse);
        }
    }

    private static Task<PopulationStatus> PassWithTicksAsync(PopulationTestHost world)
    {
        world.Ticks.Set(200, 10); // a pass needs a fresh tick window
        return world.PassAsync();
    }

    private static void UnsafeStarterServices(IServiceCollection services)
    {
        services.AddSingleton(new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 72, 3, 2, 12),
            new FactionTemplateRecord(14, 14, 0, 8, 0, 1),
        ]));
        services.AddSingleton<ICreatureDataStore>(new FixedCreatureStore());
    }

    private static Task AddSafeStarterZoneAsync(PopulationTestHost world) => world.Host.World.InvokeAsync(() =>
    {
        WorldMaps maps = WorldMaps.Of(world.Host.World);
        maps.Terrain.Areas = new AreaTable([
            .. maps.Areas.All,
            new AreaTemplate(SafeStarterZone, 0, 0, SafeStarterZone, 0, 0, "Safe Woods", AreaTeams.Ally, 0),
            new AreaTemplate(9002, 0, SafeStarterZone, 9002, 0, 5, "Safe Meadow", 0, 0),
            new AreaTemplate(9003, 0, SafeStarterZone, 9003, 0, 10, "Safe Grove", 0, 0),
        ], maps.Registry);
        WorldGraveyards.Of(world.Host.World).Load(new GraveyardContent(
        [
            new WorldSafeLoc(1, 0, -8913.23f, -133f, 81f, 0f, "Elwynn"),
            new WorldSafeLoc(9901, 0, -8200f, -700f, 90f, 0f, "Safe Woods"),
        ], [new GraveyardLink(1, PopulationTestHost.Elwynn, 0), new GraveyardLink(9901, SafeStarterZone, 0)]), null);
        return true;
    });

    private sealed class FixedCreatureStore : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CreatureContent(
            [new CreatureTemplate
            {
                Entry = 990001, Name = "test hostile", Faction = 14, MinLevel = 20, MaxLevel = 20,
                MinLevelHealth = 300, MaxLevelHealth = 300, DisplayIds = [49],
            }],
            [new CreatureSpawn { Guid = 990001, Entry = 990001, MapId = 0, X = -8880f, Y = -133f, Z = 81f }],
            [], [], []));
    }

    [Fact]
    public async Task APassCanceledBecauseTheWorldStopped_IsInformational()
    {
        var log = new CapturingLogger();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(
            configureServices: services => services.AddSingleton<Microsoft.Extensions.Logging.ILogger<PlayerbotPopulationFeature>>(log));
        world.Ticks.Set(200, 10);
        TaskCompletionSource held = world.Members.HoldNextLoad();
        Task pass = world.Feature.RunPassNowAsync();
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        world.Host.World.Stop();
        world.Members.CancelHeldLoad();
        await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(log.Infos, line => line.Contains("population pass canceled during shutdown", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Warnings, line => line.Contains("population pass failed", StringComparison.Ordinal));
        Assert.Null(world.Feature.Status.LastError);
    }

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
        Assert.True(status.Reason.Contains("created 1"), status.Reason + " / " + status.LastError);
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
        Assert.True(status.Reason.Contains("created 1"), status.Reason + " / " + status.LastError);
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
    public async Task ABotTheBattlegroundCoordinatorDrives_IsNotParked_EvenOverTheBudget()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(1);
        Guid bot = Assert.Single(world.Members.Snapshot).BotId;

        // The bot walks to a battlemaster (Travel is a phase the coordinator drives). The coordinator has no public way to start that
        // without a battlemaster and a roll, so the followed bot is put in its table directly, as its own scan would.
        var coordinator = world.Host.WorldServices.GetRequiredService<ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundCoordinator>();
        var tracked = (Dictionary<Guid, ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundCoordinator.Tracked>)typeof(ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundCoordinator)
            .GetField("_tracked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(coordinator)!;
        await world.Host.World.InvokeAsync(() =>
        {
            tracked[bot] = new(bot, "Popbot") { Phase = ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundPhase.Travel, PhaseSinceMs = world.Host.World.NowMs };
            return true;
        });
        Assert.True(await world.Host.World.InvokeAsync(() => coordinator.Drives(bot)));

        world.Ticks.Set(200, 80);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("over-budget", status.State);
        Assert.Contains("parked 0", status.Reason);
        Assert.Equal(1, world.RunningCount);
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
        // The interval kept counting while disabled, so the first update starts that pass at once. Advance less than one more interval: a
        // second pass would still be running when the population is turned off below, and its late "on" status would outlive the last update.
        world.Ticks.Set(5, 10);
        world.Population.Enabled = true;
        await world.Host.World.AdvanceClockAsync(1000);
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
    public async Task ABotThatBecomesPartyDrivenAfterThePassCapturedIt_IsNotRelocated()
    {
        var veto = new TestPlacementVeto();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true,
            configureServices: services => services.AddSingleton<IPopulationPlacementVeto>(veto));
        Guid bot = await StartOneBotInElwynnAsync(world);
        await using WorldTestClient master = await world.Host.EnterWorldAsync("POPMASTER", "Popmaster");

        await SetBotAsync(world, bot, 20, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        // The capture asks the veto after it has read the bot as autonomous: leashing the bot there is the player's invitation
        // arriving between the capture and the teleport (the pass awaits the store in between).
        veto.OnCheck = id =>
        {
            veto.OnCheck = null;
            Assert.True(world.Bots.Leash(id, world.Host.World.OnlinePlayers.Single(p => p.Name == "Popmaster"), stay: true).Success);
        };
        PopulationStatus status = await world.PassAsync();

        Assert.True(world.Bots.IsPartyDriven(bot), "the bot was leashed during the capture");
        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Theory]
    [InlineData(10)]  // under the resume line: the ordinary pass would relocate
    [InlineData(50)]  // between the resume line (48) and the budget: hysteresis
    public async Task AShedOnlyPass_NeverRelocatesABot(double p95)
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);

        await SetBotAsync(world, bot, 20, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, p95);
        await world.Feature.RunPassNowAsync(shedOnly: true);

        Assert.DoesNotContain("moved", world.Feature.Status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
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

    [Theory]
    [InlineData("vetoed")]
    [InlineData("dead")]
    [InlineData("party")]
    public async Task AnUnderLevelledBot_ThatIsVetoedDeadOrPartyDriven_IsLeftWhereItIs(string why)
    {
        var veto = new TestPlacementVeto();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true,
            configureServices: services => services.AddSingleton<IPopulationPlacementVeto>(veto));
        Guid bot = await StartOneBotInElwynnAsync(world);
        await using WorldTestClient? master = why == "party" ? await world.Host.EnterWorldAsync("POPMASTER", "Popmaster") : null;

        await SetBotAsync(world, bot, 5, PopulationTestHost.Westfall);
        switch (why)
        {
            case "vetoed":
                veto.Owned.Add(bot);
                break;
            case "dead":
                await world.Host.World.InvokeAsync(() => world.Bots.FindSession(bot)!.Player!.Health = 0);
                break;
            case "party":
                Assert.True(await world.Host.World.InvokeAsync(() =>
                    world.Bots.Leash(bot, world.Host.World.OnlinePlayers.Single(p => p.Name == "Popmaster"), stay: true).Success));
                Assert.True(world.Bots.IsPartyDriven(bot));
                break;
        }

        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task TheGraveyardTerrainLookup_RunsOffTheWorldThread_AndNoPassWaitsForIt()
    {
        // The production hookup (PlayerbotPopulationFeature.AtlasFor) with a fake terrain that is slow: it holds until the test lets it
        // go. Graveyard 2 is linked to Westfall but the terrain puts it in Elwynn, so once the lookup is in Westfall has no location.
        using var release = new ManualResetEventSlim(false);
        var onWorldThread = new System.Collections.Concurrent.ConcurrentBag<bool>();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        world.Feature.TerrainLocator = location =>
        {
            onWorldThread.Add(world.Host.World.IsWorldThread);
            release.Wait(TimeSpan.FromSeconds(30));
            return location.Id == 2 ? (PopulationTestHost.Elwynn, 25u) : (PopulationTestHost.Elwynn, 24u);
        };
        world.Population.ExcludedZones = [];

        world.Ticks.Set(200, 10);
        Task<PopulationStatus> pass = world.PassAsync();
        Assert.True(await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(10))) == pass, "a pass waited for the graveyard terrain lookup");
        Assert.Equal(2, (await pass).AllianceZones); // not in yet: every link is kept

        release.Set();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        PopulationStatus status;
        do
        {
            world.Ticks.Set(200, 10);
            status = await world.PassAsync();
            if (status.AllianceZones == 1) break;
            await Task.Delay(20);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Equal(1, status.AllianceZones); // Westfall's only graveyard stands in Elwynn
        Assert.Equal([new PopulationDroppedLocation(PopulationTestHost.Westfall, 2, PopulationTestHost.Elwynn)],
            await world.Host.World.InvokeAsync(() => world.Feature.Atlas.Dropped.ToArray()));
        Assert.NotEmpty(onWorldThread);
        Assert.DoesNotContain(true, onWorldThread);
    }

    [Fact]
    public async Task WhileTheTerrainLookupRuns_NoBotIsCreated_AndOnceItIsIn_CreationGoesAhead()
    {
        // The first pass after a start or a reload would otherwise place bots from the unlocated fallback atlas (the Duskwood bug again).
        using var release = new ManualResetEventSlim(false);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        world.Feature.TerrainLocator = location =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            return (0, 0);
        };

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("held back: waiting for the graveyard terrain lookup", status.Reason);
        Assert.DoesNotContain("created 1", status.Reason);
        Assert.Empty(world.Members.Snapshot);

        release.Set();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (world.Members.Snapshot.Count == 0 && DateTime.UtcNow < deadline)
        {
            world.Ticks.Set(200, 10);
            status = await world.PassAsync();
            await Task.Delay(20);
        }

        Assert.Single(world.Members.Snapshot);
        Assert.DoesNotContain("waiting for the graveyard terrain lookup", status.Reason);
    }

    [Fact]
    public async Task AHungTerrainLookup_HoldsCreationForAtMostTheCap_ThenTheFallbackIsUsed()
    {
        using var release = new ManualResetEventSlim(false);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        world.Feature.TerrainLocator = location =>
        {
            release.Wait(TimeSpan.FromSeconds(60));
            return (0, 0);
        };

        try
        {
            for (int pass = 1; pass <= PlayerbotPopulationFeature.MaxTerrainHoldPasses; pass++)
            {
                world.Ticks.Set(200, 10);
                PopulationStatus held = await world.PassAsync();
                Assert.Contains("waiting for the graveyard terrain lookup", held.Reason);
                Assert.Empty(world.Members.Snapshot);
            }

            world.Ticks.Set(200, 10);
            PopulationStatus status = await world.PassAsync();
            Assert.True(status.Reason.Contains("created 1"), status.Reason + " / " + status.LastError);
            Assert.Single(world.Members.Snapshot);
        }
        finally
        {
            release.Set();
        }
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

    [Fact]
    public async Task TheSlackTaper_IsTheFloorRelocationUses_ABotBelowTheShrunkSlackIsMoved()
    {
        // Sentinel Hill's floor is 12. With the whole slack only up to floor 8, floor 12 keeps 4 - (12 - 8 + 1) / 2 = 2 levels of slack:
        // level 8 (12 minus the old flat 4) is now below anywhere it could have been placed in Westfall, and goes back to Elwynn.
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);

        await SetBotAsync(world, bot, 8, PopulationTestHost.Westfall);
        world.Population.ExcludedZones = [];
        world.Population.ZoneLevelSlackMaxFloor = 8;
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task TheCreatureSurvey_JoinsTheBackgroundLookup_AndABotFarBelowTheCreaturesAtItsGraveyardIsMoved()
    {
        // Level 9 at Sentinel Hill fits the area (12 minus the slack 4), but the survey says most hostile creatures there are level 20:
        // 20 minus the margin (3) is 17, so the bot is below every place of Westfall and goes back to Elwynn. The survey runs off the
        // world thread with the graveyard lookup; a radius change (part of the atlas key) starts it again.
        var onWorldThread = new System.Collections.Concurrent.ConcurrentBag<bool>();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);

        await SetBotAsync(world, bot, 9, PopulationTestHost.Westfall);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        Assert.DoesNotContain("moved", (await world.PassAsync()).Reason); // no survey yet: 9 is within the slack

        world.Feature.CreatureSurveyor = (location, team) =>
        {
            onWorldThread.Add(world.Host.World.IsWorldThread);
            return location.Id == 2 ? 20 : 0;
        };
        world.Population.CreatureSurveyRadius = 50;
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        PopulationStatus status;
        do
        {
            world.Ticks.Set(200, 10);
            status = await world.PassAsync();
            if (status.Reason.Contains("moved 1", StringComparison.Ordinal)) break;
            await Task.Delay(20);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
        Assert.NotEmpty(onWorldThread);
        Assert.DoesNotContain(true, onWorldThread);
    }

    [Fact]
    public async Task TheRealCreatureSurvey_ReadsTheCreatureContent_UnwrapsTheReputationHooks_AndSkipsEventSpawns()
    {
        // No CreatureSurveyor: the production survey (PopulationCreatureSurvey over CreatureWorldFeature.Content), with the faction
        // templates reached through the world's combat hooks wrapped in ReputationCombatHooks, as the live world registers them. Beside
        // Sentinel Hill (graveyard 2, z 34) stand three level 20 monsters, four level 60 monsters listed under a positive game event
        // (absent while no event runs), one level 60 monster 30 yards below the graveyard (another floor) and one 150 yards away.
        // Counted: 20, 20, 20 -> 20. With the event spawns it would be 60.
        const uint Monster = 9101, Raider = 9102;
        CreatureTemplate Template(uint entry, byte level) => new()
        {
            Entry = entry, Name = "Survey" + entry, MinLevel = level, MaxLevel = level, DisplayIds = [903], Faction = 14, CreatureType = 1,
            MinLevelHealth = 100, MaxLevelHealth = 100,
        };
        CreatureSpawn Spawn(uint guid, uint entry, float dx, float dz = 0f) => new()
        {
            Guid = guid, Entry = entry, MapId = 0, X = -10_653.0f + dx, Y = 1_165.0f, Z = 34.0f + dz, SpawnTimeMinSeconds = 120, SpawnTimeMaxSeconds = 120,
        };
        CreatureContent creatures = new(
            [Template(Monster, 20), Template(Raider, 60)],
            [
                Spawn(92001, Monster, 10), Spawn(92002, Monster, 20), Spawn(92003, Monster, 30),
                Spawn(92011, Raider, 11), Spawn(92012, Raider, 12), Spawn(92013, Raider, 13), Spawn(92014, Raider, 14),
                Spawn(92021, Raider, 15, dz: -30f), Spawn(92022, Raider, 150),
            ],
            [], [], []);
        ArcaneCore.World.Tests.Creatures.CreatureTestStore.Current.Value = new ArcaneCore.World.Tests.Creatures.CreatureTestContext(creatures);
        ArcaneCore.World.Tests.WorldState.GameEventTestStore.Current.Value = new ArcaneCore.Kernel.WorldData.WorldState.GameEventContent(
            [new ArcaneCore.Kernel.WorldData.WorldState.GameEventRecord(1, 1, 1440, 180, 0, 0, "Raid")],
            [new ArcaneCore.Kernel.WorldData.WorldState.GameEventTimeRecord(1, "2089-01-01 00:00:00", "2090-12-31 22:59:59")], // never running
            [.. new uint[] { 92011, 92012, 92013, 92014 }.Select(g => new ArcaneCore.Kernel.WorldData.WorldState.GameEventSpawnRecord(g, 1))],
            [], [], [], []);
        PopulationTestHost world;
        try
        {
            world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0, withRelocation: true);
        }
        finally
        {
            ArcaneCore.World.Tests.Creatures.CreatureTestStore.Current.Value = null;
            ArcaneCore.World.Tests.WorldState.GameEventTestStore.Current.Value = null;
        }

        await using (world)
        {
            var templates = new ArcaneCore.Kernel.Npc.FactionTemplateCatalog(
            [
                new ArcaneCore.Kernel.Npc.FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
                new ArcaneCore.Kernel.Npc.FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),
                new ArcaneCore.Kernel.Npc.FactionTemplateRecord(14, 7, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
            ]);
            Assert.Null(world.Host.WorldServices.GetService<ArcaneCore.Kernel.Npc.FactionTemplateCatalog>()); // the hooks are the only source
            // The event rows really reached the game-event service (a rejected event would leave the gate silently empty).
            Assert.Equal([92011u, 92012u, 92013u, 92014u], world.Host.WorldServices.GetRequiredService<ArcaneCore.World.WorldState.GameEventFeature>()
                .Service!.Rows.Creatures[1].Order().ToArray());
            ArcaneCore.Game.Combat.CombatHooks.Register(world.Host.World, new ArcaneCore.Game.Reputation.ReputationCombatHooks(
                new ArcaneCore.Game.Combat.FactionCombatHooks(templates),
                new ArcaneCore.Game.Reputation.ReputationReactionResolver(templates, ArcaneCore.Kernel.Reputation.FactionCatalog.Empty, _ => null)));
            await PollAsync(() => world.Host.WorldServices.GetRequiredService<ArcaneCore.World.Creatures.CreatureWorldFeature>().ContentInstalled,
                "the creature content");
            world.Population.ExcludedZones = [];

            IReadOnlyDictionary<(uint LocationId, Team Team), int>? levels = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                world.Ticks.Set(200, 10);
                await world.PassAsync();
                levels = await world.Host.World.InvokeAsync(() => world.Feature.Atlas.Find(PopulationTestHost.Westfall)?.CreatureLevels);
                if (levels is { Count: > 0 }) break;
                await Task.Delay(20);
            }

            Assert.NotNull(levels);
            Assert.Equal(20, Assert.Contains((2u, Team.Alliance), levels));
            Assert.Equal(20, Assert.Contains((2u, Team.Horde), levels));
            PopulationZone westfall = await world.Host.World.InvokeAsync(() => world.Feature.Atlas.Find(PopulationTestHost.Westfall)!);
            Assert.Equal(17, westfall.LowestLevelFor(Team.Alliance, PopulationLevelRule.From(world.Population))); // 20 - 3, above 12 - 4
        }
    }

    [Fact]
    public async Task OperatorBotsInABand_DoNotSuppressThatBandsCreation_TheyOnlyReduceTheTotal()
    {
        // Live 2026-10-11: 201 operator bots (Wfbot*, Stressbot*) at levels 11-20 were counted as that band's "have", above its
        // share of the target, so the population never created an 11-20 bot. Here two operator bots stand in band 1-10 (the one band
        // the host can create in without the boost content).
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 6;
            o.Population.TargetOnline = 4;
            o.Population.Bands = [new PopulationBand { MinLevel = 1, MaxLevel = 10, Weight = 1 }, new PopulationBand { MinLevel = 11, MaxLevel = 20, Weight = 1 }];
        });
        foreach (string name in new[] { "Operatorone", "Operatortwo" })
        {
            PlayerbotOperationResult made = await world.Bots.CreateAsync(name, 1, 1);
            Assert.True((await world.Bots.StartAsync(Assert.IsType<Guid>(made.BotId).ToString())).Success);
        }

        await world.WaitForSettledAsync(2);

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        // The operator bots take 2 of the 4 from the total; the population's own 2 are split by weight, 1 per band. Band 1-10 has none
        // of its own yet, so it gets one (band 11-20 needs the boost, which the host does not have).
        Assert.Equal("under-budget", status.State);
        Assert.True(status.Reason.Contains("created 1"), status.Reason + " / " + status.LastError);
        Assert.Equal((1, 1), (status.Bands[0].Want, status.Bands[0].Have));
        Assert.Equal((1, 0), (status.Bands[1].Want, status.Bands[1].Have));
        Assert.Single(world.Members.Snapshot);
    }

    [Fact]
    public async Task APassThatChangesSomething_LogsOneInformationLine_AtMostOncePerActionLogInterval()
    {
        var log = new CapturingLogger();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1,
            configureServices: s => s.AddSingleton<Microsoft.Extensions.Logging.ILogger<PlayerbotPopulationFeature>>(log));
        static bool IsAction(string line) => line.StartsWith("Playerbot population: parked ", StringComparison.Ordinal);

        world.Ticks.Set(200, 10);
        await world.PassAsync();
        string first = Assert.Single(log.Infos, IsAction);
        Assert.Contains("parked 0, started 0, created 1", first);
        Assert.Contains("state under-budget", first);
        Assert.Contains("bands 1-10 want 1 have 1", first);
        await world.WaitForSettledAsync(1);

        // A pass that changes nothing logs nothing; a second change inside the interval is held for the next line.
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        world.Population.TargetOnline = 2;
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        Assert.Single(log.Infos, IsAction);
        await world.WaitForSettledAsync(2);

        // A minute of world time later the held change is logged by the next pass, even one that changes nothing itself.
        await world.Host.World.InvokeAsync(() => { world.Feature.ClockMs += 61_000; return true; });
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("at-target", status.State);
        Assert.Equal(2, log.Infos.Count(IsAction));
        string second = log.Infos.Last(IsAction);
        Assert.Contains("parked 0, started 0, created 1", second);
        Assert.Contains("bands 1-10 want 2 have 2", second);
    }

    [Fact]
    public async Task OverTheBudget_ADeadBotIsParkedBeforeAHealthyOne_AndTheStatusSaysWhy()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(2);
        Guid[] bots = [.. world.Members.Snapshot.Select(m => m.BotId)];
        // The bot a band-fullness or most-recently-started rule would not prefer: the later one by BotId and start.
        Guid dead = bots.Max();
        await world.Host.World.InvokeAsync(() => world.Bots.FindSession(dead)!.Player!.Health = 0);

        world.Ticks.Set(200, 80);
        PopulationStatus status = await world.PassAsync();

        Assert.Equal("over-budget", status.State);
        Assert.Contains("parked 1 (dead 1)", status.Reason);
        await world.Host.WaitForWorldAsync(() => world.RunningCount == 1, "one bot parked");
        Assert.NotEqual(ManagedPlayerbotState.Running, Assert.Single(world.Bots.Snapshot(), s => s.BotId == dead).State);
    }

    [Fact]
    public async Task TheRotation_RestartsTheBotParkedLongestAgoFirst()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 3);
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(3);

        // Park two, one pass and some world time apart (MaxParksPerInterval 1). The bot with the highest BotId dies first, so it is the
        // first one parked: a rule by row age or BotId would bring back the other one.
        Guid highest = world.Members.Snapshot.Max(m => m.BotId);
        await world.Host.World.InvokeAsync(() => world.Bots.FindSession(highest)!.Player!.Health = 0);
        var parked = new List<Guid>();
        foreach (int target in new[] { 2, 1 })
        {
            HashSet<Guid> before = [.. world.Bots.Snapshot().Where(s => s.State == ManagedPlayerbotState.Running).Select(s => s.BotId)];
            world.Population.TargetOnline = target;
            world.Ticks.Set(200, 10);
            Assert.Contains("parked 1", (await world.PassAsync()).Reason);
            await world.Host.WaitForWorldAsync(() => world.RunningCount == target, "a bot parked");
            parked.Add(Assert.Single(before, id => world.Bots.Snapshot().Single(s => s.BotId == id).State != ManagedPlayerbotState.Running));
            await world.Host.World.InvokeAsync(() => { world.Feature.ClockMs += 5_000; return true; });
        }

        foreach (Guid bot in parked)
        {
            string name = world.Bots.Snapshot().Single(s => s.BotId == bot).Name;
            await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer(name) is null, "the parked bot logged out");
        }

        // Room for one again: the bot parked first comes back, not the one parked last.
        world.Population.TargetOnline = 2;
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("started 1, created 0", status.Reason);
        await world.WaitForSettledAsync(2);
        Assert.Equal(highest, parked[0]);
        Assert.Equal(ManagedPlayerbotState.Running, world.Bots.Snapshot().Single(s => s.BotId == parked[0]).State);
        Assert.NotEqual(ManagedPlayerbotState.Running, world.Bots.Snapshot().Single(s => s.BotId == parked[1]).State);
    }

    [Fact]
    public async Task ConfirmWindows_OneSlowWindowDoesNotPark_TheSecondDoes_AndTheCountStartsAgainAfterThePark()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.ConfirmWindows = 2);
        world.Ticks.Set(200, 10);
        Assert.Equal("under-budget-confirming", (await world.PassAsync()).State);
        world.Ticks.Set(200, 10);
        Assert.Contains("created 2", (await world.PassAsync()).Reason);
        await world.WaitForSettledAsync(2);

        world.Ticks.Set(200, 80);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("over-budget-confirming", status.State);
        Assert.Equal(2, world.RunningCount);

        world.Ticks.Set(200, 80);
        status = await world.PassAsync();
        Assert.Equal("over-budget", status.State);
        Assert.Contains("parked 1", status.Reason);
        await world.Host.WaitForWorldAsync(() => world.RunningCount == 1, "one bot parked");

        // The park reset the window and the count: the next slow window is the first again.
        world.Ticks.Set(200, 80);
        Assert.Equal("over-budget-confirming", (await world.PassAsync()).State);
        Assert.Equal(1, world.RunningCount);
    }

    [Fact]
    public async Task Relocations_DoNotResetTheConfirmWindows_AnAddStillHappensWhileEveryPassMovesAMisfit()
    {
        // A levelling population keeps producing zone misfits. A pass that only moved bots used to reset the confirmation windows, so with
        // ConfirmWindows 2 two quiet windows in a row never came and no bot was ever added.
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        world.Population.ExcludedZones = [];
        world.Population.ConfirmWindows = 2;
        world.Population.TargetOnline = 2;

        await SetBotAsync(world, bot, 20, PopulationTestHost.Elwynn);
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("under-budget-confirming", status.State);
        Assert.Contains("moved 1", status.Reason);

        // The move is a teleport the bot acknowledges on its own think; until then it counts as busy and the next pass skips it. Once landed
        // the brain may start a leg or a fight at any think, which skips the bot just the same, and a pass reads the bot in several world-thread
        // steps (the facts, the transit recheck, the relocation) with ticks and thinks between them. So the same step that sees the bot free
        // also stops its thinks (the bot stays autonomous, which the relocation needs), and they stay stopped for the rest of the test.
        await world.WaitForSettledAsync(1);
        await SetBotWhenFreeAndHoldItsThinksAsync(world, bot, 20, PopulationTestHost.Elwynn); // a misfit again
        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Equal("under-budget", status.State);
        Assert.Contains("moved 1", status.Reason);
        Assert.Matches("started 1|created 1", status.Reason);
    }

    [Fact]
    public async Task OneBurstOfSlowTicks_IsNotCountedByTwoPasses_WhenTheRingStillHoldsItsTicks()
    {
        // With IntervalSeconds 10 and WindowTicks 200 a tick slower than 50 ms makes consecutive passes read the same ticks. Every pass
        // reading 200 samples was a window, so one burst confirmed a park. Only WindowTicks new ticks make the next window (20 here).
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.ConfirmWindows = 2);
        var ring = new WorldTickWindow();
        world.Feature.TickSource = ring;
        void Feed(int count, long micros) { for (int i = 0; i < count; i++) ring.Add(micros); }

        Feed(20, 10_000);
        Assert.Equal("under-budget-confirming", (await world.PassAsync()).State);
        Feed(20, 10_000);
        Assert.Contains("created 2", (await world.PassAsync()).Reason);
        await world.WaitForSettledAsync(2);

        Feed(20, 80_000);
        Assert.Equal("over-budget-confirming", (await world.PassAsync()).State);
        Feed(10, 80_000); // ten new ticks: the last 20 are the same burst, 10 of them already counted
        Assert.Equal("over-budget-confirming", (await world.PassAsync()).State);
        Assert.Equal(2, world.RunningCount);
        Feed(10, 80_000); // twenty new ticks since the counted window: a second window
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("over-budget", status.State);
        Assert.Contains("parked 1", status.Reason);
    }

    [Fact]
    public async Task AntiChurn_ABotStartedLessThanMinRunSecondsAgo_IsNotParkedForTheBudget()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.MinRunSeconds = 300);
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(2);

        world.Ticks.Set(200, 80);
        PopulationStatus status = await world.PassAsync();
        Assert.Equal("over-budget", status.State);
        Assert.Contains("parked 0", status.Reason);
        Assert.Equal(2, world.RunningCount);

        await world.Host.World.InvokeAsync(() => { world.Feature.ClockMs += 300_000; return true; });
        world.Ticks.Set(200, 80);
        status = await world.PassAsync();
        Assert.Contains("parked 1 (rotation 1)", status.Reason);
    }

    [Fact]
    public async Task ABandItsParkedBotsCannotFill_GetsNewBotsAlongsideTheRotation_NotOnlyAfterEveryParkedBotIsBack()
    {
        // Under a tick budget some bots are always parked; when restarts took every slot first, a band its parked bots could not fill
        // waited for creation for as long as anything was parked.
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 6;
            o.Population.TargetOnline = 5;
            o.Population.MaxStartsPerInterval = 2;
        });
        foreach (string name in new[] { "Parkedone", "Parkedtwo", "Parkedthree" })
        {
            PlayerbotOperationResult made = await world.Bots.CreateAsync(name, 1, 1);
            ManagedPlayerbot record = Assert.IsType<ManagedPlayerbot>(world.Owners.Peek(Assert.IsType<Guid>(made.BotId)));
            world.Members.Seed(new PlayerbotPopulationMember(record.BotId, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 100));
        }

        // Band 1-10 wants 5: 3 parked bots can fill 3 of it, so 2 must be new. Two slots: one new bot, one restart.
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("started 1, created 1", status.Reason);
        Assert.Equal(4, world.Members.Snapshot.Count);
    }

    [Fact]
    public async Task ABandNoZoneTakes_IsLoggedOnceTheAtlasIsLocated()
    {
        var log = new CapturingLogger();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 1;
            o.Population.Bands = [new PopulationBand { MinLevel = 1, MaxLevel = 10, Weight = 1 }, new PopulationBand { MinLevel = 41, MaxLevel = 50, Weight = 1 }];
        }, withRelocation: true, configureServices: s => s.AddSingleton<Microsoft.Extensions.Logging.ILogger<PlayerbotPopulationFeature>>(log));
        world.Feature.TerrainLocator = _ => (0, 0);

        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!log.Warnings.Any(w => w.Contains("no zone takes", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            world.Ticks.Set(200, 10);
            await world.PassAsync();
            await Task.Delay(20);
        }

        string warning = Assert.Single(log.Warnings, w => w.Contains("no zone takes", StringComparison.Ordinal));
        Assert.Contains("levels 41-50 for the Alliance", warning);
        Assert.DoesNotContain("levels 1-10", warning);
        Assert.DoesNotContain("Horde", warning); // the host creates Alliance bots only

        // Logged once per atlas and rule, not at every pass.
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        Assert.Single(log.Warnings, w => w.Contains("no zone takes", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<PlayerbotPopulationFeature>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _warnings = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _infos = new();

        public IReadOnlyList<string> Warnings => [.. _warnings];

        public IReadOnlyList<string> Infos => [.. _infos];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) _warnings.Enqueue(formatter(state, exception));
            else if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information) _infos.Enqueue(formatter(state, exception));
        }
    }

    private static Task SetBotAsync(PopulationTestHost world, Guid bot, byte level, uint zone) => world.Host.World.InvokeAsync(() =>
    {
        Player player = world.Bots.FindSession(bot)!.Player!;
        player.Level = level;
        player.ZoneId = zone;
        return true;
    });

    /// <summary>
    /// <see cref="SetBotAsync"/> once the bot is one a pass can move (in the world, out of combat, no teleport pending, no leg under way: that is covered
    /// by <see cref="PopulationProvisioner.IsInTransit"/>), checked and set in the same world-thread step. That step also sets the think interval
    /// so high that no bot brain thinks again: with no think the bot starts no leg and no fight, so what the step saw holds through every
    /// world-thread step of the pass that follows. The 3_600_000 ms deliberately bypasses <see cref="PlayerbotOptions.Validate"/>'s 50..60000 range
    /// (the host's options object is mutated after validation). The "no fight" half rests on nothing being near the bot, which the step checks
    /// instead of assuming: it does not hold (the step returns false and is retried) while any live creature is within
    /// <see cref="HostileFreeRadius"/> yards, far beyond any creature's aggro range, so no creature can aggro the bot between the step and the pass.
    /// The options are the host's own and the test never gives the interval back: the host is disposed when the test ends.
    /// </summary>
    private static Task SetBotWhenFreeAndHoldItsThinksAsync(PopulationTestHost world, Guid bot, byte level, uint zone) => world.Host.WaitForWorldAsync(() =>
    {
        if (world.Bots.FindSession(bot)?.Player is not { IsInWorld: true } player) return false;
        if (!player.IsAlive || player.Combat.IsInCombat || PopulationProvisioner.IsInTransit(player)) return false;
        if (world.Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player)) return false;
        if (player.Map?.FindUpdater<ArcaneCore.Game.Creatures.CreatureMapSystem>()?.Creatures.Any(c =>
            {
                float dx = c.X - player.X, dy = c.Y - player.Y;
                return c.IsAlive && (dx * dx) + (dy * dy) <= HostileFreeRadius * HostileFreeRadius;
            }) == true) return false;
        player.Level = level;
        player.ZoneId = zone;
        world.Options.ThinkIntervalMs = 3_600_000;
        return true;
    }, "the bot is free to be moved again");

    /// <summary>Yards around the bot that hold no live creature when its thinks are held (a creature's aggro range is 18 yards by default).</summary>
    private const float HostileFreeRadius = 60f;

    private sealed class TestPlacementVeto : IPopulationPlacementVeto
    {
        public HashSet<Guid> Owned { get; } = [];

        /// <summary>Run once, on the world thread, the next time the population asks about a bot (after it has read the bot's autonomy).</summary>
        public Action<Guid>? OnCheck { get; set; }

        public bool OwnsPlacement(Guid botId)
        {
            OnCheck?.Invoke(botId);
            return Owned.Contains(botId);
        }
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

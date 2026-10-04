using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Reload;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// <c>.reload game_event</c> (ArcaneCore-only, off unless <c>World:GameEvents:AllowReload</c>): rebuild the tables off the world thread, reject
/// a bad candidate naming its rows, and swap on the world thread keeping what still runs. The world runs on its own thread (the
/// reload commits there); the clock is a <see cref="FixedGameTime"/>, the event windows are hours long, so the real 5 ms ticks that run
/// in the background never reach an update by themselves.
/// </summary>
public sealed class GameEventReloadTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private sealed class MutableStore : IGameEventDataStore
    {
        public GameEventContent Content { get; set; } = GameEventContent.Empty;

        public Exception? Failure { get; set; }

        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Failure is null ? Task.FromResult(Content) : Task.FromException<GameEventContent>(Failure);

        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Recorder : IGameEventEffects, IGameEventListener
    {
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public void Clear()
        {
            lock (_calls)
            {
                _calls.Clear();
            }
        }

        private void Add(string call)
        {
            lock (_calls)
            {
                _calls.Add(call);
            }
        }

        public void SpawnEvent(int signedEventId) => Add($"spawn {signedEventId}");

        public void UnspawnEvent(int signedEventId) => Add($"unspawn {signedEventId}");

        public void OnEventChanged(ushort eventId, bool active, bool resume) => Add($"changed {eventId} {active} resume={resume}");
    }

    private static GameEventRecord Event(uint id, string name, uint length = 120, uint linkedTo = 0) => new(id, 1, 1440, length, 0, linkedTo, name);

    private static GameEventTimeRecord Time(uint id, string start) => new(id, start, "2030-12-31 22:59:59");

    /// <summary>Event 1 runs 12:00-14:00 every day and event 2 opens at 20:00 (clock 12:30, so event 1 is running).</summary>
    private static GameEventContent Tables()
        => new(
            [Event(1, "Alpha"), Event(2, "Beta")],
            [Time(1, "2026-10-03 12:00:00"), Time(2, "2026-10-03 20:00:00")],
            [], [], [], [], []);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Rig(GameEventContent initial)
        {
            Store = new MutableStore { Content = initial };
            var collection = new ServiceCollection();
            collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["World:GameEvents:AllowReload"] = "true", ["HotReload:Commands"] = "true" }).Build());
            collection.AddScoped<IGameEventDataStore>(_ => Store);
            collection.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            _services = collection.BuildServiceProvider();
            World = new WorldRuntime(
                new WorldRuntimeOptions { AutosaveIntervalMs = 0, TickIntervalMs = 5 },
                new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            Clock = new FixedGameTime(Utc(2026, 10, 3, 12, 30));
            WorldStateHooks.For(World).Time = Clock;
            Feature = _services.GetRequiredService<GameEventFeature>();
            Feature.ServiceCreated += service => service.AddEffects(Recorder);
            Feature.AddListener(Recorder);
            Feature.Attach(World);
            Reloadable = new GameEventReloadable(_services);
            Coordinator = new ReloadCoordinator(NullLogger.Instance);
            Coordinator.Attach(World);
            Coordinator.Register(Reloadable);
            World.Start(); // a real world thread: the reload commits on it, and the first tick initialises the events
        }

        public static async Task<Rig> StartAsync(GameEventContent initial)
        {
            var rig = new Rig(initial);
            for (int i = 0; i < 2000 && !await rig.OnWorldAsync(() => rig.Feature.Service?.IsInitialised == true); i++)
            {
                await Task.Delay(5);
            }

            Assert.True(await rig.OnWorldAsync(() => rig.Feature.Service?.IsInitialised == true), "the first tick did not initialise the game events");
            return rig;
        }

        public MutableStore Store { get; }

        public WorldRuntime World { get; }

        public FixedGameTime Clock { get; }

        public GameEventFeature Feature { get; }

        public GameEventReloadable Reloadable { get; }

        public ReloadCoordinator Coordinator { get; }

        public Recorder Recorder { get; } = new();

        /// <summary>Run on the world thread (the feature is single-threaded there).</summary>
        public Task<T> OnWorldAsync<T>(Func<T> read) => World.InvokeAsync(read);

        public Task<bool> ActiveAsync(ushort id) => World.InvokeAsync(() => Feature.IsActiveEvent(id));

        public ValueTask DisposeAsync()
        {
            World.Dispose();
            _services.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task TheReload_ExistsOnlyWithItsSwitch()
    {
        using ServiceProvider off = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["World:GameEvents:AllowReload"] = "false" }).Build())
            .BuildServiceProvider();
        using ServiceProvider none = new ServiceCollection().BuildServiceProvider();

        Assert.False(new GameEventReloadable(off).IsEnabled);
        Assert.False(new GameEventReloadable(none).IsEnabled);          // no configuration at all: off
        Assert.False(new GameEventReloadable(none).IncludedInAll);       // retail has no such reload, so `.reload all` skips it
        await using Rig rig = await Rig.StartAsync(Tables());
        Assert.True(rig.Reloadable.IsEnabled);
    }

    [Fact]
    public async Task TheReloadFeature_LeavesTheNameOut_UnlessTheSwitchIsOn()
    {
        static WorldTestHost Start(bool allow) => WorldTestHost.Start(configureServices: c => c.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["World:GameEvents:AllowReload"] = allow ? "true" : "false",
                ["HotReload:Commands"] = "true",
            }).Build()));

        await using (WorldTestHost off = Start(false))
        {
            Assert.DoesNotContain("game_event", off.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.Names);
            Assert.Contains("game_weather", off.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.Names);
        }

        await using (WorldTestHost on = Start(true))
        {
            Assert.Contains("game_event", on.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.Names);
        }
    }

    [Fact]
    public async Task Reload_AddsRemovesAndReschedules_AndReportsWhatChanged()
    {
        await using Rig rig = await Rig.StartAsync(Tables());
        Assert.True(await rig.ActiveAsync(1));
        Assert.False(await rig.ActiveAsync(2));
        rig.Recorder.Clear();

        // event 1 moves to tomorrow (its window is no longer open), event 2 is gone, event 3 is new and open now
        rig.Store.Content = new GameEventContent(
            [Event(1, "Alpha"), Event(3, "Gamma")],
            [Time(1, "2026-10-04 12:00:00"), Time(3, "2026-10-03 12:00:00")],
            [], [], [], [], []);

        ReloadResult result = await rig.Coordinator.ReloadAsync("game_event");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Contains("2 game events", result.Message, StringComparison.Ordinal);
        Assert.Contains(result.Notes, n => n == "events added: 3");
        Assert.Contains(result.Notes, n => n == "events removed: 2");
        Assert.Contains(result.Notes, n => n == "events changed (rescheduled by the next update): 1");
        Assert.False(await rig.ActiveAsync(1));  // its new window is tomorrow: the new service stopped it at once
        Assert.True(await rig.ActiveAsync(3));
        Assert.Contains("changed 1 False resume=False", rig.Recorder.Calls);
        Assert.Contains("changed 3 True resume=False", rig.Recorder.Calls);
        Assert.Equal(["Alpha", "Gamma"], await rig.OnWorldAsync(() => rig.Feature.Service!.Events.Select(e => e.Description).ToArray()));
    }

    [Fact]
    public async Task ARunningEventThatLeftTheTable_IsStopped_WithItsEffectsUndone()
    {
        await using Rig rig = await Rig.StartAsync(Tables());
        rig.Recorder.Clear();
        rig.Store.Content = new GameEventContent([Event(2, "Beta")], [Time(2, "2026-10-03 20:00:00")], [], [], [], [], []);

        ReloadResult result = await rig.Coordinator.ReloadAsync("game_event");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.False(await rig.ActiveAsync(1));
        Assert.Contains("unspawn 1", rig.Recorder.Calls);      // the stop ran the effects: positive objects go
        Assert.Contains("spawn -1", rig.Recorder.Calls);
        Assert.Contains("changed 1 False resume=False", rig.Recorder.Calls);
        Assert.Contains(result.Notes, n => n == "running events stopped because they left the table: 1");
    }

    [Fact]
    public async Task AnUnchangedRunningEvent_KeepsRunning_WithoutAStopOrAnObjectFlicker()
    {
        await using Rig rig = await Rig.StartAsync(Tables());
        Assert.True(await rig.ActiveAsync(1));
        rig.Recorder.Clear();
        GameEventContent same = Tables();
        rig.Store.Content = new GameEventContent([.. same.Events, Event(9, "Extra")], [.. same.Times, Time(9, "2026-10-05 00:00:00")], [], [], [], [], []);

        ReloadResult result = await rig.Coordinator.ReloadAsync("game_event");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.True(await rig.ActiveAsync(1));
        Assert.DoesNotContain("unspawn 1", rig.Recorder.Calls);       // never un-applied: it keeps its spawns
        Assert.DoesNotContain(rig.Recorder.Calls, c => c.StartsWith("changed 1 ", StringComparison.Ordinal)); // not re-announced: it never stopped
        ushort[] running = await rig.OnWorldAsync(() => rig.Feature.ActiveEvents.ToArray());
        Assert.Equal<ushort>([1], running);
    }

    [Fact]
    public async Task ARejectedCandidate_NamesTheRows_AndLeavesTheLiveStateAlone()
    {
        await using Rig rig = await Rig.StartAsync(Tables());
        rig.Recorder.Clear();
        rig.Store.Content = new GameEventContent(
            [Event(1, "Alpha"), Event(5, "Orphan", linkedTo: 99), Event(0, "Reserved")],
            [Time(1, "2026-10-03 12:00:00"), Time(5, "garbage")],
            [], [], [], [], []);

        ReloadResult result = await rig.Coordinator.ReloadAsync("game_event");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        string text = result.Message + " " + string.Join(' ', result.Notes);
        Assert.Contains("game_event 5 is linked to invalid event 99", text, StringComparison.Ordinal);
        Assert.Contains("game_event id 0 is reserved", text, StringComparison.Ordinal);
        Assert.Contains("start_time 'garbage'", text, StringComparison.Ordinal);
        Assert.True(await rig.ActiveAsync(1));
        Assert.Equal(["Alpha", "Beta"], await rig.OnWorldAsync(() => rig.Feature.Service!.Events.Select(e => e.Description).ToArray()));
        Assert.Empty(rig.Recorder.Calls);
    }

    [Fact]
    public async Task AFailingStore_ChangesNothing()
    {
        await using Rig rig = await Rig.StartAsync(Tables());
        rig.Store.Failure = new InvalidOperationException("world database unavailable");

        ReloadResult result = await rig.Coordinator.ReloadAsync("game_event");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world database unavailable", result.Message, StringComparison.Ordinal);
        Assert.True(await rig.ActiveAsync(1));
    }

    [Fact]
    public async Task ReloadingTheOldTablesBack_RestoresTheOldEvents_WhichIsWhatARollbackDoes()
    {
        await using Rig rig = await Rig.StartAsync(Tables());
        GameEventContent original = await rig.OnWorldAsync(() => rig.Feature.Content);
        rig.Store.Content = new GameEventContent([Event(7, "Seven")], [Time(7, "2026-10-03 12:00:00")], [], [], [], [], []);
        Assert.Equal(ReloadStatus.Applied, (await rig.Coordinator.ReloadAsync("game_event")).Status);
        Assert.False(await rig.ActiveAsync(1));
        Assert.True(await rig.ActiveAsync(7));

        // the roll-back of a failed commit is UseContent(previous, running, continueRunning: true)
        await rig.OnWorldAsync(() =>
        {
            rig.Feature.UseContent(original, new HashSet<ushort> { 1 }, continueRunning: true);
            return true;
        });

        Assert.True(await rig.ActiveAsync(1));
        Assert.False(await rig.ActiveAsync(7));
        Assert.Equal(["Alpha", "Beta"], await rig.OnWorldAsync(() => rig.Feature.Service!.Events.Select(e => e.Description).ToArray()));
    }

    [Fact]
    public async Task ListenersAddedToTheFeature_SurviveAReload_ButOnesAddedToTheOldServiceDoNot()
    {
        await using Rig rig = await Rig.StartAsync(Tables());
        var onService = new Recorder();
        await rig.OnWorldAsync(() =>
        {
            rig.Feature.Service!.AddListener(onService);
            return true;
        });
        rig.Store.Content = new GameEventContent([Event(1, "Alpha")], [Time(1, "2026-10-04 12:00:00")], [], [], [], [], []);

        await rig.Coordinator.ReloadAsync("game_event");

        Assert.Contains("changed 1 False resume=False", rig.Recorder.Calls);          // the feature-level listener saw the stop
        rig.Recorder.Clear();
        onService.Clear();
        rig.Clock.UtcNow = Utc(2026, 10, 4, 12, 30);
        await rig.OnWorldAsync(() =>
        {
            rig.World.RunTick(uint.MaxValue / 2);
            return true;
        });
        Assert.Contains("changed 1 True resume=False", rig.Recorder.Calls);           // still told, by the new service
        Assert.Empty(onService.Calls);                                                 // the old service's own listener is gone
    }
}

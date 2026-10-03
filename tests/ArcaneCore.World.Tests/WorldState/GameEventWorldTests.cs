using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// <see cref="GameEventFeature"/>: tables and the stored running set at start-up, the first tick initialising, the accumulated
/// delay between updates, persistence that never throws on the world thread, and the announcement. Time is a
/// <see cref="FixedGameTime"/> and an unstarted <see cref="WorldRuntime"/> that the tests tick by hand; the one end-to-end test waits on a
/// condition, not on a duration.
/// </summary>
public sealed class GameEventWorldTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private sealed class FakeDataStore(GameEventContent content) : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);

        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeStatusStore(params ushort[] stored) : IGameEventStatusStore
    {
        private readonly List<int[]> _writes = [];

        public bool Failing { get; set; }

        public IReadOnlyList<int[]> Writes
        {
            get
            {
                lock (_writes)
                {
                    return [.. _writes];
                }
            }
        }

        public Task<IReadOnlyList<int>> LoadActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<int>>([.. stored.Select(s => (int)s)]);

        public Task ReplaceActiveAsync(IReadOnlyCollection<int> events, CancellationToken cancellationToken = default)
        {
            if (Failing)
            {
                return Task.FromException(new InvalidOperationException("characters database unavailable"));
            }

            lock (_writes)
            {
                _writes.Add([.. events]);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Listener : IGameEventListener
    {
        public List<string> Calls { get; } = [];

        public void OnEventChanged(ushort eventId, bool active, bool resume) => Calls.Add($"{eventId} {active} resume={resume}");
    }

    /// <summary>Event 1: opens at <paramref name="start"/>, two hours long, every day (mangos-classic dialect: dates in game_event_time).</summary>
    private static GameEventContent Event1At(DateTimeOffset start)
        => new(
            [new GameEventRecord(1, 1, 1440, 120, 0, 0, "Test Festival")],
            [new GameEventTimeRecord(1, start.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), "2030-12-31 22:59:59")],
            [], [], [], [], []);

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _services;

        public Rig(GameEventContent? content, FakeStatusStore? status, DateTimeOffset now, Dictionary<string, string?>? config = null)
        {
            var collection = new ServiceCollection();
            if (content is not null)
            {
                collection.AddScoped<IGameEventDataStore>(_ => new FakeDataStore(content));
            }

            if (status is not null)
            {
                collection.AddScoped<IGameEventStatusStore>(_ => status);
            }

            collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build());
            _services = collection.BuildServiceProvider();
            var saves = new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
            Clock = new FixedGameTime(now);
            WorldStateHooks.For(World).Time = Clock;
            Feature = new GameEventFeature(_services, NullLogger<GameEventFeature>.Instance);
            Feature.Attach(World);
        }

        public WorldRuntime World { get; }

        public FixedGameTime Clock { get; }

        public GameEventFeature Feature { get; }

        public void Dispose()
        {
            World.Dispose();
            _services.Dispose();
        }
    }

    [Fact]
    public async Task Attach_LoadsTheTablesAndTheStoredSet_AndTheFirstTickInitialisesAndResumes()
    {
        var status = new FakeStatusStore(1);
        using var rig = new Rig(Event1At(Utc(2026, 10, 3, 12)), status, Utc(2026, 10, 3, 12, 30));
        var listener = new Listener();
        Assert.NotNull(rig.Feature.Service);
        rig.Feature.Service!.AddListener(listener);
        Assert.False(rig.Feature.IsActiveEvent(1)); // nothing runs before the first tick

        rig.World.RunTick(10);

        Assert.True(rig.Feature.IsActiveEvent(1));
        Assert.Equal([(ushort)1], rig.Feature.ActiveEvents);
        Assert.Equal(["1 True resume=True"], listener.Calls); // it was running at shutdown: a resume
        await rig.Feature.StopAsync(); // drains the status write
        // The writer coalesces (the latest set wins), so the invariant is the final stored state: the resumed event is recorded again.
        Assert.Equal([1], status.Writes[^1]);
    }

    [Fact]
    public void TheNextUpdate_WaitsForTheDelayTheLastOneAskedFor_ThenFlipsTheEventInThatTick()
    {
        using var rig = new Rig(Event1At(Utc(2026, 10, 3, 12)), null, Utc(2026, 10, 3, 11));
        rig.World.RunTick(0); // initialise: the window opens in an hour, so the next update is due in 3601 s
        Assert.False(rig.Feature.IsActiveEvent(1));

        rig.Clock.UtcNow = Utc(2026, 10, 3, 12, 5);   // the clock is past the start ...
        rig.World.RunTick(1_000_000);                 // ... but only 1000 s of the 3601 s have gone by
        Assert.False(rig.Feature.IsActiveEvent(1));

        rig.World.RunTick(2_000_000);                 // 3000 s
        Assert.False(rig.Feature.IsActiveEvent(1));

        rig.World.RunTick(700_000);                   // 3700 s: the delay has passed, the update runs and flips it in this tick
        Assert.True(rig.Feature.IsActiveEvent(1));
    }

    [Fact]
    public void WithGameEventsSwitchedOff_NothingRuns()
    {
        using var rig = new Rig(Event1At(Utc(2026, 10, 3, 12)), null, Utc(2026, 10, 3, 12, 30), new Dictionary<string, string?> { ["World:GameEvents:Enabled"] = "false" });

        rig.World.RunTick(10_000);

        Assert.NotNull(rig.Feature.Service); // the tables are loaded (their spawns stay out of the world) ...
        Assert.False(rig.Feature.Service!.IsInitialised); // ... but the system never initialises
        Assert.False(rig.Feature.IsActiveEvent(1));
        Assert.Empty(rig.Feature.ActiveEvents);
    }

    [Fact]
    public void WithoutTables_NothingRuns_AndTheOptionsAreBound()
    {
        using var rig = new Rig(null, null, Utc(2026, 10, 3, 12, 30), new Dictionary<string, string?> { ["World:GameEvents:Announce"] = "true", ["World:GameEvents:LeapDayMode"] = "VmangosLiteral" });

        rig.World.RunTick(10);

        Assert.Empty(rig.Feature.ActiveEvents);
        GameEventOptions options = WorldStateHooks.For(rig.World).GameEventSettings;
        Assert.True(options.Announce);
        Assert.Equal(LeapDayMode.VmangosLiteral, options.LeapDayMode);
    }

    [Fact]
    public async Task AFailingStatusStore_NeverReachesTheWorldThread_AndTheRetainedSetIsRetriedAtShutdown()
    {
        var status = new FakeStatusStore { Failing = true };
        using var rig = new Rig(Event1At(Utc(2026, 10, 3, 12)), status, Utc(2026, 10, 3, 12, 30));

        rig.World.RunTick(10); // the event starts; storing it fails, off the world thread
        Assert.True(rig.Feature.IsActiveEvent(1));
        Assert.Empty(status.Writes);

        status.Failing = false;
        await rig.Feature.StopAsync(); // the shutdown flush retries what was retained

        Assert.Equal([1], status.Writes[^1]);
    }

    [Fact]
    public async Task EachChange_OfTheRunningSet_IsStored_AndTheLatestWins()
    {
        var status = new FakeStatusStore();
        using var rig = new Rig(Event1At(Utc(2026, 10, 3, 12)), status, Utc(2026, 10, 3, 12, 30));
        rig.World.RunTick(0);
        rig.Clock.UtcNow = Utc(2026, 10, 3, 15);
        rig.World.RunTick(uint.MaxValue / 2); // far past the delay: the event ends

        Assert.False(rig.Feature.IsActiveEvent(1));
        await rig.Feature.StopAsync();
        Assert.Empty(status.Writes[^1]); // the writer coalesces, so the invariant is the final stored state: nothing is running
    }

    [Fact]
    public async Task Announce_SendsTheEventMessage_ToEveryPlayer_WhenTheOptionIsOn()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldStateHooks hooks = WorldStateHooks.For(host.World);
        hooks.GameEventSettings.Announce = true;
        hooks.Time = new FixedGameTime(Utc(2026, 10, 3, 12, 30));
        await using WorldTestClient client = await host.EnterWorldAsync("EVTANN", "Evtann");
        GameEventFeature feature = host.WorldServices.GetRequiredService<GameEventFeature>();

        // the host's feature was attached without tables (no store); give it one on the world thread
        await host.OnWorldAsync(() => feature.UseContent(Event1At(Utc(2026, 10, 3, 12)), new HashSet<ushort>()));
        await host.WaitForWorldAsync(() => feature.IsActiveEvent(1), "the event starts on a world tick");

        string text = string.Empty;
        for (int i = 0; i < 5 && !text.Contains("[Event Message]", StringComparison.Ordinal); i++)
        {
            text = (await client.ReadChatAsync()).Text;
        }

        Assert.Equal("|cffff0000[Event Message]: Test Festival|r", text);
    }
}

using Microsoft.EntityFrameworkCore;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Game.WorldState;
using Microsoft.Extensions.Logging.Abstractions;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots.EventScenarios;
using ArcaneCore.World.Tests.Docs;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.EventScenarios;

/// <summary>
/// The scenario feature in a real world host: with rows it listens to game events through its subscription bag (a detach and
/// re-attach leaves exactly one listener), with no rows it subscribes to nothing, and the shipped Undercity file validates clean.
/// </summary>
public sealed class EventScenarioFeatureWiringTests
{
    private sealed class FixtureStore(GameEventPlayerbotContent content) : IGameEventPlayerbotStore
    {
        public Task<GameEventPlayerbotContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private static WorldTestHost Start(GameEventPlayerbotContent content)
        => ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(),
            manualClock: true, extraServices: services => services.AddScoped<IGameEventPlayerbotStore>(_ => new FixtureStore(content)));

    private static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(30);

    /// <summary>A store whose read ends when the test says so (the feature reads it off the attach).</summary>
    private sealed class GatedStore(GameEventPlayerbotContent content) : IGameEventPlayerbotStore
    {
        public volatile TaskCompletionSource<bool> Gate = Opened();
        public static TaskCompletionSource<bool> Opened()
        {
            var open = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            open.SetResult(true);
            return open;
        }

        public async Task<GameEventPlayerbotContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            await Gate.Task.ConfigureAwait(false);
            return content;
        }
    }

    private static GameEventPlayerbotContent OneGroup() => new(
        [new GameEventPlayerbotGroupRow { Event = 2, GroupId = 1, NamePrefix = "Guard", Count = 2, Team = 1, ClassSpec = "2:1", Level = 60, Provision = true, Role = 1 }],
        [new GameEventPlayerbotPostRow { Event = 2, GroupId = 1, PostId = 1, Map = 0, X = 1, Y = 2, Z = 3, Radius = 40 }],
        [], []);

    [Fact]
    public async Task ACycle_LeavesExactlyOneListener_AndTheSameSubscriptionCount()
    {
        await using WorldTestHost host = Start(OneGroup());
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        await feature.Loading.WaitAsync(LoadWait);
        int listeners = events.ListenerCount;
        int subscriptions = features.Find(nameof(PlayerbotEventScenarioFeature))!.Subscriptions;
        Assert.True(feature.Listening);
        Assert.True(subscriptions >= 2); // the tick and the listener

        for (int i = 0; i < 3; i++)
        {
            FeatureResult cycled = await features.CycleAsync(nameof(PlayerbotEventScenarioFeature));
            Assert.True(cycled.Ok, cycled.Detail);
            await feature.Loading.WaitAsync(LoadWait);
            Assert.Equal(listeners, events.ListenerCount);
            Assert.Equal(subscriptions, features.Find(nameof(PlayerbotEventScenarioFeature))!.Subscriptions);
        }

        Assert.True(feature.Listening);
    }

    [Fact]
    public async Task Attach_DoesNotWaitForTheTables_TheSetIsEmptyUntilTheReadEnds_ThenTheFeatureListens()
    {
        var store = new GatedStore(OneGroup());
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(),
            manualClock: true, extraServices: services => services.AddScoped<IGameEventPlayerbotStore>(_ => store));
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        await feature.Loading.WaitAsync(LoadWait);
        Assert.True(feature.Listening);

        // Hold the next read: a hot re-attach returns while the store is still working, and nothing listens until it ends.
        store.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<FeatureResult> cycling = features.CycleAsync(nameof(PlayerbotEventScenarioFeature));
        FeatureResult cycled = await cycling.WaitAsync(LoadWait); // would never return if the attach waited on the gate
        Assert.True(cycled.Ok, cycled.Detail);
        Assert.False(feature.Loading.IsCompleted);
        Assert.False(feature.Listening);
        Assert.Equal(0, feature.Catalog.GroupCount);
        Assert.Contains("still being read", Assert.Single(feature.ListLines()), StringComparison.Ordinal);

        store.Gate.SetResult(true);
        await feature.Loading.WaitAsync(LoadWait);
        Assert.True(feature.Listening);
    }

    /// <summary>A store that counts its reads, holds each one on a gate (when closed) and lets the test see when one is entered.</summary>
    private sealed class CountingGatedStore(GameEventPlayerbotContent content) : IGameEventPlayerbotStore
    {
        private int _reads;
        public volatile TaskCompletionSource<bool> Gate = GatedStore.Opened();
        public volatile TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads => Volatile.Read(ref _reads);

        public async Task<GameEventPlayerbotContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            Entered.TrySetResult(true);
            await Gate.Task.ConfigureAwait(false);
            return content;
        }
    }

    [Fact]
    public async Task QuiesceAsync_StaysPendingWhileTheReadIsOpen_UntilTheReadEndsOrTheTokenFires()
    {
        var store = new CountingGatedStore(OneGroup());
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(),
            manualClock: true, extraServices: services => services.AddScoped<IGameEventPlayerbotStore>(_ => store));
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        await feature.Loading.WaitAsync(LoadWait);

        // The next read is held shut: the quiesce of that attach cannot end.
        store.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FeatureResult cycled = await features.CycleAsync(nameof(PlayerbotEventScenarioFeature)).WaitAsync(LoadWait);
        Assert.True(cycled.Ok, cycled.Detail);
        await store.Entered.Task.WaitAsync(LoadWait);

        using var cancel = new CancellationTokenSource();
        Task quiescing = feature.QuiesceAsync(cancel.Token);
        Assert.False(quiescing.IsCompleted); // the read is blocked on the gate, so this is not a matter of timing
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quiescing.WaitAsync(LoadWait));

        // Quiesce again with a token that never fires: pending until the read ends.
        Task again = feature.QuiesceAsync(CancellationToken.None);
        Assert.False(again.IsCompleted);
        store.Gate.SetResult(true);
        await again.WaitAsync(LoadWait);
        await feature.Loading.WaitAsync(LoadWait);
    }

    [Fact]
    public async Task AReattachWhileTheReadIsStillOpen_KeepsIt_AndAppliesItsRowsToTheNewAttach()
    {
        var store = new CountingGatedStore(OneGroup());
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(),
            manualClock: true, extraServices: services => services.AddScoped<IGameEventPlayerbotStore>(_ => store));
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        await feature.Loading.WaitAsync(LoadWait);
        int before = store.Reads;

        store.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True((await features.CycleAsync(nameof(PlayerbotEventScenarioFeature))).Ok);
        await store.Entered.Task.WaitAsync(LoadWait);
        Assert.Equal(before + 1, store.Reads);

        // What the feature host does after a quiesce that failed: attach again while the read of the first attach has not ended.
        Task reading = feature.Loading;
        var bag = new FeatureSubscriptions("reattach-test");
        await host.OnWorldAsync(() => feature.AttachFrom(host.World, null, bag));
        Assert.Same(reading, feature.Loading); // the read under way is the one waited on
        store.Gate.SetResult(true);
        await feature.Loading.WaitAsync(LoadWait);

        Assert.Equal(before + 1, store.Reads); // no second read
        Assert.True(feature.Listening);
        Assert.True(bag.Count >= 2); // its rows went to the new attach
    }

    /// <summary>A store whose read ends only when its token fires.</summary>
    private sealed class TokenOnlyStore : IGameEventPlayerbotStore
    {
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ended;
        public bool Ended => Volatile.Read(ref _ended) == 1;

        public async Task<GameEventPlayerbotContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return GameEventPlayerbotContent.Empty;
            }
            finally
            {
                Volatile.Write(ref _ended, 1);
            }
        }
    }

    [Fact]
    public async Task StopAsync_CancelsAndWaitsForTheRead_SoItEndsBeforeTheStopReturns_WithoutAFault()
    {
        var store = new TokenOnlyStore();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "bot-event/scenario-fault" && n.Detail.Contains("Cancel", StringComparison.Ordinal));
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(),
            manualClock: true, extraServices: services => services.AddScoped<IGameEventPlayerbotStore>(_ => store));
        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        await store.Entered.Task.WaitAsync(LoadWait);
        Assert.False(store.Ended);

        await feature.StopAsync().WaitAsync(LoadWait);

        Assert.True(store.Ended); // StopAsync did not return before the read ended
        Assert.True(feature.Loading.IsCompleted);
        Assert.Equal(0, capture.Count("bot-event/scenario-fault")); // a cut-short read is not a fault
    }

    [Fact]
    public async Task WithNoRows_TheFeatureSubscribesToNothing()
    {
        await using WorldTestHost host = Start(GameEventPlayerbotContent.Empty);
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
        await feature.Loading.WaitAsync(LoadWait);
        int listeners = events.ListenerCount;

        Assert.False(feature.Listening);
        Assert.Equal(0, features.Find(nameof(PlayerbotEventScenarioFeature))!.Subscriptions);
        Assert.True((await features.CycleAsync(nameof(PlayerbotEventScenarioFeature))).Ok);
        Assert.Equal(listeners, events.ListenerCount);
    }

    [Fact]
    public async Task TheShippedUndercityFile_LoadsAsSixValidGroups_WithNoIssues()
    {
        string script = RepoRoot.ReadText("sql/content/world/sqlite/0001_undercity_siege.sql");
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-evscenario-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path};Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<ArcaneCore.Data.Content.WorldDbContext>().UseSqlite(connectionString).Options;
            await using (var db = new ArcaneCore.Data.Content.WorldDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = script;
                await command.ExecuteNonQueryAsync();
            }

            await using var read = new ArcaneCore.Data.Content.WorldDbContext(options);
            GameEventPlayerbotContent content = await new EfGameEventPlayerbotStore(read).LoadAsync();
            EventScenarioCatalog catalog = EventScenarioCatalog.Build(content, id => id == 900);

            Assert.Empty(catalog.Issues);
            Assert.Equal(6, catalog.GroupsOf(900).Count);
            Assert.Equal(10, catalog.GroupsOf(900).Single(g => g.Siege is not null).Siege!.Waypoints.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(GameEventDialect.CMangos, false)]
    [InlineData(GameEventDialect.VMangos, false)]
    [InlineData(GameEventDialect.Auto, true)] // another row has a start_time: Auto reads the table as vmangos
    public async Task TheShippedEventRow_IsValid_NeverStartsByItself_AndStartsAndStopsByCommand_InBothDialects(GameEventDialect dialect, bool vmangosNeighbour)
    {
        string script = RepoRoot.ReadText("sql/content/world/sqlite/0001_undercity_siege.sql");
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-evevent-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path};Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<ArcaneCore.Data.Content.WorldDbContext>().UseSqlite(connectionString).Options;
            await using (var db = new ArcaneCore.Data.Content.WorldDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = script;
                await command.ExecuteNonQueryAsync();
            }

            GameEventContent content;
            await using (var read = new ArcaneCore.Data.Content.WorldDbContext(options))
            {
                content = await new EfGameEventDataStore(read).LoadAsync();
            }

            if (vmangosNeighbour)
                content = content with { Events = [.. content.Events, new GameEventRecord(7, 0, 1440, 60, 0, 0, "A dated event", "2020-01-01 00:00:00", "2030-01-01 00:00:00")] };
            var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            var gameOptions = new GameEventOptions { Dialect = dialect };
            GameEventLoadResult load = GameEventLoader.Load(content, gameOptions, now, TimeZoneInfo.Utc);
            var service = new GameEventService(load, gameOptions, () => now, TimeZoneInfo.Utc, NullLogger.Instance);

            Assert.Equal(dialect == GameEventDialect.CMangos ? GameEventDialect.CMangos : GameEventDialect.VMangos, service.Dialect);
            Assert.DoesNotContain(service.LoadIssues, issue => issue.Contains("game_event 900", StringComparison.Ordinal));
            Assert.True(service.IsValidEvent(900));
            Assert.False(service.Find(900)!.Disabled);

            service.Initialize(new HashSet<ushort>());
            now = now.AddDays(3);
            service.Update();
            Assert.False(service.IsActiveEvent(900)); // the schedule never starts it

            Assert.True(service.StartEvent(900, overwrite: true)); // what .event start does
            Assert.True(service.IsActiveEvent(900));
            now = now.AddDays(3);
            service.Update();
            Assert.True(service.IsActiveEvent(900)); // and the schedule does not stop it

            Assert.True(service.StopEvent(900, overwrite: true)); // what .event stop does
            Assert.False(service.IsActiveEvent(900));
            now = now.AddDays(3);
            service.Update();
            Assert.False(service.IsActiveEvent(900));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class ThrowingStore(string message) : IGameEventPlayerbotStore
    {
        public Task<GameEventPlayerbotContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<GameEventPlayerbotContent>(new InvalidOperationException(message));
    }

    /// <summary>
    /// Whether <paramref name="ledger"/> recorded a fault whose message carries <paramref name="marker"/>: proof that this test's own store fault
    /// reached the ledger, which a site count is not (any other test's fault at the site moves it). A snapshot keeps only a few recent messages
    /// and top subjects per entry, so the lookup is sound only while the ledger is not at its signature cap and a count-1 marker is not pushed out
    /// by concurrent faults; <see cref="FaultLedgerTestBounds"/> lifts the cap for the whole World test assembly, which is what this relies on.
    /// The cause of the earlier CI failure of the site-count check is not established: a run from before that cap was lifted is the likely one.
    /// </summary>
    private static bool HoldsMarker(FaultLedger ledger, string marker) => ledger.Snapshot().Any(s =>
        s.RecentMessages.Any(m => m.Contains(marker, StringComparison.Ordinal)) || s.TopMessages.Any(t => t.Subject.Contains(marker, StringComparison.Ordinal)));

    [Fact]
    public async Task FailingStore_RecordsFaultAndAnomaly_AndSubscribesToNothing()
    {
        string marker = "evscenario-store-fault-" + Guid.NewGuid().ToString("N");
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "bot-event/scenario-fault" && n.Detail.Contains(marker, StringComparison.Ordinal));

        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(
            new InMemoryAccountStore(),
            new InMemoryCharacterStore(),
            new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(),
            manualClock: true,
            extraServices: services => services.AddScoped<IGameEventPlayerbotStore>(_ => new ThrowingStore(marker)));

        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        await feature.Loading.WaitAsync(LoadWait);
        Assert.False(feature.Listening);
        Assert.True(HoldsMarker(FaultLedger.Shared, marker));
        Assert.Equal(1, capture.Count("bot-event/scenario-fault"));
    }
}

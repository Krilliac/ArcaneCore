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
        int listeners = events.ListenerCount;
        int subscriptions = features.Find(nameof(PlayerbotEventScenarioFeature))!.Subscriptions;
        Assert.True(feature.Listening);
        Assert.True(subscriptions >= 2); // the tick and the listener

        for (int i = 0; i < 3; i++)
        {
            FeatureResult cycled = await features.CycleAsync(nameof(PlayerbotEventScenarioFeature));
            Assert.True(cycled.Ok, cycled.Detail);
            Assert.Equal(listeners, events.ListenerCount);
            Assert.Equal(subscriptions, features.Find(nameof(PlayerbotEventScenarioFeature))!.Subscriptions);
        }

        Assert.True(feature.Listening);
    }

    [Fact]
    public async Task WithNoRows_TheFeatureSubscribesToNothing()
    {
        await using WorldTestHost host = Start(GameEventPlayerbotContent.Empty);
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        GameEventFeature events = host.WorldServices.GetRequiredService<GameEventFeature>();
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

    [Fact]
    public async Task FailingStore_RecordsFaultAndAnomaly_AndSubscribesToNothing()
    {
        static long SiteCount() => FaultLedger.Shared.Snapshot()
            .Where(s => s.Site == "playerbot.event-scenarios")
            .Sum(s => s.Count);

        string marker = "evscenario-store-fault-" + Guid.NewGuid().ToString("N");
        long before = SiteCount();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "bot-event/scenario-fault" && n.Detail.Contains(marker, StringComparison.Ordinal));

        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(
            new InMemoryAccountStore(),
            new InMemoryCharacterStore(),
            new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(),
            manualClock: true,
            extraServices: services => services.AddScoped<IGameEventPlayerbotStore>(_ => new ThrowingStore(marker)));

        PlayerbotEventScenarioFeature feature = host.WorldServices.GetRequiredService<PlayerbotEventScenarioFeature>();
        Assert.False(feature.Listening);
        Assert.True(SiteCount() > before);
        Assert.Equal(1, capture.Count("bot-event/scenario-fault"));
    }
}

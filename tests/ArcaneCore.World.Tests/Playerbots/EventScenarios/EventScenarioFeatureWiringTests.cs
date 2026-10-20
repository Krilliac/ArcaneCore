using Microsoft.EntityFrameworkCore;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Data.World.WorldState;
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
}

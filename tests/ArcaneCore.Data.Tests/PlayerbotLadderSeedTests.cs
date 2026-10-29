using ArcaneCore.Data.Content;
using ArcaneCore.Data.World;
using ArcaneCore.Data.World.Rules;
using ArcaneCore.Kernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The shipped bot goal ladder in the world initializer (the Warden check precedent): an empty <c>playerbot_goal</c> table, or an empty <c>rule</c> table (which also brings the ladder's <c>rule_action</c> rows; <c>rule_action</c> is not checked on its own), is filled from
/// <see cref="PlayerbotLadderContent"/> (the one source of the rows); a table with rows is left alone.
/// </summary>
public sealed class PlayerbotLadderSeedTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmptyTables_AreSeededFromTheShippedContent_AndEditedOnesAreKept(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
        }).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddWorldDatabase(configuration);
        await using ServiceProvider services = collection.BuildServiceProvider();
        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();

        // Empty the three tables, as a baseline install or a wiped table would leave them.
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            db.RemoveRange(await db.Set<PlayerbotGoalRow>().ToListAsync());
            db.RemoveRange(await db.Set<RuleRow>().ToListAsync());
            db.RemoveRange(await db.Set<RuleActionRow>().ToListAsync());
            await db.SaveChangesAsync();
        }

        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            RuleContent loaded = await new EfRuleContentStore(db).LoadAsync();
            Assert.Equal(PlayerbotLadderContent.Goals.Count, loaded.Goals.Count);
            Assert.Equal(PlayerbotLadderContent.Rules.Count, loaded.Rules.Count);
            Assert.Equal(PlayerbotLadderContent.Actions.Count, loaded.Actions.Count);
            Assert.Equal(PlayerbotLadderContent.Goals.OrderBy(g => g.GoalId), loaded.Goals.OrderBy(g => g.GoalId));
            Assert.Equal(PlayerbotLadderContent.Rules.OrderBy(r => r.Id), loaded.Rules.OrderBy(r => r.Id));
            Assert.Equal(PlayerbotLadderContent.Actions.OrderBy(a => a.OwnerId), loaded.Actions.OrderBy(a => a.OwnerId));

            // An edited goal table (one custom row left) is not refilled.
            db.RemoveRange(await db.Set<PlayerbotGoalRow>().Where(g => g.GoalId != 101).ToListAsync());
            await db.SaveChangesAsync();
        }

        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            Assert.Equal(101, Assert.Single(await db.Set<PlayerbotGoalRow>().ToListAsync()).GoalId);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

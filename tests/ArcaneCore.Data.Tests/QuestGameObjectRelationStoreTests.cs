using ArcaneCore.Data.Content;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Kernel.Quests;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The quest content store also loads the game object quest relations (gameobject_questrelation /
/// gameobject_involvedrelation, owned by the game object module) so a quest-giving object can start and end quests.
/// </summary>
public sealed class QuestGameObjectRelationStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheQuestContentStoreCarriesTheGameObjectRelations_SeparateFromTheCreatureOnes(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader(
            "INSERT INTO `gameobject_questrelation` (`id`,`quest`) VALUES (68,176),(56,71);\n" +
            "INSERT INTO `gameobject_involvedrelation` (`id`,`quest`) VALUES (56,45),(55,37);"));

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: false);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            QuestContent content = await new EfQuestContentStore(db).LoadAsync();
            Assert.Equal([(56u, 71u), (68u, 176u)], content.GameObjectStarters.Select(r => (r.Id, r.Quest)));
            Assert.Equal([(55u, 37u), (56u, 45u)], content.GameObjectEnders.Select(r => (r.Id, r.Quest)));
            Assert.Empty(content.Starters);
            Assert.Empty(content.Enders);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();
}

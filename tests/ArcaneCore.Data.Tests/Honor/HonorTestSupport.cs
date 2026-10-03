using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Data.Tests.Honor;

/// <summary>Shared fixtures for the honor store tests.</summary>
internal static class HonorTestSupport
{
    public static async Task<DatabaseConnectionOptions> CreateWithCharactersAsync(
        TestDatabases databases, DatabaseProvider provider, params (string Name, byte Race, byte Level, int Account)[] characters)
    {
        DatabaseConnectionOptions connection = await databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        foreach ((string name, byte race, byte level, int account) in characters)
        {
            await store.CreateAsync(new CharacterRecord { AccountId = account, Name = name, Race = race, Class = 1, Level = level });
        }

        return connection;
    }

    public static async Task<int> IdOfAsync(DatabaseConnectionOptions connection, string name)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return db.Characters.Single(c => c.Name == name).Id;
    }
}

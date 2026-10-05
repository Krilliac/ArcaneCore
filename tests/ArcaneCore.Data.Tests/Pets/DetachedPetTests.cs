using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Pets;

public sealed class DetachedPetTests
{
    [Fact]
    public async Task DetachedPet_IsCallableButNeverCurrent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfPersistentPetStore(db);
        var snapshot = new PersistentPetSnapshot(44, 804, 910416, 5, 0, 75, 12, 300, 1, [1, 2, 3], []);
        await store.SaveDetachedAsync(snapshot);
        Assert.Null(await store.LoadCurrentAsync(44));
        Assert.Equal(804u, (await store.LoadCallableAsync(44))!.PetNumber);
    }
}

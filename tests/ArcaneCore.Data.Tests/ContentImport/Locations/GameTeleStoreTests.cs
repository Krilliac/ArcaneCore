using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Locations;

public sealed class GameTeleStoreTests
{
    [Fact]
    public async Task AddAndDelete_UseTheExistingWorldTable_AndCaseInsensitiveExactNames()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite(connection).Options;
        await using (var db = new WorldDbContext(options))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<GameTeleRow>().Add(new GameTeleRow { Id = 5, Name = "Old", Map = 0 });
            await db.SaveChangesAsync();
        }

        GameTele proposed = new(0, 1, 2, 3, 4, 1, "New Spot");
        await using (var db = new WorldDbContext(options))
        {
            var store = new EfGameTeleStore(db);
            Assert.Equal(6u, (await store.AddAsync(proposed))!.Id);
            Assert.Null(await store.AddAsync(proposed with { Name = "NEW SPOT" }));
            Assert.Null(await store.DeleteAsync("New"));
            Assert.Equal("New Spot", (await store.DeleteAsync("new spot"))!.Name);
        }

        await using (var verify = new WorldDbContext(options))
        {
            Assert.Equal(["Old"], (await verify.Set<GameTeleRow>().AsNoTracking().ToListAsync()).Select(row => row.Name));
        }
    }
}

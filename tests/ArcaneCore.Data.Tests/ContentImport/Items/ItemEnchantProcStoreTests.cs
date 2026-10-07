using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Items;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Items;

public sealed class ItemEnchantProcStoreTests
{
    [Fact]
    public async Task SqliteWorld22Store_LoadsExistingAndNewPpmRows()
    {
        DatabaseConnectionOptions options = new() { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" };
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(options);
        await db.Database.OpenConnectionAsync();
        await SchemaBootstrapper.EnsureAsync(db, SchemaProbe.ThroughVersion(WorldDbContext.Schema, ItemEnchantmentWorldDataModule.Version - 1));
        db.Set<ArcaneCore.Data.Content.Names.ReservedNameRow>().Add(new() { Name = "retained" });
        await db.SaveChangesAsync();
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        Assert.Equal("retained", (await db.Set<ArcaneCore.Data.Content.Names.ReservedNameRow>().SingleAsync()).Name);
        db.Set<ItemEnchantProcRow>().Add(new ItemEnchantProcRow { Entry = 8034, PpmRate = 9 });
        db.Set<ItemEnchantProcRow>().Add(new ItemEnchantProcRow { Entry = 49501, PpmRate = 1.6f });
        await db.SaveChangesAsync();

        IReadOnlyList<ItemEnchantProc> rows = await new EfItemEnchantProcStore(db).LoadAsync();
        Assert.Collection(rows.OrderBy(row => row.SpellId),
            row => Assert.Equal(new ItemEnchantProc(8034, 9), row),
            row => Assert.Equal(new ItemEnchantProc(49501, 1.6f), row));
    }
}

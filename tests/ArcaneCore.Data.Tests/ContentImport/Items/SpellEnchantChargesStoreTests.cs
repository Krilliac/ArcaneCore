using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Items;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Items;

public sealed class SpellEnchantChargesStoreTests
{
    [Fact]
    public async Task World24SqliteStore_RoundTripsSpellEnchantCharges()
    {
        DatabaseConnectionOptions options = new() { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" };
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(options);
        await db.Database.OpenConnectionAsync();
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<SpellEnchantChargesRow>().Add(new SpellEnchantChargesRow { Entry = 49701, Charges = 7 });
        await db.SaveChangesAsync();

        IReadOnlyList<SpellEnchantCharges> rows = await new EfSpellEnchantChargesStore(db).LoadAsync();
        Assert.Equal(new SpellEnchantCharges(49701, 7), Assert.Single(rows));
    }
}

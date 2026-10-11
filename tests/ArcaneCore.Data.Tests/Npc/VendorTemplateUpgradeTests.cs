using ArcaneCore.Data.Content;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Npc;

/// <summary>
/// World schema 52 to 53, the live deploy path (<c>arcane-db upgrade</c>, then the content refresh): a world built before the shared
/// vendor stock gains <c>npc_vendor_template</c> and <c>creature_template.VendorTemplateId</c>, keeps its creature rows with no link,
/// and a second run over the finished schema issues no DDL. MariaDB and PostgreSQL run when their test servers are configured
/// (<see cref="TestDatabases"/>); SQLite always runs.
/// </summary>
public sealed class VendorTemplateUpgradeTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromWorld52_AddsTheTableAndTheLink_KeepsTheCreatures_AndRerunsWithoutDdl(DatabaseProvider provider)
    {
        Assert.Equal(53, VendorTemplateDataModule.Version);
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureTemplateRow>().AddRange(
                new CreatureTemplateRow { Entry = 77, Name = "Before" },
                new CreatureTemplateRow { Entry = 78, Name = "Also Before", TrainerSpell = 9 });
            await db.SaveChangesAsync();

            // Recreate a world from before this step: neither the table nor the column, version 52.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            List<string> ddl = [$"DROP TABLE {sql.DelimitIdentifier("npc_vendor_template")}"];
            foreach (AddColumnChange change in WorldDbContext.Schema.Steps.Single(s => s.Version == VendorTemplateDataModule.Version).Changes.OfType<AddColumnChange>())
            {
                ddl.Add($"ALTER TABLE {sql.DelimitIdentifier(change.Table)} DROP COLUMN {sql.DelimitIdentifier(change.Column)}");
            }

            foreach (string statement in ddl)
            {
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, VendorTemplateDataModule.Version - 1));
        }

        // The first run is the upgrade: it must issue DDL (the table and the column), and it must not lose or change a creature.
        var first = new CommandTap();
        await using (WorldDbContext db = SchemaProbe.CreateWith<WorldDbContext>(cs, first))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        Assert.True(first.Ddl >= 2, $"the 52 -> 53 step issued {first.Ddl} DDL statement(s)");
        await AssertUpgradedAsync(cs);

        // A second run over the finished schema changes nothing.
        var second = new CommandTap();
        await using (WorldDbContext db = SchemaProbe.CreateWith<WorldDbContext>(cs, second))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        Assert.Equal(0, second.Ddl);
        await AssertUpgradedAsync(cs);

        // The new table and column take rows.
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            db.Set<VendorTemplateItemRow>().Add(new VendorTemplateItemRow { Entry = 133, Item = 2901, MaxCount = 2, IncrTime = 60, Slot = 1 });
            (await db.Set<CreatureTemplateRow>().SingleAsync(r => r.Entry == 77)).VendorTemplateId = 133;
            await db.SaveChangesAsync();
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Equal(133u, (await db.Set<CreatureTemplateRow>().SingleAsync(r => r.Entry == 77)).VendorTemplateId);
            Assert.Equal(2901u, (await db.Set<VendorTemplateItemRow>().SingleAsync()).Item);
        }
    }

    private static async Task AssertUpgradedAsync(DatabaseConnectionOptions cs)
    {
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
        List<CreatureTemplateRow> creatures = await db.Set<CreatureTemplateRow>().AsNoTracking().OrderBy(r => r.Entry).ToListAsync();
        Assert.Equal([(77u, "Before", 0u, 0u), (78u, "Also Before", 9u, 0u)],
            creatures.Select(r => (r.Entry, r.Name, r.TrainerSpell, r.VendorTemplateId)));
        Assert.Equal(0, await db.Set<VendorTemplateItemRow>().CountAsync());
    }
}

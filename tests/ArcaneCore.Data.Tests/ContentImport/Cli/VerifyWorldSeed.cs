using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// One row in each table <c>verify</c> requires to be non-empty (map, area, area trigger, taxi), so a test that checks another part of
/// <c>verify</c> is not failed by the geography tables. Rows that already exist are left alone.
/// </summary>
internal static class VerifyWorldSeed
{
    public static async Task AddGeographyAsync(string database)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options;
        await using var db = new WorldDbContext(options);
        if (!await db.Set<MapTemplateRow>().AnyAsync())
        {
            db.Set<MapTemplateRow>().Add(new MapTemplateRow { Entry = 0, MapName = "Seed" });
        }

        if (!await db.Set<AreaTemplateRow>().AnyAsync())
        {
            db.Set<AreaTemplateRow>().Add(new AreaTemplateRow { Entry = 1, Name = "Seed" });
        }

        if (!await db.Set<AreaTriggerTemplateRow>().AnyAsync())
        {
            db.Set<AreaTriggerTemplateRow>().Add(new AreaTriggerTemplateRow { Id = 1, Name = "Seed" });
        }

        if (!await db.Set<TaxiNode>().AnyAsync())
        {
            db.Set<TaxiNode>().Add(new TaxiNode { Id = 1, Name = "Seed" });
        }

        if (!await db.Set<TaxiPath>().AnyAsync())
        {
            db.Set<TaxiPath>().Add(new TaxiPath { Id = 1, FromNode = 1, ToNode = 1, Price = 0 });
        }

        await db.SaveChangesAsync();
    }
}

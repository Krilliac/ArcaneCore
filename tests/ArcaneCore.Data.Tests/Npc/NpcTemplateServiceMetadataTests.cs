using ArcaneCore.Data.Content;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Npc;

public sealed class NpcTemplateServiceMetadataTests
{
    [Fact]
    public void ImportsClassicAndVmangosAliasesWithStrictDomains()
    {
        var importer = new NpcTemplateServiceMetadataDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO creature_template (Entry,gossip_menu_id,trainer_type,trainer_class,trainer_race,trainer_spell) VALUES
            (911,0,0,1,0,0),(198,0,0,8,0,0),(152,0,0,0,0,0);
            INSERT INTO creature_template (entry,gossip_menu_id,trainer_type,trainer_class,trainer_race,trainer_spell) VALUES
            (911,1,0,1,0,0);
            """));

        Assert.Equal(3, importer.BuildReport().Rows);
        Assert.Equal(1, importer.BuildReport().Replaced);
        Assert.Equal((byte)1, Assert.Single(importer.Snapshot(), r => r.Entry == 911).TrainerClass);
        Assert.Equal((byte)8, Assert.Single(importer.Snapshot(), r => r.Entry == 198).TrainerClass);
    }

    [Fact]
    public void RejectsMissingEntryAndInvalidTrainerDomainsWithoutClamping()
    {
        var importer = new NpcTemplateServiceMetadataDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO creature_template (entry,trainer_type,trainer_class,trainer_race) VALUES
            (100,4,1,0),(101,0,12,0),(102,0,1,9),(103,0,6,0),(104,0,10,0),('this-entry-is-deliberately-too-long-to-log',0,0,0);
            """));

        NpcTemplateServiceMetadataImportReport report = importer.BuildReport();
        Assert.Equal(0, report.Rows);
        Assert.Equal(6, report.Skipped);
        Assert.NotEmpty(report.Diagnostics);
        Assert.DoesNotContain(report.Diagnostics, d => d.Contains("this-entry-is-deliberately-too-long", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorldModuleMapsAndSourceRoundTrip()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.World), m => m is NpcTemplateServiceMetadataModule);
        Assert.Equal(NpcTemplateServiceMetadataModule.Version, module.SchemaVersion);

        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new WorldDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.Set<NpcTemplateServiceMetadata>().Add(new NpcTemplateServiceMetadata { Entry = 911, TrainerType = 0, TrainerClass = 1 });
        await db.SaveChangesAsync();
        var source = new EfNpcTemplateServiceMetadataSource(db);
        Assert.Equal(911u, Assert.Single(await source.LoadAsync()).Entry);
    }
}

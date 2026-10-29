using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <c>creature_immunities</c> and <c>creature_template.MechanicImmuneMask</c> / <c>SchoolImmuneMask</c> (<see cref="CreatureImmunityDataModule"/>,
/// world schema 61): the importer reads the cmangos table and both dialects' template masks, the store loads them into
/// <see cref="CreatureContent"/>, an existing world upgrades from 60 and <c>RefreshImmunitiesAsync</c> backfills it. Rows are hand-written in the
/// classic-db / vmangos layouts; no GPL rows are copied.
/// </summary>
public sealed class CreatureImmunityImportTests
{
    private const string ClassicDb = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`,`MechanicImmuneMask`,`SchoolImmuneMask`) VALUES
        (930201,'Stun Proof','',60,60,11,1024,4),(930202,'Plain','',1,1,12,0,0);
        INSERT INTO `creature_immunities` (`Entry`,`SetId`,`Type`,`Value`) VALUES
        (930201,0,1,12),(930201,0,4,1),(930201,0,5,7),(930201,1,2,16),(930201,1,3,32),(930201,1,0,2),(930203,0,1,99),(930201,0,9,5);
        """;

    private const string VMangos = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`display_id1`,`mechanic_immune_mask`,`school_immune_mask`) VALUES
        (930301,0,'Fire Wall','',10,10,1,2048,4);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private static CreatureDumpImporter Read(string dump)
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void WorldStep_IsTheImmunityTableAndTheTwoMaskColumns()
    {
        Assert.Equal(61, CreatureImmunityDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureImmunityDataModule.Version);
        Assert.Equal(["creature_immunities"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Equal(
            [("creature_template", nameof(CreatureTemplateRow.MechanicImmuneMask)), ("creature_template", nameof(CreatureTemplateRow.SchoolImmuneMask))],
            step.Changes.OfType<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is CreatureImmunityDataModule);
    }

    [Fact]
    public void Importer_ReadsTheClassicDbTemplateMasksAndTheImmunityRows_AndSkipsAnInvalidType()
    {
        CreatureDumpImporter importer = Read(ClassicDb);

        Assert.True(importer.HasImmunityTable);
        CreatureImportReport report = importer.BuildReport();
        Assert.Equal(7, report.Immunities); // the type 9 row is refused
        Assert.Contains(report.Warnings, w => w.Contains("creature_immunities", StringComparison.Ordinal) && w.Contains('9', StringComparison.Ordinal));
        Assert.Equal([(930201u, 0u, (byte)1, 12u), (930201u, 0u, (byte)4, 1u), (930201u, 0u, (byte)5, 7u), (930201u, 1u, (byte)0, 2u),
                (930201u, 1u, (byte)2, 16u), (930201u, 1u, (byte)3, 32u), (930203u, 0u, (byte)1, 99u)],
            importer.ImmunitySnapshot().OrderBy(r => r.Entry).ThenBy(r => r.SetId).ThenBy(r => r.Type).Select(r => (r.Entry, r.SetId, r.Type, r.Value)));
        Assert.Equal([(930201u, 1024u, 4u), (930202u, 0u, 0u)],
            importer.Snapshot().Templates.OrderBy(t => t.Entry).Select(t => (t.Entry, t.MechanicImmuneMask, t.SchoolImmuneMask)));
    }

    [Fact]
    public void Importer_ReadsTheVMangosTemplateMasks()
    {
        CreatureDumpImporter importer = Read(VMangos);

        Assert.False(importer.HasImmunityTable);
        CreatureTemplateRow row = Assert.Single(importer.Snapshot().Templates);
        Assert.Equal((2048u, 4u), (row.MechanicImmuneMask, row.SchoolImmuneMask));
        Assert.Equal(0, importer.BuildReport().Immunities);
    }

    [Fact]
    public void TheSpecListsTheImmunityTableAndTheMaskColumns()
    {
        TableSpec table = ContentTableSpecs.All.Single(s => s.Table == "creature_immunities");
        Assert.Equal(["Entry", "SetId", "Type", "Value"], table.Keys.Select(k => k.Canonical));
        TableSpec template = ContentTableSpecs.All.Single(s => s.Table == "creature_template");
        Assert.True(template.IsMapped("MechanicImmuneMask"));
        Assert.True(template.IsMapped("SchoolImmuneMask"));
        Assert.True(template.IsMapped("mechanic_immune_mask"));
        Assert.True(template.IsMapped("school_immune_mask"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ImportedImmunities_LoadIntoTheCreatureContent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            CreatureImportReport report = await Read(ClassicDb).WriteAsync(db, replace: false);
            Assert.Equal(7, report.Immunities);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            CreatureTemplate template = content.FindTemplate(930201)!;
            Assert.Equal((1024u, 4u), (template.MechanicImmuneMask, template.SchoolImmuneMask));

            CreatureImmunitySet zero = content.FindImmunitySet(930201, 0)!;
            Assert.Equal(SpellMechanicBit(7), zero.MechanicMask);
            Assert.Equal(1u << 1, zero.DispelMask);
            Assert.Equal(new uint[] { 12u }, zero.States);
            CreatureImmunitySet one = content.FindImmunitySet(930201, 1)!;
            Assert.Equal((16u, 32u), (one.SchoolMask, one.DamageMask));
            Assert.Equal(new uint[] { 2u }, one.Effects);
            Assert.Null(content.FindImmunitySet(930201, 2));
            Assert.Null(content.FindImmunitySet(930202, 0));
            Assert.True(content.HasImmunitySets);
        }
    }

    private static uint SpellMechanicBit(uint mechanic) => 1u << (int)(mechanic - 1);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromWorld60_AddsTheTableAndTheColumns_AndTheRefreshBackfillsThem(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureTemplateRow>().AddRange(
                new CreatureTemplateRow { Entry = 930201, Name = "Stun Proof" },
                new CreatureTemplateRow { Entry = 930202, Name = "Plain", TrainerSpell = 9 });
            await db.SaveChangesAsync();

            // Recreate a world from before this step: neither the table nor the columns, version 60.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            List<string> ddl = [$"DROP TABLE {sql.DelimitIdentifier("creature_immunities")}"];
            foreach (AddColumnChange change in WorldDbContext.Schema.Steps.Single(s => s.Version == CreatureImmunityDataModule.Version).Changes.OfType<AddColumnChange>())
            {
                ddl.Add($"ALTER TABLE {sql.DelimitIdentifier(change.Table)} DROP COLUMN {sql.DelimitIdentifier(change.Column)}");
            }

            foreach (string statement in ddl)
            {
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, CreatureImmunityDataModule.Version - 1));
        }

        var first = new CommandTap();
        await using (WorldDbContext db = SchemaProbe.CreateWith<WorldDbContext>(cs, first))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        Assert.True(first.Ddl >= 3, $"the 60 -> 61 step issued {first.Ddl} DDL statement(s)");

        var second = new CommandTap();
        await using (WorldDbContext db = SchemaProbe.CreateWith<WorldDbContext>(cs, second))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        Assert.Equal(0, second.Ddl);

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal(0, await db.Set<CreatureImmunityRow>().CountAsync());
            Assert.All(await db.Set<CreatureTemplateRow>().AsNoTracking().ToListAsync(), r => Assert.Equal((0u, 0u), (r.MechanicImmuneMask, r.SchoolImmuneMask)));

            (int rows, int templates) = await Read(ClassicDb).RefreshImmunitiesAsync(db);
            Assert.Equal((7, 1), (rows, templates)); // 930202 already has 0 and 0; 930203 has no template in the world
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureTemplateRow stun = await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync(r => r.Entry == 930201);
            Assert.Equal((1024u, 4u), (stun.MechanicImmuneMask, stun.SchoolImmuneMask));
            Assert.Equal(9u, (await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync(r => r.Entry == 930202)).TrainerSpell);
            Assert.Equal(7, await db.Set<CreatureImmunityRow>().CountAsync());

            // A second refresh changes no template and rewrites the same rows.
            (int rows, int templates) = await Read(ClassicDb).RefreshImmunitiesAsync(db);
            Assert.Equal((7, 0), (rows, templates));
        }
    }

    [Fact]
    public async Task Refresh_WithADumpThatHasNeither_LeavesTheWorldAsItIs()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<CreatureImmunityRow>().Add(new CreatureImmunityRow { Entry = 930205, SetId = 0, Type = 5, Value = 7 });
        db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 930205, Name = "Kept", MechanicImmuneMask = 64, SchoolImmuneMask = 2 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var importer = Read("INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`) VALUES (930205,'Kept','',1,1);");
        Assert.Equal((0, 0), await importer.RefreshImmunitiesAsync(db));
        CreatureTemplateRow kept = await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync();
        Assert.Equal((64u, 2u), (kept.MechanicImmuneMask, kept.SchoolImmuneMask));
        Assert.Equal(1, await db.Set<CreatureImmunityRow>().CountAsync());
    }
}

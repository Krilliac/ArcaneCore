using System.Text.Json;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.Totems;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>Production CLI integration against SQLite; all source rows are synthetic.</summary>
public sealed class TotemContentImporterCliTests : IDisposable
{
    private const string Dump = """
        CREATE TABLE `creature_template` (`Entry` int, `Name` text, `MinLevel` int, `ModelId1` int, `AIName` text, `SpellList` int);
        INSERT INTO `creature_template` VALUES (3,'Passive totem',1,1,'TotemAI',0),(5878,'Active totem',1,1,'TotemAI',9100),(3968,'Sentry totem',1,1,'TotemAI',0);
        CREATE TABLE `spell_template` (`Id` int, `Effect1` int, `EffectMiscValue1` int);
        INSERT INTO `spell_template` VALUES (8071,87,3),(3599,89,5878),(6495,74,3968);
        CREATE TABLE `creature_template_spells` (`entry` int, `setId` int, `spell1` int, `spell2` int);
        INSERT INTO `creature_template_spells` VALUES (3,0,5728,0),(5878,1,999,0);
        CREATE TABLE `creature_spell_list` (`Id` int, `Position` int, `SpellId` int);
        INSERT INTO `creature_spell_list` VALUES (9100,0,2222),(9100,1,3333);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-totem-cli-" + Guid.NewGuid().ToString("N"));

    public TotemContentImporterCliTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Import_PopulatesTotemMappings_AndReplaceIsDeterministic()
    {
        string source = Write("totems.sql", Dump);
        string database = Path.Combine(_directory, "world.db");
        string reportPath = Path.Combine(_directory, "report.json");

        for (int run = 0; run < 2; run++)
        {
            (int code, string output, string error) = await Run("import", source, "--database", database, "--replace", "--report", reportPath);
            Assert.Equal(ExitCodes.Ok, code);
            Assert.Empty(error);
            Assert.Contains("totem_spell  2", output, StringComparison.Ordinal);
            Assert.Contains("3968", output, StringComparison.Ordinal);
            await using WorldDbContext db = Open(database);
            Assert.Equal([(3u, 5728u), (5878u, 2222u)],
                (await db.Set<TotemSpellRow>().AsNoTracking().OrderBy(r => r.CreatureEntry).ToListAsync()).Select(r => (r.CreatureEntry, r.SpellId)));
        }

        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal(2, report.RootElement.GetProperty("imported").GetProperty("totem_spell").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("skipped").GetProperty("totem_creatures_without_spell").GetInt32());
        Assert.Equal(64, report.RootElement.GetProperty("inputs")[0].GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public async Task PlanAndDryRun_ReportTotemSourcesAndCounts_WithoutCreatingDatabase()
    {
        string source = Write("totems.sql", Dump);
        string database = Path.Combine(_directory, "world.db");
        (int planCode, string plan, _) = await Run("plan", source);
        (int dryCode, string dry, _) = await Run("import", source, "--dry-run", "--database", database);

        Assert.Equal(ExitCodes.Ok, planCode);
        Assert.Contains("creature_template_spells  -  rows 2", plan, StringComparison.Ordinal);
        Assert.Contains("creature_spell_list  -  rows 2", plan, StringComparison.Ordinal);
        Assert.Contains("spell_template  -  rows 3", plan, StringComparison.Ordinal);
        Assert.Contains("SpellList", plan, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Ok, dryCode);
        Assert.Contains("totem_spell  2", dry, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public async Task Verify_CountsMappings_AndReportsMissingCreatureReferences()
    {
        string database = Path.Combine(_directory, "world.db");
        Assert.Equal(ExitCodes.Ok, (await Run("import", Write("totems.sql", Dump), "--database", database)).Code);
        (int clean, string output, _) = await Run("verify", "--database", database);
        Assert.Equal(ExitCodes.Ok, clean);
        Assert.Contains("totem_spell  2", output, StringComparison.Ordinal);

        await using (WorldDbContext db = Open(database))
        {
            db.Set<TotemSpellRow>().Add(new TotemSpellRow { CreatureEntry = 9999, SpellId = 5728 });
            await db.SaveChangesAsync();
        }

        (int bad, string problem, _) = await Run("verify", "--database", database);
        Assert.Equal(ExitCodes.Verify, bad);
        Assert.Contains("1 totem_spell row(s) name a creature", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingTotemSourceKey_IsRejectedBeforeWritingDatabase()
    {
        string database = Path.Combine(_directory, "world.db");
        string source = Write("bad.sql", Dump.Replace("`setId` int", "`wrong_set_key` int", StringComparison.Ordinal));
        (int code, _, string error) = await Run("import", source, "--database", database);
        Assert.Equal(ExitCodes.Schema, code);
        Assert.Contains("setId", error, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public async Task Import_VmangosUsesDirectTotemSpellField_AndRetailPatchSelection()
    {
        string source = Write("vmangos.sql", """
            CREATE TABLE `creature_template` (`entry` int, `patch` int, `name` text, `level_min` int, `display_id1` int, `totem_spell_id` int);
            INSERT INTO `creature_template` VALUES (5878,10,'Active totem',1,1,2222),(3968,0,'Sentry totem',1,1,0),(5878,0,'Old totem',1,1,1111),(5878,11,'Future totem',1,1,9999);
            """);
        string database = Path.Combine(_directory, "world.db");

        (int code, string output, string error) = await Run("import", source, "--dialect", "vmangos", "--database", database);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Empty(error);
        Assert.Contains("totem_spell  1", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(database);
        TotemSpellRow mapping = await db.Set<TotemSpellRow>().AsNoTracking().SingleAsync();
        Assert.Equal((5878u, 2222u), (mapping.CreatureEntry, mapping.SpellId));
    }

    [Fact]
    public async Task Import_TotemCollision_RollsBackEarlierCreatureWrites()
    {
        string database = Path.Combine(_directory, "world.db");
        await using (WorldDbContext db = Open(database))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<TotemSpellRow>().Add(new TotemSpellRow { CreatureEntry = 3, SpellId = 99 });
            await db.SaveChangesAsync();
        }

        (int code, _, string error) = await Run("import", Write("totems.sql", Dump), "--database", database);
        Assert.Equal(ExitCodes.Database, code);
        Assert.Contains("nothing was changed", error, StringComparison.Ordinal);
        await using WorldDbContext verify = Open(database);
        Assert.Empty(await verify.Set<CreatureTemplateRow>().AsNoTracking().ToListAsync());
        Assert.Equal(99u, (await verify.Set<TotemSpellRow>().AsNoTracking().SingleAsync()).SpellId);
    }

    [Fact]
    public async Task Verify_WithImportedSpellData_ReportsMissingSpellReferences()
    {
        string database = Path.Combine(_directory, "world.db");
        Assert.Equal(ExitCodes.Ok, (await Run("import", Write("totems.sql", Dump), "--database", database)).Code);
        await using (WorldDbContext db = Open(database))
        {
            db.Set<SpellTemplateRow>().Add(new SpellTemplateRow { Id = 5728 });
            await db.SaveChangesAsync();
        }

        (int code, string output, _) = await Run("verify", "--database", database);
        Assert.Equal(ExitCodes.Verify, code);
        Assert.Contains("1 totem_spell row(s) name a spell", output, StringComparison.Ordinal);
    }

    private string Write(string name, string sql)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, sql);
        return path;
    }

    private static WorldDbContext Open(string database)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options);

    private static async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }
}

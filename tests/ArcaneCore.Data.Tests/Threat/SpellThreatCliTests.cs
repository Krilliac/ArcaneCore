using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Threat;
using ArcaneCore.Kernel.WorldData.Threat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Threat;

/// <summary>
/// The content importer's <c>spell-threat</c> command: an operator's classic-db or vmangos dump into <c>spell_threat</c> without writing code
/// (until now <see cref="SpellThreatDumpImporter"/> had no caller). Driven in-process against SQLite files in the temp directory.
/// </summary>
public sealed class SpellThreatCliTests : IDisposable
{
    // classic-db z2815 tuples: Heroic Strike R1 (78), Sunder Armor R1 (7386, flat 100), Mind Blast-style flat rows, and the one multiplier row (8092 = 2).
    private const string ClassicDump = """
        CREATE TABLE `spell_threat` (
          `entry` mediumint unsigned NOT NULL,
          `Threat` smallint NOT NULL,
          `multiplier` float NOT NULL DEFAULT '1',
          `ap_bonus` float NOT NULL DEFAULT '0',
          PRIMARY KEY (`entry`)
        ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3 ROW_FORMAT=FIXED;
        INSERT INTO `spell_threat` VALUES (78,20,1,0),(7386,100,1,0),(8092,0,2,0),(11597,261,1,0);
        """;

    private const string VMangosDump = """
        INSERT INTO `spell_threat` (`entry`,`threat`,`multiplier`,`inverse_effect_mask`,`build_min`,`build_max`) VALUES (100,10,1,0,0,9999),(200,50,1,1,0,5464),(200,60,1,0,5875,9999),(300,0,0,0,0,9999);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-threatcli-" + Guid.NewGuid().ToString("N"));

    public SpellThreatCliTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private string WriteDump(string name, string sql)
    {
        string path = PathOf(name);
        File.WriteAllText(path, sql);
        return path;
    }

    private static async Task<SpellThreatContent> LoadAsync(string path)
    {
        await using var db = new WorldDbContext(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        return await new EfSpellThreatStore(db).LoadAsync();
    }

    [Fact]
    public async Task SpellThreat_ImportsAClassicDbDump_WithFlatThreatAndMultipliers()
    {
        string dump = WriteDump("world.sql", ClassicDump);
        string db = PathOf("world.db");

        (int code, string output, string error) = await RunAsync("spell-threat", dump, "--database", db);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("spell_threat: 4 row(s) read, 0 outside build 5875, 4 imported", output, StringComparison.Ordinal);
        SpellThreatContent content = await LoadAsync(db);
        Assert.Equal(4, content.Count);
        Assert.Equal(new SpellThreatRecord(7386, 100, 1f, 0), content.Find(7386)); // Sunder Armor: flat threat
        Assert.Equal(new SpellThreatRecord(8092, 0, 2f, 0), content.Find(8092));
    }

    [Fact]
    public async Task SpellThreat_KeepsOnlyTheBuild5875RowsOfAVMangosDump_AndAZeroMultiplierSurvives()
    {
        string dump = WriteDump("vmangos.sql", VMangosDump);
        string db = PathOf("world.db");

        (int code, string output, string error) = await RunAsync("spell-threat", dump, "--database", db);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("4 row(s) read, 1 outside build 5875, 3 imported", output, StringComparison.Ordinal);
        SpellThreatContent content = await LoadAsync(db);
        Assert.Equal(60, content.Find(200)!.Threat);
        Assert.Equal(new SpellThreatRecord(300, 0, 0f, 0), content.Find(300)); // multiplier 0 yields 0 threat, the row is kept
    }

    [Fact]
    public async Task SpellThreat_ASecondImportReplacesTheTable()
    {
        string db = PathOf("world.db");
        await RunAsync("spell-threat", WriteDump("a.sql", ClassicDump), "--database", db);

        (int code, _, string error) = await RunAsync("spell-threat",
            WriteDump("b.sql", "INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (7386,45);"), "--database", db);

        Assert.True(code == ExitCodes.Ok, error);
        SpellThreatContent content = await LoadAsync(db);
        Assert.Equal(1, content.Count);
        Assert.Equal(45, content.Find(7386)!.Threat);
    }

    [Fact]
    public async Task SpellThreat_DryRun_WritesNoDatabase()
    {
        string dump = WriteDump("world.sql", ClassicDump);
        string db = PathOf("world.db");

        (int code, string output, string error) = await RunAsync("spell-threat", dump, "--database", db, "--dry-run");

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("dry run", output, StringComparison.Ordinal);
        Assert.False(File.Exists(db));
    }

    [Fact]
    public async Task SpellThreat_ADumpWithoutTheTable_IsASchemaError_AndAMalformedOneIsUnreadable()
    {
        (int none, _, string noneError) = await RunAsync("spell-threat", WriteDump("other.sql", "CREATE TABLE `other` (`a` int);\nINSERT INTO `other` VALUES (1);"),
            "--database", PathOf("a.db"));
        (int bad, _, _) = await RunAsync("spell-threat", WriteDump("bad.sql", "INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (1,2),(1,3);"),
            "--database", PathOf("b.db"));
        (int apBonus, _, _) = await RunAsync("spell-threat", WriteDump("ap.sql", "INSERT INTO `spell_threat` (`entry`,`threat`,`ap_bonus`) VALUES (1,2,0.5);"),
            "--database", PathOf("c.db"));

        Assert.Equal(ExitCodes.Schema, none);
        Assert.Contains("spell_threat", noneError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Io, bad);
        Assert.Equal(ExitCodes.Io, apBonus);
        Assert.False(File.Exists(PathOf("b.db")));
    }

    [Fact]
    public async Task Plan_ReportsSpellThreatAsAnImportedTable_WithItsDialect()
    {
        string dump = WriteDump("world.sql", VMangosDump);

        (int code, string output, string error) = await RunAsync("plan", dump);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("  spell_threat  VMangos  rows 4", output, StringComparison.Ordinal); // listed with the tables an importer reads, not with the unread ones
    }

    [Fact]
    public void ThePlanSpec_NamesEveryColumnTheParserReads()
    {
        TableSpec spec = ContentTableSpecs.Find("spell_threat")!;

        foreach (string column in new[] { "Threat", "threat", "multiplier", "ap_bonus", "inverse_effect_mask", "build_min", "build_max" })
        {
            Assert.True(spec.IsMapped(column), column);
        }

        Assert.False(spec.IsMapped("Comment"));
    }
}

using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Procs;
using ArcaneCore.Kernel.WorldData.Procs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Procs;

/// <summary>
/// The content importer's <c>proc-events</c> command: an operator's classic-db or vmangos dump into <c>spell_proc_event</c> without writing code
/// (until now <see cref="SpellProcEventDumpImporter"/> had no caller). Driven in-process against SQLite files in the temp directory.
/// </summary>
public sealed class SpellProcEventCliTests : IDisposable
{
    // Real classic-db Full_DB z2815 tuples (cooldowns in seconds before z2829): Lightning Shield 324 and Hand of Justice 15600.
    private const string ClassicDump = """
        CREATE TABLE `spell_proc_event` (
          `entry` mediumint unsigned NOT NULL DEFAULT '0',
          `SchoolMask` tinyint unsigned NOT NULL DEFAULT '0',
          `SpellFamilyName` smallint unsigned NOT NULL DEFAULT '0',
          `SpellFamilyMask0` bigint unsigned NOT NULL DEFAULT '0',
          `SpellFamilyMask1` bigint unsigned NOT NULL DEFAULT '0',
          `SpellFamilyMask2` bigint unsigned NOT NULL DEFAULT '0',
          `procFlags` int unsigned NOT NULL DEFAULT '0',
          `procEx` int unsigned NOT NULL DEFAULT '0',
          `ppmRate` float NOT NULL DEFAULT '0',
          `CustomChance` float NOT NULL DEFAULT '0',
          `Cooldown` int unsigned NOT NULL DEFAULT '0',
          PRIMARY KEY (`entry`)
        ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3;
        INSERT INTO `spell_proc_event` VALUES (324,0,0,0,0,0,0,65536,0,0,3),(15600,0,0,0,0,0,0,0,0.6,0,3);
        """;

    private const string VMangosDump = """
        INSERT INTO `spell_proc_event` (`entry`,`SchoolMask`,`SpellFamilyName`,`SpellFamilyMask0`,`SpellFamilyMask1`,`SpellFamilyMask2`,`procFlags`,`procEx`,`ppmRate`,`CustomChance`,`Cooldown`,`build_min`,`build_max`) VALUES (100,0,0,0,0,0,4,2,0,0,0,0,5464),(100,0,0,0,0,0,4,1,0,0,0,5875,9999),(200,4,3,0,0,0,0,0,'2.5',0,10000,0,9999);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-proccli-" + Guid.NewGuid().ToString("N"));

    public SpellProcEventCliTests() => Directory.CreateDirectory(_directory);

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

    private static async Task<SpellProcEventContent> LoadAsync(string path)
    {
        await using var db = new WorldDbContext(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        return await new EfSpellProcEventStore(db).LoadAsync();
    }

    [Fact]
    public async Task ProcEvents_ImportsAClassicDbDump_WithSecondCooldowns()
    {
        string dump = WriteDump("world.sql", ClassicDump);
        string db = PathOf("world.db");

        (int code, string output, string error) = await RunAsync("proc-events", dump, "--database", db, "--cooldown-unit", "seconds");

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("spell_proc_event: 2 row(s) read, 0 outside build 5875, 2 imported", output, StringComparison.Ordinal);
        SpellProcEventContent content = await LoadAsync(db);
        Assert.Equal(2, content.Count);
        Assert.Equal(3000u, content.Find(324)!.Cooldown);
        Assert.Equal(0.6f, content.Find(15600)!.PpmRate);
    }

    [Fact]
    public async Task ProcEvents_KeepsOnlyTheBuild5875RowsOfAVMangosDump_WithMillisecondCooldownsByDefault()
    {
        string dump = WriteDump("vmangos.sql", VMangosDump);
        string db = PathOf("world.db");

        (int code, string output, string error) = await RunAsync("proc-events", dump, "--database", db);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("3 row(s) read, 1 outside build 5875, 2 imported", output, StringComparison.Ordinal);
        SpellProcEventContent content = await LoadAsync(db);
        Assert.Equal(1u, content.Find(100)!.ProcEx);
        Assert.Equal(10000u, content.Find(200)!.Cooldown);
    }

    [Fact]
    public async Task ProcEvents_DryRun_WritesNoDatabase()
    {
        string dump = WriteDump("world.sql", ClassicDump);
        string db = PathOf("world.db");

        (int code, string output, string error) = await RunAsync("proc-events", dump, "--database", db, "--dry-run", "--cooldown-unit", "seconds");

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("dry run", output, StringComparison.Ordinal);
        Assert.False(File.Exists(db));
    }

    [Theory]
    [InlineData("--cooldown-unit", "minutes")]
    public async Task ProcEvents_RefusesAnUnknownCooldownUnit(string option, string value)
    {
        string dump = WriteDump("world.sql", ClassicDump);

        (int code, _, string error) = await RunAsync("proc-events", dump, "--database", PathOf("world.db"), option, value);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("cooldown", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcEvents_ADumpWithoutTheTable_IsASchemaError_AndAMalformedOneIsUnreadable()
    {
        (int none, _, string noneError) = await RunAsync("proc-events", WriteDump("other.sql", "CREATE TABLE `other` (`a` int);\nINSERT INTO `other` VALUES (1);"),
            "--database", PathOf("a.db"));
        (int bad, _, _) = await RunAsync("proc-events", WriteDump("bad.sql", "INSERT INTO `spell_proc_event` (`entry`,`procFlags`) VALUES (1,2),(1,3);"),
            "--database", PathOf("b.db"));

        Assert.Equal(ExitCodes.Schema, none);
        Assert.Contains("spell_proc_event", noneError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Io, bad);
        Assert.False(File.Exists(PathOf("b.db")));
    }
}

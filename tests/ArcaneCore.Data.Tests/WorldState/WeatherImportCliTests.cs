using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.WorldState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.WorldState;

/// <summary>
/// The content importer command line imports <c>game_weather</c> and <c>exploration_basexp</c> (until now only the library
/// importer existed, so an imported world had no weather chances at all). Driven in-process against SQLite files in the
/// temp directory with synthetic dumps; the real-dump check is skipped, visibly, without classic-db. The importer logic is
/// provider-neutral EF code, and the provider schema theories for the two tables live in <see cref="WorldStateDataTests"/>;
/// this class ran on SQLite only.
/// </summary>
public sealed class WeatherImportCliTests : IDisposable
{
    private const string Dump = """
        CREATE TABLE `creature_template` (`Entry` int unsigned NOT NULL, `Name` varchar(100), `MinLevel` tinyint, `MaxLevel` tinyint, `ModelId1` int, PRIMARY KEY (`Entry`));
        INSERT INTO `creature_template` VALUES (1001,'Wolf',1,2,100);
        CREATE TABLE `exploration_basexp` (
          `level` tinyint NOT NULL DEFAULT '0',
          `basexp` mediumint NOT NULL DEFAULT '0',
          PRIMARY KEY (`level`)
        ) ENGINE=MyISAM;
        INSERT INTO `exploration_basexp` VALUES (0,0),(1,5),(60,660);
        CREATE TABLE `game_weather` (
          `zone` mediumint unsigned NOT NULL DEFAULT '0',
          `spring_rain_chance` tinyint unsigned NOT NULL,
          `spring_snow_chance` tinyint unsigned NOT NULL,
          `spring_storm_chance` tinyint unsigned NOT NULL,
          `summer_rain_chance` tinyint unsigned NOT NULL,
          `summer_snow_chance` tinyint unsigned NOT NULL,
          `summer_storm_chance` tinyint unsigned NOT NULL,
          `fall_rain_chance` tinyint unsigned NOT NULL,
          `fall_snow_chance` tinyint unsigned NOT NULL,
          `fall_storm_chance` tinyint unsigned NOT NULL,
          `winter_rain_chance` tinyint unsigned NOT NULL,
          `winter_snow_chance` tinyint unsigned NOT NULL,
          `winter_storm_chance` tinyint unsigned NOT NULL,
          PRIMARY KEY (`zone`)
        ) ENGINE=MyISAM;
        INSERT INTO `game_weather` VALUES (12,20,0,0,10,0,0,20,0,0,25,0,5),(1377,0,0,20,0,0,10,0,0,20,0,0,10);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-weathercli-" + Guid.NewGuid().ToString("N"));

    public WeatherImportCliTests() => Directory.CreateDirectory(_directory);

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

    private string Path_(string name) => Path.Combine(_directory, name);

    private string WriteDump(string name, string sql)
    {
        string path = Path_(name);
        File.WriteAllText(path, sql);
        return path;
    }

    private static WorldDbContext Open(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static async Task<(List<GameWeatherRow> Weather, List<ExplorationBaseXpRow> BaseXp)> ReadAsync(string path)
    {
        await using WorldDbContext db = Open(path);
        return (await db.Set<GameWeatherRow>().AsNoTracking().OrderBy(r => r.Zone).ToListAsync(),
            await db.Set<ExplorationBaseXpRow>().AsNoTracking().OrderBy(r => r.Level).ToListAsync());
    }

    [Fact]
    public async Task Import_WritesTheWeatherChancesAndTheExplorationXp()
    {
        string dump = WriteDump("dump.sql", Dump);
        string db = Path_("world.db");

        (int code, string output, string err) = await RunAsync("import", dump, "--database", db);

        Assert.True(code == 0, err + output);
        Assert.Matches(@"game_weather\s+2", output);
        Assert.Matches(@"exploration_basexp\s+3", output);
        (List<GameWeatherRow> weather, List<ExplorationBaseXpRow> baseXp) = await ReadAsync(db);
        Assert.Equal([12u, 1377u], weather.Select(w => w.Zone));
        Assert.Equal((20, 25, 5), (weather[0].SpringRainChance, weather[0].WinterRainChance, weather[0].WinterStormChance));
        Assert.Equal(20, weather[1].SpringStormChance);
        Assert.Equal([0u, 1u, 60u], baseXp.Select(b => b.Level));
        Assert.Equal(660u, baseXp[^1].BaseXp);
    }

    [Fact]
    public async Task DryRun_ReadsAndCounts_ButWritesNothing()
    {
        string dump = WriteDump("dump.sql", Dump);
        string db = Path_("world.db");

        (int code, string output, string err) = await RunAsync("import", dump, "--database", db, "--dry-run");

        Assert.True(code == 0, err + output);
        Assert.Matches(@"game_weather\s+2", output);
        Assert.False(File.Exists(db));
    }

    [Fact]
    public async Task ASecondImport_FailsWithoutReplace_AndLeavesTheRows_AndReplaceEmptiesFirst()
    {
        string db = Path_("world.db");
        Assert.Equal(0, (await RunAsync("import", WriteDump("a.sql", Dump), "--database", db)).Code);

        (int code, _, _) = await RunAsync("import", WriteDump("b.sql", Dump), "--database", db);
        Assert.Equal(ExitCodes.Database, code);
        Assert.Equal(2, (await ReadAsync(db)).Weather.Count);

        // --replace: only zone 12 is in the new dump, so 1377 goes away
        string smaller = Dump.Replace(",(1377,0,0,20,0,0,10,0,0,20,0,0,10)", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(Dump, smaller);
        (int replaced, string output, string err) = await RunAsync("import", WriteDump("c.sql", smaller), "--database", db, "--replace");
        Assert.True(replaced == 0, err + output);
        Assert.Equal([12u], (await ReadAsync(db)).Weather.Select(w => w.Zone));
    }

    [Fact]
    public async Task AMangledWeatherRow_IsRejectedBeforeAnythingIsWritten()
    {
        string db = Path_("world.db");
        string broken = Dump.Replace("(1377,0,0,20,0,0,10,0,0,20,0,0,10)", "(1377,0,0,x,0,0,10,0,0,20,0,0,10)", StringComparison.Ordinal);

        (int code, _, string err) = await RunAsync("import", WriteDump("broken.sql", broken), "--database", db);

        Assert.Equal(ExitCodes.Io, code);
        Assert.Contains("not a number", err, StringComparison.Ordinal);
        Assert.False(File.Exists(db));
    }

    [ClassicDbDumpFact]
    public async Task RealDump_Counts33WeatherZonesAnd61ExplorationLevels()
    {
        // A dry run over the real classic-db dump: the CLI reaches the two tables (no database is written).
        string path = Path_("classic.sql");
        await using (FileStream source = File.OpenRead(ClassicDbDumpFactAttribute.DumpPath))
        await using (var gzip = new GZipStream(source, CompressionMode.Decompress))
        await using (FileStream target = File.Create(path))
        {
            await gzip.CopyToAsync(target);
        }

        (int code, string output, string err) = await RunAsync("import", path, "--dry-run");

        Assert.True(code == 0, err);
        Assert.Matches(@"game_weather\s+33\b", output);
        Assert.Matches(@"exploration_basexp\s+61\b", output);
    }
}

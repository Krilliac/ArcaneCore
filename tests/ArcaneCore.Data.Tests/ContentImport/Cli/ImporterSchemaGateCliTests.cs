using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// The writing verbs of the content importer (<c>import</c>, <c>import-dbc</c>, <c>import-map-dbc</c>, <c>proc-events</c>, <c>spell-threat</c>)
/// must not upgrade an existing world silently (the library default policy is <c>Always</c>): like <c>refresh</c> they refuse a world behind
/// the importer's schema unless <c>--migrate</c> is given, while still creating a database that holds no schema.
/// </summary>
public sealed class ImporterSchemaGateCliTests : IDisposable
{
    private const string ProcDump = """
        INSERT INTO `spell_proc_event` (`entry`,`SchoolMask`,`SpellFamilyName`,`SpellFamilyMask0`,`SpellFamilyMask1`,`SpellFamilyMask2`,`procFlags`,`procEx`,`ppmRate`,`CustomChance`,`Cooldown`,`build_min`,`build_max`) VALUES (100,0,0,0,0,0,4,1,0,0,0,5875,9999);
        """;

    private const string ThreatDump = """
        INSERT INTO `spell_threat` (`entry`,`threat`,`multiplier`,`inverse_effect_mask`,`build_min`,`build_max`) VALUES (100,10,1,0,0,9999);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-gate-" + Guid.NewGuid().ToString("N"));

    public ImporterSchemaGateCliTests() => Directory.CreateDirectory(_directory);

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

    private static WorldDbContext Open(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("proc-events", ProcDump)]
    [InlineData("spell-threat", ThreatDump)]
    public async Task WritingVerb_RefusesAWorldBehindTheImportersSchema_UnlessToldToMigrate(string verb, string sql)
    {
        string world = Path.Combine(_directory, "world.db");
        int current = WorldDbContext.Schema.CurrentVersion;
        await using (WorldDbContext db = Open(world))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Upgrade.UpgradeTestSupport.SetVersionAsync(db, "world", current - 1);
        }

        string dump = Path.Combine(_directory, "dump.sql");
        await File.WriteAllTextAsync(dump, sql);

        (int code, string output, string error) = await RunAsync(verb, dump, "--database", world);

        Assert.Equal(ExitCodes.Schema, code);
        Assert.Contains("--migrate", error, StringComparison.Ordinal);
        await using (WorldDbContext db = Open(world))
        {
            Assert.Equal(current - 1, await Upgrade.UpgradeTestSupport.ReadVersionAsync(db, "world"));
        }

        (code, output, error) = await RunAsync(verb, dump, "--database", world, "--migrate");

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains($"world schema {current - 1} -> {current}", output, StringComparison.Ordinal);
        await using (WorldDbContext db = Open(world))
        {
            Assert.Equal(current, await Upgrade.UpgradeTestSupport.ReadVersionAsync(db, "world"));
        }
    }

    [Theory]
    [InlineData("proc-events", ProcDump)]
    [InlineData("spell-threat", ThreatDump)]
    public async Task WritingVerb_StillCreatesAMissingWorld(string verb, string sql)
    {
        string world = Path.Combine(_directory, "new.db");
        string dump = Path.Combine(_directory, "dump.sql");
        await File.WriteAllTextAsync(dump, sql);

        (int code, string output, string error) = await RunAsync(verb, dump, "--database", world);

        Assert.True(code == ExitCodes.Ok, error + output);
        await using WorldDbContext db = Open(world);
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, await Upgrade.UpgradeTestSupport.ReadVersionAsync(db, "world"));
    }
}

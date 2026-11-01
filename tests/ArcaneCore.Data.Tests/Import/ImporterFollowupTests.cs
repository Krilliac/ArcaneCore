using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Import;

/// <summary>
/// Importer follow-ups: the refresh fills <c>points_of_interest</c>, a dump whose copy lacks a column warns and skips instead of aborting
/// the NPC import, markers outside the map are dropped, <c>verify</c> counts the table, the conditions importer tells vmangos by the missing
/// <c>comments</c> column, a full import's spell script TEAM condition never reads the pre-import <c>conditions</c> table, a refresh whose
/// safe locations come only from <c>WorldSafeLocs.dbc</c> keeps the stored facings, and the world-state importer refuses an unbackticked
/// INSERT of a table it reads. Driven against SQLite files in the temp directory; the dump rows are synthetic.
/// </summary>
public sealed class ImporterFollowupTests : IDisposable
{
    private const string PoiColumns = "(`entry`,`x`,`y`,`icon`,`flags`,`data`,`icon_name`)";

    private const string VmangosColumns =
        "`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`datalong4`,`target_param1`,`target_param2`,`target_type`,`data_flags`,"
        + "`dataint`,`dataint2`,`dataint3`,`dataint4`,`x`,`y`,`z`,`o`,`condition_id`,`comments`";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-importer-followups-" + Guid.NewGuid().ToString("N"));

    public ImporterFollowupTests() => Directory.CreateDirectory(_directory);

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

    private async Task<string> NewWorldAsync()
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db");
        await using WorldDbContext db = Open(path);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        return path;
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static NpcDumpImporter ReadNpc(string dump)
    {
        var importer = new NpcDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public async Task Refresh_FillsPointsOfInterest_AndVerifyCountsThem()
    {
        string world = await NewWorldAsync();
        string dump = Path.Combine(_directory, "poi.sql");
        await File.WriteAllTextAsync(dump,
            $"INSERT INTO `points_of_interest` {PoiColumns} VALUES (1,-8830.5,626.3,7,99,0,'Stormwind'),(2,1629.4,-4373.1,6,99,0,'Orgrimmar');");

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("  points_of_interest  2", output, StringComparison.Ordinal);
        await using (WorldDbContext db = Open(world))
        {
            Assert.Equal([1u, 2u], await db.Set<PointOfInterest>().OrderBy(p => p.Entry).Select(p => p.Entry).ToListAsync());
        }

        (int verifyCode, string verifyOut, string verifyError) = await RunAsync("verify", "--database", world);

        Assert.True(verifyOut.Length > 0, verifyError);
        Assert.Contains("  points_of_interest  2", verifyOut, StringComparison.Ordinal);
        _ = verifyCode; // the empty geography tables of this bare world fail the verify; only its counts are under test
    }

    [Fact]
    public void ADumpWhosePointsOfInterestLackAColumn_WarnsAndSkips_InsteadOfAbortingTheNpcImport()
    {
        NpcDumpImporter importer = ReadNpc("""
            INSERT INTO `gossip_menu` (`entry`,`text_id`,`condition_id`) VALUES (7,8,0);
            INSERT INTO `points_of_interest` (`entry`,`x`,`y`,`icon`,`flags`,`data`) VALUES (1,10,20,7,99,0);
            """);

        NpcImportReport report = importer.BuildReport();
        Assert.True(importer.PointsOfInterestTableRejected);
        Assert.Equal(1, report.GossipMenus);
        Assert.Equal(0, report.PointsOfInterest);
        Assert.Contains(report.Diagnostics, d => d.StartsWith("points_of_interest", StringComparison.Ordinal) && d.Contains("icon_name", StringComparison.Ordinal));
    }

    [Fact]
    public void APointOfInterestOutsideTheMap_IsSkippedAndCounted()
    {
        NpcDumpImporter importer = ReadNpc($"INSERT INTO `points_of_interest` {PoiColumns} VALUES (1,-8830.5,626.3,7,99,0,'ok'),(2,99999,5,7,99,0,'far'),(3,5,-17100,7,99,0,'edge');");

        NpcImportReport report = importer.BuildReport();
        Assert.Equal(1, report.PointsOfInterest);
        Assert.Equal(2, report.Skipped);
        Assert.Equal(2, report.Diagnostics.Count(d => d.Contains("not a valid map coordinate", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConditionsRowsWithValue3AndValue4ButNoCommentsColumn_AreTheVmangosLayout_AndRefused()
    {
        var importer = new ConditionsDumpImporter();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => importer.Read(new StringReader(
            "INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES (2,6,67,0,0,0,0);")));

        Assert.Contains("vmangos", error.Message, StringComparison.Ordinal);
        Assert.Contains("comments", error.Message, StringComparison.Ordinal);

        var cmangos = new ConditionsDumpImporter();
        cmangos.Read(new StringReader(
            "INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`,`comments`) VALUES (3,6,67,0,0,0,0,'Horde');"));
        Assert.Single(cmangos.Rows);
    }

    [Fact]
    public async Task AFullImportsTeamCondition_DoesNotReadThePreImportConditionsTable_ButARefreshDoes()
    {
        // vmangos condition 3 is TEAM 469; the input has no cmangos row of that meaning. The world's stored conditions table holds one (4),
        // but a full import writes conditions later in the same transaction (--replace empties it), so that row is not a safe answer.
        string dump = $"""
            INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES (3,6,469,0,0,0,0);
            INSERT INTO `spell_scripts` ({VmangosColumns}) VALUES (11,0,0,15,101,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,3,'alliance');
            """;

        string full = await NewWorldAsync();
        var fullImporter = new DbScriptDumpImporter();
        fullImporter.Read(new StringReader(dump));
        await using (WorldDbContext db = Open(full))
        {
            db.Set<ConditionRow>().Add(new ConditionRow { ConditionEntry = 4, Type = 6, Value1 = 469 });
            await db.SaveChangesAsync();
            await fullImporter.InsertAllAsync(db, CancellationToken.None);
        }

        await using (WorldDbContext db = Open(full))
        {
            Assert.Empty(await db.Set<SpellScriptRow>().ToListAsync());
        }

        Assert.Contains(fullImporter.Warnings, w => w.Contains("spell_scripts 11", StringComparison.Ordinal));

        string refresh = await NewWorldAsync();
        var refreshImporter = new DbScriptDumpImporter();
        refreshImporter.Read(new StringReader(dump));
        await using (WorldDbContext db = Open(refresh))
        {
            db.Set<ConditionRow>().Add(new ConditionRow { ConditionEntry = 4, Type = 6, Value1 = 469 });
            await db.SaveChangesAsync();
            await refreshImporter.ReplaceAsync(db);
        }

        await using (WorldDbContext db = Open(refresh))
        {
            Assert.Equal(4u, Assert.Single(await db.Set<SpellScriptRow>().ToListAsync()).ConditionId);
        }
    }

    [Fact]
    public async Task ARefreshWhoseSafeLocationsComeOnlyFromTheDbc_KeepsTheStoredFacings()
    {
        string world = await NewWorldAsync();
        await using (WorldDbContext db = Open(world))
        {
            db.Set<WorldSafeLocRow>().AddRange(
                new WorldSafeLocRow { Id = 890, MapId = 489, X = 1, Y = 2, Z = 3, Orientation = 1.5f, Name = "Stored" },
                new WorldSafeLocRow { Id = 891, MapId = 489, X = 4, Y = 5, Z = 6, Orientation = 2.5f, Name = "Faced by the dump" });
            await db.SaveChangesAsync();
        }

        var importer = new GraveyardDumpImporter();
        importer.Read(new StringReader("INSERT INTO `world_safe_locs_facing` (`id`,`orientation`) VALUES (891,0.75);"));
        Assert.Equal(2, importer.AddMissingSafeLocs(
        [
            new WorldSafeLoc(890, 489, 10, 20, 30, 0f, "From the DBC"),
            new WorldSafeLoc(891, 489, 40, 50, 60, 0f, "From the DBC"),
        ]));

        await using (WorldDbContext db = Open(world))
        {
            await importer.WriteAsync(db, replace: true, CancellationToken.None, onlyCarried: true);
        }

        await using (WorldDbContext db = Open(world))
        {
            List<WorldSafeLocRow> rows = await db.Set<WorldSafeLocRow>().OrderBy(r => r.Id).ToListAsync();
            Assert.Equal([(890u, 10f, 1.5f), (891u, 40f, 0.75f)], rows.Select(r => (r.Id, r.X, r.Orientation)));
        }
    }

    [Theory]
    [InlineData("INSERT INTO game_weather VALUES (12,1,2,3,4,5,6,7,8,9,10,11,12);")]
    [InlineData("INSERT INTO exploration_basexp VALUES (1,5);")]
    [InlineData("INSERT IGNORE INTO game_weather VALUES (12,1,2,3,4,5,6,7,8,9,10,11,12);")]
    [InlineData("REPLACE exploration_basexp (level,basexp) VALUES (1,5);")]
    public void AnUnbackticked_InsertOfTheTableItReads_IsRefused_NotSilentlyDropped(string line)
        => Assert.Throws<InvalidDataException>(() => WorldStateDumpImporter.Parse(new StringReader(line)));

    [Fact]
    public void AnUnbackticked_InsertOfAnotherTableWithTheSamePrefix_IsNotMistakenForOne()
    {
        WorldStateContent content = WorldStateDumpImporter.Parse(new StringReader("INSERT INTO game_weather_extra VALUES (1,2);"));

        Assert.Empty(content.Weather);
        Assert.Empty(content.BaseXp);
    }
}

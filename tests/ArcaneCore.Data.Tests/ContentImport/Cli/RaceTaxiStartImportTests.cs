using System.Text;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Tests.Skills;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// race_taxi_start (a race's starting flight-path mask) is filled from ChrRaces.dbc field 14 by import-dbc and refresh. Every DBC here is
/// written by the test into the temp directory; the real-file probe reads the directory named by ARCANECORE_TEST_DBC_DIR and commits nothing.
/// </summary>
public sealed class RaceTaxiStartImportTests : IDisposable
{
    private const int ChrRacesFields = 29;
    private const int StartingTaxiNodesField = 14;

    private const string TeleportDump =
        "CREATE TABLE `areatrigger_teleport` (`id` mediumint unsigned NOT NULL, `name` text, `required_level` tinyint unsigned NOT NULL, `required_item` mediumint unsigned, `required_item2` mediumint unsigned, `required_quest_done` mediumint unsigned, `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL, `status_failed_text` text, `condition_id` int, PRIMARY KEY (`id`));\n" +
        "INSERT INTO `areatrigger_teleport` VALUES (2848,'Onyxia',50,16309,7,0,249,1,2,3,4,'amulet',11);\n";

    // The masks the build-5875 ChrRaces.dbc holds for the eight playable races.
    private static readonly (uint Race, uint Mask)[] Races =
    [
        (1, 2), (2, 0x400000), (3, 32), (4, 0x6000000), (5, 1024), (6, 0x200000), (7, 32), (8, 0x400000),
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-racetaxi-" + Guid.NewGuid().ToString("N"));

    public RaceTaxiStartImportTests() => Directory.CreateDirectory(_directory);

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

    private string DbcDirectory => Path.Combine(_directory, "DBFilesClient");

    private string DatabasePath => Path.Combine(_directory, "world.db");

    private string WriteChrRaces(params (uint Race, uint Mask)[] races)
    {
        Directory.CreateDirectory(DbcDirectory);
        var records = new List<uint[]>();
        foreach ((uint race, uint mask) in races)
        {
            uint[] record = new uint[ChrRacesFields];
            record[0] = race;
            record[StartingTaxiNodesField] = mask;
            records.Add(record);
        }

        File.WriteAllBytes(Path.Combine(DbcDirectory, "ChrRaces.dbc"), Wdbc(ChrRacesFields, records));
        return DbcDirectory;
    }

    private static byte[] Wdbc(int fields, IReadOnlyList<uint[]> records, string stringBlock = "\0")
    {
        byte[] strings = Encoding.UTF8.GetBytes(stringBlock);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("WDBC"));
            writer.Write((uint)records.Count);
            writer.Write((uint)fields);
            writer.Write((uint)(fields * 4));
            writer.Write((uint)strings.Length);
            foreach (uint[] record in records)
            {
                foreach (uint value in record)
                {
                    writer.Write(value);
                }
            }

            writer.Write(strings);
        }

        return stream.ToArray();
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static WorldDbContext Open(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private async Task<string> EmptyWorldAsync()
    {
        await using WorldDbContext db = Open(DatabasePath);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        return DatabasePath;
    }

    /// <summary>The five spell DBCs import-dbc insists on, with one row each.</summary>
    private void WriteSpellDbcs()
    {
        Directory.CreateDirectory(DbcDirectory);
        uint[] spell = new uint[SpellDbcImporter.SpellFieldCount];
        spell[0] = 133;
        spell[120] = 1;
        File.WriteAllBytes(Path.Combine(DbcDirectory, "Spell.dbc"), Wdbc(spell.Length, [spell], "\0Fireball\0"));
        File.WriteAllBytes(Path.Combine(DbcDirectory, "SpellCastTimes.dbc"), Wdbc(4, [[5u, 3500u, 0u, 3500u]]));
        File.WriteAllBytes(Path.Combine(DbcDirectory, "SpellDuration.dbc"), Wdbc(4, [[21u, unchecked((uint)-1), 0u, unchecked((uint)-1)]]));
        File.WriteAllBytes(Path.Combine(DbcDirectory, "SpellRange.dbc"), Wdbc(22, [[4u, 0u, BitConverter.SingleToUInt32Bits(30f), 0u, .. new uint[18]]]));
        File.WriteAllBytes(Path.Combine(DbcDirectory, "SpellRadius.dbc"), Wdbc(4, [[7u, BitConverter.SingleToUInt32Bits(2f), 0u, BitConverter.SingleToUInt32Bits(2f)]]));
    }

    [Fact]
    public void Reader_ReadsRaceAndFieldFourteen_SkippingRacesWithNoStartingNode()
    {
        string dir = WriteChrRaces((1, 2), (2, 0x400000), (9, 0));

        IReadOnlyList<RaceTaxiStart> rows = NpcServiceDbcReaders.LoadRaceTaxiStarts(Path.Combine(dir, "ChrRaces.dbc"));

        Assert.Equal([(1, 2u), (2, 0x400000u)], rows.Select(r => ((int)r.Race, r.Mask)).ToArray());
    }

    [Fact]
    public void Reader_RefusesARaceIdThatDoesNotFitTheByteKey_AndAFileTooNarrowForField14()
    {
        string dir = WriteChrRaces((300, 2));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.LoadRaceTaxiStarts(Path.Combine(dir, "ChrRaces.dbc")));

        File.WriteAllBytes(Path.Combine(dir, "Narrow.dbc"), Wdbc(10, [new uint[10]]));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.LoadRaceTaxiStarts(Path.Combine(dir, "Narrow.dbc")));
    }

    [Fact]
    public async Task ImportDbc_FillsRaceTaxiStart_FromChrRaces_AndKeepsExistingRowsOnARerun()
    {
        string database = await EmptyWorldAsync();
        WriteSpellDbcs();
        WriteChrRaces(Races);

        (int code, string output, string error) = await RunAsync("import-dbc", DbcDirectory, "--database", database);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("Imported 8 race starting flight masks (race_taxi_start) from ChrRaces.dbc.", output, StringComparison.Ordinal);
        await using (WorldDbContext db = Open(database))
        {
            Dictionary<byte, uint> rows = await db.Set<RaceTaxiStart>().ToDictionaryAsync(r => r.Race, r => r.Mask);
            Assert.Equal(Races.ToDictionary(r => (byte)r.Race, r => r.Mask), rows);
            Assert.Equal(2u, rows[1]);
        }

        WriteChrRaces((1, 0xFF));
        (code, output, error) = await RunAsync("import-dbc", DbcDirectory, "--database", database);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("race_taxi_start already have rows", output, StringComparison.Ordinal);
        await using WorldDbContext again = Open(database);
        Assert.Equal(8, await again.Set<RaceTaxiStart>().CountAsync());
        Assert.Equal(2u, (await again.Set<RaceTaxiStart>().SingleAsync(r => r.Race == 1)).Mask);
    }

    [Fact]
    public async Task ImportDbc_WithoutChrRaces_LeavesTheTableEmpty_AndSaysSo()
    {
        string database = await EmptyWorldAsync();
        WriteSpellDbcs();

        (int code, string output, string error) = await RunAsync("import-dbc", DbcDirectory, "--database", database);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("no ChrRaces.dbc in", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(database);
        Assert.Equal(0, await db.Set<RaceTaxiStart>().CountAsync());
    }

    [Fact]
    public async Task ImportedRows_LoadThroughTheNpcContentStore()
    {
        string database = await EmptyWorldAsync();
        WriteSpellDbcs();
        WriteChrRaces(Races);
        Assert.Equal(ExitCodes.Ok, (await RunAsync("import-dbc", DbcDirectory, "--database", database)).Code);

        await using WorldDbContext db = Open(database);
        NpcContent content = await new EfNpcContentStore(db).LoadAsync();

        Assert.Equal(8, content.RaceTaxiStarts.Count);
        Assert.Equal(32u, content.RaceTaxiStarts.Single(r => r.Race == 3).Mask);
    }

    [Fact]
    public async Task Refresh_ReplacesRaceTaxiStart_FromChrRaces()
    {
        string database = await EmptyWorldAsync();
        await using (WorldDbContext seed = Open(database))
        {
            seed.Set<RaceTaxiStart>().Add(new RaceTaxiStart { Race = 1, Mask = 0xFF });
            seed.Set<RaceTaxiStart>().Add(new RaceTaxiStart { Race = 77, Mask = 1 });
            await seed.SaveChangesAsync();
        }

        string dump = Path.Combine(_directory, "dump.sql");
        await File.WriteAllTextAsync(dump, TeleportDump);
        WriteChrRaces(Races);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", database, "--dbc-dir", DbcDirectory);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("  race_taxi_start  8", output, StringComparison.Ordinal);
        await using WorldDbContext check = Open(database);
        Assert.Equal(Races.ToDictionary(r => (byte)r.Race, r => r.Mask),
            await check.Set<RaceTaxiStart>().ToDictionaryAsync(r => r.Race, r => r.Mask));
    }

    [Fact]
    public async Task Refresh_WithoutChrRaces_KeepsTheRows()
    {
        string database = await EmptyWorldAsync();
        await using (WorldDbContext seed = Open(database))
        {
            seed.Set<RaceTaxiStart>().Add(new RaceTaxiStart { Race = 1, Mask = 2 });
            await seed.SaveChangesAsync();
        }

        string dump = Path.Combine(_directory, "dump.sql");
        await File.WriteAllTextAsync(dump, TeleportDump);
        Directory.CreateDirectory(DbcDirectory);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", database, "--dbc-dir", DbcDirectory);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("no ChrRaces.dbc in", output, StringComparison.Ordinal);
        await using WorldDbContext check = Open(database);
        Assert.Equal(2u, (await check.Set<RaceTaxiStart>().SingleAsync()).Mask);
    }

    [RealDbcFact]
    public void RealChrRaces_HoldsTheEightPlayableRaces_WithTheKnownMasks()
    {
        string source = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;

        IReadOnlyList<RaceTaxiStart> rows = NpcServiceDbcReaders.LoadRaceTaxiStarts(Path.Combine(source, "ChrRaces.dbc"));

        Assert.Equal(Races.ToDictionary(r => (byte)r.Race, r => r.Mask), rows.ToDictionary(r => r.Race, r => r.Mask));
    }
}

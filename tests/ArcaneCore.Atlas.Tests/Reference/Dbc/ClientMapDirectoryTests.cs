using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Dbc;

/// <summary>Map.dbc / AreaTable.dbc decoding: synthetic tables in the 1.12.1 layouts, then the real client's tables.</summary>
public sealed class ClientMapDirectoryTests(ITestOutputHelper output)
{
    /// <summary>A Map.dbc row in the 1.12.1.5875 layout (id, directory, instance type, map type, enUS name at cell 4).</summary>
    internal static void AddMap(WdbcTestBuilder maps, uint id, string directory, uint instanceType, string name)
        => maps.Row([(0, id), (1, maps.String(directory)), (2, instanceType), .. maps.Localized(4, name)]);

    /// <summary>An AreaTable.dbc row in the 1.12.1 layout (id at cell 0, enUS name at cell 11).</summary>
    internal static void AddArea(WdbcTestBuilder areas, uint id, string name)
        => areas.Row([(0, id), (1, 0), .. areas.Localized(11, name)]);

    /// <summary>Client files holding a small Map.dbc and AreaTable.dbc.</summary>
    internal static MemoryClientFiles SampleFiles()
    {
        var maps = new WdbcTestBuilder(ClientMapDirectory.MapFieldCount);
        AddMap(maps, 1, "Kalimdor", 0, "Kalimdor");
        AddMap(maps, 0, "Azeroth", 0, "Eastern Kingdoms");
        AddMap(maps, 409, "MoltenCore", 2, "Molten Core");
        var areas = new WdbcTestBuilder(ClientMapDirectory.AreaFieldCount);
        AddArea(areas, 12, "Elwynn Forest");
        AddArea(areas, 40, "Westfall");
        areas.Row((0, 99));
        var files = new MemoryClientFiles();
        files.Add(ClientMapDirectory.MapTablePath, maps.Build());
        files.Add(ClientMapDirectory.AreaTablePath, areas.Build());
        return files;
    }

    [Fact]
    public void Load_ReadsMapsSortedById_AndAreaNames()
    {
        ClientMapDirectory directory = ClientMapDirectory.Load(SampleFiles());

        Assert.Equal(new[] { 0u, 1u, 409u }, directory.Maps.Select(m => m.Id));
        Assert.Equal(new ClientMap(409, "MoltenCore", "Molten Core", 2), directory.Maps[2]);
        Assert.True(directory.TryGetMap(0, out ClientMap azeroth));
        Assert.Equal("Azeroth", azeroth.Directory);
        Assert.Equal("Eastern Kingdoms", azeroth.Name);
        Assert.False(directory.TryGetMap(2, out _));
        Assert.Equal("Elwynn Forest", directory.AreaName(12));
        Assert.Equal("Westfall", directory.AreaName(40));
        Assert.Null(directory.AreaName(99));
        Assert.Null(directory.AreaName(7));
        Assert.Equal(3, directory.AreaCount);
    }

    [Fact]
    public void Load_RefusesAnotherBuildsLayout()
    {
        var files = new MemoryClientFiles();
        var maps = new WdbcTestBuilder(ClientMapDirectory.MapFieldCount - 1);
        maps.Row((0, 0));
        files.Add(ClientMapDirectory.MapTablePath, maps.Build());
        files.Add(ClientMapDirectory.AreaTablePath, new WdbcTestBuilder(ClientMapDirectory.AreaFieldCount).Build());

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ClientMapDirectory.Load(files));
        Assert.Contains("41 fields", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RefusesMissingTablesAndDuplicateIds()
    {
        var onlyMaps = new MemoryClientFiles();
        onlyMaps.Add(ClientMapDirectory.MapTablePath, new WdbcTestBuilder(ClientMapDirectory.MapFieldCount).Build());
        Assert.Contains(ClientMapDirectory.AreaTablePath, Assert.Throws<FileNotFoundException>(() => ClientMapDirectory.Load(onlyMaps)).Message, StringComparison.Ordinal);

        var maps = new WdbcTestBuilder(ClientMapDirectory.MapFieldCount);
        AddMap(maps, 5, "A", 0, "A");
        AddMap(maps, 5, "B", 0, "B");
        var twice = new MemoryClientFiles();
        twice.Add(ClientMapDirectory.MapTablePath, maps.Build());
        twice.Add(ClientMapDirectory.AreaTablePath, new WdbcTestBuilder(ClientMapDirectory.AreaFieldCount).Build());
        Assert.Contains("map id 5 twice", Assert.Throws<InvalidDataException>(() => ClientMapDirectory.Load(twice)).Message, StringComparison.Ordinal);
    }

    [ClientArchiveFact]
    public void TheRealClientTables_ListEveryExtractedMap()
    {
        using MpqFileSystem files = MpqFileSystem.Open(Path.Combine(ClientArchiveData.ClientRoot!, "Data"));
        ClientMapDirectory directory = ClientMapDirectory.Load(files);
        output.WriteLine($"Map.dbc from {files.ArchiveOf(ClientMapDirectory.MapTablePath)}: {directory.Maps.Count} maps; "
            + $"AreaTable.dbc from {files.ArchiveOf(ClientMapDirectory.AreaTablePath)}: {directory.AreaCount} areas");
        foreach (ClientMap map in directory.Maps)
        {
            output.WriteLine($"  {map.Id,4} {map.InstanceType} {map.Directory} / {map.Name}");
        }

        // Record counts printed by an independent reader (client-dbc-5875 README.txt:26,104).
        Assert.Equal(44, directory.Maps.Count);
        Assert.Equal(1081, directory.AreaCount);
        Assert.Equal("patch-2.MPQ", files.ArchiveOf(ClientMapDirectory.MapTablePath));

        // Instance types are vmangos's MapTypes (vmangos@0e3ff01 src/game/Database/DBCEnums.h:143-149): 0..3 only.
        Assert.All(directory.Maps, m => Assert.InRange(m.InstanceType, 0u, 3u));
        Assert.True(directory.TryGetMap(0, out ClientMap azeroth));
        Assert.Equal("Azeroth", azeroth.Directory);
        Assert.Equal(0u, azeroth.InstanceType);
        Assert.True(directory.TryGetMap(1, out ClientMap kalimdor));
        Assert.Equal("Kalimdor", kalimdor.Directory);
        Assert.All(directory.Maps, m => Assert.False(string.IsNullOrEmpty(m.Name), $"map {m.Id} has no enUS name"));

        // Names read from the enUS column of each table; ids and names cross-checked with a separate C reader of the extracted
        // DBCs (AreaTable column 11, Map column 4).
        Assert.Equal("Eastern Kingdoms", azeroth.Name);
        Assert.Equal("Kalimdor", kalimdor.Name);
        Assert.Equal("Dun Morogh", directory.AreaName(1));
    }

    [ClientAndTerrainFact]
    public void EveryMapTheVmangosExtractorWrote_IsInMapDbc_WithAWdt()
    {
        using MpqFileSystem files = MpqFileSystem.Open(Path.Combine(ClientArchiveData.ClientRoot!, "Data"));
        ClientMapDirectory directory = ClientMapDirectory.Load(files);
        string terrain = Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable)!;
        // Every map the vmangos extractor wrote .map tiles for (it read Map.dbc with its own reader) is listed here, and the
        // directory column names a folder holding that map's WDT in the archives.
        uint[] extracted = [.. Directory.EnumerateFiles(Path.Combine(terrain, "maps"), "*.map")
            .Select(f => uint.Parse(Path.GetFileName(f)[..3], System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .Order()];
        output.WriteLine($"server-extracted map ids: {string.Join(' ', extracted)}");
        Assert.True(extracted.Length > 10, $"only {extracted.Length} extracted maps");
        foreach (uint id in extracted)
        {
            Assert.True(directory.TryGetMap(id, out ClientMap map), $"extracted map {id} is not in Map.dbc");
            Assert.True(files.Exists($"World\\Maps\\{map.Directory}\\{map.Directory}.wdt"), $"no WDT for map {id} ({map.Directory})");
        }
    }
}

/// <summary>A fact that needs the real client's MPQs and the server-extracted terrain folder; skipped, visibly, without either.</summary>
public sealed class ClientAndTerrainFactAttribute : FactAttribute
{
    public ClientAndTerrainFactAttribute()
    {
        if (ClientArchiveData.SkipReason() is { } reason)
        {
            Skip = reason;
        }
        else if (Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable) is not { Length: > 0 } terrain
            || !Directory.Exists(Path.Combine(terrain, "maps")))
        {
            Skip = $"{AtlasPaths.TerrainEnvironmentVariable} is unset or has no maps folder.";
        }
    }
}

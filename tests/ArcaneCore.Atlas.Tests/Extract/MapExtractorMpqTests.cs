using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// <c>extract.maps</c> over a client that is really MPQ archives: the files come out of <see cref="MpqFileSystem"/> (hash
/// lookup, sector decompression, patch override) and must give the same <c>.map</c> files as the same client held in memory.
/// </summary>
public sealed class MapExtractorMpqTests
{
    private static MemoryClientFiles InMemoryClient()
    {
        var fixture = new TerrainFixture();
        var maps = new WdbcTestBuilder(ClientMapDirectory.MapFieldCount);
        ClientMapDirectoryTests.AddMap(maps, 0, "Azeroth", 0, "Eastern Kingdoms");
        ClientMapDirectoryTests.AddMap(maps, 1, "Kalimdor", 0, "Kalimdor");
        ClientMapDirectoryTests.AddMap(maps, TerrainFixture.DungeonMap, "DeadminesInstance", 1, "The Deadmines");
        fixture.Files.Add(ClientMapDirectory.MapTablePath, maps.Build());
        fixture.Files.Add(ClientMapDirectory.AreaTablePath, ExtractFixtures.AreaTable((TerrainFixture.Area, 0x0042)));
        return fixture.Files;
    }

    private static MapExtractReport Run(IClientFileSource files, string output)
        => new MapExtractor(files, ClientMapDirectory.Load(files), AreaExploreFlags.Load(files)).Extract(output, null, overwrite: false, threads: 2, CancellationToken.None);

    [Fact]
    public void Extract_FromMpqArchives_WritesTheFilesTheInMemoryClientWrites()
    {
        MemoryClientFiles memory = InMemoryClient();
        using var expectedOutput = new TestDirectory();
        MapExtractReport expected = Run(memory, expectedOutput.Path);
        Assert.Equal(2, expected.Written);

        // dbc.MPQ holds a stale AreaTable (no explore flag), patch.MPQ the real one; terrain.MPQ holds every WDT and ADT, compressed.
        using var client = new TestDirectory();
        byte[] staleAreas = ExtractFixtures.AreaTable((TerrainFixture.Area, 0xFFFF));
        client.Write(Path.Combine("Data", "dbc.MPQ"), MpqTestWriter.Build(
        [
            new TestMpqFile(ClientMapDirectory.MapTablePath, memory.Read(ClientMapDirectory.MapTablePath)!) { Zlib = true },
            new TestMpqFile(ClientMapDirectory.AreaTablePath, staleAreas),
        ]));
        client.Write(Path.Combine("Data", "patch.MPQ"), MpqTestWriter.Build(
            [new TestMpqFile(ClientMapDirectory.AreaTablePath, memory.Read(ClientMapDirectory.AreaTablePath)!) { Zlib = true }],
            new TestMpqOptions { FormatVersion = 1 }));
        TestMpqFile[] terrain =
        [
            .. memory.List("World\\Maps\\").Select(name => new TestMpqFile(name, memory.Read(name)!) { Zlib = true }),
        ];
        Assert.Equal(4, terrain.Length);
        client.Write(Path.Combine("Data", "terrain.MPQ"), MpqTestWriter.Build(terrain));

        using MpqFileSystem archives = MpqFileSystem.Open(Path.Combine(client.Path, "Data"));
        using var actualOutput = new TestDirectory();
        MapExtractReport actual = Run(archives, actualOutput.Path);

        Assert.Equal(new[] { "dbc.MPQ", "terrain.MPQ", "patch.MPQ" }, archives.Archives.Select(a => a.Name));
        Assert.Empty(actual.Failures);
        Assert.Equal(expected.Maps, actual.Maps);
        string[] names = [.. Directory.EnumerateFiles(expected.MapsDirectory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        Assert.Equal(new[] { "0004832.map", "0004833.map" }, names);
        Assert.Equal(names, Directory.EnumerateFiles(actual.MapsDirectory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));
        foreach (string name in names)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(expected.MapsDirectory, name)), File.ReadAllBytes(Path.Combine(actual.MapsDirectory, name)));
        }
    }
}

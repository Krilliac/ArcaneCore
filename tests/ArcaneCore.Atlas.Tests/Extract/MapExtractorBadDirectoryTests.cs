using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// A Map.dbc row whose directory cannot be a folder name is that map's problem only: the reference fails to open its WDT
/// and goes on with the next map.
/// </summary>
public sealed class MapExtractorBadDirectoryTests
{
    private static readonly AreaExploreFlags Areas = ExtractFixtures.Areas((TerrainFixture.Area, 0x0042));

    /// <summary>The fixture's files with a real Map.dbc table: Azeroth, then four rows with unusable directories.</summary>
    private static (TerrainFixture Fixture, ClientMapDirectory Maps) Client()
    {
        var fixture = new TerrainFixture();
        var maps = new WdbcTestBuilder(ClientMapDirectory.MapFieldCount);
        ClientMapDirectoryTests.AddMap(maps, 0, "Azeroth", 0, "Eastern Kingdoms");
        ClientMapDirectoryTests.AddMap(maps, 7, string.Empty, 0, "Empty");
        ClientMapDirectoryTests.AddMap(maps, 8, "Dungeons\\Deadmines", 0, "Backslash");
        ClientMapDirectoryTests.AddMap(maps, 9, "Dungeons/Deadmines", 0, "Slash");
        ClientMapDirectoryTests.AddMap(maps, 10, "C:Evil", 0, "Colon");
        fixture.Files.Add(ClientMapDirectory.MapTablePath, maps.Build());
        fixture.Files.Add(ClientMapDirectory.AreaTablePath, ExtractFixtures.AreaTable((TerrainFixture.Area, 0x0042)));
        return (fixture, ClientMapDirectory.Load(fixture.Files));
    }

    [Fact]
    public void Extract_ReportsEachUnusableDirectoryAsThatMapsFailure_AndExtractsTheOtherMaps()
    {
        (TerrainFixture fixture, ClientMapDirectory maps) = Client();
        using var output = new TestDirectory();

        MapExtractReport report = new MapExtractor(fixture.Files, maps, Areas).Extract(output.Path, null, overwrite: false, threads: 2, CancellationToken.None, Path.Combine(output.Path, MapExtractor.MapsFolder));

        Assert.Equal(new uint[] { 0, 7, 8, 9, 10 }, report.Maps.Select(m => m.Id));
        MapExtractMapReport azeroth = report.Maps[0];
        Assert.Equal(2, azeroth.Written);
        Assert.Equal(0, azeroth.Failed);
        foreach (MapExtractMapReport bad in report.Maps.Skip(1))
        {
            Assert.False(bad.HasWdt);
            Assert.Equal(1, bad.Failed);
            Assert.Equal(0, bad.Written);
        }

        Assert.Equal(new uint[] { 7, 8, 9, 10 }, report.Failures.Select(f => f.Map));
        Assert.All(report.Failures, f =>
        {
            Assert.Equal((-1, -1), (f.TileX, f.TileY));
            Assert.Contains("not a single folder name", f.Reason, StringComparison.Ordinal);
        });
        Assert.Equal(2, Directory.EnumerateFiles(report.MapsDirectory).Count());
    }

    [Fact]
    public void Extract_ReportsAnUnusableDirectory_WhenThatMapIsRequestedByItself()
    {
        (TerrainFixture fixture, ClientMapDirectory maps) = Client();
        using var output = new TestDirectory();

        MapExtractReport report = new MapExtractor(fixture.Files, maps, Areas).Extract(output.Path, [8], overwrite: false, threads: 1, CancellationToken.None, Path.Combine(output.Path, MapExtractor.MapsFolder));

        Assert.Equal(8u, Assert.Single(report.Failures).Map);
        Assert.Empty(Directory.EnumerateFiles(report.MapsDirectory));
    }
}

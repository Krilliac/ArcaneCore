using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// <see cref="VmapExtractor"/> on a Map.dbc with unusable directories, and on a folder where a tree file belongs: each is that
/// map's problem, and the run goes on with the next map.
/// </summary>
public sealed class VmapMapDirectoryTests
{
    /// <summary>The fixture's files with a real Map.dbc table: Azeroth, then four rows with unusable directories.</summary>
    private static ClientMapDirectory Client(TerrainFixture fixture)
    {
        var maps = new WdbcTestBuilder(ClientMapDirectory.MapFieldCount);
        ClientMapDirectoryTests.AddMap(maps, 0, "Azeroth", 0, "Eastern Kingdoms");
        ClientMapDirectoryTests.AddMap(maps, 7, string.Empty, 0, "Empty");
        ClientMapDirectoryTests.AddMap(maps, 8, "Dungeons\\Deadmines", 0, "Backslash");
        ClientMapDirectoryTests.AddMap(maps, 9, "Dungeons/Deadmines", 0, "Slash");
        ClientMapDirectoryTests.AddMap(maps, 10, "C:Evil", 0, "Colon");
        fixture.Files.Add(ClientMapDirectory.MapTablePath, maps.Build());
        fixture.Files.Add(ClientMapDirectory.AreaTablePath, ExtractFixtures.AreaTable((TerrainFixture.Area, 0x0042)));
        return ClientMapDirectory.Load(fixture.Files);
    }

    [Fact]
    public void Extract_ReportsEachUnusableDirectoryAsThatMapsProblem_AndGoesOn()
    {
        var fixture = new TerrainFixture();
        ClientMapDirectory maps = Client(fixture);
        using var output = new TestDirectory();

        VmapExtractReport report = new VmapExtractor(fixture.Files, maps).Extract(output.Path, null, threads: 1, CancellationToken.None);

        Assert.Equal(new uint[] { 0, 7, 8, 9, 10 }, report.Maps.Select(m => m.Id));
        Assert.True(report.Maps[0].HasWdt);
        Assert.Equal(0, report.Maps[0].Failed);
        foreach (VmapExtractMapReport bad in report.Maps.Skip(1))
        {
            Assert.False(bad.HasWdt);
            Assert.Equal(1, bad.Failed);
        }

        Assert.Equal(4, report.Problems.Count(p => p.Contains("not a single folder name", StringComparison.Ordinal)));
    }

    [Fact]
    public void Extract_SkipsAFolderWhereATreeFileBelongs_AndReportsIt()
    {
        var fixture = new TerrainFixture();
        ClientMapDirectory maps = Client(fixture);
        using var output = new TestDirectory();
        string folder = Path.Combine(output.Path, "vmaps", "000.vmtree");
        Directory.CreateDirectory(folder);
        string inside = Path.Combine(folder, "keep.txt");
        File.WriteAllBytes(inside, [1]);

        // Azeroth has no placements, so the extractor wants to delete an earlier run's tree file, which is a folder here.
        VmapExtractReport report = new VmapExtractor(fixture.Files, maps).Extract(output.Path, null, threads: 1, CancellationToken.None);

        Assert.Equal(new uint[] { 0, 7, 8, 9, 10 }, report.Maps.Select(m => m.Id));
        Assert.Contains(report.Problems, p => p.Contains("'000.vmtree'", StringComparison.Ordinal) && p.Contains("is a folder", StringComparison.Ordinal));
        Assert.True(File.Exists(inside));
    }
}

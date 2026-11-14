using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// A liquid height that is not a number must not reach a <c>.map</c> file: the server's reader returns it as a water level
/// and the navigation mesh builder takes it as geometry. Only the corners of a shown square are used, so only those count.
/// </summary>
public sealed class MapLiquidFiniteTests
{
    private const int Chunk = (6 * 16) + 7;

    private static readonly AreaExploreFlags Areas = ExtractFixtures.Areas((1, 0));

    /// <summary>One river layer whose only shown square is (2, 2); vertex (<paramref name="vy"/>, <paramref name="vx"/>) holds <paramref name="value"/>.</summary>
    private static AdtSpec OneSquareRiver(int vy, int vx, float value)
    {
        var spec = new AdtSpec();
        spec.Liquid[Chunk] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, 150, 152, (y, x) => y == vy && x == vx ? value : 150f + y, (y, x) => y == 2 && x == 2 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
        ];
        return spec;
    }

    [Theory]
    [InlineData(2, 2, float.NaN)]
    [InlineData(3, 3, float.NaN)]
    [InlineData(2, 3, float.PositiveInfinity)]
    [InlineData(3, 2, float.NegativeInfinity)]
    public void Encode_RefusesANonFiniteHeightOnAShownSquare_NamingTheChunk(int vy, int vx, float value)
    {
        AdtFile adt = ExtractFixtures.Adt(OneSquareRiver(vy, vx, value));

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => MapTileEncoder.Encode(adt, Areas));

        Assert.Contains("liquid", e.Message, StringComparison.Ordinal);
        Assert.Contains("(6, 7)", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, float.NaN)]
    [InlineData(8, 8, float.PositiveInfinity)]
    [InlineData(5, 5, float.NaN)]
    public void Encode_IgnoresANonFiniteHeightThatNoShownSquareTouches(int vy, int vx, float value)
    {
        byte[] withValue = MapTileEncoder.Encode(ExtractFixtures.Adt(OneSquareRiver(vy, vx, value)), Areas);
        byte[] without = MapTileEncoder.Encode(ExtractFixtures.Adt(OneSquareRiver(vy, vx, 150f + vy)), Areas);

        Assert.Equal(without, withValue);
    }

    [Fact]
    public void Extract_ReportsATileWithANonFiniteLiquidHeight_AndWritesNothingForIt_ButTheOthers()
    {
        var fixture = new TerrainFixture();
        AdtSpec bad = OneSquareRiver(2, 2, float.NaN);
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.ListedButAbsent), SyntheticTerrain.Adt(bad).Bytes);
        using var output = new TestDirectory();

        MapExtractReport report = new MapExtractor(fixture.Files, fixture.Maps, Areas).Extract(output.Path, [0], overwrite: false, threads: 2, CancellationToken.None);

        MapExtractMapReport azeroth = Assert.Single(report.Maps);
        Assert.Equal(2, azeroth.Written);
        Assert.Equal(1, azeroth.Failed);
        MapExtractFailure failure = Assert.Single(report.Failures);
        Assert.Equal((TerrainFixture.ListedButAbsent.X, TerrainFixture.ListedButAbsent.Y), (failure.TileX, failure.TileY));
        Assert.Contains("liquid", failure.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(report.MapsDirectory, TerrainTileName(TerrainFixture.ListedButAbsent))));
        Assert.DoesNotContain(Directory.EnumerateFiles(report.MapsDirectory), f => f.EndsWith(".part", StringComparison.Ordinal));
    }

    private static string TerrainTileName(TileKey tile) => ArcaneCore.Game.Maps.Terrain.TerrainTile.FileName(tile.MapId, tile.X, tile.Y);
}

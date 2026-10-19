using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class AdtGeometryTests
{
    private static readonly TileKey Tile = new(0, 33, 31);

    /// <summary>The world position of fraction (fx, fy) inside tile-wide square (row, column).</summary>
    private static (double X, double Y) At(TileKey tile, int row, int column, double fx, double fy)
    {
        WorldPoint origin = WorldGrid.Origin(tile);
        return (origin.X - ((row + fx) * WorldGrid.SquareSize), origin.Y - ((column + fy) * WorldGrid.SquareSize));
    }

    private static AdtFile Wavy(int seed = 7)
    {
        // Heights vary from sample to sample but agree where chunks share an edge, so the surface is continuous.
        float Corner(int gy, int gx) => (float)(50 + (20 * Math.Sin((gy * 0.37) + seed)) + (15 * Math.Cos((gx * 0.23) - seed)));
        float Centre(int gy, int gx) => Corner(gy, gx) + 1.5f + ((gy + gx) % 3);
        var spec = new AdtSpec
        {
            Tile = Tile,
            Outer = (row, col, y, x) => Corner((row * 8) + y, (col * 8) + x),
            Inner = (row, col, y, x) => Centre((row * 8) + y, (col * 8) + x),
        };
        return AdtFile.Parse(SyntheticTerrain.Adt(spec).Bytes);
    }

    [Fact]
    public void Vertices_SitOnTheTileGridFromTheNorthEastCorner()
    {
        AdtFile adt = Wavy();
        WorldPoint origin = WorldGrid.Origin(Tile);
        double sq = WorldGrid.TileSize / 128;
        AdtChunk chunk = adt.Chunk(2, 3);

        WorldPoint outer = AdtGeometry.OuterVertex(Tile, chunk, 4, 5);
        WorldPoint inner = AdtGeometry.InnerVertex(Tile, chunk, 4, 5);

        Assert.Equal(origin.X - (((2 * 8) + 4) * sq), outer.X, 1e-9);
        Assert.Equal(origin.Y - (((3 * 8) + 5) * sq), outer.Y, 1e-9);
        Assert.Equal(chunk.OuterHeight(4, 5), outer.Z);
        Assert.Equal(origin.X - (((2 * 8) + 4.5) * sq), inner.X, 1e-9);
        Assert.Equal(origin.Y - (((3 * 8) + 5.5) * sq), inner.Y, 1e-9);
        Assert.Equal(chunk.InnerHeight(4, 5), inner.Z);
    }

    [Fact]
    public void Vertices_StayInsideTheirTile()
    {
        AdtFile adt = Wavy();
        (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(Tile);

        foreach (AdtChunk chunk in adt.Chunks)
        {
            foreach ((int y, int x) in new[] { (0, 0), (8, 8), (0, 8), (8, 0) })
            {
                WorldPoint p = AdtGeometry.OuterVertex(Tile, chunk, y, x);
                Assert.InRange(p.X, minX - 1e-6, maxX + 1e-6);
                Assert.InRange(p.Y, minY - 1e-6, maxY + 1e-6);
            }
        }
    }

    [Fact]
    public void SampleAt_ReturnsTheCentreSampleAtTheCentreOfEverySquare()
    {
        AdtFile adt = Wavy();

        for (int row = 0; row < 128; row += 5)
        {
            for (int column = 0; column < 128; column += 3)
            {
                (double x, double y) = At(Tile, row, column, 0.5, 0.5);
                TerrainSample? sample = AdtGeometry.SampleAt(adt, Tile, x, y);

                Assert.NotNull(sample);
                AdtChunk chunk = adt.Chunk(row / 8, column / 8);
                Assert.False(sample.Value.IsHole);
                Assert.Equal(chunk.InnerHeight(row % 8, column % 8), sample.Value.Z, 1e-3);
                Assert.Equal(row, sample.Value.SquareRow);
                Assert.Equal(column, sample.Value.SquareColumn);
                Assert.Equal(row / 8, sample.Value.ChunkRow);
                Assert.Equal(column / 8, sample.Value.ChunkColumn);
            }
        }
    }

    [Fact]
    public void SampleAt_ReturnsTheCornerSampleAtEveryCorner()
    {
        AdtFile adt = Wavy();

        for (int row = 0; row < 128; row += 9)
        {
            for (int column = 0; column < 128; column += 11)
            {
                // A hair inside the square, so the owning square is unambiguous; the plane is continuous so the error is tiny.
                (double x, double y) = At(Tile, row, column, 0.0001, 0.0001);
                TerrainSample? sample = AdtGeometry.SampleAt(adt, Tile, x, y);

                Assert.NotNull(sample);
                AdtChunk chunk = adt.Chunk(row / 8, column / 8);
                Assert.Equal(chunk.OuterHeight(row % 8, column % 8), sample.Value.Z, 0.05);
            }
        }
    }

    [Fact]
    public void SampleAt_MatchesAnIndependentBarycentricEvaluationInEveryTriangle()
    {
        AdtFile adt = Wavy(11);
        var random = new XorShift(0xC0FFEE1234567);
        var seen = new HashSet<int>();

        for (int round = 0; round < 4000; round++)
        {
            int row = random.Next(128);
            int column = random.Next(128);
            double fx = (random.Next(1_000_000) + 0.5) / 1_000_000;
            double fy = (random.Next(1_000_000) + 0.5) / 1_000_000;
            if (Math.Abs(fx - fy) < 1e-4 || Math.Abs(fx + fy - 1) < 1e-4)
            {
                continue;
            }

            AdtChunk chunk = adt.Chunk(row / 8, column / 8);
            int lr = row % 8;
            int lc = column % 8;
            double h1 = chunk.OuterHeight(lr, lc);
            double h2 = chunk.OuterHeight(lr + 1, lc);
            double h3 = chunk.OuterHeight(lr, lc + 1);
            double h4 = chunk.OuterHeight(lr + 1, lc + 1);
            double c = chunk.InnerHeight(lr, lc);

            // The fan: (1,2,C), (1,3,C), (2,4,C), (3,4,C) with corners at (0,0), (1,0), (0,1), (1,1) in (fx, fy).
            (double, double, double)[][] fan =
            [
                [(0, 0, h1), (1, 0, h2), (0.5, 0.5, c)],
                [(0, 0, h1), (0, 1, h3), (0.5, 0.5, c)],
                [(1, 0, h2), (1, 1, h4), (0.5, 0.5, c)],
                [(0, 1, h3), (1, 1, h4), (0.5, 0.5, c)],
            ];
            double expected = double.NaN;
            int expectedTriangle = 0;
            for (int t = 0; t < 4; t++)
            {
                if (Barycentric(fan[t], fx, fy) is { } z)
                {
                    expected = z;
                    expectedTriangle = t + 1;
                    break;
                }
            }

            (double x, double y) = At(Tile, row, column, fx, fy);
            TerrainSample? sample = AdtGeometry.SampleAt(adt, Tile, x, y);

            Assert.NotNull(sample);
            Assert.Equal(expectedTriangle, sample.Value.Triangle);
            Assert.Equal(expected, sample.Value.Z, 1e-6);
            seen.Add(expectedTriangle);
        }

        Assert.Equal(4, seen.Count);
    }

    private static double? Barycentric((double, double, double)[] triangle, double fx, double fy)
    {
        (double ax, double ay, double az) = triangle[0];
        (double bx, double by, double bz) = triangle[1];
        (double cx, double cy, double cz) = triangle[2];
        double det = ((by - cy) * (ax - cx)) + ((cx - bx) * (ay - cy));
        double l1 = (((by - cy) * (fx - cx)) + ((cx - bx) * (fy - cy))) / det;
        double l2 = (((cy - ay) * (fx - cx)) + ((ax - cx) * (fy - cy))) / det;
        double l3 = 1 - l1 - l2;
        return l1 >= 0 && l2 >= 0 && l3 >= 0 ? (l1 * az) + (l2 * bz) + (l3 * cz) : null;
    }

    [Fact]
    public void SampleAt_ReportsHolesWithoutAHeight()
    {
        var spec = new AdtSpec { Tile = Tile };
        spec.Holes[1 * 16 + 2] = 0x0010;
        AdtFile adt = AdtFile.Parse(SyntheticTerrain.Adt(spec).Bytes);

        // Bit 4 of chunk (1, 2) is hole row 1, hole column 0: squares (2..3, 0..1) of that chunk.
        (double x, double y) = At(Tile, (1 * 8) + 2, (2 * 8) + 1, 0.5, 0.5);
        TerrainSample? hole = AdtGeometry.SampleAt(adt, Tile, x, y);
        (double x2, double y2) = At(Tile, (1 * 8) + 4, (2 * 8) + 1, 0.5, 0.5);
        TerrainSample? solid = AdtGeometry.SampleAt(adt, Tile, x2, y2);

        Assert.NotNull(hole);
        Assert.True(hole.Value.IsHole);
        Assert.True(double.IsNaN(hole.Value.Z));
        Assert.Equal(0, hole.Value.Triangle);
        Assert.NotNull(solid);
        Assert.False(solid.Value.IsHole);
    }

    [Fact]
    public void SampleAt_UsesTheChunkThatOwnsTheSquare()
    {
        // Chunk (0, 0) ends at height 10 along its last row; chunk (1, 0) starts at 20 on the same line.
        var spec = new AdtSpec
        {
            Tile = Tile,
            BaseHeight = (row, _) => row == 0 ? 10f : 20f,
        };
        AdtFile adt = AdtFile.Parse(SyntheticTerrain.Adt(spec).Bytes);

        (double xa, double ya) = At(Tile, 7, 3, 0.999, 0.5);
        (double xb, double yb) = At(Tile, 8, 3, 0.001, 0.5);
        TerrainSample? north = AdtGeometry.SampleAt(adt, Tile, xa, ya);
        TerrainSample? south = AdtGeometry.SampleAt(adt, Tile, xb, yb);

        Assert.Equal(10, north!.Value.Z, 0.05);
        Assert.Equal(0, north.Value.ChunkRow);
        Assert.Equal(20, south!.Value.Z, 0.05);
        Assert.Equal(1, south.Value.ChunkRow);
    }

    [Fact]
    public void SampleAt_IsNullOutsideTheTileAndForBadInput()
    {
        AdtFile adt = Wavy();
        (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(Tile);

        Assert.Null(AdtGeometry.SampleAt(adt, Tile, maxX + 1, (minY + maxY) / 2));
        Assert.Null(AdtGeometry.SampleAt(adt, Tile, minX - 1, (minY + maxY) / 2));
        Assert.Null(AdtGeometry.SampleAt(adt, Tile, (minX + maxX) / 2, maxY + 1));
        Assert.Null(AdtGeometry.SampleAt(adt, Tile, (minX + maxX) / 2, minY - 1));
        Assert.Null(AdtGeometry.SampleAt(adt, Tile, double.NaN, 0));
        Assert.Null(AdtGeometry.SampleAt(adt, Tile, 0, double.PositiveInfinity));
        Assert.Null(AdtGeometry.SampleAt(adt, TileKey.Global(0), 0, 0));
        Assert.NotNull(AdtGeometry.SampleAt(adt, Tile, (minX + maxX) / 2, (minY + maxY) / 2));
    }

    [Fact]
    public void LiquidAt_FollowsTheTwoTrianglesTheLayerDraws()
    {
        var spec = new AdtSpec { Tile = Tile };
        spec.Liquid[0] =
        [
            new LiquidLayerSpec(AdtLiquidKind.Ocean, 0, 20, (y, x) => (y, x) switch { (0, 0) => 0f, (1, 0) => 4f, (0, 1) => 8f, (1, 1) => 20f, _ => 1f }, (y, x) => y == 0 && x == 0 ? (byte)0x80 : (y == 5 ? AdtLiquidLayer.HiddenSquare : (byte)0)),
        ];
        AdtFile adt = AdtFile.Parse(SyntheticTerrain.Adt(spec).Bytes);

        (double x1, double y1) = At(Tile, 0, 0, 0.25, 0.25);
        (double x2, double y2) = At(Tile, 0, 0, 0.75, 0.75);
        (double x3, double y3) = At(Tile, 5, 2, 0.5, 0.5);
        (double x4, double y4) = At(Tile, 20, 20, 0.5, 0.5);

        LiquidSample low = Assert.Single(AdtGeometry.LiquidAt(adt, Tile, x1, y1)!);
        LiquidSample high = Assert.Single(AdtGeometry.LiquidAt(adt, Tile, x2, y2)!);

        // fx + fy <= 1: h00 + fx * (h10 - h00) + fy * (h01 - h00) = 0 + 1 + 2.
        Assert.Equal(3.0, low.Level, 1e-6);

        // fx + fy > 1: h11 + (1 - fy) * (h10 - h11) + (1 - fx) * (h01 - h11) = 20 - 4 - 3.
        Assert.Equal(13.0, high.Level, 1e-6);
        Assert.Equal(AdtLiquidKind.Ocean, low.Kind);
        Assert.True(low.IsDarkWater);
        Assert.Equal((byte)0x80, low.SquareFlags);
        Assert.Equal(0f, low.HeightRowColumn);
        Assert.Equal(4f, low.HeightNextRow);
        Assert.Equal(8f, low.HeightNextColumn);
        Assert.Equal(20f, low.HeightNextBoth);
        Assert.Empty(AdtGeometry.LiquidAt(adt, Tile, x3, y3)!);
        Assert.Empty(AdtGeometry.LiquidAt(adt, Tile, x4, y4)!);
        Assert.Null(AdtGeometry.LiquidAt(adt, Tile, WorldGrid.Bounds(Tile).MaxX + 5, y1));
    }
}

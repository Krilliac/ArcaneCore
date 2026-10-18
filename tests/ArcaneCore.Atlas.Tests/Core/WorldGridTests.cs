using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

public sealed class WorldGridTests
{
    [Fact]
    public void GoldshireInn_NamesTheSameFilesAsTheServer()
    {
        // Inside the Lion's Pride Inn (tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:57).
        TileKey tile = WorldGrid.TileOf(0, -9462.66, 16.1915)!.Value;

        Assert.Equal(new TileKey(0, 49, 31), tile);
        Assert.Equal("0004931.map", WorldGrid.MapFileName(tile));
        Assert.Equal("0004931.mmtile", WorldGrid.NavTileFileName(tile));
        Assert.Equal("000_31_49.vmtile", WorldGrid.VMapTileFileName(tile));
    }

    [Fact]
    public void TileOf_AgreesWithTheServer_AndTheTileBoundsHoldThePoint()
    {
        int checkedPoints = 0;
        for (double x = -17000; x <= 17000; x += 731.7)
        {
            for (double y = -17000; y <= 17000; y += 677.3)
            {
                (int X, int Y)? server = TerrainTile.TileOf((float)x, (float)y);
                TileKey? atlas = WorldGrid.TileOf(1, x, y);
                Assert.Equal(server.HasValue, atlas.HasValue);
                if (atlas is not { } tile)
                {
                    continue;
                }

                Assert.Equal(server!.Value, (tile.X, tile.Y));
                (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(tile);
                Assert.InRange(x, minX, maxX);
                Assert.InRange(y, minY, maxY);
                (double cx, double cy) = WorldGrid.Centre(tile);
                Assert.Equal(tile, WorldGrid.TileOf(1, cx, cy));
                checkedPoints++;
            }
        }

        // The sweep must actually have compared tiles, not skipped every point.
        Assert.True(checkedPoints > 2000, $"only {checkedPoints} points compared");
    }

    [Fact]
    public void Origin_IsTheMaxXMaxYCorner()
    {
        WorldPoint origin = WorldGrid.Origin(new TileKey(0, 32, 32));

        Assert.Equal(0, origin.X, 6);
        Assert.Equal(0, origin.Y, 6);
        Assert.Equal(new TileKey(0, 32, 32), WorldGrid.TileOf(0, -1, -1));
        Assert.Equal(new TileKey(0, 31, 31), WorldGrid.TileOf(0, 1, 1));
    }

    [Fact]
    public void TileKeys_ValidateTheirRange()
    {
        Assert.True(new TileKey(0, 0, 0).IsValid);
        Assert.True(new TileKey(0, 63, 63).IsValid);
        Assert.False(new TileKey(0, 64, 0).IsValid);
        Assert.False(new TileKey(0, -1, 3).IsValid);
        Assert.True(TileKey.Global(489).IsValid);
        Assert.Equal("489/global", TileKey.Global(489).ToString());
        Assert.Equal("0/49/31", new TileKey(0, 49, 31).ToString());
    }

    [Fact]
    public void Around_ClipsAtTheMapEdge()
    {
        Assert.Equal(9, WorldGrid.Around(0, -9462.66, 16.1915, 1).Count());

        // Tile (0, 0) is the +X/+Y corner of the map: only four of its 3x3 neighbourhood exist.
        (double cx, double cy) = WorldGrid.Centre(new TileKey(0, 0, 0));
        Assert.Equal(4, WorldGrid.Around(0, cx, cy, 1).Count());
    }
}

using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The release calls the zone capacity survey uses on <see cref="VMapManager"/> (<see cref="VMapManager.HoldsTile"/>,
/// <see cref="VMapManager.ReleaseOnDemandTile"/>, <see cref="VMapManager.TrimUnusedModels"/>), on synthetic tiles: models "wall" on tile 0,
/// "box" on tile 1 and "ghost" (whose model file is missing, so its cache entry is null) on tile 2 of the same row, on maps 0 and 1.
/// </summary>
public sealed class VMapModelTrimTests : IDisposable
{
    private const int TileRow = 30;

    private readonly VMapFixture _models = new();

    public VMapModelTrimTests()
    {
        _models.Place("wall", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), On(0));
        _models.Place("box", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), On(1));
        _models.Place("ghost", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), On(2));
        _models.Write(0);
        _models.Write(1);
        File.Delete(_models.PathOf(VMapFormat.ModelFileName("ghost"))); // a model the manager cannot read
    }

    public void Dispose() => _models.Dispose();

    private static Vector3 On(int tileX) => new((32f - tileX - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0f);

    private static bool Demand(VMapManager vmaps, uint map, int tileX) => vmaps.LoadTileOnDemand(map, On(tileX).X, On(tileX).Y);

    private static bool Holds(VMapManager vmaps, uint map, int tileX) => vmaps.HoldsTile(map, On(tileX).X, On(tileX).Y);

    private static bool Release(VMapManager vmaps, uint map, int tileX) => vmaps.ReleaseOnDemandTile(map, On(tileX).X, On(tileX).Y);

    private static bool WallBlocks(VMapManager vmaps, uint map)
        => !vmaps.IsInLineOfSight(map, On(0) - new Vector3(5, 0, -2), On(0) + new Vector3(5, 0, 2));

    [Fact]
    public void ReleaseOnDemandTile_NeverReleasesATileAGridHolds()
    {
        var vmaps = new VMapManager(_models.Directory);
        ICollisionTileLifecycle grids = vmaps;
        VMapTree tree = vmaps.GetTree(0)!;
        Assert.True(Demand(vmaps, 0, 0));
        grids.OnTileLoaded(0, 0, TileRow); // a grid takes the tile over

        Assert.False(Release(vmaps, 0, 0));
        Assert.True(tree.IsTileLoaded(0, TileRow));
        Assert.True(WallBlocks(vmaps, 0));
        Assert.False(Release(vmaps, 0, 1)); // a tile never loaded: nothing to release
        Assert.False(tree.IsTileLoaded(1, TileRow));
    }

    [Fact]
    public void ATileAnotherCallerLoadedOnDemand_IsHeld_SoTheSurveysGuardLeavesItAlone()
    {
        var vmaps = new VMapManager(_models.Directory);
        VMapTree tree = vmaps.GetTree(0)!;
        Assert.True(Demand(vmaps, 0, 0)); // another caller's tile
        Assert.False(Holds(vmaps, 0, 1));

        // The survey's pattern: ask whether the tile is held, load it, and release it again only when it was not held.
        foreach (int tile in new[] { 0, 1 })
        {
            bool held = Holds(vmaps, 0, tile);
            Assert.True(Demand(vmaps, 0, tile));
            if (!held) Assert.True(Release(vmaps, 0, tile));
        }

        Assert.True(tree.IsTileLoaded(0, TileRow), "the other caller's tile was released");
        Assert.False(tree.IsTileLoaded(1, TileRow), "the survey's own tile stayed loaded");
        Assert.Equal(1, vmaps.OnDemandTileCount);
        Assert.False(Holds(vmaps, 0, 1));
    }

    [Fact]
    public void AnEmptyOnDemandTile_IsReleasedAndForgotten()
    {
        File.Delete(_models.PathOf(VMapFormat.TileFileName(0, 1, TileRow)));
        var vmaps = new VMapManager(_models.Directory);

        Assert.False(Demand(vmaps, 0, 1)); // no vmtile: loaded, and nothing in it
        Assert.True(Holds(vmaps, 0, 1));
        Assert.Equal(ModelCoverage.Loaded, vmaps.CoverageAt(0, 1, TileRow));

        Assert.True(Release(vmaps, 0, 1));
        Assert.False(Holds(vmaps, 0, 1));
        Assert.Equal(ModelCoverage.TileNotLoaded, vmaps.CoverageAt(0, 1, TileRow));
        Assert.Equal(0, vmaps.OnDemandTileCount);
    }

    [Fact]
    public void Trim_KeepsEveryModelALoadedTileUsesOnAnyMap_AndLineOfSightIsUnchanged()
    {
        var vmaps = new VMapManager(_models.Directory);
        Assert.True(Demand(vmaps, 0, 0)); // wall, map 0
        Assert.True(Demand(vmaps, 1, 1)); // box, map 1
        Assert.True(Demand(vmaps, 0, 1)); // box, map 0 too
        Assert.True(Release(vmaps, 0, 1)); // map 0 lets go of the box; map 1 still uses it
        Assert.True(WallBlocks(vmaps, 0));

        Assert.Equal(2, vmaps.CachedModelCount);
        Assert.Equal(0, vmaps.TrimUnusedModels());
        Assert.Equal(2, vmaps.CachedModelCount);
        Assert.True(WallBlocks(vmaps, 0));
        Assert.True(vmaps.GetTree(1)!.IsTileLoaded(1, TileRow));
        Assert.False(vmaps.IsInLineOfSight(1, On(1) - new Vector3(5, 0, -2), On(1) + new Vector3(5, 0, 2)), "the box on map 1 still blocks");
    }

    [Fact]
    public void Trim_DropsTheModelOfAnUnloadedTile_AndALaterLoadParsesItAgain()
    {
        var vmaps = new VMapManager(_models.Directory);
        Assert.True(Demand(vmaps, 0, 0));
        Assert.True(Demand(vmaps, 0, 1));
        int parsed = vmaps.ModelFilesLoaded;
        Assert.Equal(2, vmaps.CachedModelCount);

        Assert.True(Release(vmaps, 0, 1));
        Assert.Equal(1, vmaps.TrimUnusedModels()); // the box
        Assert.Equal(1, vmaps.CachedModelCount);
        Assert.True(WallBlocks(vmaps, 0));

        Assert.True(Demand(vmaps, 0, 1));
        Assert.Equal(parsed + 1, vmaps.ModelFilesLoaded); // the box is read and parsed again
        Assert.Equal(2, vmaps.CachedModelCount);
    }

    [Fact]
    public void Trim_LeavesTheNullEntryOfAModelThatCouldNotBeRead()
    {
        var vmaps = new VMapManager(_models.Directory);
        Assert.True(Demand(vmaps, 0, 2)); // the ghost: its file is missing, the entry is null and the tile loads without it
        int cached = vmaps.CachedModelCount;
        Assert.Equal(1, cached);
        Assert.True(Release(vmaps, 0, 2));

        Assert.Equal(0, vmaps.TrimUnusedModels()); // a null entry is not a model held in memory: it stays, and is not read again
        Assert.Equal(cached, vmaps.CachedModelCount);
        int parsed = vmaps.ModelFilesLoaded;
        Assert.True(Demand(vmaps, 0, 2));
        Assert.Equal(parsed, vmaps.ModelFilesLoaded);
    }
}

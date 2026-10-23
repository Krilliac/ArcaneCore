using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// One box of real vmap geometry (<see cref="VMapFixture"/> files read back by a <see cref="VMapManager"/>, its tile loaded) installed as
/// a world's line of sight: a wall whose top sits at a chosen height, for checks that depend on how high each end of a sight line is.
/// </summary>
internal sealed class VMapWall : IDisposable
{
    private readonly VMapFixture _files;

    private VMapWall(VMapFixture files, VMapManager manager)
    {
        _files = files;
        Manager = manager;
    }

    public VMapManager Manager { get; }

    /// <summary>
    /// A wall across the X axis at <paramref name="x"/>: 0.2 yd thick, 40 yd long on Y around <paramref name="y"/>, from
    /// <paramref name="floorZ"/> up <paramref name="height"/> yards.
    /// </summary>
    public static VMapWall AcrossX(WorldRuntime world, float x, float y, float floorZ, float height, uint mapId = 0)
        => Box(world, new Vector3(x - 0.1f, y - 20f, floorZ), new Vector3(x + 0.1f, y + 20f, floorZ + height), mapId);

    /// <summary>A box from <paramref name="low"/> to <paramref name="high"/> (world coordinates) on <paramref name="mapId"/>.</summary>
    public static VMapWall Box(WorldRuntime world, Vector3 low, Vector3 high, uint mapId = 0)
    {
        Vector3 centre = (low + high) / 2;
        var files = new VMapFixture();
        files.Place("wall", VMapFixture.Box(low - centre, high - centre), centre);
        files.Write(mapId);
        var manager = new VMapManager(files.Directory);
        (int tileX, int tileY) = TerrainTile.TileOf(centre.X, centre.Y)!.Value;
        Assert.True(manager.LoadTile(mapId, tileX, tileY));
        WorldCollision.Of(world).Install(manager);
        return new VMapWall(files, manager);
    }

    public void Dispose() => _files.Dispose();
}

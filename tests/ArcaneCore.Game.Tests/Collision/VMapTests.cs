using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>The vmap reader on synthetic data: rays, transforms, heights, area info and adverse files.</summary>
public sealed class VMapTests
{
    private const uint MapId = 0;

    // World (100, 100) lies in terrain tile (31, 31).
    private static readonly Vector3 WallOrigin = new(100, 100, 0);

    /// <summary>Model-space wall: 2 yd thick on X, 20 yd long on Y, 10 yd high.</summary>
    private static WorldModel Wall() => VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10));

    private static VMapManager Load(VMapFixture fixture, int tileX = 31, int tileY = 31)
    {
        var manager = new VMapManager(fixture.Directory);
        Assert.True(manager.LoadTile(MapId, tileX, tileY));
        return manager;
    }

    [Fact]
    public void Ray_HitsTheWall_AndMissesBesideAndAboveIt()
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", Wall(), WallOrigin);
        fixture.Write(MapId);
        VMapManager vmaps = Load(fixture);

        Assert.True(vmaps.Enabled);
        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(105, 95, 5), new Vector3(95, 105, 5)));
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 120, 2), new Vector3(105, 120, 2)));
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 12), new Vector3(105, 100, 12)));
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(98, 100, 2)), "segment stops short of the wall");
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(95, 100, 2)), "zero-length segment");
        Assert.True(vmaps.IsInLineOfSight(1, new Vector3(95, 100, 2), new Vector3(105, 100, 2)), "another map has no data");

        Assert.True(vmaps.TryGetObjectHit(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2), 0, out Vector3 hit));
        Assert.Equal(99f, hit.X, 3);
        Assert.Equal(100f, hit.Y, 3);
        Assert.False(vmaps.TryGetObjectHit(MapId, new Vector3(95, 120, 2), new Vector3(105, 120, 2), 0, out Vector3 miss));
        Assert.Equal(new Vector3(105, 120, 2), miss);
    }

    [Fact]
    public void Transform_RotationAndScale_MoveTheModel()
    {
        using var fixture = new VMapFixture();
        // rotation.Y = 90 turns the model about Z: its long Y axis now runs along X. Scale 2: 4 yd thick, 40 long, 20 high.
        fixture.Place("wall", Wall(), WallOrigin, rotation: new Vector3(0, 90, 0), scale: 2);
        fixture.Write(MapId);
        VMapManager vmaps = Load(fixture);

        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(100, 90, 5), new Vector3(100, 110, 5)));
        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(115, 90, 15), new Vector3(115, 110, 15)), "inside the scaled length and height");
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(125, 90, 5), new Vector3(125, 110, 5)), "past the scaled length");
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(90, 100, 25), new Vector3(110, 100, 25)), "above the scaled height");
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(90, 95, 5), new Vector3(110, 95, 5)), "parallel to the rotated wall, beside it");

        Assert.True(vmaps.TryGetObjectHit(MapId, new Vector3(100, 90, 5), new Vector3(100, 110, 5), -1, out Vector3 hit));
        Assert.Equal(97f, hit.Y, 3); // face at y = 98, pulled back 1 yd
        Assert.True(vmaps.TryGetObjectHit(MapId, new Vector3(100, 97.5f, 5), new Vector3(100, 110, 5), -5, out Vector3 clamped));
        Assert.Equal(new Vector3(100, 97.5f, 5), clamped); // pulling back past the start stops at the start
    }

    [Fact]
    public void Instance_TransformsRoundTrip()
    {
        var spawn = new ModelSpawn(0, 0, 1, new Vector3(10, 20, 30), new Vector3(15, 40, 70), 1.5f, default, default, "m");
        var instance = new ModelInstance(spawn, Wall());
        var p = new Vector3(3, -4, 5);
        Vector3 back = instance.ToModel(instance.FromModel(p));
        Assert.Equal(p.X, back.X, 4);
        Assert.Equal(p.Y, back.Y, 4);
        Assert.Equal(p.Z, back.Z, 4);
    }

    [Fact]
    public void Doodads_DoNotBreakLineOfSight_ButStillHaveHeight()
    {
        using var fixture = new VMapFixture();
        fixture.Place("tree", Wall(), WallOrigin, flags: VMapFormat.ModM2);
        fixture.Write(MapId);
        VMapManager vmaps = Load(fixture);

        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2), ignoreM2: false));
        Assert.Equal(10f, vmaps.GetModelHeight(MapId, 100, 100, 20, 50)!.Value, 3);
        Assert.False(vmaps.TryGetAreaInfo(MapId, 100, 100, 5, out _), "doodads have no area info");
    }

    [Fact]
    public void ModelHeight_FindsTheFloorBelowOrTheCeilingAbove()
    {
        using var fixture = new VMapFixture();
        fixture.Place("slab", VMapFixture.Box(new Vector3(-5, -5, -0.5f), new Vector3(5, 5, 0.5f)), new Vector3(100, 100, 10));
        fixture.Write(MapId);
        VMapManager vmaps = Load(fixture);

        Assert.Equal(10.5f, vmaps.GetModelHeight(MapId, 100, 100, 20, 50)!.Value, 3);
        Assert.Null(vmaps.GetModelHeight(MapId, 100, 100, 20, 5)); // search too short
        Assert.Null(vmaps.GetModelHeight(MapId, 100, 100, 5, 50)); // nothing below
        Assert.Equal(9.5f, vmaps.GetModelHeight(MapId, 100, 100, 5, -50)!.Value, 3); // upward search
        Assert.Null(vmaps.GetModelHeight(MapId, 120, 100, 20, 50)); // beside the slab
    }

    [Fact]
    public void MapHeight_CombinesTerrainAndModelFloors()
    {
        using var fixture = new VMapFixture();
        fixture.Place("slab", VMapFixture.Box(new Vector3(-5, -5, -0.5f), new Vector3(5, 5, 0.5f)), new Vector3(100, 100, 10));
        fixture.Write(MapId);
        string terrain = Path.Combine(fixture.Directory, "terrain");
        Directory.CreateDirectory(Path.Combine(terrain, "maps"));
        File.WriteAllBytes(Path.Combine(terrain, "maps", TerrainTile.FileName(MapId, 31, 31)), new MapFileBuilder { GridHeight = 0f }.Build());

        using var world = new WorldRuntime(
            new WorldRuntimeOptions { UpdateCompressionThreshold = 0, Maps = new MapOptions { DataDirectory = terrain } },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        var vmaps = new VMapManager(fixture.Directory);
        WorldCollision.Of(world).Install(vmaps);
        Map map = world.GetMap(MapId);
        vmaps.LoadTile(MapId, 31, 31);

        Assert.Equal(10.5f, map.Collision.GetHeight(100, 100, 10.6f), 3); // standing on the slab
        Assert.Equal(0f, map.Collision.GetHeight(100, 100, 3), 3);       // under the slab: the ground
        Assert.Equal(0f, map.Collision.GetHeight(120, 100, 10.6f), 3);   // beside the slab
        Assert.Equal(0f, map.Collision.GetHeight(100, 100, 10.6f, useModels: false), 3);
        Assert.Equal(10.5f, map.Collision.GetHeight(100, 100, 200), 3);  // high above: search reaches down to the terrain
    }

    [Fact]
    public void AreaInfo_FindsTheRoomAPointIsIn()
    {
        using var fixture = new VMapFixture();
        fixture.Place("room", VMapFixture.Box(new Vector3(-5, -5, 0), new Vector3(5, 5, 4), mogpFlags: ModelAreaInfo.MogpInterior, rootId: 42, groupId: 9), new Vector3(100, 100, 10));
        fixture.Write(MapId);
        VMapManager vmaps = Load(fixture);

        Assert.True(vmaps.TryGetAreaInfo(MapId, 100, 100, 12, out ModelAreaInfo info));
        Assert.Equal(ModelAreaInfo.MogpInterior, info.MogpFlags);
        Assert.Equal(42, info.RootId);
        Assert.Equal(9, info.GroupId);
        Assert.Equal(10f, info.GroundZ, 3);
        Assert.False(info.IsOutdoors);
        Assert.False(vmaps.TryGetAreaInfo(MapId, 120, 100, 12, out _));
        Assert.False(vmaps.TryGetAreaInfo(MapId, 100, 100, 20, out _)); // above the roof: outside the group bound
    }

    [Fact]
    public void IsOutdoors_UsesTheGroupFlags()
    {
        using var fixture = new VMapFixture();
        fixture.Place("room", VMapFixture.Box(new Vector3(-5, -5, 0), new Vector3(5, 5, 4), mogpFlags: ModelAreaInfo.MogpInterior), new Vector3(100, 100, 10));
        fixture.Place("porch", VMapFixture.Box(new Vector3(-5, -5, 0), new Vector3(5, 5, 4), mogpFlags: ModelAreaInfo.MogpExterior), new Vector3(140, 100, 10));
        fixture.Write(MapId);
        using var world = TestWorld.CreateRuntime();
        VMapManager vmaps = Load(fixture);
        WorldCollision.Of(world).Install(vmaps);
        Map map = world.GetMap(MapId);

        Assert.False(map.Collision.IsOutdoors(100, 100, 12));
        Assert.True(map.Collision.IsOutdoors(140, 100, 12));
        Assert.True(map.Collision.IsOutdoors(180, 100, 12));
    }

    [Fact]
    public void Tiles_LoadAndUnloadWithTheGrids()
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", Wall(), WallOrigin);
        fixture.Write(MapId);
        using var world = TestWorld.CreateRuntime();
        var vmaps = new VMapManager(fixture.Directory);
        WorldCollision.Of(world).Install(vmaps);
        Map map = world.GetMap(MapId);
        var from = new Vector3(95, 100, 2);
        var to = new Vector3(105, 100, 2);

        Assert.True(map.Collision.IsInLineOfSight(from.X, from.Y, from.Z, to.X, to.Y, to.Z), "no grid yet: no tile, open");

        // A grid created over the wall loads its tile; LOS is now blocked.
        var unit = new TestUnit(1, 100, 100);
        map.AddObject(unit);
        Assert.True(vmaps.GetTree(MapId)!.IsTileLoaded(31, 31));
        Assert.False(map.Collision.IsInLineOfSight(from.X, from.Y, from.Z, to.X, to.Y, to.Z));

        vmaps.UnloadTile(MapId, 31, 31);
        Assert.Equal(0, vmaps.GetTree(MapId)!.LoadedInstanceCount);
        Assert.True(map.Collision.IsInLineOfSight(from.X, from.Y, from.Z, to.X, to.Y, to.Z));
        vmaps.UnloadTile(MapId, 31, 31); // twice is harmless
    }

    [Fact]
    public void SpawnListedByTwoTiles_StaysUntilBothAreUnloaded()
    {
        using var fixture = new VMapFixture();
        ModelSpawn spawn = fixture.Place("wall", Wall(), WallOrigin);
        fixture.Write(MapId);
        fixture.WriteTile(MapId, 31, 32, [(spawn, 0u)]);
        VMapManager vmaps = Load(fixture);
        Assert.True(vmaps.LoadTile(MapId, 31, 32));
        Assert.Equal(1, vmaps.ModelFilesLoaded);

        vmaps.UnloadTile(MapId, 31, 31);
        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
        vmaps.UnloadTile(MapId, 31, 32);
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
    }

    [Fact]
    public void UntiledMap_LoadsItsGlobalModelsFromTheTree()
    {
        using var fixture = new VMapFixture();
        fixture.Place("dungeon", Wall(), WallOrigin, flags: VMapFormat.ModWorldSpawn);
        fixture.Write(MapId, tiled: false);
        var vmaps = new VMapManager(fixture.Directory);

        Assert.True(vmaps.LoadTile(MapId, 0, 0)); // any tile: the global models are already in
        Assert.False(vmaps.GetTree(MapId)!.IsTiled);
        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
    }

    [Fact]
    public void MultiGroupModel_IsSearchedThroughItsGroupTree()
    {
        WorldModel left = VMapFixture.Box(new Vector3(-10, -1, 0), new Vector3(-8, 1, 5));
        WorldModel right = VMapFixture.Box(new Vector3(8, -1, 0), new Vector3(10, 1, 5));
        var model = new WorldModel(5, [left.Groups[0], right.Groups[0]]);
        WorldModel copy = WorldModel.Parse(model.ToBytes());
        Assert.Equal(2, copy.Groups.Count);

        float d = 100;
        Assert.True(copy.IntersectRay(new Vector3(0, 0, 2), Vector3.UnitX, ref d, stopAtFirstHit: false));
        Assert.Equal(8f, d, 3);
        d = 100;
        Assert.True(copy.IntersectRay(new Vector3(0, 0, 2), -Vector3.UnitX, ref d, stopAtFirstHit: false));
        Assert.Equal(8f, d, 3);
        d = 100;
        Assert.False(copy.IntersectRay(new Vector3(0, 0, 2), Vector3.UnitY, ref d, stopAtFirstHit: false));
    }

    [Fact]
    public void ModelFile_RoundTrips_WithLiquidAndEmptyGroups()
    {
        var liquid = new WmoLiquid(1, 1, new Vector3(1, 2, 3), 4, [1, 2, 3, 4], [0]);
        WorldModel box = VMapFixture.Box(Vector3.Zero, Vector3.One);
        var withLiquid = new GroupModel(Vector3.Zero, Vector3.One, 0, 1, [.. box.Groups[0].Vertices], [.. box.Groups[0].Triangles], liquid: liquid);
        var empty = new GroupModel(Vector3.Zero, Vector3.One, 0, 2, [], []);
        WorldModel copy = WorldModel.Parse(new WorldModel(9, [withLiquid, empty]).ToBytes());

        Assert.Equal(9u, copy.RootWmoId);
        Assert.Equal(4u, copy.Groups[0].Liquid!.Type);
        Assert.Equal([1f, 2f, 3f, 4f], copy.Groups[0].Liquid!.Heights);
        Assert.Empty(copy.Groups[1].Triangles);
        Assert.Empty(WorldModel.Parse(new WorldModel(1, []).ToBytes()).Groups);
    }

    [Fact]
    public void ModelFile_WithVmangosLiquidChunkSize_Parses()
    {
        // vmangos GroupModel::writeToFile stores WmoLiquid::GetFileSize() as the LIQU chunk size,
        // which leaves out the u32 liquid type (4 bytes short); its reader ignores the size and
        // reads the liquid by its own grid. Every real .vmo with liquid has this layout.
        var liquid = new WmoLiquid(3, 4, new Vector3(1, 2, 3), 4, [.. Enumerable.Range(0, 20).Select(i => (float)i)], new byte[12]);
        WorldModel box = VMapFixture.Box(Vector3.Zero, Vector3.One);
        var withLiquid = new GroupModel(Vector3.Zero, Vector3.One, 0, 1, [.. box.Groups[0].Vertices], [.. box.Groups[0].Triangles], liquid: liquid);
        var after = new GroupModel(Vector3.Zero, Vector3.One, 0, 2, [.. box.Groups[0].Vertices], [.. box.Groups[0].Triangles]);
        byte[] bytes = new WorldModel(9, [withLiquid, after]).ToBytes();
        int liqu = bytes.AsSpan().IndexOf("LIQU"u8);
        const uint vmangosSize = (2 * 4) + 12 + (4 * 5 * 4) + (3 * 4); // GetFileSize(): no type field
        BitConverter.GetBytes(vmangosSize).CopyTo(bytes, liqu + 4);

        WorldModel copy = WorldModel.Parse(bytes);

        Assert.Equal(4u, copy.Groups[0].Liquid!.Type);
        Assert.Equal(19f, copy.Groups[0].Liquid!.Heights[^1]);
        Assert.Equal(2u, copy.Groups[1].GroupWmoId);
        Assert.Equal(box.Groups[0].Triangles.Count, copy.Groups[1].Triangles.Count);
    }

    [Fact]
    public void ModelWriter_StoresTheVmangosLiquidChunkSize()
    {
        var liquid = new WmoLiquid(3, 4, Vector3.Zero, 4, new float[20], new byte[12]);
        WorldModel box = VMapFixture.Box(Vector3.Zero, Vector3.One);
        var group = new GroupModel(Vector3.Zero, Vector3.One, 0, 1, [.. box.Groups[0].Vertices], [.. box.Groups[0].Triangles], liquid: liquid);
        byte[] bytes = new WorldModel(9, [group]).ToBytes();
        int liqu = bytes.AsSpan().IndexOf("LIQU"u8);

        Assert.Equal((2u * 4) + 12 + (4 * 5 * 4) + (3 * 4), BitConverter.ToUInt32(bytes, liqu + 4));
    }

    [Fact]
    public void MissingData_ReadsAsOpen()
    {
        using var fixture = new VMapFixture();
        var vmaps = new VMapManager(fixture.Directory);

        Assert.False(vmaps.LoadTile(MapId, 31, 31));
        Assert.Null(vmaps.GetTree(MapId));
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
        Assert.Null(vmaps.GetModelHeight(MapId, 100, 100, 20, 50));
        Assert.False(vmaps.TryGetAreaInfo(MapId, 100, 100, 20, out _));

        var nowhere = new VMapManager(Path.Combine(fixture.Directory, "does-not-exist"));
        Assert.False(nowhere.LoadTile(MapId, 31, 31));
    }

    [Fact]
    public void MissingTile_IsNormal_AndOtherTilesStillLoad()
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", Wall(), WallOrigin);
        fixture.Write(MapId);
        var vmaps = new VMapManager(fixture.Directory);

        Assert.False(vmaps.LoadTile(MapId, 10, 10));
        Assert.True(vmaps.LoadTile(MapId, 31, 31));
        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("truncated")]
    [InlineData("empty")]
    public void CorruptTree_ReadsAsOpen(string damage)
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", Wall(), WallOrigin);
        fixture.Write(MapId);
        string path = fixture.PathOf(VMapFormat.TreeFileName(MapId));
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, damage switch
        {
            "magic" => [.. "VMAP_4.0"u8, .. bytes[8..]],
            "truncated" => bytes[..20],
            _ => [],
        });
        var vmaps = new VMapManager(fixture.Directory);

        Assert.False(vmaps.LoadTile(MapId, 31, 31));
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
    }

    [Fact]
    public void CorruptTile_IsDroppedWhole()
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", Wall(), WallOrigin);
        fixture.Write(MapId);
        string path = fixture.PathOf(VMapFormat.TileFileName(MapId, 31, 31));
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^6]);
        var vmaps = new VMapManager(fixture.Directory);

        Assert.False(vmaps.LoadTile(MapId, 31, 31));
        Assert.Equal(0, vmaps.GetTree(MapId)!.LoadedInstanceCount);
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
    }

    [Fact]
    public void MissingOrCorruptModel_OrBadSlot_SkipsOnlyThatSpawn()
    {
        using var fixture = new VMapFixture();
        ModelSpawn good = fixture.Place("wall", Wall(), WallOrigin);
        ModelSpawn missing = fixture.Place("gone", Wall(), new Vector3(150, 100, 0));
        ModelSpawn corrupt = fixture.Place("broken", Wall(), new Vector3(200, 100, 0));
        fixture.Write(MapId);
        File.Delete(fixture.PathOf(VMapFormat.ModelFileName("gone")));
        File.WriteAllBytes(fixture.PathOf(VMapFormat.ModelFileName("broken")), [.. "VMAP_7.0WMOD"u8]);
        ModelSpawn escaping = good with { Name = "../wall" };
        fixture.WriteTile(MapId, 31, 31, [(good, 0u), (missing, 1u), (corrupt, 2u), (good, 99u), (escaping, 0u)]);
        var vmaps = new VMapManager(fixture.Directory);

        Assert.True(vmaps.LoadTile(MapId, 31, 31));
        Assert.Equal(1, vmaps.GetTree(MapId)!.LoadedInstanceCount);
        Assert.False(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(145, 100, 2), new Vector3(155, 100, 2)));
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(195, 100, 2), new Vector3(205, 100, 2)));
    }

    [Fact]
    public void Disabled_ReportsOpen()
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", Wall(), WallOrigin);
        fixture.Write(MapId);
        var vmaps = new VMapManager(fixture.Directory, enableLineOfSight: false, enableHeight: false);
        vmaps.LoadTile(MapId, 31, 31);

        Assert.False(vmaps.Enabled);
        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(95, 100, 2), new Vector3(105, 100, 2)));
        Assert.Null(vmaps.GetModelHeight(MapId, 100, 100, 20, 50));
    }

    [Fact]
    public void NonFinitePositions_ReadAsOpen()
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", Wall(), WallOrigin);
        fixture.Write(MapId);
        VMapManager vmaps = Load(fixture);

        Assert.True(vmaps.IsInLineOfSight(MapId, new Vector3(float.NaN, 100, 2), new Vector3(105, 100, 2)));
        Assert.Null(vmaps.GetModelHeight(MapId, float.PositiveInfinity, 100, 2, 50));
    }

    [Fact]
    public void ModelNames_MustBePlainFileNames()
    {
        Assert.True(VMapFormat.IsSafeModelName("Stormwind_000.wmo"));
        Assert.False(VMapFormat.IsSafeModelName("../etc"));
        Assert.False(VMapFormat.IsSafeModelName("a/b"));
        Assert.False(VMapFormat.IsSafeModelName("a\\b"));
        Assert.False(VMapFormat.IsSafeModelName(""));
    }

    [Fact]
    public void FileNames_FollowTheVmangosLayout()
    {
        Assert.Equal("000.vmtree", VMapFormat.TreeFileName(0));
        Assert.Equal("001_32_31.vmtile", VMapFormat.TileFileName(1, 31, 32));
        Assert.Equal(new Vector3(VMapFormat.MapMid - 1, VMapFormat.MapMid - 2, 3), VMapFormat.ToInternal(new Vector3(1, 2, 3)));
    }
}

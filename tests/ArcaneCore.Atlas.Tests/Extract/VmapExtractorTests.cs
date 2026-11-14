using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;
using FakeMapDirectory = ArcaneCore.Atlas.Tests.Reference.Client.Terrain.FakeMapDirectory;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary><see cref="VmapBih"/>: exact words for small inputs, and the server's traversal finding every primitive of large ones.</summary>
public sealed class VmapBihTests
{
    private const uint Leaf = 3u << 30;

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    private static (Vector3 Low, Vector3 High) Box(float x, float y = 0, float z = 0, float size = 1) => (new Vector3(x, y, z), new Vector3(x + size, y + size, z + size));

    [Fact]
    public void Build_NoPrimitives_IsOneEmptyLeafWithTheReferencesEmptyBounds()
    {
        BihTree tree = VmapBih.Build([]);

        Assert.Equal(new[] { Leaf, 0u, 0u }, tree.Nodes);
        Assert.Empty(tree.Objects);
        Assert.Equal(0x7FC00000u, F(tree.Low.X));
        Assert.Equal(0x7FC00000u, F(tree.High.Z));
    }

    [Fact]
    public void Build_AsManyPrimitivesAsALeafHolds_IsOneLeafOverTheMergedBounds()
    {
        BihTree tree = VmapBih.Build([Box(4), Box(-2, 7), Box(1, 1, -3)]);

        Assert.Equal(new[] { Leaf, 3u, 0u }, tree.Nodes);
        Assert.Equal(new[] { 0u, 1u, 2u }, tree.Objects);
        Assert.Equal(new Vector3(-2, 0, -3), tree.Low);
        Assert.Equal(new Vector3(5, 8, 1), tree.High);
    }

    [Fact]
    public void Build_FourBoxesInARow_SplitsTheLongestAxisInTheMiddle_WordForWord()
    {
        // Boxes at x = 0, 10, 20 and 30, one per leaf. The root splits x at 15.5: 0 and 1 stay left (their upper bound 11 is
        // the left clip), 2 is swapped to the end and 3 follows (lower bound 20 is the right clip). Each half splits again.
        BihTree tree = VmapBih.Build([Box(0), Box(10), Box(20), Box(30)], leafSize: 1);

        uint[] expected =
        [
            3, F(11), F(20),        // root: axis x, children at 3 and 6
            9, F(1), F(10),         // left half: children at 9 and 12
            15, F(21), F(30),       // right half: children at 15 and 18
            Leaf | 0, 1, 0,
            Leaf | 1, 1, 0,
            Leaf | 2, 1, 0,
            Leaf | 3, 1, 0,
        ];
        Assert.Equal(expected, tree.Nodes);
        Assert.Equal(new[] { 0u, 1u, 2u, 3u }, tree.Objects);
        Assert.Equal(Vector3.Zero, tree.Low);
        Assert.Equal(new Vector3(31, 1, 1), tree.High);
    }

    [Fact]
    public void Build_ScatteredAndClusteredBoxes_GivesATreeTheServerFindsEveryPrimitiveIn()
    {
        // Two tight clusters far apart plus scattered boxes: this drives the one-child clip nodes and the empty-side nodes.
        var random = new Random(5875);
        var boxes = new List<(Vector3 Low, Vector3 High)>();
        for (int i = 0; i < 400; i++)
        {
            Vector3 centre = (i % 4) switch
            {
                0 => new Vector3(100 + ((float)random.NextDouble() * 4), 2000 + ((float)random.NextDouble() * 4), (float)random.NextDouble() * 4),
                1 => new Vector3(-3000 + ((float)random.NextDouble() * 6), -50 + ((float)random.NextDouble() * 6), 40 + ((float)random.NextDouble() * 6)),
                _ => new Vector3(((float)random.NextDouble() * 8000) - 4000, ((float)random.NextDouble() * 8000) - 4000, ((float)random.NextDouble() * 300) - 100),
            };
            var half = new Vector3(0.1f + ((float)random.NextDouble() * 3), 0.1f + ((float)random.NextDouble() * 3), 0.1f + ((float)random.NextDouble() * 3));
            boxes.Add((centre - half, centre + half));
        }

        BihTree tree = VmapBih.Build(boxes);

        Assert.Equal(Enumerable.Range(0, 400).Select(i => (uint)i), tree.Objects.Order());
        Assert.Contains(tree.Nodes.Where((_, at) => at % 3 == 0), word => (word >> 30) != 3 && (word & (1u << 29)) != 0);
        for (int i = 0; i < boxes.Count; i++)
        {
            var found = new List<int>();
            tree.IntersectPoint((boxes[i].Low + boxes[i].High) * 0.5f, found.Add);
            Assert.Contains(i, found);
        }
    }

    [Fact]
    public void Build_IdenticalBoxes_EndsInALeafInsteadOfSplittingForEver()
    {
        (Vector3, Vector3)[] boxes = [.. Enumerable.Repeat(Box(5, 5, 5), 20)];

        BihTree tree = VmapBih.Build(boxes, leafSize: 2);

        Assert.Equal(20, tree.Objects.Count);
        var found = new List<int>();
        tree.IntersectPoint(new Vector3(5.5f), found.Add);
        Assert.Equal(20, found.Count);
    }
}

/// <summary><see cref="VmapRotation"/> against plain trigonometry.</summary>
public sealed class VmapRotationTests
{
    [Fact]
    public void FromEulerAnglesZyx_TurnsAboutZThenYThenX()
    {
        Vector3 yawed = VmapRotation.FromEulerAnglesZyx(MathF.PI / 2, 0, 0).Transform(new Vector3(1, 0, 0));
        Vector3 pitched = VmapRotation.FromEulerAnglesZyx(0, MathF.PI / 2, 0).Transform(new Vector3(1, 0, 0));
        Vector3 rolled = VmapRotation.FromEulerAnglesZyx(0, 0, MathF.PI / 2).Transform(new Vector3(0, 1, 0));

        AssertNear(new Vector3(0, 1, 0), yawed);
        AssertNear(new Vector3(0, 0, -1), pitched);
        AssertNear(new Vector3(0, 0, 1), rolled);
    }

    [Fact]
    public void QuaternionToEulerAngles_RecoversAYaw_AndNormalisesTheQuaternion()
    {
        float half = MathF.PI / 12;
        var yaw30 = new Quaternion(0, 0, 3 * MathF.Sin(half), 3 * MathF.Cos(half));

        (float x, float y, float z) = VmapRotation.FromQuaternion(yaw30).ToEulerAnglesXyz();

        Assert.Equal(0f, x, 1e-6f);
        Assert.Equal(0f, y, 1e-6f);
        Assert.Equal(30f, VmapRotation.ToDegrees(z), 1e-3f);
        AssertNear(new Vector3(MathF.Cos(MathF.PI / 6), MathF.Sin(MathF.PI / 6), 0), VmapRotation.FromQuaternion(yaw30).Transform(new Vector3(1, 0, 0)));
    }

    [Fact]
    public void ToEulerAnglesXyz_AtAPole_ReportsNoZAngle()
    {
        (float x, float y, float z) = new VmapRotation(0, 0, 1, 0, 1, 0, -1, 0, 0).ToEulerAnglesXyz();

        Assert.Equal(0f, x);
        Assert.Equal(1.57079633f, y);
        Assert.Equal(0f, z);
    }

    [Fact]
    public void AngleConversions_UseTheReferencesPi()
    {
        Assert.Equal(180f * 3.1415926535898f / 180f, VmapRotation.ToRadians(180f));
        Assert.Equal((float)(3.1415926535898 * 90f / 180f), VmapRotation.SpawnRadians(90f));
        Assert.Equal(90f, VmapRotation.ToDegrees(VmapRotation.ToRadians(90f)), 1e-4f);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected}, got {actual}");
}

/// <summary>A small client with models: a wall WMO with a doodad set, a cube M2, an Azeroth tile and a one-building dungeon.</summary>
internal sealed class VmapFixture
{
    public const string WallPath = "World\\wmo\\Test\\Wall.wmo";
    public const string SpacedWallPath = "World\\wmo\\Test\\big wall.wmo";
    public const string CubeStoredPath = "World\\Doodads\\Test\\Cube.mdx";
    public const uint Dungeon = 36;

    /// <summary>Server tile 0/49/31, client file <c>Azeroth_31_49.adt</c>.</summary>
    public static readonly TileKey Tile = new(0, 49, 31);

    /// <summary>The tile next to it, which lists the same wall again.</summary>
    public static readonly TileKey Neighbour = new(0, 48, 31);

    /// <summary>World position of the wall WMO's origin: the wall is the plane x = -9400, y 95..105, z 45..55.</summary>
    public static readonly Vector3 Wall = new(-9400, 100, 50);

    /// <summary>World position of the cube doodad, placed at twice its size: it spans 2 yards either way.</summary>
    public static readonly Vector3 Cube = new(-9300, 200, 60);

    public MemoryClientFiles Files { get; } = new();

    public FakeMapDirectory Maps { get; } = SyntheticTerrain.Maps(
        new ClientMap(0, "Azeroth", "Eastern Kingdoms", 0),
        new ClientMap(1, "Kalimdor", "Kalimdor", 0),
        new ClientMap(Dungeon, "DeadminesInstance", "The Deadmines", 1));

    public VmapFixture()
    {
        AddWall(WallPath);
        AddWall(SpacedWallPath);
        Files.Add("World\\Doodads\\Test\\Cube.m2", ModelFixtures.CubeM2());
        Files.Add("World\\Doodads\\Test\\Flat.m2", ModelFixtures.CubeM2(withHull: false));

        var tile = new AdtSpec { Tile = Tile };
        tile.Wmos.Add(WallAt(9001, WallPath, Wall));
        tile.Doodads.Add(new DoodadSpec(CubeStoredPath, 7001, ModelWorld.Raw(Cube.X, Cube.Y, Cube.Z), new WorldPoint(0, 0, 0), 2048, 0));
        tile.Doodads.Add(new DoodadSpec("World\\Doodads\\Test\\Flat.mdx", 7002, ModelWorld.Raw(Cube.X, Cube.Y + 20, Cube.Z), new WorldPoint(0, 0, 0), 1024, 0));
        tile.Doodads.Add(new DoodadSpec("World\\Doodads\\Test\\Gone.mdx", 7003, ModelWorld.Raw(Cube.X, Cube.Y + 40, Cube.Z), new WorldPoint(0, 0, 0), 1024, 0));
        var neighbour = new AdtSpec { Tile = Neighbour };
        neighbour.Wmos.Add(WallAt(9001, WallPath, Wall));
        SyntheticTerrain.AddMap(
            Files,
            "Azeroth",
            SyntheticTerrain.Wdt([(Tile.X, Tile.Y), (Neighbour.X, Neighbour.Y)]),
            (Tile, SyntheticTerrain.Adt(tile).Bytes),
            (Neighbour, SyntheticTerrain.Adt(neighbour).Bytes));

        // The dungeon is one building placed by the WDT at the stored position (0, 20, 0): the map centre, 20 yards up.
        // Its stored bound is around the building's own origin. A second entry names a WMO with a space in its file name.
        WmoSpec[] global =
        [
            new(WallPath, 5001, new WorldPoint(0, 20, 0), new WorldPoint(0, 0, 0), new WorldPoint(-6, 14, -1), new WorldPoint(6, 26, 1), 0, 0, 3),
            new(SpacedWallPath, 5002, new WorldPoint(0, 80, 0), new WorldPoint(0, 0, 0), new WorldPoint(-6, 74, -1), new WorldPoint(6, 86, 1), 0, 0, 0),
        ];
        Files.Add(ClientMapPaths.WdtPath("DeadminesInstance"), SyntheticTerrain.Wdt([], WdtFile.GlobalWmoFlag, global));
    }

    public VmapExtractor Extractor() => new(Files, Maps);

    /// <summary>A MODF entry placing a wall WMO with its origin at a world position, with a stored bound around the wall.</summary>
    public static WmoSpec WallAt(uint uniqueId, string path, Vector3 world, ushort flags = 0, ushort doodadSet = 0)
        => new(
            path,
            uniqueId,
            ModelWorld.Raw(world.X, world.Y, world.Z),
            new WorldPoint(0, 0, 0),
            ModelWorld.Raw(world.X + 1, world.Y + 6, world.Z - 6),
            ModelWorld.Raw(world.X - 1, world.Y - 6, world.Z + 6),
            flags,
            doodadSet,
            4);

    private void AddWall(string path)
    {
        // Group 0 is the wall and references both doodads; group 1 is unreachable; group 2 is named "antiportal".
        var root = new SyntheticWmoRoot { WmoId = 77, GroupCount = 3 };
        root.GroupNames.AddRange(["main", "antiportal"]);
        root.DoodadSets.Add(("Set_$DefaultGlobal", 0, 2));
        root.DoodadSets.Add(("Empty", 2, 0));
        root.Doodads.Add((CubeStoredPath, new Vector3(3, 0, 0), Quaternion.Identity, 1f));
        root.Doodads.Add(("World\\Doodads\\Test\\Flat.mdx", new Vector3(-3, 0, 0), Quaternion.Identity, 1f));
        Files.Add(path, ModelFixtures.WmoRoot(root));

        SyntheticWmoGroup wall = ModelFixtures.WallGroup(0);
        wall.GroupId = 501;
        wall.DoodadReferences.AddRange([0, 1]);
        wall.LiquidHeights = [1, 2, 3, 4];
        SyntheticWmoGroup unreachable = ModelFixtures.WallGroup(2, groupFlags: 0x80);
        SyntheticWmoGroup antiportal = ModelFixtures.WallGroup(4);
        antiportal.NameOffset = 5;
        string stem = path[..^4];
        Files.Add(stem + "_000.wmo", ModelFixtures.WmoGroup(wall));
        Files.Add(stem + "_001.wmo", ModelFixtures.WmoGroup(unreachable));
        Files.Add(stem + "_002.wmo", ModelFixtures.WmoGroup(antiportal));
    }
}

/// <summary><see cref="VmapModelLibrary"/>: which client file a name gets, and what is kept of it.</summary>
public sealed class VmapModelLibraryTests
{
    [Fact]
    public void Doodad_ExtractsTheBoundingHull_UnderTheFixedM2Name()
    {
        var fixture = new VmapFixture();
        var library = new VmapModelLibrary(fixture.Files);

        (string name, VmapModelInfo? model) = library.Doodad("WORLD\\DOODADS\\TEST\\CUBE.MDX");

        Assert.Equal("Cube.m2", name);
        Assert.NotNull(model);
        Assert.False(model.IsWmo);
        Assert.Equal(8, model.HeaderCount);
        Assert.Equal(8, model.Hull.Length);

        VmapRawModel raw = library.Load(model);
        VmapRawGroup hull = Assert.Single(raw.Groups);
        Assert.Equal(0u, raw.RootWmoId);
        Assert.Equal(Vector3.Zero, hull.BoundLow);
        Assert.Equal(Vector3.Zero, hull.BoundHigh);

        // The fixture's first two triangles are 0, 1, 2 and 0, 2, 3; the second and third corner of each are swapped.
        Assert.Equal(new ushort[] { 0, 2, 1, 0, 3, 2 }, hull.Indices[..6]);

        WorldModel world = WorldModel.Parse(raw.ToWorldModel().ToBytes());
        Assert.Equal(12, Assert.Single(world.Groups).Triangles.Count);
        float distance = 10;
        Assert.True(world.IntersectRay(new Vector3(0, 0, 5), new Vector3(0, 0, -1), ref distance, stopAtFirstHit: false));
        Assert.Equal(4f, distance, 1e-4f);
    }

    [Fact]
    public void Doodad_WithoutAHullOrWithoutAFile_IsNotExtracted()
    {
        var library = new VmapModelLibrary(new VmapFixture().Files);

        (string flat, VmapModelInfo? flatModel) = library.Doodad("World\\Doodads\\Test\\Flat.mdx");
        (string gone, VmapModelInfo? goneModel) = library.Doodad("World\\Doodads\\Test\\Gone.mdx");
        (string tooShort, VmapModelInfo? tooShortModel) = library.Doodad("a.m");

        Assert.Equal(("Flat.m2", "Gone.m2", string.Empty), (flat, gone, tooShort));
        Assert.Null(flatModel);
        Assert.Null(goneModel);
        Assert.Null(tooShortModel);
        Assert.Empty(library.Problems);
    }

    [Fact]
    public void Doodad_WhoseFileNameHasASpace_IsFoundUnderItsStoredPath_AndNamedWithAnUnderscore()
    {
        var fixture = new VmapFixture();
        fixture.Files.Add("World\\Doodads\\Test\\old tree.m2", ModelFixtures.CubeM2());
        var library = new VmapModelLibrary(fixture.Files);

        (string name, VmapModelInfo? model) = library.Doodad("World\\Doodads\\Test\\old tree.mdx");

        Assert.Equal("Old_Tree.m2", name);
        Assert.Equal("World\\Doodads\\Test\\old tree.m2", model?.ClientPath);
    }

    [Fact]
    public void Wmo_KeepsReachableGroups_AndTheDoodadsTheyReferenceThatHaveAModel()
    {
        var library = new VmapModelLibrary(new VmapFixture().Files);

        VmapModelInfo? model = library.Wmo("Wall.wmo");

        Assert.NotNull(model);
        Assert.True(model.IsWmo);
        Assert.Equal(2, model.HeaderCount);
        Assert.Equal(new ushort[] { 0 }, model.Doodads.References);
        Assert.Equal(2, model.Doodads.Sets.Count);

        VmapRawModel raw = library.Load(model);
        Assert.Equal(77u, raw.RootWmoId);
        VmapRawGroup group = Assert.Single(raw.Groups);
        Assert.Equal(501u, group.GroupWmoId);
        Assert.Equal(new Vector3(-10), group.BoundLow);
        Assert.Equal(new Vector3(10), group.BoundHigh);
        Assert.Equal(4, group.Vertices.Length);
        Assert.Equal(new ushort[] { 0, 1, 2, 0, 2, 3 }, group.Indices);

        // The fixture's group stores liquid type 9 under a root without the "ids are LiquidType.dbc ids" flag: entry 10,
        // which is an ocean.
        Assert.NotNull(group.Liquid);
        Assert.Equal(2u, group.Liquid.Type);
        Assert.Equal((1u, 1u), (group.Liquid.TilesX, group.Liquid.TilesY));
        Assert.Equal(new Vector3(1, 2, 3), group.Liquid.Corner);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, group.Liquid.Heights);

        WorldModel world = WorldModel.Parse(raw.ToWorldModel().ToBytes());
        Assert.Equal(77u, world.RootWmoId);
        Assert.Equal(2u, Assert.Single(world.Groups).Liquid?.Type);
    }

    [Fact]
    public void Wmo_IsFoundOnlyUnderItsUnderscoredName_AndNotWithAGroupMissing()
    {
        var fixture = new VmapFixture();
        var broken = new SyntheticWmoRoot { GroupCount = 2 };
        fixture.Files.Add("World\\wmo\\Test\\Broken.wmo", ModelFixtures.WmoRoot(broken));
        fixture.Files.Add("World\\wmo\\Test\\Broken_000.wmo", ModelFixtures.WmoGroup(ModelFixtures.WallGroup(0)));
        var library = new VmapModelLibrary(fixture.Files);

        Assert.NotNull(library.Wmo("Big_Wall.wmo"));
        Assert.Null(library.Wmo("Big Wall.wmo"));
        Assert.Null(library.Wmo("Wall_000.wmo"));
        Assert.Null(library.Wmo("Broken.wmo"));
        Assert.Null(library.Wmo("Nowhere.wmo"));
    }

    [Fact]
    public void Wmo_NameUsedInTwoFolders_GoesToTheFirstPath_AndIsReported()
    {
        var fixture = new VmapFixture();
        fixture.Files.Add("World\\wmo\\Another\\Wall.wmo", ModelFixtures.WmoRoot(new SyntheticWmoRoot()));
        var library = new VmapModelLibrary(fixture.Files);

        Assert.Equal("World\\wmo\\Another\\Wall.wmo", library.Wmo("Wall.wmo")?.ClientPath);
        Assert.Contains(library.Problems, p => p.Contains("'Wall.wmo' is used by both", StringComparison.Ordinal));
    }
}

/// <summary>
/// <see cref="VmapExtractor"/> end to end: the files it writes are loaded by the world server's own
/// <see cref="VMapManager"/>, which must then see the wall, the cube and the dungeon building where the client puts them.
/// </summary>
public sealed class VmapExtractorTests
{
    private static string[] FileNames(string directory) => [.. Directory.EnumerateFiles(directory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    [Fact]
    public void Extract_WritesTreeTileAndModelFiles_TheServerLoadsAndCollidesWith()
    {
        var fixture = new VmapFixture();
        using var output = new TestDirectory();

        VmapExtractReport report = fixture.Extractor().Extract(output.Path, null, threads: 2, CancellationToken.None);

        Assert.Equal(Path.Combine(output.Path, "vmaps"), report.VmapsDirectory);
        Assert.Equal(
            new[] { "000.vmtree", "000_31_48.vmtile", "000_31_49.vmtile", "036.vmtree", "Cube.m2", "Cube.m2.vmo", "Wall.wmo.vmo" },
            FileNames(report.VmapsDirectory));
        Assert.Equal(3, report.ModelFiles);
        Assert.Empty(report.Problems);
        Assert.Equal(FileNames(report.VmapsDirectory).Sum(f => new FileInfo(Path.Combine(report.VmapsDirectory, f)).Length), report.Bytes);

        // Azeroth: the wall, the cube and the wall's one doodad that has a model; the wall listed by two tiles is one spawn.
        Assert.Equal(new VmapExtractMapReport(0, "Azeroth", HasWdt: true, Tiled: true, Spawns: 3, TileFiles: 2, Failed: 0), report.Maps[0]);
        Assert.False(report.Maps[1].HasWdt);
        Assert.Equal(new VmapExtractMapReport(VmapFixture.Dungeon, "DeadminesInstance", HasWdt: true, Tiled: false, Spawns: 2, TileFiles: 0, Failed: 0), report.Maps[2]);

        var server = new VMapManager(report.VmapsDirectory);
        Assert.True(server.LoadTile(0, VmapFixture.Tile.X, VmapFixture.Tile.Y));
        Vector3 wall = VmapFixture.Wall;

        // The wall blocks sight across it and nowhere else; doodads only block when they are asked to.
        Assert.False(server.IsInLineOfSight(0, wall + new Vector3(5, 0, 0), wall + new Vector3(-5, 0, 0)));
        Assert.True(server.IsInLineOfSight(0, wall + new Vector3(5, 20, 0), wall + new Vector3(-5, 20, 0)));
        Assert.True(server.IsInLineOfSight(0, wall + new Vector3(5, 0, 20), wall + new Vector3(-5, 0, 20)));
        Vector3 cube = VmapFixture.Cube;
        Assert.True(server.IsInLineOfSight(0, cube + new Vector3(5, 0, 0), cube + new Vector3(-5, 0, 0)));
        Assert.False(server.IsInLineOfSight(0, cube + new Vector3(5, 0, 0), cube + new Vector3(-5, 0, 0), ignoreM2: false));
        Assert.True(server.IsInLineOfSight(0, cube + new Vector3(5, 0, 3), cube + new Vector3(-5, 0, 3), ignoreM2: false));

        // The cube was placed at scale 2: its top is 2 yards above its origin. The wall's doodad cube sits 3 yards along
        // the building's own x axis, which a placement without rotation maps onto world -x.
        Assert.Equal(cube.Z + 2, server.GetModelHeight(0, cube.X, cube.Y, cube.Z + 10, 50)!.Value, 1e-3f);
        Assert.Equal(wall.Z + 1, server.GetModelHeight(0, wall.X - 3, wall.Y, wall.Z + 10, 50)!.Value, 1e-3f);
        Assert.Null(server.GetModelHeight(0, wall.X + 3, wall.Y, wall.Z + 10, 50));

        // The dungeon's building stands at the map centre, 20 yards up; the entry whose file name has a space is not placed.
        Assert.True(server.LoadTile(VmapFixture.Dungeon, 32, 32));
        Assert.False(server.IsInLineOfSight(VmapFixture.Dungeon, new Vector3(5, 0, 20), new Vector3(-5, 0, 20)));
        Assert.True(server.IsInLineOfSight(VmapFixture.Dungeon, new Vector3(5, 0, 80), new Vector3(-5, 0, 80)));
    }

    [Fact]
    public void Extract_StoresPlacementsAsTheReferenceLaysThemOut()
    {
        var fixture = new VmapFixture();
        using var output = new TestDirectory();
        string directory = fixture.Extractor().Extract(output.Path, [0], threads: 1, CancellationToken.None).VmapsDirectory;

        (VMapTree tree, List<(ModelSpawn Spawn, uint Slot)> global) = VMapTree.Parse(0, File.ReadAllBytes(Path.Combine(directory, "000.vmtree")));
        Assert.True(tree.IsTiled);
        Assert.Empty(global);
        Assert.Equal(3, tree.Tree.PrimitiveCount);

        // A tile lists its doodads first, then each WMO followed by the doodads of its set. Ids count up as placements are
        // first met, and the neighbour (Azeroth_31_48) is read before this tile (Azeroth_31_49), so the wall and its doodad
        // already have ids 1 and 2. A spawn's slot is its place in id order.
        List<(ModelSpawn Spawn, uint Slot)> tile = VMapTree.ParseTile(File.ReadAllBytes(Path.Combine(directory, "000_31_49.vmtile")));
        Assert.Equal(new[] { "Cube.m2", "Wall.wmo", "Cube.m2\0" }, tile.Select(s => s.Spawn.Name));
        Assert.Equal(new uint[] { 3, 1, 2 }, tile.Select(s => s.Spawn.Id));
        Assert.Equal(new uint[] { 2, 0, 1 }, tile.Select(s => s.Slot));

        ModelSpawn cube = tile[0].Spawn;
        Assert.Equal(VMapFormat.ModM2 | VMapFormat.ModHasBound, cube.Flags);
        Assert.Equal(2f, cube.Scale);
        Assert.Equal(VMapFormat.ToInternal(VmapFixture.Cube), cube.Position);
        Assert.Equal(cube.Position - new Vector3(2), cube.BoundLow);
        Assert.Equal(cube.Position + new Vector3(2), cube.BoundHigh);

        ModelSpawn wall = tile[1].Spawn;
        Assert.Equal(VMapFormat.ModHasBound, wall.Flags);
        Assert.Equal(4, wall.AdtId);
        Assert.Equal(1f, wall.Scale);
        Assert.Equal(VMapFormat.ToInternal(VmapFixture.Wall), wall.Position);
        Assert.Equal(wall.Position + new Vector3(-1, -6, -6), wall.BoundLow);
        Assert.Equal(wall.Position + new Vector3(1, 6, 6), wall.BoundHigh);

        ModelSpawn doodad = tile[2].Spawn;
        Assert.Equal(VMapFormat.ModM2 | VMapFormat.ModHasBound, doodad.Flags);
        Assert.Equal(wall.Position + new Vector3(3, 0, 0), doodad.Position);
        Assert.Equal(1f, doodad.Scale);
        Assert.True(doodad.Rotation.Length() < 1e-4f);

        // The neighbour lists the wall under the same id, and the doodads of its set again.
        List<(ModelSpawn Spawn, uint Slot)> neighbour = VMapTree.ParseTile(File.ReadAllBytes(Path.Combine(directory, "000_31_48.vmtile")));
        Assert.Equal(new uint[] { 1, 2 }, neighbour.Select(s => s.Spawn.Id));
        Assert.Equal(tile[1].Spawn, neighbour[0].Spawn);
    }

    [Fact]
    public void Extract_GlobalWmo_IsStoredInTheTree_MovedToTheMapCentre()
    {
        var fixture = new VmapFixture();
        using var output = new TestDirectory();
        string directory = fixture.Extractor().Extract(output.Path, [VmapFixture.Dungeon], threads: 1, CancellationToken.None).VmapsDirectory;

        (VMapTree tree, List<(ModelSpawn Spawn, uint Slot)> global) = VMapTree.Parse(VmapFixture.Dungeon, File.ReadAllBytes(Path.Combine(directory, "036.vmtree")));

        Assert.False(tree.IsTiled);
        Assert.Equal(new[] { "Wall.wmo", "Cube.m2\0" }, global.Select(s => s.Spawn.Name));
        ModelSpawn building = global[0].Spawn;
        const float centre = 533.33333f * 32;
        Assert.Equal(VMapFormat.ModHasBound | VMapFormat.ModWorldSpawn, building.Flags);
        Assert.Equal(3, building.AdtId);
        Assert.Equal(new Vector3(centre, centre, 20), building.Position);
        Assert.Equal(new Vector3(centre - 1, centre - 6, 14), building.BoundLow);
        Assert.Equal(new Vector3(centre + 1, centre + 6, 26), building.BoundHigh);
        Assert.Equal(VMapFormat.ModM2 | VMapFormat.ModHasBound | VMapFormat.ModWorldSpawn, global[1].Spawn.Flags);
        Assert.Empty(Directory.EnumerateFiles(directory, "036_*.vmtile"));
    }

    [Fact]
    public void Extract_LeavesOutDestructibleWmos_AndReportsADamagedTile()
    {
        var fixture = new VmapFixture();
        var tile = new AdtSpec { Tile = VmapFixture.Tile };
        tile.Wmos.Add(VmapFixture.WallAt(9001, VmapFixture.WallPath, VmapFixture.Wall, flags: 1));
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", VmapFixture.Tile), SyntheticTerrain.Adt(tile).Bytes);
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", VmapFixture.Neighbour), [1, 2, 3]);
        using var output = new TestDirectory();

        VmapExtractReport report = fixture.Extractor().Extract(output.Path, [0], threads: 1, CancellationToken.None);

        // The destructible wall is not placed, but the doodad of its set still is.
        VmapExtractMapReport azeroth = Assert.Single(report.Maps);
        Assert.Equal(1, azeroth.Spawns);
        Assert.Equal(1, azeroth.Failed);
        Assert.Contains(report.Problems, p => p.Contains("Azeroth_31_48.adt", StringComparison.Ordinal));
        List<(ModelSpawn Spawn, uint Slot)> spawns = VMapTree.ParseTile(File.ReadAllBytes(Path.Combine(report.VmapsDirectory, "000_31_49.vmtile")));
        Assert.Equal("Cube.m2\0", Assert.Single(spawns).Spawn.Name);
    }

    [Fact]
    public void Extract_ReplacesAMapsOldTileFiles_AndRefusesUnknownMaps()
    {
        var fixture = new VmapFixture();
        using var output = new TestDirectory();
        string stale = output.Write(Path.Combine("vmaps", "000_10_10.vmtile"), [9]);
        string other = output.Write(Path.Combine("vmaps", "001_10_10.vmtile"), [9]);

        fixture.Extractor().Extract(output.Path, [0], threads: 1, CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(other));
        Assert.Throws<ArgumentException>(() => fixture.Extractor().Extract(output.Path, [7], threads: 1, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Extractor().Extract(output.Path, null, threads: 1, cancelled.Token));
    }

    [Fact]
    public async Task Command_ExtractsVmaps_AndRefusesARepositoryFolder()
    {
        var fixture = new VmapFixture();
        var registry = new CommandRegistry();
        ExtractCommands.Register(registry);
        var services = new AtlasServices();
        services.Add(new AtlasPaths(null, null));
        services.Add<IClientFileSource>(fixture.Files);
        services.Add<IMapDirectory>(fixture.Maps);
        using var output = new TestDirectory();

        CommandResult result = await registry.InvokeAsync("extract.vmaps", new JsonObject { ["out"] = output.Path, ["maps"] = "0,36" }, services, CommandOrigin.Cli, headless: true, CancellationToken.None);

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode data = result.Data!;
        Assert.Equal(Path.Combine(output.Path, "vmaps"), data["out"]!.GetValue<string>());
        Assert.Equal(2, data["mapsWithModels"]!.GetValue<int>());
        Assert.Equal(5, data["spawns"]!.GetValue<long>());
        Assert.Equal(2, data["tileFiles"]!.GetValue<int>());
        Assert.Equal(3, data["modelFiles"]!.GetValue<int>());
        Assert.Equal(0, data["problemCount"]!.GetValue<int>());
        Assert.False(data["maps"]![1]!["tiled"]!.GetValue<bool>());

        CommandResult refused = await registry.InvokeAsync("extract.vmaps", new JsonObject { ["out"] = Path.Combine(AppContext.BaseDirectory, "x") }, services, CommandOrigin.Cli, headless: true, CancellationToken.None);
        Assert.Equal(CommandException.BadArguments, refused.ErrorCode);
        Assert.StartsWith("extract.vmaps:", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.True(registry.TryGet("extract.vmaps", out CommandDefinition definition));
        Assert.Equal(new[] { "out", "maps", "threads" }, definition.Args.Select(a => a.Name));
    }
}

/// <summary>
/// A fact that needs the reference tools' vmaps (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, holding <c>vmaps/</c>) and the
/// extracted 1.12.1 client files (<c>ARCANECORE_TEST_CLIENT_FILES_DIR</c>); reported as skipped, with the reason, otherwise.
/// </summary>
public sealed class VmapRealFactAttribute : FactAttribute
{
    /// <summary>Skips the fact unless both data roots are present.</summary>
    public VmapRealFactAttribute()
    {
        if (VmapRealRun.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>One full extraction of the real client, shared by the real-data facts. Does nothing when they are skipped.</summary>
public sealed class VmapRealRun : IDisposable
{
    private readonly TestDirectory? _output;

    /// <summary>Extracts every map of the real client into a scratch folder.</summary>
    public VmapRealRun()
    {
        if (SkipReason() is not null)
        {
            return;
        }

        _output = new TestDirectory();
        Report = new VmapExtractor(ClientTerrainRealData.Files, ClientTerrainRealData.Maps)
            .Extract(_output.Path, null, Environment.ProcessorCount, CancellationToken.None);
    }

    /// <summary>The run's report (null when skipped).</summary>
    public VmapExtractReport? Report { get; }

    /// <summary>The reference tools' <c>vmaps</c> folder.</summary>
    public static string ReferenceDirectory => Path.Combine(Environment.GetEnvironmentVariable(ClientTerrainRealData.TerrainVariable)!, "vmaps");

    /// <summary>Why the real-data facts cannot run here, or null when they can.</summary>
    public static string? SkipReason()
    {
        if (ClientTerrainRealData.SkipReason() is { } reason)
        {
            return reason;
        }

        return Directory.Exists(ReferenceDirectory) ? null : $"{ClientTerrainRealData.TerrainVariable} holds no vmaps/ folder (vmangos VMapExtractor and VMapAssembler output for 1.12.1).";
    }

    /// <inheritdoc />
    public void Dispose() => _output?.Dispose();
}

/// <summary>
/// The extractor against the real 1.12.1 client and the reference tools' own output. Model files must be equal byte for
/// byte; placements must be the same set per tile, with positions, rotations and bounds equal to within the last bits a
/// different sine and cosine can move (ids and slots are compared through the spawns they name, not as numbers).
/// </summary>
public sealed class VmapExtractorRealDataTests(VmapRealRun run, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<VmapRealRun>
{
    private const float PositionTolerance = 1e-2f;
    private const float AngleTolerance = 1e-2f;

    [VmapRealFact]
    public void ModelFiles_EqualTheReferenceAssemblersFiles()
    {
        VmapExtractReport report = run.Report!;
        string[] written = [.. Directory.EnumerateFiles(report.VmapsDirectory).Select(f => Path.GetFileName(f)!).Where(IsModelFile).Order(StringComparer.Ordinal)];
        var reference = new HashSet<string>(Directory.EnumerateFiles(VmapRealRun.ReferenceDirectory).Select(f => Path.GetFileName(f)!).Where(IsModelFile), StringComparer.Ordinal);
        var missing = new List<string>();
        var different = new List<string>();
        foreach (string name in written)
        {
            if (!reference.Contains(name))
            {
                missing.Add(name);
            }
            else if (!File.ReadAllBytes(Path.Combine(report.VmapsDirectory, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(VmapRealRun.ReferenceDirectory, name))))
            {
                different.Add(name);
            }
        }

        // The reference also writes the models that only game objects use; this extractor does not.
        output.WriteLine($"{written.Length} model files written, {reference.Count} in the reference ({reference.Count - written.Length + missing.Count} reference-only), {report.Problems.Count} problems reported.");
        foreach (string problem in report.Problems.Take(40))
        {
            output.WriteLine("  " + problem);
        }

        Assert.True(written.Length > 3000, $"Only {written.Length} model files were written; a full 1.12.1 extraction places several thousand models.");
        Assert.True(missing.Count == 0, $"{missing.Count} model files have no reference file: {string.Join(", ", missing.Take(20))}");
        Assert.True(different.Count == 0, $"{different.Count} of {written.Length} model files differ from the reference: {string.Join(", ", different.Take(20))}");
    }

    [VmapRealFact]
    public void TreesAndTiles_HoldTheReferencesPlacements()
    {
        VmapExtractReport report = run.Report!;
        string[] referenceFiles = [.. Directory.EnumerateFiles(VmapRealRun.ReferenceDirectory).Select(f => Path.GetFileName(f)!).Where(f => f.EndsWith(".vmtree", StringComparison.Ordinal) || f.EndsWith(".vmtile", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        string[] writtenFiles = [.. Directory.EnumerateFiles(report.VmapsDirectory).Select(f => Path.GetFileName(f)!).Where(f => f.EndsWith(".vmtree", StringComparison.Ordinal) || f.EndsWith(".vmtile", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        Assert.Equal(referenceFiles, writtenFiles);

        int spawns = 0;
        var problems = new List<string>();
        foreach (string file in referenceFiles)
        {
            byte[] expectedBytes = File.ReadAllBytes(Path.Combine(VmapRealRun.ReferenceDirectory, file));
            byte[] actualBytes = File.ReadAllBytes(Path.Combine(report.VmapsDirectory, file));
            List<(ModelSpawn Spawn, uint Slot)> expected;
            List<(ModelSpawn Spawn, uint Slot)> actual;
            if (file.EndsWith(".vmtree", StringComparison.Ordinal))
            {
                (VMapTree expectedTree, expected) = VMapTree.Parse(0, expectedBytes);
                (VMapTree actualTree, actual) = VMapTree.Parse(0, actualBytes);
                if (expectedTree.IsTiled != actualTree.IsTiled || expectedTree.Tree.PrimitiveCount != actualTree.Tree.PrimitiveCount)
                {
                    problems.Add($"{file}: tiled {actualTree.IsTiled} with {actualTree.Tree.PrimitiveCount} spawns, the reference is tiled {expectedTree.IsTiled} with {expectedTree.Tree.PrimitiveCount}.");
                }
            }
            else
            {
                expected = VMapTree.ParseTile(expectedBytes);
                actual = VMapTree.ParseTile(actualBytes);
            }

            if (expected.Count != actual.Count)
            {
                problems.Add($"{file}: {actual.Count} spawns, the reference has {expected.Count}.");
                continue;
            }

            // Ids differ between the two runs, so spawns are paired by what they are: name, then position.
            static IEnumerable<ModelSpawn> Sorted(List<(ModelSpawn Spawn, uint Slot)> list)
                => list.Select(s => s.Spawn).OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Position.X).ThenBy(s => s.Position.Y).ThenBy(s => s.Position.Z);

            foreach ((ModelSpawn e, ModelSpawn a) in Sorted(expected).Zip(Sorted(actual)))
            {
                spawns++;
                bool same = e.Name == a.Name && e.Flags == a.Flags && e.AdtId == a.AdtId
                    && MathF.Abs(e.Scale - a.Scale) <= 1e-6f
                    && Vector3.Distance(e.Position, a.Position) <= PositionTolerance
                    && Vector3.Distance(e.Rotation, a.Rotation) <= AngleTolerance
                    && Vector3.Distance(e.BoundLow, a.BoundLow) <= PositionTolerance
                    && Vector3.Distance(e.BoundHigh, a.BoundHigh) <= PositionTolerance;
                if (!same && problems.Count < 50)
                {
                    problems.Add($"{file}: {a} is not the reference's {e}.");
                }
            }
        }

        output.WriteLine($"{referenceFiles.Length} tree and tile files, {spawns} spawn records compared.");
        Assert.True(spawns > 50000, $"Only {spawns} spawn records were compared.");
        Assert.True(problems.Count == 0, $"{problems.Count} differences from the reference (first 20):\n{string.Join("\n", problems.Take(20))}");
    }

    private static bool IsModelFile(string name)
        => !name.EndsWith(".vmtree", StringComparison.Ordinal) && !name.EndsWith(".vmtile", StringComparison.Ordinal) && !name.EndsWith(".part", StringComparison.Ordinal) && name != "temp_gameobject_models";
}

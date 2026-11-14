using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>
/// A server data root made from a synthetic client by the two extractors, then meshed once for every test of the class:
/// a gently sloping Azeroth tile with a wall, a pond and a floor hovering two yards above the ground, and a dungeon that is
/// one floor at the map centre.
/// </summary>
public sealed class MmapWorld : IDisposable
{
    public const string FloorPath = "World\\wmo\\Test\\Floor.wmo";

    /// <summary>World position of the hovering floor's centre; the floor is 20 yards square.</summary>
    public static readonly Vector3 Floor = new(-9300, 300, Ground(-9300) + 2);

    /// <summary>A point in the middle of the pond (chunk row 2, column 2 of the tile).</summary>
    public static readonly Vector3 Pond = new(-9150, 450, Ground(-9150));

    /// <summary>The pond's surface.</summary>
    public const float PondLevel = 58f;

    private readonly TestDirectory _directory = new();

    public MmapWorld()
    {
        var fixture = new VmapFixture();
        var floorRoot = new SyntheticWmoRoot { WmoId = 78, GroupCount = 1 };
        fixture.Files.Add(FloorPath, ModelFixtures.WmoRoot(floorRoot));
        var floor = new SyntheticWmoGroup { GroupId = 601 };
        floor.Vertices.AddRange([new Vector3(-10, -10, 0), new Vector3(10, -10, 0), new Vector3(10, 10, 0), new Vector3(-10, 10, 0)]);
        floor.Triangles.Add(new SyntheticTriangle(0, 1, 2));
        floor.Triangles.Add(new SyntheticTriangle(0, 2, 3));
        fixture.Files.Add(FloorPath[..^4] + "_000.wmo", ModelFixtures.WmoGroup(floor));

        // The ground is the plane z = 144 + x / 100: 50 at the wall.
        AdtSpec tile = SyntheticTerrain.Plane(VmapFixture.Tile, 144f, 0.01f, 0f);
        tile.Wmos.Add(VmapFixture.WallAt(9001, VmapFixture.WallPath, VmapFixture.Wall));
        tile.Wmos.Add(new WmoSpec(
            FloorPath,
            9002,
            ModelWorld.Raw(Floor.X, Floor.Y, Floor.Z),
            new WorldPoint(0, 0, 0),
            ModelWorld.Raw(Floor.X + 11, Floor.Y + 11, Floor.Z - 1),
            ModelWorld.Raw(Floor.X - 11, Floor.Y - 11, Floor.Z + 1),
            0,
            0,
            0));
        tile.Liquid[(2 * 16) + 2] = [new LiquidLayerSpec(AdtLiquidKind.River, PondLevel, PondLevel, (_, _) => PondLevel, (_, _) => 0)];
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", VmapFixture.Tile), SyntheticTerrain.Adt(tile).Bytes);

        // The dungeon: the floor alone, 20 yards up at the map centre, which is the corner four tiles share.
        WmoSpec[] global = [new(FloorPath, 5001, new WorldPoint(0, 20, 0), new WorldPoint(0, 0, 0), new WorldPoint(-11, 19, -11), new WorldPoint(11, 21, 11), 0, 0, 0)];
        fixture.Files.Add(ClientMapPaths.WdtPath("DeadminesInstance"), SyntheticTerrain.Wdt([], WdtFile.GlobalWmoFlag, global));

        new MapExtractor(fixture.Files, fixture.Maps, ExtractFixtures.Areas((1, 0))).Extract(DataRoot, null, overwrite: false, threads: 2, CancellationToken.None);
        new VmapExtractor(fixture.Files, fixture.Maps).Extract(DataRoot, null, threads: 2, CancellationToken.None);
        Report = new MmapExtractor(DataRoot).Extract(null, overwrite: false, quick: false, Environment.ProcessorCount, CancellationToken.None);
    }

    /// <summary>The data root holding maps, vmaps and mmaps.</summary>
    public string DataRoot => _directory.Path;

    /// <summary>The navigation mesh run's report.</summary>
    public MmapExtractReport Report { get; }

    /// <summary>The ground height of the Azeroth tile at a world x.</summary>
    public static float Ground(float x) => 144f + (0.01f * x);

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();
}

/// <summary>
/// <see cref="MmapExtractor"/> end to end: the tiles it writes are loaded by the world server's own
/// <see cref="NavMeshPathfinder"/>, which must then walk around the wall, across the dungeon floor's four tiles, and find
/// the pond as water.
/// </summary>
public sealed class MmapExtractorTests(MmapWorld world) : IClassFixture<MmapWorld>
{
    private string Mmaps => Path.Combine(world.DataRoot, "mmaps");

    private static float Length(IReadOnlyList<Vector3> points)
    {
        float length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            length += Vector3.Distance(points[i - 1], points[i]);
        }

        return length;
    }

    [Fact]
    public void Extract_WritesAParameterFilePerMap_AndATilePerTileWithGround()
    {
        Assert.Equal(Mmaps, world.Report.MmapsDirectory);
        Assert.Empty(world.Report.Problems);
        string[] files = [.. Directory.EnumerateFiles(Mmaps).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        Assert.Equal(new[] { "000.mmap", "0004931.mmtile", "036.mmap", "0363131.mmtile", "0363132.mmtile", "0363231.mmtile", "0363232.mmtile" }, files);

        // Azeroth: the sloped tile is meshed; the flat neighbour stores no heights, so it has no ground.
        MmapExtractMapReport azeroth = world.Report.Maps[0];
        Assert.Equal((0u, 2, 1, 0, 1, 0), (azeroth.Id, azeroth.Tiles, azeroth.Written, azeroth.Existing, azeroth.Empty, azeroth.Failed));
        Assert.True(azeroth.Polygons > 100, $"only {azeroth.Polygons} polygons");
        MmapExtractMapReport dungeon = world.Report.Maps[1];
        Assert.Equal((VmapFixture.Dungeon, 4, 4, 0, 0, 0), (dungeon.Id, dungeon.Tiles, dungeon.Written, dungeon.Existing, dungeon.Empty, dungeon.Failed));
        Assert.Equal(files.Sum(f => new FileInfo(Path.Combine(Mmaps, f)).Length), world.Report.Bytes);

        // The map's origin is the low corner of its highest-numbered tile; the tile count is the map's tile count.
        NavMeshParams parameters = NavMeshParams.Parse(File.ReadAllBytes(Path.Combine(Mmaps, "000.mmap")));
        Assert.Equal(2, parameters.MaxTiles);
        Assert.Equal(0, parameters.MaxPolys);
        Assert.Equal(533.33333f, parameters.TileWidth);
        Assert.Equal(new Vector3(0f, 1.17549435E-38f, (32 - 49) * 533.33333f - 533.33333f), parameters.Origin);
    }

    [Fact]
    public void Tile_HoldsGroundWaterAndTheHoveringFloor_ButNoGroundUnderTheFloor()
    {
        NavMeshTile tile = NavMeshTile.ParseFile(File.ReadAllBytes(Path.Combine(Mmaps, "0004931.mmtile")));

        // Polygon vertices are in Recast space: (world y, world z, world x).
        List<(Vector3 Centre, ushort Flags)> polys = [.. tile.Polys.Select(p =>
        {
            Vector3 sum = Vector3.Zero;
            foreach (ushort v in p.Vertices)
            {
                sum += tile.Vertices[v];
            }

            Vector3 recast = sum / p.Vertices.Count;
            return (new Vector3(recast.Z, recast.X, recast.Y), p.Flags);
        })];

        Assert.All(polys, p => Assert.InRange(p.Centre.X, -9600.1f, -9066.5f));
        Assert.All(polys, p => Assert.InRange(p.Centre.Y, -0.1f, 533.4f));

        // The pond is 33 yards square and 5.5 deep: the polygons centred within 15 yards of its middle are water only; far
        // from it the ground is plain ground.
        List<(Vector3 Centre, ushort Flags)> pond = [.. polys.Where(p => Vector2.Distance(new Vector2(p.Centre.X, p.Centre.Y), new Vector2(MmapWorld.Pond.X, MmapWorld.Pond.Y)) < 15)];
        Assert.NotEmpty(pond);
        Assert.All(pond, p => Assert.Equal((ushort)NavTerrain.Water, p.Flags));
        Assert.Contains(polys, p => p.Flags == (ushort)NavTerrain.Ground && Vector2.Distance(new Vector2(p.Centre.X, p.Centre.Y), new Vector2(-9500, 100)) < 30);

        // The floor hovers two yards over the ground: it is meshed, the ground right under its middle is not.
        List<(Vector3 Centre, ushort Flags)> atFloor = [.. polys.Where(p => MathF.Abs(p.Centre.X - MmapWorld.Floor.X) < 5 && MathF.Abs(p.Centre.Y - MmapWorld.Floor.Y) < 5)];
        Assert.Contains(atFloor, p => MathF.Abs(p.Centre.Z - MmapWorld.Floor.Z) < 0.6f);
        Assert.DoesNotContain(atFloor, p => p.Centre.Z < MmapWorld.Floor.Z - 1f);
    }

    [Fact]
    public void Tile_IsReadTheSameByTheIndependentDetourReader()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Mmaps, "0004931.mmtile"));
        NavMeshTile server = NavMeshTile.ParseFile(file);

        ArcaneCore.Atlas.Reference.Navmesh.ParsedNavTile reference = ArcaneCore.Atlas.Reference.Navmesh.MmapTileReader.Parse(file);

        Assert.Equal(server.Polys.Length, reference.Data.header.polyCount);
        Assert.Equal(server.Vertices.Length, reference.Data.header.vertCount);
        Assert.Equal(server.DetailVertices.Length, reference.Data.header.detailVertCount);
        Assert.Equal(7, reference.Data.header.version);
        Assert.Equal(0, reference.Data.header.x);
        Assert.Equal(0, reference.Data.header.y);
        Assert.True(reference.Data.header.bvNodeCount > 0);
        for (int i = 0; i < server.Polys.Length; i++)
        {
            Assert.Equal(server.Polys[i].Flags, reference.Data.polys[i].flags);
            Assert.Equal(server.Polys[i].Vertices.Count, reference.Data.polys[i].vertCount);
        }
    }

    [Fact]
    public void Server_WalksAroundTheWall()
    {
        var server = new NavMeshPathfinder(Mmaps);
        Assert.True(server.HasNavigationData(0));
        Assert.True(server.LoadTile(0, VmapFixture.Tile.X, VmapFixture.Tile.Y));
        Vector3 wall = VmapFixture.Wall;
        var start = new Vector3(wall.X + 5, wall.Y, MmapWorld.Ground(wall.X + 5));
        var end = new Vector3(wall.X - 5, wall.Y, MmapWorld.Ground(wall.X - 5));

        PathResult path = server.FindPath(0, start, end);

        Assert.True(path.HasPath, path.Type.ToString());
        Assert.Equal(PathType.Normal, path.Type);
        Assert.True(Vector3.Distance(end, path.End) < 0.5f, $"the path ends at {path.End}");

        // The wall is 10 yards wide and the straight line is 10 yards long: the way round is clearly longer, and it
        // crosses the wall's plane beside the wall, not through it.
        Assert.True(Length(path.Points) > 13f, $"path length {Length(path.Points)}");
        for (int i = 1; i < path.Points.Count; i++)
        {
            Vector3 a = path.Points[i - 1];
            Vector3 b = path.Points[i];
            if ((a.X - wall.X) * (b.X - wall.X) < 0)
            {
                float y = a.Y + ((b.Y - a.Y) * ((wall.X - a.X) / (b.X - a.X)));
                Assert.True(MathF.Abs(y - wall.Y) > 5f, $"the path crosses the wall's plane at y {y}");
            }
        }

        // Away from the wall the same distance is a straight walk.
        PathResult open = server.FindPath(0, start + new Vector3(0, 40, 0), end + new Vector3(0, 40, 0));
        Assert.Equal(PathType.Normal, open.Type);
        Assert.Equal(10f, Length(open.Points), 0.5f);
    }

    [Fact]
    public void Server_CrossesTheFourTilesOfTheDungeonFloor()
    {
        var server = new NavMeshPathfinder(Mmaps);
        for (int x = 31; x <= 32; x++)
        {
            for (int y = 31; y <= 32; y++)
            {
                Assert.True(server.LoadTile(VmapFixture.Dungeon, x, y));
            }
        }

        PathResult path = server.FindPath(VmapFixture.Dungeon, new Vector3(-6, -6, 20), new Vector3(6, 6, 20));

        Assert.Equal(PathType.Normal, path.Type);
        Assert.True(Vector3.Distance(new Vector3(6, 6, 20), path.End) < 0.5f, $"the path ends at {path.End}");
        Assert.Equal(12f * MathF.Sqrt(2), Length(path.Points), 1.0f);
        Assert.All(path.Points, p => Assert.InRange(p.Z, 19.5f, 20.6f));
    }

    [Fact]
    public void Extract_KeepsCurrentTiles_RebuildsOnRequest_AndRefusesAMapWithoutData()
    {
        // A copy of the dungeon's data, so the shared world stays as it is.
        using var copy = new TestDirectory();
        foreach (string folder in new[] { "maps", "vmaps", "mmaps" })
        {
            Directory.CreateDirectory(Path.Combine(copy.Path, folder));
            foreach (string file in Directory.EnumerateFiles(Path.Combine(world.DataRoot, folder)))
            {
                File.Copy(file, Path.Combine(copy.Path, folder, Path.GetFileName(file)));
            }
        }

        var extractor = new MmapExtractor(copy.Path);
        string tile = Path.Combine(copy.Path, "mmaps", "0363131.mmtile");
        byte[] built = File.ReadAllBytes(tile);
        File.WriteAllBytes(Path.Combine(copy.Path, "mmaps", "0363232.mmtile"), [1, 2, 3]);

        MmapExtractMapReport kept = Assert.Single(extractor.Extract([VmapFixture.Dungeon], overwrite: false, quick: false, threads: 2, CancellationToken.None).Maps);

        // Three current tiles are kept; the damaged one is not a current tile and is built again.
        Assert.Equal((3, 1), (kept.Existing, kept.Written));

        MmapExtractMapReport rebuilt = Assert.Single(extractor.Extract([VmapFixture.Dungeon], overwrite: true, quick: true, threads: 2, CancellationToken.None).Maps);
        Assert.Equal((0, 4), (rebuilt.Existing, rebuilt.Written));
        Assert.Equal(built, File.ReadAllBytes(tile));
        Assert.Throws<ArgumentException>(() => extractor.Extract([7], overwrite: false, quick: false, threads: 1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.Extract(null, overwrite: false, quick: false, threads: 0, CancellationToken.None));
    }

    [Fact]
    public void Models_RemoveTheTerrainUnderAFloor_UnlessQuick()
    {
        int Blanked(bool quick)
        {
            var mesh = new MmapMeshData();
            new MmapTerrain(Path.Combine(world.DataRoot, "maps"), skipLiquid: false).Load(0, VmapFixture.Tile.Y, VmapFixture.Tile.X, mesh);
            MmapTerrain.CleanVertices(mesh.SolidVerts, mesh.SolidTris);
            int terrainTriangles = mesh.SolidTris.Count / 3;
            Assert.Equal(128 * 128 * 4, terrainTriangles);
            Assert.True(new MmapModels(new MmapModelCache(Path.Combine(world.DataRoot, "vmaps")), quick).Load(0, VmapFixture.Tile.Y, VmapFixture.Tile.X, mesh));
            Assert.Equal(terrainTriangles, mesh.VmapFirstTriangle);
            Assert.True(mesh.VmapLastTriangle > mesh.VmapFirstTriangle);
            int blanked = 0;
            for (int i = 0; i < terrainTriangles; i++)
            {
                blanked += mesh.SolidTris[i * 3] == 0 && mesh.SolidTris[(i * 3) + 1] == 0 && mesh.SolidTris[(i * 3) + 2] == 0 ? 1 : 0;
            }

            return blanked;
        }

        // The floor covers 20 x 20 yards of 4.17-yard squares of four triangles each.
        Assert.InRange(Blanked(quick: false), 40, 120);
        Assert.Equal(0, Blanked(quick: true));
    }

    [Fact]
    public void Terrain_ReadsHeightsHolesAndLiquidFromTheMapFile()
    {
        var mesh = new MmapMeshData();
        var terrain = new MmapTerrain(Path.Combine(world.DataRoot, "maps"), skipLiquid: false);
        terrain.Load(0, VmapFixture.Tile.Y, VmapFixture.Tile.X, mesh);

        Assert.Equal(MmapWorld.Ground(-9400), terrain.GetHeight(-9400, 100), 0.01f);
        Assert.Equal(MmapWorld.Ground(-9123.4f), terrain.GetHeight(-9123.4f, 17.5f), 0.01f);
        Assert.Equal(-50000f, terrain.GetHeight(-9000, 100));
        Assert.Equal(-50000f, terrain.GetHeight(-9400, 600));

        // The pond is one chunk: 64 squares of two liquid triangles, all water; the terrain under it stays.
        Assert.Equal(128, mesh.LiquidType.Count);
        Assert.All(mesh.LiquidType, type => Assert.Equal(MmapArea.Water, type));
        Assert.Equal(128 * 128 * 4 * 3, mesh.SolidTris.Count);
        MmapTerrain.CleanVertices(mesh.LiquidVerts, mesh.LiquidTris);
        Assert.Equal(81, mesh.LiquidVerts.Count / 3);
        Assert.All(Enumerable.Range(0, 81), i => Assert.Equal(MmapWorld.PondLevel, mesh.LiquidVerts[(i * 3) + 1]));

        var dry = new MmapMeshData();
        new MmapTerrain(Path.Combine(world.DataRoot, "maps"), skipLiquid: true).Load(0, VmapFixture.Tile.Y, VmapFixture.Tile.X, dry);
        Assert.Empty(dry.LiquidTris);
    }

    [Fact]
    public async Task Command_BuildsFromADataRoot_AndRefusesAFolderWithoutData()
    {
        var registry = new CommandRegistry();
        ExtractCommands.Register(registry);
        var services = new AtlasServices();
        services.Add(new AtlasPaths(world.DataRoot, null));

        // The configured data root is the default; everything in it is current, so nothing is rebuilt.
        CommandResult result = await registry.InvokeAsync("extract.mmaps", new JsonObject { ["maps"] = "36" }, services, CommandOrigin.Cli, headless: true, CancellationToken.None);

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(Mmaps, result.Data!["out"]!.GetValue<string>());
        Assert.Equal(4, result.Data!["existing"]!.GetValue<int>());
        Assert.Equal(0, result.Data!["written"]!.GetValue<int>());

        using var empty = new TestDirectory();
        CommandResult noData = await registry.InvokeAsync("extract.mmaps", new JsonObject { ["data"] = empty.Path }, services, CommandOrigin.Cli, headless: true, CancellationToken.None);
        Assert.Equal(CommandException.BadArguments, noData.ErrorCode);
        Assert.Contains("neither a maps nor a vmaps folder", noData.ErrorMessage, StringComparison.Ordinal);
        CommandResult unknownMap = await registry.InvokeAsync("extract.mmaps", new JsonObject { ["maps"] = "7" }, services, CommandOrigin.Cli, headless: true, CancellationToken.None);
        Assert.Equal(CommandException.BadArguments, unknownMap.ErrorCode);
        CommandResult inRepository = await registry.InvokeAsync("extract.mmaps", new JsonObject { ["data"] = AppContext.BaseDirectory }, services, CommandOrigin.Cli, headless: true, CancellationToken.None);
        Assert.Equal(CommandException.BadArguments, inRepository.ErrorCode);
        Assert.True(registry.TryGet("extract.mmaps", out CommandDefinition definition));
        Assert.Equal(new[] { "data", "maps", "tile", "overwrite", "quick", "threads" }, definition.Args.Select(a => a.Name));
        CommandResult badTile = await registry.InvokeAsync("extract.mmaps", new JsonObject { ["maps"] = "0,36", ["tile"] = "49,31" }, services, CommandOrigin.Cli, headless: true, CancellationToken.None);
        Assert.Equal(CommandException.BadArguments, badTile.ErrorCode);
    }

    [Fact]
    public void Extract_OneTile_WritesThatTileIntoAnotherFolder_WithTheMapsFullParameters()
    {
        using var output = new TestDirectory();
        var extractor = new MmapExtractor(world.DataRoot, output.Path);

        MmapExtractReport report = extractor.Extract([VmapFixture.Dungeon], overwrite: true, quick: false, threads: 1, CancellationToken.None, onlyTile: (31, 32));

        Assert.Equal(output.Path, report.MmapsDirectory);
        Assert.Equal(new[] { "036.mmap", "0363132.mmtile" }, Directory.EnumerateFiles(output.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Mmaps, "036.mmap")), File.ReadAllBytes(Path.Combine(output.Path, "036.mmap")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Mmaps, "0363132.mmtile")), File.ReadAllBytes(Path.Combine(output.Path, "0363132.mmtile")));
        Assert.Throws<ArgumentException>(() => extractor.Extract([VmapFixture.Dungeon], overwrite: true, quick: false, threads: 1, CancellationToken.None, onlyTile: (10, 10)));
    }
}

/// <summary>
/// A fact that needs the reference tools' full data root (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, holding <c>maps/</c>,
/// <c>vmaps/</c> and <c>mmaps/</c>); reported as skipped, with the reason, otherwise.
/// </summary>
public sealed class MmapRealFactAttribute : FactAttribute
{
    /// <summary>Skips the fact unless the data root is complete.</summary>
    public MmapRealFactAttribute()
    {
        string? root = Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root) || !new[] { "maps", "vmaps", "mmaps" }.All(f => Directory.Exists(Path.Combine(root, f))))
        {
            Skip = $"{AtlasPaths.TerrainEnvironmentVariable} is not set to a data root holding maps/, vmaps/ and mmaps/ (vmangos tool output for 1.12.1).";
        }
    }
}

/// <summary>
/// The generator against the reference generator's own tiles, built from the same maps and vmaps. Recast gives each tool
/// its own polygons, so the comparison is of what the meshes cover: the walkable area per kind of ground, and a path the
/// server finds on both.
/// </summary>
public sealed class MmapExtractorRealDataTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Goldshire, Northshire and Stormwind's trade district: open ground, an abbey and its walls, a dense city.</summary>
    private static readonly (uint Map, int X, int Y)[] Tiles = [(0, 49, 31), (0, 48, 32), (0, 48, 30)];

    private static Dictionary<ushort, double> AreaByFlags(NavMeshTile tile)
    {
        var areas = new Dictionary<ushort, double>();
        foreach (NavPoly poly in tile.Polys)
        {
            double area = 0;
            Vector3 first = tile.Vertices[poly.Vertices[0]];
            for (int i = 2; i < poly.Vertices.Count; i++)
            {
                Vector3 a = tile.Vertices[poly.Vertices[i - 1]] - first;
                Vector3 b = tile.Vertices[poly.Vertices[i]] - first;
                area += Math.Abs((a.Z * b.X) - (a.X * b.Z)) / 2;
            }

            areas[poly.Flags] = areas.GetValueOrDefault(poly.Flags) + area;
        }

        return areas;
    }

    [MmapRealFact]
    public void Tiles_CoverTheGroundTheReferenceGeneratorsTilesCover()
    {
        string root = Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable)!;
        using var scratch = new TestDirectory();
        var extractor = new MmapExtractor(root, scratch.Path);
        var failures = new List<string>();
        foreach ((uint map, int x, int y) in Tiles)
        {
            MmapExtractReport report = extractor.Extract([map], overwrite: true, quick: false, threads: 1, CancellationToken.None, onlyTile: (x, y));
            string name = NavMeshFormat.TileFileName(map, x, y);
            Assert.True(File.Exists(Path.Combine(scratch.Path, name)), $"{name} was not built: {string.Join("; ", report.Problems)}");
            Assert.Equal(File.ReadAllBytes(Path.Combine(root, "mmaps", NavMeshFormat.ParamsFileName(map))), File.ReadAllBytes(Path.Combine(scratch.Path, NavMeshFormat.ParamsFileName(map))));
            NavMeshTile mine = NavMeshTile.ParseFile(File.ReadAllBytes(Path.Combine(scratch.Path, name)));
            NavMeshTile reference = NavMeshTile.ParseFile(File.ReadAllBytes(Path.Combine(root, "mmaps", name)));
            Assert.Equal((reference.X, reference.Y), (mine.X, mine.Y));
            Dictionary<ushort, double> mineArea = AreaByFlags(mine);
            Dictionary<ushort, double> referenceArea = AreaByFlags(reference);
            output.WriteLine($"{name}: {mine.Polys.Length} polygons (reference {reference.Polys.Length}); problems: {report.Problems.Count}");
            foreach (ushort flags in mineArea.Keys.Union(referenceArea.Keys).Order())
            {
                double a = mineArea.GetValueOrDefault(flags);
                double b = referenceArea.GetValueOrDefault(flags);
                output.WriteLine($"  flags 0x{flags:X2}: {a:F0} sq yd (reference {b:F0})");

                // Kinds of ground covering under half a percent of a tile are too small to compare by ratio.
                if (Math.Max(a, b) > 1400 && Math.Abs(a - b) > 0.1 * Math.Max(a, b))
                {
                    failures.Add($"{name} flags 0x{flags:X2}: {a:F0} sq yd, reference {b:F0}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Walkable area differs from the reference by more than 10%:\n" + string.Join("\n", failures));
    }
}

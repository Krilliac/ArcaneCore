using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>Builds z1.4 <c>.map</c> files (the server's terrain format) in memory, for the generator's terrain reader.</summary>
internal static class MmapMapFile
{
    private const int V9 = 129 * 129;
    private const int V8 = 128 * 128;

    /// <summary>The flat height of every sample.</summary>
    public const float Height = 10f;

    /// <param name="holes">The 256 hole words of the tile's 16 x 16 cells, or null for none.</param>
    /// <param name="liquid">A whole-tile liquid (its type byte and level), or null for none.</param>
    /// <param name="noHeights">Store no height samples (the tile's height block says so).</param>
    public static byte[] Build(ushort[]? holes = null, (byte Type, float Level)? liquid = null, bool noHeights = false)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(new byte[40]);

        int heightOffset = (int)stream.Position;
        w.Write(0x5350414Du);
        w.Write(noHeights ? 1u : 0u);
        w.Write(Height);
        w.Write(Height);
        if (!noHeights)
        {
            for (int i = 0; i < V9 + V8; i++)
            {
                w.Write(Height);
            }
        }

        int liquidOffset = 0;
        if (liquid is { } water)
        {
            liquidOffset = (int)stream.Position;
            var block = new byte[16];
            block[4] = 0x03;
            block[5] = water.Type;
            BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(12), water.Level);
            w.Write(block);
        }

        int holesOffset = 0;
        int holesSize = 0;
        if (holes is not null)
        {
            holesOffset = (int)stream.Position;
            holesSize = 512;
            foreach (ushort word in holes)
            {
                w.Write(word);
            }
        }

        w.Flush();
        byte[] file = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x342E317Au);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)heightOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), (uint)liquidOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), (uint)holesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(36), (uint)holesSize);
        return file;
    }

    /// <summary>Writes a tile file under the generator's tile indices (the file is named server X = <paramref name="tileY"/>).</summary>
    public static void Write(string mapsDirectory, uint mapId, int tileX, int tileY, byte[] file)
    {
        Directory.CreateDirectory(mapsDirectory);
        File.WriteAllBytes(Path.Combine(mapsDirectory, $"{mapId:D3}{tileY:D2}{tileX:D2}.map"), file);
    }
}

/// <summary>
/// The terrain reader's rules (<see cref="MmapTerrain"/>) on maps built here: holes, the neighbours' strips, deep water, water
/// under the ground, and damaged files. Each rule has a count that changes when the rule is removed.
/// </summary>
public sealed class MmapTerrainRuleTests : IDisposable
{
    private const int SquaresPerTile = 128 * 128;
    private const int TrianglesPerSquare = 4;
    private const int TileX = 10;
    private const int TileY = 20;

    private readonly TestDirectory _directory = new();

    private string Maps => Path.Combine(_directory.Path, "maps");

    public void Dispose() => _directory.Dispose();

    private MmapMeshData Load(int tileX = TileX, int tileY = TileY, bool skipLiquid = false)
    {
        var mesh = new MmapMeshData();
        new MmapTerrain(Maps, skipLiquid).Load(0, tileX, tileY, mesh);
        return mesh;
    }

    [Fact]
    public void Holes_RemoveTheirTriangles()
    {
        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build());
        Assert.Equal(SquaresPerTile * TrianglesPerSquare, Load().SolidTris.Count / 3);

        // One hole bit is 2 x 2 squares; a full word is the 8 x 8 squares of a cell.
        var oneBit = new ushort[256];
        oneBit[0] = 0x0001;
        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build(oneBit));
        Assert.Equal((SquaresPerTile - 4) * TrianglesPerSquare, Load().SolidTris.Count / 3);

        var cell = new ushort[256];
        cell[17] = 0xFFFF;
        cell[255] = 0xFFFF;
        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build(cell));
        Assert.Equal((SquaresPerTile - 128) * TrianglesPerSquare, Load().SolidTris.Count / 3);
    }

    [Fact]
    public void Neighbours_ContributeTheStripFacingTheTile_WhenTheyStoreHeights()
    {
        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build());
        int centre = SquaresPerTile * TrianglesPerSquare;
        Assert.Equal(centre, Load().SolidTris.Count / 3);

        // Each neighbour with heights adds the one row or column of 128 squares that touches the tile.
        MmapMapFile.Write(Maps, 0, TileX + 1, TileY, MmapMapFile.Build());
        Assert.Equal(centre + (128 * TrianglesPerSquare), Load().SolidTris.Count / 3);
        MmapMapFile.Write(Maps, 0, TileX - 1, TileY, MmapMapFile.Build());
        MmapMapFile.Write(Maps, 0, TileX, TileY + 1, MmapMapFile.Build());
        MmapMapFile.Write(Maps, 0, TileX, TileY - 1, MmapMapFile.Build());
        Assert.Equal(centre + (4 * 128 * TrianglesPerSquare), Load().SolidTris.Count / 3);

        // A neighbour that stores no heights adds nothing.
        MmapMapFile.Write(Maps, 0, TileX - 1, TileY, MmapMapFile.Build(noHeights: true));
        Assert.Equal(centre + (3 * 128 * TrianglesPerSquare), Load().SolidTris.Count / 3);

        // Without the tile itself there is no mesh, whatever its neighbours hold.
        File.Delete(Path.Combine(Maps, $"000{TileY:D2}{TileX:D2}.map"));
        Assert.Empty(Load().SolidTris);
    }

    [Fact]
    public void DeepWater_RemovesTheTerrainAndTheLiquid()
    {
        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build(liquid: (0x10, 50f)));

        MmapMeshData mesh = Load();

        Assert.Empty(mesh.SolidTris);
        Assert.Empty(mesh.LiquidTris);
    }

    [Fact]
    public void Water_IsKeptAboveTheGround_AndDroppedBelowIt()
    {
        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build(liquid: (0x08, MmapMapFile.Height + 10f)));
        MmapMeshData above = Load();
        Assert.Equal(SquaresPerTile * TrianglesPerSquare, above.SolidTris.Count / 3);
        Assert.Equal(SquaresPerTile * 2, above.LiquidTris.Count / 3);
        Assert.All(above.LiquidType, type => Assert.Equal(MmapArea.Water, type));

        // Liquid wholly under the terrain is not part of the mesh; the terrain stays.
        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build(liquid: (0x08, MmapMapFile.Height - 5f)));
        MmapMeshData below = Load();
        Assert.Equal(SquaresPerTile * TrianglesPerSquare, below.SolidTris.Count / 3);
        Assert.Empty(below.LiquidTris);

        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build(liquid: (0x04, MmapMapFile.Height + 10f)));
        Assert.All(Load().LiquidType, type => Assert.Equal(MmapArea.Slime, type));
        MmapMapFile.Write(Maps, 0, TileX, TileY, MmapMapFile.Build(liquid: (0x01, MmapMapFile.Height + 10f)));
        Assert.All(Load().LiquidType, type => Assert.Equal(MmapArea.Magma, type));
    }

    [Fact]
    public void DamagedFiles_AreInvalidData_AtEveryCutPoint()
    {
        var holes = new ushort[256];
        byte[] whole = MmapMapFile.Build(holes, (0x08, 20f));

        // Inside the header, inside the height block's own header (flags, grid height, grid maximum), in the samples, in the
        // liquid block and in the holes: every one is reported as invalid data, never as an index error.
        int[] cuts = [0, 20, 39, 41, 44, 47, 49, 50, 53, 55, 56, 100, 40 + 16 + 5000, whole.Length - 512 - 10, whole.Length - 512 + 1, whole.Length - 1];
        foreach (int cut in cuts)
        {
            MmapMapFile.Write(Maps, 0, TileX, TileY, whole[..cut]);
            InvalidDataException e = Assert.Throws<InvalidDataException>(() => Load());
            Assert.Contains("000" + TileY.ToString("D2") + TileX.ToString("D2") + ".map", e.Message, StringComparison.Ordinal);
        }

        MmapMapFile.Write(Maps, 0, TileX, TileY, whole);
        Assert.NotEmpty(Load().SolidTris);
    }

    [Theory]
    [InlineData(0xFFFFFFFCu)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x80000000u)]
    [InlineData(0x7FFFFFFCu)]
    public void AHugeHeightOffset_IsInvalidData(uint offset)
    {
        byte[] file = MmapMapFile.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), offset);
        MmapMapFile.Write(Maps, 0, TileX, TileY, file);

        Assert.Throws<InvalidDataException>(() => Load());
    }

    [Fact]
    public void AHugeLiquidOrHolesOffset_IsInvalidData()
    {
        byte[] liquid = MmapMapFile.Build(liquid: (0x08, 20f));
        BinaryPrimitives.WriteUInt32LittleEndian(liquid.AsSpan(24), 0xFFFFFFF8u);
        MmapMapFile.Write(Maps, 0, TileX, TileY, liquid);
        Assert.Throws<InvalidDataException>(() => Load());

        byte[] holes = MmapMapFile.Build(new ushort[256]);
        BinaryPrimitives.WriteUInt32LittleEndian(holes.AsSpan(32), 0xFFFFFFF8u);
        MmapMapFile.Write(Maps, 0, TileX, TileY, holes);
        Assert.Throws<InvalidDataException>(() => Load());
    }
}

/// <summary>The extractor's handling of damaged inputs, stale tiles and sub-tile failures, on a synthetic server data root.</summary>
public sealed class MmapExtractorRobustnessTests(MmapWorld world) : IClassFixture<MmapWorld>
{
    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    /// <summary>A private copy of the shared world's maps, vmaps and mmaps.</summary>
    private TestDirectory Copy()
    {
        var copy = new TestDirectory();
        foreach (string folder in new[] { "maps", "vmaps", "mmaps" })
        {
            CopyFolder(Path.Combine(world.DataRoot, folder), Path.Combine(copy.Path, folder));
        }

        return copy;
    }

    private static Dictionary<string, byte[]> Files(string directory)
        => Directory.EnumerateFiles(directory).ToDictionary(f => Path.GetFileName(f)!, File.ReadAllBytes, StringComparer.Ordinal);

    [Fact]
    public void Extract_RebuildsTheTilesOfAMapWhoseOriginMoved_AndKeepsThemOtherwise()
    {
        using TestDirectory copy = Copy();
        string mmaps = Path.Combine(copy.Path, "mmaps");
        string tilePath = Path.Combine(mmaps, "0004931.mmtile");
        byte[] before = File.ReadAllBytes(tilePath);
        NavMeshTile old = NavMeshTile.ParseFile(before);

        // A new terrain tile with a higher index moves the map's origin: the tile on disk was built against the old one.
        File.Copy(Path.Combine(copy.Path, "maps", "0004931.map"), Path.Combine(copy.Path, "maps", "0005031.map"));
        MmapExtractReport report = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 2, CancellationToken.None);

        MmapExtractMapReport azeroth = Assert.Single(report.Maps);
        Assert.Equal(0, azeroth.Existing);
        Assert.Equal(2, azeroth.Written);
        string problem = Assert.Single(report.Problems);
        Assert.Contains("0.mmap", problem, StringComparison.Ordinal);
        Assert.Contains("rebuilt", problem, StringComparison.Ordinal);
        NavMeshTile rebuilt = NavMeshTile.ParseFile(File.ReadAllBytes(tilePath));
        Assert.NotEqual((old.X, old.Y), (rebuilt.X, rebuilt.Y));

        // The result is what a run from scratch writes.
        using var fresh = new TestDirectory();
        new MmapExtractor(copy.Path, fresh.Path).Extract([0], overwrite: true, quick: false, threads: 2, CancellationToken.None);
        Dictionary<string, byte[]> expected = Files(fresh.Path);
        Dictionary<string, byte[]> actual = Files(mmaps);
        foreach ((string name, byte[] bytes) in expected)
        {
            Assert.True(actual.TryGetValue(name, out byte[]? got) && bytes.AsSpan().SequenceEqual(got), $"{name} is not what a clean run writes");
        }

        // Nothing moved this time: every tile of the map is kept and nothing is reported.
        MmapExtractReport again = new MmapExtractor(copy.Path).Extract([0], overwrite: false, quick: false, threads: 2, CancellationToken.None);
        MmapExtractMapReport kept = Assert.Single(again.Maps);
        Assert.Equal((2, 0), (kept.Existing, kept.Written));
        Assert.Empty(again.Problems);
    }

    [Fact]
    public void Extract_BuildsTheHealthyMapsAfterADamagedTreeOfATilelessMap()
    {
        using TestDirectory copy = Copy();
        string vmaps = Path.Combine(copy.Path, "vmaps");

        // Map 5 is the dungeon's tree cut off: it has no terrain tiles, so its tiles come from the tree.
        byte[] tree = File.ReadAllBytes(Path.Combine(vmaps, "036.vmtree"));
        File.WriteAllBytes(Path.Combine(vmaps, "005.vmtree"), tree[..(tree.Length / 2)]);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract(null, overwrite: false, quick: false, threads: 2, CancellationToken.None);

        Assert.Equal(new uint[] { 0, 5, 36 }, report.Maps.Select(m => m.Id));
        MmapExtractMapReport damaged = report.Maps[1];
        Assert.Equal((0, 0, 1), (damaged.Tiles, damaged.Written, damaged.Failed));
        Assert.Equal((4, 4), (report.Maps[2].Tiles, report.Maps[2].Written + report.Maps[2].Existing));
        string problem = Assert.Single(report.Problems);
        Assert.Contains("005.vmtree", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ReportsADamagedTreeOfAMapWithTerrainAsFailedTiles_AndGoesOn()
    {
        using TestDirectory copy = Copy();
        File.WriteAllBytes(Path.Combine(copy.Path, "vmaps", "000.vmtree"), [1, 2, 3]);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract(null, overwrite: false, quick: false, threads: 2, CancellationToken.None);

        // The tile with a file is kept; the tile without one is built again and fails on the tree.
        Assert.Equal((1, 1), (report.Maps[0].Existing, report.Maps[0].Failed));
        Assert.Contains(report.Problems, p => p.Contains("000.vmtree", StringComparison.Ordinal));
        Assert.Equal(4, report.Maps[^1].Existing);
    }

    [Fact]
    public void ADamagedTerrainFile_FailsItsTileOnly()
    {
        using TestDirectory copy = Copy();
        string map = Path.Combine(copy.Path, "maps", "0004931.map");
        byte[] bytes = File.ReadAllBytes(map);
        File.WriteAllBytes(map, bytes[..50]);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract(null, overwrite: true, quick: false, threads: 2, CancellationToken.None);

        Assert.True(report.Maps[0].Failed >= 1);
        Assert.Contains(report.Problems, p => p.Contains("0004931.map", StringComparison.Ordinal) && p.Contains("cut off", StringComparison.Ordinal));
        Assert.Equal(4, report.Maps[^1].Written);
    }

    [Fact]
    public void ModelDamage_NamesTheModelFile()
    {
        using TestDirectory copy = Copy();
        string[] models = [.. Directory.EnumerateFiles(Path.Combine(copy.Path, "vmaps"), "*.vmo")];
        Assert.NotEmpty(models);
        File.WriteAllBytes(models[0], [9, 9, 9]);

        MmapExtractReport report = new MmapExtractor(copy.Path).Extract(null, overwrite: true, quick: false, threads: 2, CancellationToken.None);

        Assert.Contains(report.Problems, p => p.Contains(Path.GetFileName(models[0]), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Command_CountsTilesWrittenWithAFailedSubTileAsPartial()
    {
        // A 100-floor tower: one column of the tile has more connected walkable layers than Recast can number (63), so the
        // sub-tiles it falls in fail. A single floor beside it meshes, so the tile is written without them.
        const string tower = "World\\wmo\\Test\\Tower.wmo";
        var files = new MemoryClientFiles();
        var root = new SyntheticWmoRoot { WmoId = 79, GroupCount = 1 };
        files.Add(tower, ModelFixtures.WmoRoot(root));
        var group = new SyntheticWmoGroup { GroupId = 602 };
        void Floor(float x, float y, float z)
        {
            ushort first = (ushort)group.Vertices.Count;
            group.Vertices.AddRange([new Vector3(x - 10, y - 10, z), new Vector3(x + 10, y - 10, z), new Vector3(x + 10, y + 10, z), new Vector3(x - 10, y + 10, z)]);
            group.Triangles.Add(new SyntheticTriangle(first, (ushort)(first + 1), (ushort)(first + 2)));
            group.Triangles.Add(new SyntheticTriangle(first, (ushort)(first + 2), (ushort)(first + 3)));
        }

        for (int i = 0; i < 100; i++)
        {
            Floor(-100, -100, i * 1.7f);
        }

        Floor(-100, -60, 0);
        files.Add(tower[..^4] + "_000.wmo", ModelFixtures.WmoGroup(group));
        WmoSpec[] global = [new(tower, 5002, new WorldPoint(0, 20, 0), new WorldPoint(0, 0, 0), new WorldPoint(-300, 0, -300), new WorldPoint(300, 400, 300), 0, 0, 0)];
        files.Add(ClientMapPaths.WdtPath("DeadminesInstance"), SyntheticTerrain.Wdt([], WdtFile.GlobalWmoFlag, global));
        var maps = SyntheticTerrain.Maps(new ClientMap(VmapFixture.Dungeon, "DeadminesInstance", "The Deadmines", 1));
        using var data = new TestDirectory();
        new VmapExtractor(files, maps).Extract(data.Path, null, threads: 1, CancellationToken.None);

        var registry = new CommandRegistry();
        ExtractCommands.Register(registry);
        var services = new AtlasServices();
        services.Add(new AtlasPaths(data.Path, null));
        CommandResult result = await registry.InvokeAsync("extract.mmaps", new JsonObject { ["data"] = data.Path, ["threads"] = 2 }, services, CommandOrigin.Cli, headless: true, CancellationToken.None);

        Assert.True(result.Ok, result.ErrorMessage);
        JsonObject map = Assert.Single(result.Data!["maps"]!.AsArray())!.AsObject();
        string problems = string.Join("\n", result.Data["problems"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(map["partial"]!.GetValue<int>() >= 1, problems);
        Assert.Equal(map["partial"]!.GetValue<int>(), result.Data["partial"]!.GetValue<int>());
        Assert.True(map["written"]!.GetValue<int>() >= map["partial"]!.GetValue<int>(), problems);
        Assert.Equal(0, map["failed"]!.GetValue<int>());
        Assert.Contains("Recast gave up on", problems, StringComparison.Ordinal);
        Assert.Contains("too many layers", problems, StringComparison.Ordinal);
    }

    [Fact]
    public void AnM2sTrianglesAreWoundTheOppositeWayToItsStoredHull()
    {
        using var data = new TestDirectory();
        new VmapFixture().Extractor().Extract(data.Path, null, threads: 1, CancellationToken.None);
        string vmaps = Path.Combine(data.Path, "vmaps");
        var mesh = new MmapMeshData();
        Assert.True(new MmapModels(new MmapModelCache(vmaps), quick: true).Load(0, VmapFixture.Tile.Y, VmapFixture.Tile.X, mesh));

        // The cube doodad is a closed box: which way its stored triangles face (into the box or out of it) is the stored
        // hull's winding. A WMO's triangles are used as stored; an M2's are used the other way round.
        var cache = new MmapModelCache(vmaps);
        (ModelSpawn spawn, _) = cache.Tile(0, VmapFixture.Tile.X, VmapFixture.Tile.Y)!.First(s => s.Spawn.Name.Contains("Cube", StringComparison.Ordinal));
        Assert.Contains(".m2", spawn.Name, StringComparison.OrdinalIgnoreCase);
        WorldModel model = cache.Model(spawn.Name)!;
        Vector3 modelCentre = Vector3.Zero;
        int vertexCount = 0;
        foreach (GroupModel group in model.Groups)
        {
            foreach (Vector3 v in group.Vertices)
            {
                modelCentre += v;
                vertexCount++;
            }
        }

        modelCentre /= vertexCount;
        int storedOutward = 0;
        int storedInward = 0;
        foreach (GroupModel group in model.Groups)
        {
            foreach (MeshTriangle t in group.Triangles)
            {
                Vector3 a = group.Vertices[(int)t.Index0];
                Vector3 b = group.Vertices[(int)t.Index1];
                Vector3 c = group.Vertices[(int)t.Index2];
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), ((a + b + c) / 3) - modelCentre) > 0)
                {
                    storedOutward++;
                }
                else
                {
                    storedInward++;
                }
            }
        }

        Assert.Equal(12, storedOutward + storedInward);
        Assert.True(storedOutward == 0 || storedInward == 0, "the stored hull is not wound one way");

        // Recast space (y, z, x) is a cyclic permutation of world space, so it keeps a triangle's handedness.
        var centre = new Vector3(200, 60, -9300);
        int outward = 0;
        int inward = 0;
        for (int i = 0; i < mesh.SolidTris.Count / 3; i++)
        {
            Vector3 a = Vertex(mesh, mesh.SolidTris[i * 3]);
            Vector3 b = Vertex(mesh, mesh.SolidTris[(i * 3) + 1]);
            Vector3 c = Vertex(mesh, mesh.SolidTris[(i * 3) + 2]);
            if (new[] { a, b, c }.Any(v => Vector3.Distance(v, centre) > 3.6f))
            {
                continue;
            }

            if (Vector3.Dot(Vector3.Cross(b - a, c - a), ((a + b + c) / 3) - centre) > 0)
            {
                outward++;
            }
            else
            {
                inward++;
            }
        }

        Assert.Equal((storedInward, storedOutward), (outward, inward));
    }

    private static Vector3 Vertex(MmapMeshData mesh, int index)
        => new(mesh.SolidVerts[index * 3], mesh.SolidVerts[(index * 3) + 1], mesh.SolidVerts[(index * 3) + 2]);
}

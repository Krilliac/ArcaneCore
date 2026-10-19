using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Deterministic, seeded fuzzers covering all server terrain (<c>.map</c>), static collision (<c>.vmtree</c>,
/// <c>.vmtile</c>, <c>.vmo</c>, <see cref="BihTree"/>, <see cref="WmoLiquid"/>), and navigation mesh (<c>.mmap</c>,
/// <c>.mmtile</c>, <see cref="NavMeshTile"/>, <see cref="NavMeshPathfinder"/>) binary parsers and geometric queries.
/// </summary>
public sealed class MappingFuzzTests
{
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    private static readonly uint[] ExtremeU32 =
    [
        0u, 1u, 2u, 3u, 6u, 7u, 16u, 128u, 129u, 255u, 256u, 0x7FFFu, 0x8000u, 0xFFFFu,
        0x3FFFFFFFu, 0x40000000u, 0x7FFFFFFFu, 0x80000000u, 0xFFFFFFF0u, 0xFFFFFFFFu,
        BitConverter.SingleToUInt32Bits(float.NaN),
        BitConverter.SingleToUInt32Bits(float.PositiveInfinity),
        BitConverter.SingleToUInt32Bits(float.NegativeInfinity),
        BitConverter.SingleToUInt32Bits(-0.0f),
        BitConverter.SingleToUInt32Bits(float.MaxValue),
        BitConverter.SingleToUInt32Bits(float.MinValue),
    ];

    private static readonly ushort[] ExtremeU16 = [0, 1, 2, 3, 6, 7, 127, 128, 255, 256, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF];

    private static readonly byte[] ExtremeU8 = [0x00, 0x01, 0x02, 0x03, 0x06, 0x07, 0x3F, 0x40, 0x7F, 0x80, 0xC0, 0xFE, 0xFF];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OffMeshConnectionPolygon_WithFewerThanTwoVertices_ThrowsInvalidData(byte vertexCount)
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteTile(0, 31, 31, new CellTile(0, 0, 0, 0, 2, 2));
        byte[] bytes = File.ReadAllBytes(fixture.PathOf(NavMeshFormat.TileFileName(0, 31, 31)));

        // Offset of first polygon record: 20B prefix + 100B header + 9 * 12B vertices = 228;
        // inside the 32B dtPoly record, vertCount is at +30 and areaAndType is at +31.
        const int firstPolyOffset = 20 + 100 + (9 * 12);
        bytes[firstPolyOffset + 30] = vertexCount;
        bytes[firstPolyOffset + 31] = (byte)(NavMeshFormat.PolyTypeOffMeshConnection << 6);

        Assert.Throws<InvalidDataException>(() => NavMeshTile.ParseFile(bytes));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TerrainTile_ParseAndQuery_UnderStructuredMutations_NeverEscapes(int seed)
    {
        var rng = new Random(seed);
        byte[][] corpora =
        [
            new MapFileBuilder
            {
                GridArea = 12,
                AreaFlags = Enumerable.Range(0, 256).Select(i => (ushort)(i + 1)).ToArray(),
                GridHeight = -10f,
                GridMaxHeight = 250f,
                V9 = MapFileBuilder.Fill(129 * 129, (i, r, c) => (r * 0.25f) + (c * 0.125f), 129),
                V8 = MapFileBuilder.Fill(128 * 128, (i, r, c) => (r * 0.25f) + (c * 0.125f) + 0.5f, 128),
                Holes = Enumerable.Range(0, 256).Select(i => (ushort)(i % 7 == 0 ? 0x000F : 0)).ToArray(),
                HasLiquid = true,
                LiquidGlobalFlags = (byte)LiquidTypeFlags.Water,
                LiquidGlobalEntry = 5,
                LiquidOffX = 4,
                LiquidOffY = 4,
                LiquidWidth = 8,
                LiquidHeight = 8,
                LiquidFlags = Enumerable.Range(0, 256).Select(i => (byte)((i & 3) == 0 ? LiquidTypeFlags.Water : LiquidTypeFlags.Magma)).ToArray(),
                LiquidEntries = Enumerable.Range(0, 256).Select(i => (ushort)(i + 10)).ToArray(),
                LiquidHeights = Enumerable.Range(0, 64).Select(i => 25f + (i * 0.1f)).ToArray(),
            }.Build(),
            new MapFileBuilder
            {
                GridArea = 99,
                GridHeight = 10f,
                GridMaxHeight = 90f,
                V9 = Enumerable.Range(0, 129 * 129).Select(i => (ushort)(i & 0xFFFF)).ToArray(),
                V8 = Enumerable.Range(0, 128 * 128).Select(i => (ushort)((i * 3) & 0xFFFF)).ToArray(),
                HasLiquid = true,
                LiquidGlobalFlags = (byte)LiquidTypeFlags.Ocean,
                LiquidLevel = 15f,
            }.Build(),
            new MapFileBuilder
            {
                GridArea = 7,
                GridHeight = 0f,
                GridMaxHeight = 50f,
                V9 = Enumerable.Range(0, 129 * 129).Select(i => (byte)(i & 0xFF)).ToArray(),
                V8 = Enumerable.Range(0, 128 * 128).Select(i => (byte)((i * 5) & 0xFF)).ToArray(),
            }.Build(),
            new MapFileBuilder { GridArea = 1, GridHeight = 42f }.Build(),
        ];

        int validParses = 0;
        for (int iteration = 0; iteration < 500; iteration++)
        {
            byte[] baseImage = corpora[iteration % corpora.Length];
            byte[] mutated = MutateBinary(rng, baseImage, benignInterval: 10);
            try
            {
                TerrainTile tile = TerrainTile.Parse(mutated);
                validParses++;
                ExerciseTerrainTileQueries(rng, tile);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"TerrainTile escaped on seed={seed} iteration={iteration} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        Assert.True(validParses >= 40, $"Expected benign mutations to parse cleanly (got {validParses})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void VMapParsersAndQueries_UnderStructuredMutations_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        var liquid = new WmoLiquid(2, 2, new Vector3(-2, -2, 0), 4, [1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4.5f, 5f], [0x01, 0x0F, 0x01, 0x04]);
        WorldModel baseBox = VMapFixture.Box(new Vector3(-4, -4, 0), new Vector3(4, 4, 6));
        var groupWithLiquid = new GroupModel(new Vector3(-4, -4, 0), new Vector3(4, 4, 6), 0x2000, 11, [.. baseBox.Groups[0].Vertices], [.. baseBox.Groups[0].Triangles], liquid: liquid);
        WorldModel richModel = new(77, [baseBox.Groups[0], groupWithLiquid]);
        byte[] vmoBytes = richModel.ToBytes();

        using var tiledFixture = new VMapFixture();
        tiledFixture.Place("BoxA", richModel, new Vector3(100, 100, 0), new Vector3(0, 15, 30), 1.25f);
        tiledFixture.Place("BoxB", baseBox, new Vector3(112, 96, 2), Vector3.Zero, 1.0f, VMapFormat.ModM2);
        tiledFixture.Write(0, tiled: true);
        byte[] tiledTreeBytes = File.ReadAllBytes(tiledFixture.PathOf(VMapFormat.TreeFileName(0)));
        byte[] tileBytes = File.ReadAllBytes(Directory.GetFiles(tiledFixture.Directory, "*.vmtile").Single());

        using var globalFixture = new VMapFixture();
        globalFixture.Place("BoxGlobal", richModel, new Vector3(50, 50, 0));
        globalFixture.Write(0, tiled: false);
        byte[] globalTreeBytes = File.ReadAllBytes(globalFixture.PathOf(VMapFormat.TreeFileName(0)));

        int validModels = 0;
        int validTrees = 0;
        int validTiles = 0;

        for (int iteration = 0; iteration < 500; iteration++)
        {
            byte[] mutatedModel = MutateBinary(rng, vmoBytes, benignInterval: 10);
            try
            {
                WorldModel model = WorldModel.Parse(mutatedModel);
                validModels++;
                ExerciseWorldModelQueries(rng, model);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"WorldModel.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            byte[] sourceTree = (iteration & 1) == 0 ? tiledTreeBytes : globalTreeBytes;
            byte[] mutatedTree = MutateBinary(rng, sourceTree, benignInterval: 10);
            try
            {
                (VMapTree tree, List<(ModelSpawn Spawn, uint Slot)> spawns) = VMapTree.Parse(0, mutatedTree);
                validTrees++;
                var loaded = new List<(ModelInstance Instance, uint Slot)>();
                foreach ((ModelSpawn spawn, uint slot) in spawns)
                {
                    if (tree.IsValidSlot(slot))
                    {
                        loaded.Add((new ModelInstance(spawn, richModel), slot));
                    }
                }

                if (loaded.Count > 0)
                {
                    tree.AddTile((-1, -1), loaded);
                }

                ExerciseVMapTreeQueries(rng, tree);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"VMapTree.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            byte[] mutatedTile = MutateBinary(rng, tileBytes, benignInterval: 10);
            try
            {
                List<(ModelSpawn Spawn, uint Slot)> spawns = VMapTree.ParseTile(mutatedTile);
                validTiles++;
                foreach ((ModelSpawn spawn, _) in spawns)
                {
                    var instance = new ModelInstance(spawn, richModel);
                    float maxDist = 200f;
                    _ = instance.IntersectRay(RandomVector3(rng, 200f), Vector3.Normalize(new Vector3(1, 1, -1)), ref maxDist, stopAtFirstHit: false, ignoreM2: false);
                }
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"VMapTree.ParseTile escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(validModels >= 35, $"Expected benign WorldModel parses (got {validModels})");
        Assert.True(validTrees >= 35, $"Expected benign VMapTree parses (got {validTrees})");
        Assert.True(validTiles >= 35, $"Expected benign VMapTile parses (got {validTiles})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void BihTree_FromRawAndRead_UnderFuzzedNodeGraphs_NeverLoopsOrEscapes(int seed)
    {
        var rng = new Random(seed);
        int validTrees = 0;

        for (int iteration = 0; iteration < 500; iteration++)
        {
            int nodeInts = rng.Next(0, 24) * 3;
            uint[] treeData = new uint[nodeInts];
            for (int i = 0; i < nodeInts; i += 3)
            {
                int axis = rng.Next(0, 5);
                if (axis < 3 && nodeInts >= 6)
                {
                    // Internal node: points to a later or arbitrary index to test cycle/bounds rejection.
                    int child = iteration % 5 == 0 ? Math.Min(nodeInts - 3, i + 3) : rng.Next(-6, nodeInts + 12);
                    treeData[i] = (uint)((axis << 30) | (child & 0x3FFFFFFF));
                    treeData[i + 1] = (uint)BitConverter.SingleToInt32Bits(RandomFloat(rng, -100f, 100f));
                    treeData[i + 2] = (uint)BitConverter.SingleToInt32Bits(RandomFloat(rng, -100f, 100f));
                }
                else
                {
                    // Leaf node.
                    uint start = (uint)(iteration % 5 == 0 ? 0 : rng.Next(0, 16));
                    uint count = (uint)(iteration % 5 == 0 ? 1 : rng.Next(0, 16));
                    treeData[i] = 3u << 30;
                    treeData[i + 1] = start;
                    treeData[i + 2] = count;
                }
            }

            int objCount = rng.Next(0, 16);
            uint[] objects = new uint[objCount];
            for (int i = 0; i < objCount; i++)
            {
                objects[i] = (uint)(iteration % 5 == 0 ? i : rng.Next(0, 32));
            }

            Vector3 low = iteration % 5 == 0 ? new Vector3(-50, -50, -50) : RandomVector3(rng, 200f, allowNonFinite: true);
            Vector3 high = iteration % 5 == 0 ? new Vector3(50, 50, 50) : RandomVector3(rng, 200f, allowNonFinite: true);

            try
            {
                BihTree bih = BihTree.FromRaw(low, high, treeData, objects);
                validTrees++;
                for (int q = 0; q < 8; q++)
                {
                    Vector3 origin = RandomVector3(rng, 100f, allowNonFinite: true);
                    Vector3 dir = RandomVector3(rng, 10f, allowNonFinite: true);
                    float maxDist = RandomFloat(rng, -10f, 500f, allowNonFinite: true);
                    var rayCb = new CountingRayCallback();
                    bih.IntersectRay(origin, dir, ref maxDist, ref rayCb, stopAtFirstHit: (q & 1) == 0);
                    bih.IntersectPoint(origin, _ => { });
                }
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"BihTree.FromRaw escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(validTrees >= 50, $"Expected valid BIH trees to be exercised (got {validTrees})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void NavMesh_ParseAndQueries_UnderStructuredMutations_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        using var fixture = new NavMeshFixture();
        var cellTile = new CellTile(31, 31, -20f, -20f, 4, 4, Size: 4f)
        {
            LinkedSides = [0, 2, 4, 6],
            Height = (x, y) => (x * 0.1f) + (y * 0.05f),
        };
        fixture.WriteParams(0);
        fixture.WriteTile(0, 31, 31, cellTile);
        fixture.WriteTile(0, 31, 32, new CellTile(30, 31, -20f, -36f, 4, 4, Size: 4f) { LinkedSides = [0, 2, 4, 6] });

        byte[] paramsBytes = File.ReadAllBytes(fixture.PathOf(NavMeshFormat.ParamsFileName(0)));
        byte[] tileBytes = File.ReadAllBytes(fixture.PathOf(NavMeshFormat.TileFileName(0, 31, 31)));
        byte[] neighbourBytes = File.ReadAllBytes(fixture.PathOf(NavMeshFormat.TileFileName(0, 31, 32)));
        NavMeshTile validNeighbour = NavMeshTile.ParseFile(neighbourBytes);

        int validParams = 0;
        int validTiles = 0;

        for (int iteration = 0; iteration < 600; iteration++)
        {
            byte[] mutatedParams = MutateBinary(rng, paramsBytes, benignInterval: 10);
            try
            {
                NavMeshParams p = NavMeshParams.Parse(mutatedParams);
                validParams++;
                _ = new NavMesh(0, p);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"NavMeshParams.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            byte[] mutatedTile = MutateBinary(rng, tileBytes, benignInterval: 10);
            try
            {
                NavMeshTile tile = NavMeshTile.ParseFile(mutatedTile);
                validTiles++;
                ExerciseNavMeshTileQueries(rng, tile, validNeighbour);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"NavMeshTile.ParseFile escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Also fuzz NavMeshPathfinder end-to-end across random/extreme/non-finite coordinates and options.
        var pathfinder = new NavMeshPathfinder(fixture.Directory, NullLogger<NavMeshPathfinder>.Instance);
        pathfinder.LoadTile(0, 31, 31);
        pathfinder.LoadTile(0, 31, 32);

        for (int q = 0; q < 200; q++)
        {
            Vector3 start = RandomVector3(rng, 250f, allowNonFinite: q % 7 == 0);
            Vector3 end = RandomVector3(rng, 250f, allowNonFinite: q % 11 == 0);
            var options = new PathOptions
            {
                IncludeFlags = (NavTerrain)rng.Next(0, 0x20),
                ExcludeFlags = (NavTerrain)rng.Next(0, 0x10),
                ExcludeOnlyInSearch = (q & 1) != 0,
                WideEndSearch = (q & 2) != 0,
                AllowPartial = (q & 4) != 0,
                EndTolerance = (q & 64) != 0 ? RandomFloat(rng, 0f, 20f, allowNonFinite: true) : 0f,
                MaxSearchNodes = rng.Next(1, 512),
                MaxPoints = rng.Next(2, 256),
            };

            PathResult path = pathfinder.FindPath(0, start, end, options);
            Assert.NotNull(path.Points);
            PathResult sharedPath = pathfinder.FindPathShared(0, start, end, options);
            Assert.NotNull(sharedPath.Points);
            _ = pathfinder.Raycast(0, start, end, options);
            _ = pathfinder.RaycastShared(0, start, end, options);
            _ = pathfinder.TryGetRandomPointAround(0, start, RandomFloat(rng, -5f, 50f, allowNonFinite: true), rng, options, out _);
            _ = pathfinder.TryGetRandomPointAroundShared(0, start, RandomFloat(rng, -5f, 50f, allowNonFinite: true), rng, options, out _);
            _ = pathfinder.TryGetMeshFloor(0, start.X, start.Y, start.Z, RandomFloat(rng, -5f, 50f, allowNonFinite: true), RandomFloat(rng, -5f, 50f, allowNonFinite: true), out _);
        }

        Assert.True(validParams >= 40, $"Expected benign NavMeshParams parses (got {validParams})");
        Assert.True(validTiles >= 40, $"Expected benign NavMeshTile parses (got {validTiles})");
    }

    private static void ExerciseTerrainTileQueries(Random rng, TerrainTile tile)
    {
        for (int q = 0; q < 8; q++)
        {
            float x = RandomFloat(rng, -20000f, 20000f, allowNonFinite: q == 0);
            float y = RandomFloat(rng, -20000f, 20000f, allowNonFinite: q == 1);
            float z = RandomFloat(rng, -1000f, 1000f, allowNonFinite: q == 2);
            _ = tile.GetHeight(x, y);
            _ = tile.GetAreaFlag(x, y);
            _ = tile.GetLiquidLevel(x, y);
            _ = tile.GetTerrainType(x, y);
            _ = tile.GetLiquidStatus(x, y, z, (LiquidTypeFlags)rng.Next(0, 16), out _);
            _ = TerrainTile.TileOf(x, y);
        }

        int row = rng.Next(0, GridDefines.MapResolution);
        int col = rng.Next(0, GridDefines.MapResolution);
        _ = tile.CornerHeight(row, col);
        _ = tile.CentreHeight(row, col);
        _ = tile.HoleMaskCovers(row, col);
        _ = tile.IsServerHole(row, col);
        _ = tile.TryGetLiquidSquare(row, col, out _, out _, out _);
    }

    private static void ExerciseWorldModelQueries(Random rng, WorldModel model)
    {
        for (int q = 0; q < 6; q++)
        {
            Vector3 origin = RandomVector3(rng, 20f, allowNonFinite: q == 0);
            Vector3 dir = RandomVector3(rng, 5f, allowNonFinite: q == 1);
            float maxDist = RandomFloat(rng, 0.1f, 100f, allowNonFinite: q == 2);
            _ = model.IntersectRay(origin, dir, ref maxDist, stopAtFirstHit: (q & 1) == 0);
            _ = model.TryFindGroup(origin, -Vector3.UnitZ, out _, out _);
        }
    }

    private static void ExerciseVMapTreeQueries(Random rng, VMapTree tree)
    {
        for (int q = 0; q < 6; q++)
        {
            Vector3 a = RandomVector3(rng, 250f, allowNonFinite: q == 0);
            Vector3 b = RandomVector3(rng, 250f, allowNonFinite: q == 1);
            _ = tree.IsInLineOfSight(a, b, ignoreM2: (q & 1) != 0);
            _ = tree.TryGetObjectHit(a, b, RandomFloat(rng, -1f, 1f), out _);
            _ = tree.GetHeight(a, RandomFloat(rng, -50f, 50f, allowNonFinite: q == 2));
            _ = tree.TryGetAreaInfo(a, out _);
        }
    }

    private static void ExerciseNavMeshTileQueries(Random rng, NavMeshTile tile, NavMeshTile neighbour)
    {
        const NavTerrain AllNavTerrain = NavTerrain.Ground | NavTerrain.Water | NavTerrain.Magma | NavTerrain.Slime | NavTerrain.SteepSlopes;
        for (int q = 0; q < 6; q++)
        {
            Vector3 center = RandomVector3(rng, 100f, allowNonFinite: q == 0);
            Vector3 extents = new(MathF.Abs(RandomFloat(rng, 0.1f, 50f)), MathF.Abs(RandomFloat(rng, 0.1f, 50f)), MathF.Abs(RandomFloat(rng, 0.1f, 50f)));
            if (tile.TryFindNearestPoly(center, extents, AllNavTerrain, NavTerrain.Empty, out int poly, out Vector3 nearest, out _))
            {
                _ = tile.ClosestPointOnPoly(poly, nearest, out _);
                _ = tile.GetPolyHeight(poly, nearest);
                _ = tile.Center(poly);
            }
        }

        for (int i = 0; i < Math.Min(tile.Polys.Length, 4); i++)
        {
            Vector3 pos = RandomVector3(rng, 50f);
            _ = tile.ClosestPointOnPoly(i, pos, out _);
            _ = tile.GetPolyHeight(i, pos);
            NavPoly poly = tile.Polys[i];
            for (int e = 0; e < poly.VertexCount; e++)
            {
                if ((poly.Neighbour(e) & NavMeshFormat.ExternalLink) != 0)
                {
                    _ = tile.GetCrossPortals(i, e, neighbour);
                }
            }
        }
    }

    private static byte[] MutateBinary(Random rng, byte[] source, int benignInterval)
    {
        if (rng.Next(benignInterval) == 0)
        {
            return (byte[])source.Clone();
        }

        int mode = rng.Next(8);
        switch (mode)
        {
            case 0: // Truncate
            {
                int len = rng.Next(0, source.Length);
                return source[..len];
            }

            case 1: // Bit flips (1..6 bytes)
            {
                byte[] copy = (byte[])source.Clone();
                int flips = rng.Next(1, 7);
                for (int i = 0; i < flips && copy.Length > 0; i++)
                {
                    copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
                }

                return copy;
            }

            case 2: // Extreme u32 overwrite (1..3 locations)
            {
                byte[] copy = (byte[])source.Clone();
                int count = rng.Next(1, 4);
                for (int i = 0; i < count && copy.Length >= 4; i++)
                {
                    int offset = rng.Next(0, copy.Length - 3);
                    BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), ExtremeU32[rng.Next(ExtremeU32.Length)]);
                }

                return copy;
            }

            case 3: // Extreme u16 overwrite (1..4 locations)
            {
                byte[] copy = (byte[])source.Clone();
                int count = rng.Next(1, 5);
                for (int i = 0; i < count && copy.Length >= 2; i++)
                {
                    int offset = rng.Next(0, copy.Length - 1);
                    BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(offset), ExtremeU16[rng.Next(ExtremeU16.Length)]);
                }

                return copy;
            }

            case 4: // Extreme u8 overwrite (1..6 locations)
            {
                byte[] copy = (byte[])source.Clone();
                int count = rng.Next(1, 7);
                for (int i = 0; i < count && copy.Length > 0; i++)
                {
                    copy[rng.Next(copy.Length)] = ExtremeU8[rng.Next(ExtremeU8.Length)];
                }

                return copy;
            }

            case 5: // Splice out a middle slice
            {
                if (source.Length < 4)
                {
                    return [];
                }

                int cutAt = rng.Next(0, source.Length - 1);
                int cutLen = rng.Next(1, Math.Min(64, source.Length - cutAt));
                byte[] result = new byte[source.Length - cutLen];
                Buffer.BlockCopy(source, 0, result, 0, cutAt);
                Buffer.BlockCopy(source, cutAt + cutLen, result, cutAt, source.Length - cutAt - cutLen);
                return result;
            }

            case 6: // Append junk tail
            {
                int extra = rng.Next(1, 48);
                byte[] result = new byte[source.Length + extra];
                Buffer.BlockCopy(source, 0, result, 0, source.Length);
                rng.NextBytes(result.AsSpan(source.Length));
                return result;
            }

            default: // Pure random bytes
            {
                byte[] noise = new byte[rng.Next(0, 256)];
                rng.NextBytes(noise);
                return noise;
            }
        }
    }

    private static float RandomFloat(Random rng, float min, float max, bool allowNonFinite = false)
    {
        if (allowNonFinite)
        {
            switch (rng.Next(16))
            {
                case 0: return float.NaN;
                case 1: return float.PositiveInfinity;
                case 2: return float.NegativeInfinity;
                case 3: return float.MaxValue;
                case 4: return float.MinValue;
            }
        }

        return min + ((float)rng.NextDouble() * (max - min));
    }

    private static Vector3 RandomVector3(Random rng, float range, bool allowNonFinite = false)
        => new(
            RandomFloat(rng, -range, range, allowNonFinite),
            RandomFloat(rng, -range, range, allowNonFinite),
            RandomFloat(rng, -range, range, allowNonFinite));

    private struct CountingRayCallback : IRayPrimitiveCallback
    {
        public int Invocations;

        public bool Invoke(int primitive, ref float maxDistance)
        {
            Invocations++;
            return false;
        }
    }
}

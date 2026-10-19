using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Host.Cli;
using ArcaneCore.Atlas.Live;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Atlas.Render;
using ArcaneCore.Atlas.Tests.Host;
using ArcaneCore.Atlas.Tests.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Reference.Navmesh;
using DotRecast.Detour;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Fuzz;

/// <summary>
/// Deterministic, seeded fuzzers covering all <c>arcane-atlas</c> binary formats (<c>ATG1</c>, MPQ, PKWARE implode,
/// MPQ sparse, WDBC, ADT, WDT, WMO root/group, M2, <c>.mmap</c>/<c>.mmtile</c>), live NDJSON frame parsing,
/// CLI/script/view-state parsers, MCP JSON-RPC framing, ray picking, and software rasterization + PNG encoding.
/// </summary>
public sealed class AtlasFuzzTests
{
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    private static readonly uint[] ExtremeU32 =
    [
        0u, 1u, 2u, 3u, 6u, 7u, 17u, 18u, 255u, 256u, 0x7FFFu, 0x8000u, 0xFFFFu,
        0x3FFFFFFFu, 0x40000000u, 0x7FFFFFFFu, 0x80000000u, 0xFFFFFFF0u, 0xFFFFFFFFu,
        BitConverter.SingleToUInt32Bits(float.NaN),
        BitConverter.SingleToUInt32Bits(float.PositiveInfinity),
        BitConverter.SingleToUInt32Bits(float.NegativeInfinity),
        BitConverter.SingleToUInt32Bits(-0.0f),
        BitConverter.SingleToUInt32Bits(float.MaxValue),
        BitConverter.SingleToUInt32Bits(float.MinValue),
    ];

    private static readonly ushort[] ExtremeU16 = [0, 1, 2, 3, 6, 7, 127, 128, 255, 256, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF];

    private static readonly byte[] ExtremeU8 = [0x00, 0x01, 0x02, 0x03, 0x06, 0x07, 0x20, 0x40, 0x7F, 0x80, 0xFE, 0xFF];

    [Fact]
    public void GeometryWire_Read_WithExtremeHeaderCountsNonFiniteOriginOrMalformedJson_ThrowsInvalidData()
    {
        TileKey tile = new(0, 49, 31);
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.Terrain, tile, origin);
        uint v0 = builder.AddVertex(origin.X - 1, origin.Y - 1, 10);
        uint v1 = builder.AddVertex(origin.X - 2, origin.Y - 1, 11);
        uint v2 = builder.AddVertex(origin.X - 1, origin.Y - 2, 12);
        builder.AddTriangle(v0, v1, v2, attribute: 1);
        builder.AddPoint(v0, attribute: 2);
        byte[] valid = GeometryWire.Write(builder.Build());

        // 1. Extreme counts that previously overflowed checked((int)...) or checked(count * 3).
        foreach (int countOffset in new[] { 32, 36, 40, 44 })
        {
            foreach (uint badCount in new[] { 0xFFFFFFFFu, 0x80000000u, 0x7FFFFFFFu, 0x40000000u })
            {
                byte[] copy = (byte[])valid.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(countOffset), badCount);
                Assert.Throws<InvalidDataException>(() => GeometryWire.Read(copy));
            }
        }

        // 2. Non-finite Origin coordinates at offsets 8, 16, 24.
        foreach (int originOffset in new[] { 8, 16, 24 })
        {
            foreach (double badCoord in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                byte[] copy = (byte[])valid.Clone();
                BinaryPrimitives.WriteDoubleLittleEndian(copy.AsSpan(originOffset), badCoord);
                Assert.Throws<InvalidDataException>(() => GeometryWire.Read(copy));
            }
        }

        // 3. Corrupt JSON trailers.
        string[] badJsonTrailers =
        [
            "{not valid json",
            "[]",
            "\"string\"",
            "{}",
            """{"layer":123,"tile":[0,49,31]}""",
            """{"layer":"terrain","tile":[0,49]}""",
            """{"layer":"terrain","tile":["bad",49,31]}""",
            """{"layer":"terrain","tile":[0,49,31],"attributes":{"not_a_uint":{}}}""",
            """{"layer":"terrain","tile":[0,49,31],"attributes":{"1":{},"1":{}}}""",
            """{"layer":"terrain","tile":[0,49,31],"attributes":[1,2,3]}""",
            """{"layer":"terrain","tile":[0,49,31],"stats":[1,2,3]}""",
            """{"layer":"terrain","tile":[0,49,31],"part":"bad"}""",
        ];

        var emptyBuilder = new GeometryBuilder(LayerIds.Terrain, tile, origin);
        byte[] emptyWire = GeometryWire.Write(emptyBuilder.Build());
        foreach (string trailer in badJsonTrailers)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(trailer);
            byte[] packet = new byte[GeometryWire.HeaderBytes + 4 + utf8.Length];
            Buffer.BlockCopy(emptyWire, 0, packet, 0, GeometryWire.HeaderBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(GeometryWire.HeaderBytes), (uint)utf8.Length);
            Buffer.BlockCopy(utf8, 0, packet, GeometryWire.HeaderBytes + 4, utf8.Length);
            Assert.Throws<InvalidDataException>(() => GeometryWire.Read(packet));
        }
    }

    [Fact]
    public void WmoGroupFile_Parse_WithExtremeMliqDimensions_ThrowsInvalidData()
    {
        var group = new SyntheticWmoGroup
        {
            GroupId = 1,
            LiquidHeights = [1f, 2f, 3f, 4f],
        };
        group.Vertices.AddRange([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)]);
        group.Triangles.Add(new SyntheticTriangle(0, 1, 2));
        byte[] valid = ModelFixtures.WmoGroup(group);

        // Locate MLIQ tag ("QILM" reversed on disk) and write int.MaxValue into xverts and yverts.
        ReadOnlySpan<byte> tag = "QILM"u8;
        int mliqAt = valid.AsSpan().IndexOf(tag);
        Assert.True(mliqAt >= 0, "Expected MLIQ chunk in synthetic WMO group");
        int payloadAt = mliqAt + 8;

        byte[] overflowVerts = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(overflowVerts.AsSpan(payloadAt), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(overflowVerts.AsSpan(payloadAt + 4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse(overflowVerts));

        byte[] overflowTiles = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(overflowTiles.AsSpan(payloadAt + 8), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(overflowTiles.AsSpan(payloadAt + 12), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => WmoGroupFile.Parse(overflowTiles));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MmapTileReader_Parse_OffMeshConnectionWithFewerThanTwoVertices_ThrowsInvalidData(byte vertCount)
    {
        Vector3 min = SyntheticNav.TileMinRecast(32, 32);
        var conn = new OffMeshSpec(min + new Vector3(20, 10, 20), min + new Vector3(200, 10, 200), 2f, Bidirectional: true, UserId: 1);
        BuiltTile built = SyntheticNav.Build(new TileSpec
        {
            TerrainX = 32,
            TerrainY = 32,
            Cells = 2,
            OffMesh = [conn],
        });

        // Off-mesh polygon is the last polygon record (index PolyCount, 32 bytes per record).
        byte[] copy = (byte[])built.File.Clone();
        int offMeshPolyAt = built.Layout.Polys + (built.PolyCount * 32);
        copy[offMeshPolyAt + 30] = vertCount;

        Assert.Throws<InvalidDataException>(() => MmapTileReader.Parse(copy));
    }

    [Fact]
    public void MmapTileReader_Parse_InvertedOrNonFiniteHeaderBounds_ThrowsInvalidData()
    {
        BuiltTile built = SyntheticNav.Build(new TileSpec { TerrainX = 32, TerrainY = 32, Cells = 2, BvTree = true });

        // dtMeshHeader starts at offset 20; bmin is at +72..+83, bmax is at +84..+95, bvQuantFactor is at +96.
        byte[] invertedBmin = (byte[])built.File.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(invertedBmin.AsSpan(20 + 76), 1000f);
        BinaryPrimitives.WriteSingleLittleEndian(invertedBmin.AsSpan(20 + 88), 0f);
        Assert.Throws<InvalidDataException>(() => MmapTileReader.Parse(invertedBmin));

        byte[] nanBmax = (byte[])built.File.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(nanBmax.AsSpan(20 + 84), float.NaN);
        Assert.Throws<InvalidDataException>(() => MmapTileReader.Parse(nanBmax));

        byte[] negativeQuant = (byte[])built.File.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(negativeQuant.AsSpan(20 + 96), -1f);
        Assert.Throws<InvalidDataException>(() => MmapTileReader.Parse(negativeQuant));
    }

    [Fact]
    public void ViewState_And_FrameParser_WithUnpairedSurrogatesOrDuplicateKeys_ReportCleanErrors()
    {
        byte[] surrogateHello = """{"op":"hello","protocol":1,"sha":"\uD800"}"""u8.ToArray();
        var helloObj = (JsonObject)JsonNode.Parse(surrogateHello)!;
        Assert.False(FrameParser.TryParseHello(helloObj, out _, out string? helloProblem));
        Assert.False(string.IsNullOrWhiteSpace(helloProblem));

        byte[] duplicateFrame = """{"op":"frame","seq":1,"map":0,"clockMs":10,"counters":{"a":1,"a":2},"entities":[]}"""u8.ToArray();
        var frameObj = (JsonObject)JsonNode.Parse(duplicateFrame)!;
        Assert.False(FrameParser.TryParseFrame(frameObj, 100L, out _, out string? frameProblem));
        Assert.False(string.IsNullOrWhiteSpace(frameProblem));

        byte[] surrogateView = """{"map":0,"camera":{"target":[0,0,0],"distance":100,"yaw":0,"pitch":30,"fov":60,"mode":"\uD800"},"layers":{},"compare":"off","split":0.5,"loadRadius":1,"theme":"dark","hud":true}"""u8.ToArray();
        var viewObj = (JsonObject)JsonNode.Parse(surrogateView)!;
        Assert.Throws<CommandException>(() => ViewState.FromJson(viewObj));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeometryWire_And_ChunkPicker_UnderStructuredMutations_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        TileKey tile = new(0, 49, 31);
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.Terrain, tile, origin, part: 1);
        uint v0 = builder.AddVertex(origin.X - 10, origin.Y - 10, 50);
        uint v1 = builder.AddVertex(origin.X - 20, origin.Y - 10, 52);
        uint v2 = builder.AddVertex(origin.X - 10, origin.Y - 20, 54);
        uint v3 = builder.AddVertex(origin.X - 20, origin.Y - 20, 56);
        builder.AddTriangle(v0, v1, v2, attribute: 1);
        builder.AddTriangle(v1, v3, v2, attribute: 2);
        builder.AddPoint(v3, attribute: 3);
        builder.DescribeAttribute(1, () => new JsonObject { ["colorKey"] = ColorKeys.Ground });
        builder.DescribeAttribute(2, () => new JsonObject { ["colorKey"] = ColorKeys.Steep });
        builder.DescribeAttribute(3, () => new JsonObject { ["note"] = "point" });
        builder.Stats["triangles"] = 2;
        byte[] validWire = GeometryWire.Write(builder.Build());

        int validReads = 0;
        for (int iteration = 0; iteration < 500; iteration++)
        {
            byte[] mutated = MutateBinary(rng, validWire, benignInterval: 8);
            try
            {
                GeometryChunk chunk = GeometryWire.Read(mutated);
                validReads++;
                byte[] reencoded = GeometryWire.Write(chunk);
                Assert.NotEmpty(reencoded);

                for (int q = 0; q < 4; q++)
                {
                    WorldPoint rayOrigin = new(
                        chunk.Origin.X + ((rng.NextDouble() - 0.5) * 60),
                        chunk.Origin.Y + ((rng.NextDouble() - 0.5) * 60),
                        chunk.Origin.Z + ((rng.NextDouble() - 0.5) * 60));
                    WorldPoint dir = RandomUnitWorldPoint(rng);
                    _ = ChunkPicker.Nearest(chunk, rayOrigin, dir, maxDistance: 500.0, includeLinesAndPoints: (q & 1) == 0);
                }
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"GeometryWire.Read escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(validReads >= 45, $"Expected benign ATG1 mutations to parse (got {validReads})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void MpqArchive_Codecs_And_Wdbc_UnderStructuredMutations_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        using var dir = new TestDirectory();
        string mpqPath = dir.NewName(".MPQ");

        byte[] filePayloadA = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ArcaneCore MPQ fuzz payload ", 80)));
        byte[] filePayloadB = new byte[512];
        rng.NextBytes(filePayloadB);
        byte[] validMpq = MpqTestWriter.Build(
            [
                new TestMpqFile("DBFilesClient\\Map.dbc", filePayloadA) { Zlib = true, Encrypted = true, FixKey = true, Checksums = ChecksumTableForm.Plain },
                new TestMpqFile("World\\Maps\\Azeroth\\Azeroth.wdt", filePayloadB) { Zlib = true, SingleUnit = true },
            ],
            new TestMpqOptions { FormatVersion = 1, UserData = true, SectorShift = 2 });

        var wdbcBuilder = new WdbcTestBuilder(11);
        wdbcBuilder.Row([(0, 0u), (1, wdbcBuilder.String("Azeroth")), .. wdbcBuilder.Localized(2, "Eastern Kingdoms")]);
        wdbcBuilder.Row([(0, 1u), (1, wdbcBuilder.String("Kalimdor")), .. wdbcBuilder.Localized(2, "Kalimdor")]);
        byte[] validWdbc = wdbcBuilder.Build();

        int validArchives = 0;
        int validTables = 0;
        Span<byte> output = stackalloc byte[128];

        for (int iteration = 0; iteration < 350; iteration++)
        {
            byte[] mutatedMpq = MutateBinary(rng, validMpq, benignInterval: 8);
            File.WriteAllBytes(mpqPath, mutatedMpq);
            try
            {
                using MpqArchive archive = MpqArchive.Open(mpqPath, verifyChecksums: (iteration & 1) == 0);
                validArchives++;
                _ = archive.Contains("DBFilesClient\\Map.dbc");
                if (archive.Contains("DBFilesClient\\Map.dbc"))
                {
                    _ = archive.Read("DBFilesClient\\Map.dbc");
                }

                if (archive.Contains("World\\Maps\\Azeroth\\Azeroth.wdt"))
                {
                    _ = archive.Read("World\\Maps\\Azeroth\\Azeroth.wdt");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"MpqArchive escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            // Fuzz MpqCompression, PkwareExplode, and MpqSparse directly.
            byte[] randomSector = new byte[rng.Next(0, 96)];
            rng.NextBytes(randomSector);
            try
            {
                MpqCompression.Decompress(randomSector, output, formatVersion: iteration & 1);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
            }

            try
            {
                _ = PkwareExplode.Decompress(randomSector, output);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
            }

            try
            {
                _ = MpqSparse.Decompress(randomSector, output);
            }
            catch (InvalidDataException)
            {
            }

            byte[] mutatedWdbc = MutateBinary(rng, validWdbc, benignInterval: 8);
            try
            {
                WdbcFile table = WdbcFile.Parse(mutatedWdbc);
                validTables++;
                for (int r = 0; r < Math.Min(table.RecordCount, 4); r++)
                {
                    for (int f = 0; f < Math.Min(table.FieldCount, 4); f++)
                    {
                        _ = table.GetUInt32(r, f);
                        _ = table.GetString(r, f);
                    }

                    if (table.FieldCount >= 11)
                    {
                        _ = table.GetLocalizedString(r, 2);
                    }
                }
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"WdbcFile escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(validArchives >= 30, $"Expected benign MPQ archives to open (got {validArchives})");
        Assert.True(validTables >= 30, $"Expected benign WDBC tables to parse (got {validTables})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ClientTerrain_Adt_And_Wdt_UnderStructuredMutations_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        var adtSpec = new AdtSpec
        {
            Tile = new TileKey(0, 49, 31),
            BaseHeight = (r, c) => 50f + r + c,
            Outer = (_, _, y, x) => (y * 0.25f) + (x * 0.125f),
            Inner = (_, _, y, x) => (y * 0.25f) + (x * 0.125f) + 0.1f,
        };
        adtSpec.Textures.Add("Tileset\\Elwynn\\ElwynnGrass.blp");
        adtSpec.Doodads.Add(new DoodadSpec("World\\Generic\\Human\\PassiveDoodads\\LampPost.mdx", 101, new WorldPoint(16000, 80, 17000), new WorldPoint(0, 45, 0), 1024, 0));
        adtSpec.Wmos.Add(new WmoSpec("World\\wmo\\Azeroth\\Buildings\\GoldshireInn\\GoldshireInn.wmo", 202, new WorldPoint(16050, 82, 17050), new WorldPoint(0, 90, 0), new WorldPoint(16000, 70, 17000), new WorldPoint(16100, 110, 17100), 0, 0, 0));
        adtSpec.Holes[0] = 0x000F;
        adtSpec.Areas[0] = 87;
        adtSpec.Liquid[0] = [new LiquidLayerSpec(AdtLiquidKind.River, 40f, 42f, (y, x) => 41f + ((x + y) * 0.05f), (y, x) => (byte)((x == 0 && y == 0) ? AdtLiquidLayer.HiddenSquare : 0x04))];
        byte[] validAdt = SyntheticTerrain.Adt(adtSpec).Bytes;

        byte[] validWdt = SyntheticTerrain.Wdt(
            [(49, 31), (48, 31)],
            flags: 0,
            wmos: [new WmoSpec("World\\wmo\\Dungeon\\Deadmines\\Deadmines.wmo", 1, new WorldPoint(100, 20, 100), default, new WorldPoint(0, 0, 0), new WorldPoint(200, 50, 200), 0, 0, 0)]);

        int parsedAdts = 0;
        int parsedWdts = 0;

        for (int iteration = 0; iteration < 350; iteration++)
        {
            byte[] mutatedAdt = MutateBinary(rng, validAdt, benignInterval: 8);
            try
            {
                AdtFile adt = AdtFile.Parse(mutatedAdt);
                parsedAdts++;
                Assert.Equal(256, adt.Chunks.Count);
                AdtChunk c0 = adt.Chunks[0];
                _ = c0.OuterHeight(0, 0);
                _ = c0.InnerHeight(0, 0);
                _ = c0.IsHoleSquare(0, 0);
                foreach (AdtLiquidLayer layer in c0.LiquidLayers)
                {
                    _ = layer.Height(0, 0);
                    _ = layer.SquareFlags(0, 0);
                    _ = layer.IsVisible(0, 0);
                }
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"AdtFile.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            byte[] mutatedWdt = MutateBinary(rng, validWdt, benignInterval: 8);
            try
            {
                WdtFile wdt = WdtFile.Parse(mutatedWdt);
                parsedWdts++;
                _ = wdt.HasTile(49, 31);
                _ = wdt.HasTile(0, 0);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"WdtFile.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(parsedAdts >= 30, $"Expected benign ADTs to parse (got {parsedAdts})");
        Assert.True(parsedWdts >= 30, $"Expected benign WDTs to parse (got {parsedWdts})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ClientModels_WmoRoot_WmoGroup_M2_And_Transforms_UnderStructuredMutations_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        var rootSpec = new SyntheticWmoRoot { Version = 17, WmoId = 77, GroupCount = 2 };
        rootSpec.GroupNames.AddRange(["exterior", "interior"]);
        rootSpec.DoodadSets.Add(("Default", 0, 1));
        rootSpec.Doodads.Add(("World\\Generic\\Chair.mdx", new Vector3(1, 2, 3), Quaternion.Identity, 1.0f));
        byte[] validRoot = ModelFixtures.WmoRoot(rootSpec);

        var groupSpec = new SyntheticWmoGroup
        {
            GroupId = 10,
            Flags = 0x2008,
            LiquidHeights = [10f, 10.5f, 11f, 11.5f],
        };
        groupSpec.Vertices.AddRange([new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 4, 0), new Vector3(0, 4, 2)]);
        groupSpec.Triangles.AddRange([new SyntheticTriangle(0, 1, 2, 0x20, 1), new SyntheticTriangle(0, 2, 3, 0x04, 0xFF)]);
        groupSpec.DoodadReferences.Add(0);
        byte[] validGroup = ModelFixtures.WmoGroup(groupSpec);

        byte[] validM2 = ModelFixtures.M2(
            vertices: [new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 2)],
            viewLookup: [0, 1, 2],
            viewTriangles: [0, 1, 2],
            boundingVertices: [new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 2)],
            boundingIndices: [0, 1, 2]);

        int validRoots = 0;
        int validGroups = 0;
        int validM2s = 0;

        for (int iteration = 0; iteration < 400; iteration++)
        {
            byte[] mutatedRoot = MutateBinary(rng, validRoot, benignInterval: 8);
            try
            {
                WmoRootFile root = WmoRootFile.Parse(mutatedRoot);
                validRoots++;
                _ = root.GroupNameAt(0);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"WmoRootFile.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            byte[] mutatedGroup = MutateBinary(rng, validGroup, benignInterval: 8);
            try
            {
                WmoGroupFile grp = WmoGroupFile.Parse(mutatedGroup);
                validGroups++;
                _ = grp.TriangleCount;
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"WmoGroupFile.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            byte[] mutatedM2 = MutateBinary(rng, validM2, benignInterval: 8);
            try
            {
                M2ModelFile m2 = M2ModelFile.Parse(mutatedM2);
                validM2s++;
                _ = m2.BoundingTriangleCount;
                _ = m2.RenderTriangleCount;
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"M2ModelFile.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            // Fuzz PlacementTransform and ReferenceModelQueries geometric helpers.
            var pos = new WorldPoint((rng.NextDouble() - 0.5) * 30000, (rng.NextDouble() - 0.5) * 2000, (rng.NextDouble() - 0.5) * 30000);
            var rot = new WorldPoint((rng.NextDouble() - 0.5) * 720, (rng.NextDouble() - 0.5) * 720, (rng.NextDouble() - 0.5) * 720);
            var placement = new ClientPlacement(PlacementKind.M2, "Test.m2", 1, pos, rot, rng.NextDouble() * 2048, 0, 0, new TileKey(0, 32, 32));
            InstanceTransform tf = PlacementTransform.ForM2(placement);
            _ = tf.ModelToWorld(new Vec3d(1, 2, 3));
            _ = PlacementTransform.ForWmo(placement with { Kind = PlacementKind.Wmo }, out _);
            var q = new Quaternion((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5));
            _ = PlacementTransform.ExtractorDoodadRotation(q, rot);
        }

        Assert.True(validRoots >= 35, $"Expected benign WMO roots to parse (got {validRoots})");
        Assert.True(validGroups >= 35, $"Expected benign WMO groups to parse (got {validGroups})");
        Assert.True(validM2s >= 35, $"Expected benign M2 models to parse (got {validM2s})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ReferenceNavmesh_MmapParams_And_MmapTile_UnderStructuredMutations_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        byte[] validParams = SyntheticNav.BuildParams(maxTiles: 4096);
        Vector3 min = SyntheticNav.TileMinRecast(32, 32);
        var conn = new OffMeshSpec(min + new Vector3(30, 10, 30), min + new Vector3(200, 10, 200), 2.5f, Bidirectional: true, UserId: 12);
        BuiltTile builtTile = SyntheticNav.Build(new TileSpec
        {
            TerrainX = 32,
            TerrainY = 32,
            Cells = 3,
            BvTree = true,
            OffMesh = [conn],
        });
        byte[] validTile = builtTile.File;

        int parsedParams = 0;
        int parsedTiles = 0;

        for (int iteration = 0; iteration < 500; iteration++)
        {
            byte[] mutatedParams = MutateBinary(rng, validParams, benignInterval: 8);
            try
            {
                _ = MmapParamsReader.Parse(mutatedParams);
                parsedParams++;
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"MmapParamsReader.Parse escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }

            byte[] mutatedTile = MutateBinary(rng, validTile, benignInterval: 8);
            try
            {
                ParsedNavTile parsed = MmapTileReader.Parse(mutatedTile);
                parsedTiles++;

                // Every tile accepted by MmapTileReader.Parse must be safe to add to DotRecast DtNavMesh and query.
                var dtParams = new DtNavMeshParams
                {
                    orig = new DotRecast.Core.Numerics.RcVec3f(SyntheticNav.OriginXz, 0, SyntheticNav.OriginXz),
                    tileWidth = SyntheticNav.TileSize,
                    tileHeight = SyntheticNav.TileSize,
                    maxTiles = 16,
                    maxPolys = 1024,
                };
                var navMesh = new DtNavMesh();
                navMesh.Init(dtParams, 6);
                navMesh.AddTile(parsed.Data, 0, 0, out _);
                var query = new DtNavMeshQuery(navMesh);
                var center = new DotRecast.Core.Numerics.RcVec3f(min.X + 50f, 10f, min.Z + 50f);
                var halfExtents = new DotRecast.Core.Numerics.RcVec3f(20f, 20f, 20f);
                query.FindNearestPoly(center, halfExtents, new DtQueryDefaultFilter(), out _, out _, out _);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"MmapTileReader.Parse / DtNavMesh escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(parsedParams >= 40, $"Expected benign .mmap params to parse (got {parsedParams})");
        Assert.True(parsedTiles >= 40, $"Expected benign .mmtile files to parse (got {parsedTiles})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Live_FrameParser_NdjsonReader_And_LiveStore_UnderFuzzedInputs_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        var store = new LiveStore();

        string[] validFrames =
        [
            """{"op":"hello","protocol":1,"pid":1234,"sha":"07bde2c30","maps":[{"map":0,"instance":0,"players":2,"creatures":15}]}""",
            """{"op":"frame","seq":1,"map":0,"instance":0,"clockMs":500,"grids":[[31,49],[32,32]],"counters":{"players":1,"bots":1,"creatures":1,"gameobjects":1},"entities":[{"g":"0000000000000001","k":"player","n":"Alice","x":-8949.95,"y":-132.49,"z":83.53,"o":1.5,"hp":[100,100],"lv":1},{"g":"0000000000000002","k":"bot","n":"Botbob","x":-8945.0,"y":-130.0,"z":83.5,"o":0.5,"hp":[80,100],"lv":2,"route":{"pts":[[-8945,-130,83.5],[-8940,-128,83.5]],"next":1}},{"g":"F13000012B001092","k":"creature","e":299,"n":"Young Wolf","x":-8940.0,"y":-132.0,"z":83.5,"o":3.14,"hp":[55,55],"lv":2,"mt":"random","home":[-8940,-132,83.5],"sp":{"id":7,"t0":450,"dur":2000,"run":false,"start":[-8940,-132,83.5],"pts":[[-8935,-132,83.5]]}},{"g":"F110000B1B012CCF","k":"gameobject","e":2843,"n":"Battered Chest","x":-8948.0,"y":-132.5,"z":83.5,"o":0.0}]}""",
        ];

        int validHellos = 0;
        int validParsedFrames = 0;

        for (int iteration = 0; iteration < 500; iteration++)
        {
            string template = validFrames[iteration & 1];
            byte[] raw = Encoding.UTF8.GetBytes(template);
            byte[] mutated = MutateBinary(rng, raw, benignInterval: 6);

            JsonObject? obj = null;
            try
            {
                using var doc = JsonDocument.Parse(mutated, FrameParser.DocumentOptions);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    obj = JsonNode.Parse(mutated, documentOptions: FrameParser.DocumentOptions) as JsonObject;
                }
            }
            catch (JsonException)
            {
            }

            if (obj is not null)
            {
                if (FrameParser.TryParseHello(obj, out HelloInfo? hello, out string? helloProblem))
                {
                    validHellos++;
                    Assert.NotNull(hello);
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(helloProblem));
                }

                if (FrameParser.TryParseFrame(obj, iteration * 100L, out ParsedFrame? parsed, out string? frameProblem))
                {
                    validParsedFrames++;
                    Assert.NotNull(parsed);
                    _ = store.Apply(parsed!.Frame);
                    _ = store.Summaries();
                    _ = store.Snapshot(e => e.Map == parsed.Frame.Map && e.Instance == parsed.Frame.Instance, withTrails: (iteration & 1) == 0);
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(frameProblem));
                }
            }
        }

        // Also fuzz NdjsonReader with random line lengths, blank lines, oversize lines, and truncated tails.
        var streamBytes = new List<byte>();
        for (int i = 0; i < 40; i++)
        {
            int len = rng.Next(0, 200);
            byte[] line = new byte[len];
            for (int b = 0; b < len; b++)
            {
                line[b] = (byte)rng.Next(32, 126);
            }

            streamBytes.AddRange(line);
            if (i < 39 || rng.Next(2) == 0)
            {
                streamBytes.Add((byte)'\n');
            }
        }

        var ndjson = new NdjsonReader(new MemoryStream(streamBytes.ToArray()), maxLineBytes: 96, bufferSize: 64);
        while (true)
        {
            NdjsonItem item = await ndjson.ReadAsync(CancellationToken.None);
            Assert.True(ndjson.RetainedBytes <= 64 + 96 + 64);
            if (item.Kind is NdjsonKind.End or NdjsonKind.Truncated)
            {
                break;
            }
        }

        Assert.True(validHellos >= 30, $"Expected valid hello parses (got {validHellos})");
        Assert.True(validParsedFrames >= 30, $"Expected valid frame parses (got {validParsedFrames})");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Host_ViewState_Cli_McpServer_And_Rasterizer_UnderFuzzedInputs_NeverEscape(int seed)
    {
        var rng = new Random(seed);
        JsonObject validViewJson = ViewState.Default.ToJson();
        byte[] validViewBytes = Encoding.UTF8.GetBytes(validViewJson.ToJsonString());

        int validViews = 0;
        for (int iteration = 0; iteration < 300; iteration++)
        {
            byte[] mutated = MutateBinary(rng, validViewBytes, benignInterval: 6);
            try
            {
                if (JsonNode.Parse(mutated) is JsonObject obj)
                {
                    ViewState state = ViewState.FromJson(obj);
                    validViews++;
                    _ = ViewState.Default.Apply(obj);
                    _ = new ViewTransform(state.Camera, 64, 64);
                }
            }
            catch (Exception ex) when (ex is JsonException or CommandException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"ViewState.FromJson/Apply escaped on seed={seed} iteration={iteration}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(validViews >= 25, $"Expected benign ViewState parses (got {validViews})");

        // Fuzz ParsedArguments, CommandLineArguments, and ScriptFile.
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["--json"] = true,
            ["--quiet"] = false,
            ["--port"] = true,
        };
        for (int iteration = 0; iteration < 200; iteration++)
        {
            string[] tokens = Enumerable.Range(0, rng.Next(0, 6))
                .Select(_ => rng.Next(5) switch
                {
                    0 => "--quiet",
                    1 => "--json={\"map\":0}",
                    2 => "--unknown-" + rng.Next(100),
                    3 => "key" + rng.Next(10) + "=" + rng.Next(-100, 100),
                    _ => "bad_pair_without_equals",
                })
                .ToArray();
            try
            {
                ParsedArguments parsed = ParsedArguments.Parse(tokens, allowed);
                _ = await CommandLineArguments.BuildAsync(parsed.Positionals, parsed.Value("--json"), new StringReader("{}"), CancellationToken.None);
            }
            catch (UsageException)
            {
            }

            string scriptText = iteration % 4 == 0
                ? "{\"command\":\"status\",\"args\":{}}\n{\"command\":\"view.get\"}\n"
                : Encoding.UTF8.GetString(MutateBinary(rng, "{\"command\":\"status\",\"args\":{}}"u8.ToArray(), benignInterval: 5));
            try
            {
                _ = ScriptFile.Parse(scriptText);
            }
            catch (UsageException)
            {
            }
        }

        // Fuzz McpServer with malformed JSON-RPC lines followed by a valid request to prove liveness.
        var fakeClient = new FakeCommandClient();
        fakeClient.AddEcho("test.echo");
        await using (var mcp = new McpSession(fakeClient))
        {
            await mcp.HandshakeAsync();
            for (int i = 0; i < 40; i++)
            {
                string fuzzedRpc = (i % 5) switch
                {
                    0 => "{not json at all",
                    1 => "[]",
                    2 => """{"jsonrpc":"1.0","id":1,"method":"tools/list"}""",
                    3 => $$"""{"jsonrpc":"2.0","id":{{i + 10}},"method":"unknown/method","params":123}""",
                    _ => $$$"""{"jsonrpc":"2.0","id":{{{i + 10}}},"method":"tools/call","params":{"name":123,"arguments":"bad"}}""",
                };
                mcp.Send(fuzzedRpc);
            }

            JsonNode echoReply = await mcp.RequestAsync(McpSession.Call(999, "test_echo", "{\"hello\":1}"), 999);
            Assert.NotNull(echoReply["result"]);
            await mcp.FinishAsync();
        }

        // Fuzz Rasterizer + PngEncoder with random triangles, lines, and points (including near-plane clipping and off-screen coordinates).
        var fb = new Framebuffer(32, 32, new Rgb(10, 12, 16));
        var view = new ViewTransform(ViewState.Default.Camera, 32, 32);
        var rasterizer = new Rasterizer(fb, in view);
        for (int i = 0; i < 100; i++)
        {
            WorldPoint pa = RandomWorldPoint(rng, 20000);
            WorldPoint pb = RandomWorldPoint(rng, 20000);
            WorldPoint pc = RandomWorldPoint(rng, 20000);
            ViewVertex va = view.ToView(pa.X - view.Eye.X, pa.Y - view.Eye.Y, pa.Z - view.Eye.Z);
            ViewVertex vb = view.ToView(pb.X - view.Eye.X, pb.Y - view.Eye.Y, pb.Z - view.Eye.Z);
            ViewVertex vc = view.ToView(pc.X - view.Eye.X, pc.Y - view.Eye.Y, pc.Z - view.Eye.Z);
            var fill = new TriangleFill(Occlude: (i & 1) == 0, Paint: true, new Rgb((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)), Alpha: rng.NextDouble());
            var stroke = new StrokeStyle(new Rgb((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)), Alpha: rng.NextDouble(), HalfWidth: 0.75, DepthTest: (i & 2) == 0);
            rasterizer.FillTriangle(in va, in vb, in vc, in fill);
            rasterizer.StrokeLine(in va, in vb, in stroke);
            rasterizer.DrawPoint(in va, in stroke);
        }

        byte[] png = PngEncoder.Encode(fb.Pixels, fb.Width, fb.Height);
        Assert.True(png.Length > 32);
    }

    private static byte[] MutateBinary(Random rng, byte[] source, int benignInterval)
    {
        if (rng.Next(benignInterval) == 0)
        {
            return (byte[])source.Clone();
        }

        switch (rng.Next(8))
        {
            case 0: // Truncate
                return source[..rng.Next(0, source.Length)];

            case 1: // Bit flips
            {
                byte[] copy = (byte[])source.Clone();
                int flips = rng.Next(1, 6);
                for (int i = 0; i < flips && copy.Length > 0; i++)
                {
                    copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
                }

                return copy;
            }

            case 2: // Extreme u32 overwrite
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

            case 3: // Extreme u16 overwrite
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

            case 4: // Extreme u8 overwrite
            {
                byte[] copy = (byte[])source.Clone();
                int count = rng.Next(1, 6);
                for (int i = 0; i < count && copy.Length > 0; i++)
                {
                    copy[rng.Next(copy.Length)] = ExtremeU8[rng.Next(ExtremeU8.Length)];
                }

                return copy;
            }

            case 5: // Splice out middle slice
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

            default: // Pure random noise
            {
                byte[] noise = new byte[rng.Next(0, 192)];
                rng.NextBytes(noise);
                return noise;
            }
        }
    }

    private static WorldPoint RandomWorldPoint(Random rng, double range)
        => new(
            (rng.NextDouble() - 0.5) * 2 * range,
            (rng.NextDouble() - 0.5) * 2 * range,
            (rng.NextDouble() - 0.5) * 2 * range);

    private static WorldPoint RandomUnitWorldPoint(Random rng)
    {
        double x = (rng.NextDouble() * 2) - 1;
        double y = (rng.NextDouble() * 2) - 1;
        double z = (rng.NextDouble() * 2) - 1;
        double len = Math.Sqrt((x * x) + (y * y) + (z * z));
        return len < 1e-9 ? new WorldPoint(0, 0, -1) : new WorldPoint(x / len, y / len, z / len);
    }
}

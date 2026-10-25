using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Materials;
using ArcaneCore.Atlas.Tests.Host;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

/// <summary>
/// ATG1 version 2 (design section 2.3). Version 1 stays byte-identical for chunks without the new streams; version 2 has a
/// 64-byte header (the 48 bytes of v1 plus <c>u32 streamMask</c>, <c>u32 batchCount</c>, two reserved words) and writes, after the
/// positions, every present stream in bit order: 0 normal f32x3, 1 uv0 f32x2, 2 uv1 f32x2, 3 color0 u8x4, 4 color1 u8x4,
/// 5 boneIndices u8x4, 6 boneWeights u8x4, 7 submesh u16 + u16 pad; then triangles, triangleAttributes, lines, lineAttributes,
/// points, pointAttributes, the batches (16 bytes: firstTriangle, triangleCount, material, tag) and the JSON, which gains
/// <c>"materials"</c> (and <c>"model"</c>). Packed colour words (<c>VertexExtras.Color0</c> and friends) are the four bytes R, G, B, A
/// in memory order, that is R in the least significant byte.
/// </summary>
public sealed class GeometryWireV2Tests
{
    private const int V2Header = 64;

    private static GeometryChunk Raw(float[]? normals = null, GeometryBatch[]? batches = null, Material[]? materials = null, ushort[]? submesh = null, float[]? uv0 = null)
        => new()
        {
            LayerId = LayerIds.RefM2Tex,
            Tile = GeometryV2Fixtures.Tile,
            Origin = WorldGrid.Origin(GeometryV2Fixtures.Tile),
            Positions = new float[12],
            Triangles = [0, 1, 2, 0, 2, 3],
            TriangleAttributes = [0, 0],
            Normals = normals is null ? null : Vectors3(normals),
            Uv0 = uv0 is null ? null : Vectors2(uv0),
            Submesh = submesh,
            Batches = batches ?? [],
            Materials = materials ?? [],
        };

    private static Vector3[] Vectors3(float[] values)
        => [.. Enumerable.Range(0, values.Length / 3).Select(i => new Vector3(values[3 * i], values[(3 * i) + 1], values[(3 * i) + 2]))];

    private static Vector2[] Vectors2(float[] values)
        => [.. Enumerable.Range(0, values.Length / 2).Select(i => new Vector2(values[2 * i], values[(2 * i) + 1]))];

    private static float F(byte[] wire, int at) => BinaryPrimitives.ReadSingleLittleEndian(wire.AsSpan(at));

    private static uint U(byte[] wire, int at) => BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(at));

    [Fact]
    public void AChunkWithoutNewStreams_StillWritesVersion1_ByteForByte()
    {
        WorldPoint origin = WorldGrid.Origin(GeometryV2Fixtures.Tile);
        var builder = new GeometryBuilder(LayerIds.Terrain, GeometryV2Fixtures.Tile, origin);
        uint a = builder.AddVertex(origin.X - 1, origin.Y - 1, 5);
        uint b = builder.AddVertex(origin.X - 2, origin.Y - 1, 6);
        uint c = builder.AddVertex(origin.X - 2, origin.Y - 2, 7);
        builder.AddTriangle(a, b, c, attribute: 4, edges: true);
        GeometryChunk chunk = builder.Build();

        byte[] wire = GeometryWire.Write(chunk);

        // The v1 layout written out by hand from the documented header: 48 bytes, then the arrays, then the JSON length word.
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(4)));
        Assert.Equal(3u, U(wire, 32));
        Assert.Equal(1u, U(wire, 36));
        Assert.Equal(3u, U(wire, 40));
        Assert.Equal(0u, U(wire, 44));
        int arrays = 48 + (4 * (9 + 3 + 1 + 6 + 3));
        Assert.Equal((uint)(wire.Length - arrays - 4), U(wire, arrays));
        JsonObject json = JsonNode.Parse(wire.AsSpan(arrays + 4))!.AsObject();
        Assert.Equal(["layer", "tile", "part", "attributes", "stats"], json.Select(p => p.Key).ToArray());
        Assert.False(chunk.IsV2);
    }

    [Fact]
    public void AChunkWithAnyNewStream_BatchOrMaterial_IsVersion2()
    {
        Assert.True(GeometryV2Fixtures.Quad().IsV2);
        Assert.True(Raw(normals: new float[12]).IsV2);
        Assert.False(Raw().IsV2);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(GeometryWire.Write(GeometryV2Fixtures.Quad()).AsSpan(4)));
    }

    [Fact]
    public void Header_HasTheDocumentedV2Fields()
    {
        GeometryChunk chunk = GeometryV2Fixtures.Quad();
        byte[] wire = GeometryWire.Write(chunk);

        Assert.Equal(64, GeometryWire.HeaderBytesV2);
        Assert.Equal("ATG1"u8.ToArray(), wire[..4]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(6))); // triangles only: no edges were added
        Assert.Equal(chunk.Origin.X, BitConverter.ToDouble(wire, 8));
        Assert.Equal(4u, U(wire, 32));
        Assert.Equal(2u, U(wire, 36));
        Assert.Equal(0u, U(wire, 40));
        Assert.Equal(0u, U(wire, 44));
        Assert.Equal(0b1000_1011u, U(wire, 48)); // normal, uv0, color0, submesh
        Assert.Equal(2u, U(wire, 52));
        Assert.Equal(0u, U(wire, 56));
        Assert.Equal(0u, U(wire, 60));
    }

    [Fact]
    public void Payload_IsPositionsThenStreamsInBitOrder_ThenIndices_ThenBatches_ThenJson()
    {
        byte[] wire = GeometryWire.Write(GeometryV2Fixtures.Quad());

        // positions 64..112, normals 112..160, uv0 160..192, color0 192..208, submesh 208..224
        Assert.Equal(-1f, F(wire, V2Header));
        Assert.Equal(0f, F(wire, 112));
        Assert.Equal(0f, F(wire, 116));
        Assert.Equal(1f, F(wire, 120));
        Assert.Equal(0f, F(wire, 160)); // uv0 of vertex 0
        Assert.Equal(1f, F(wire, 168)); // uv0 u of vertex 1
        Assert.Equal(0f, F(wire, 172)); // uv0 v of vertex 1
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0xFF }, wire[192..196]); // red opaque
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00, 0xFF }, wire[196..200]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x80 }, wire[204..208]); // white, alpha 128
        Assert.Equal(new byte[] { 7, 0, 0, 0, 7, 0, 0, 0, 8, 0, 0, 0, 8, 0, 0, 0 }, wire[208..224]);

        // triangles 224..248, triangle attributes 248..256, batches 256..288, json length at 288
        Assert.Equal([0u, 1u, 2u, 0u, 2u, 3u], Enumerable.Range(0, 6).Select(i => U(wire, 224 + (4 * i))).ToArray());
        Assert.Equal(new[] { 1u, 2u }, new[] { U(wire, 248), U(wire, 252) });
        Assert.Equal([0u, 1u, 0u, 0x0002_0007u], Enumerable.Range(0, 4).Select(i => U(wire, 256 + (4 * i))).ToArray());
        Assert.Equal([1u, 1u, 1u, 0x0001_0008u], Enumerable.Range(0, 4).Select(i => U(wire, 272 + (4 * i))).ToArray());
        Assert.Equal((uint)(wire.Length - 288 - 4), U(wire, 288));
    }

    [Fact]
    public void AllEightStreams_AreWrittenInBitOrder_WithTheirDocumentedWidths()
    {
        byte[] wire = GeometryWire.Write(GeometryV2Fixtures.AllStreams());

        Assert.Equal(255u, U(wire, 48));
        Assert.Equal(1u, U(wire, 52));
        // 3 vertices: positions 64..100, normal 100, uv0 136, uv1 160, color0 184, color1 196, boneIndices 208, boneWeights 220, submesh 232..244
        Assert.Equal(new[] { 0.25f, 0.5f, 0.75f }, new[] { F(wire, 100), F(wire, 104), F(wire, 108) });
        Assert.Equal(1f, F(wire, 112));
        Assert.Equal(new[] { 0.125f, 0.25f }, new[] { F(wire, 136), F(wire, 140) });
        Assert.Equal(1f, F(wire, 144));
        Assert.Equal(new[] { 0.375f, 0.625f }, new[] { F(wire, 160), F(wire, 164) });
        Assert.Equal(2f, F(wire, 168));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, wire[184..188]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, wire[188..192]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, wire[196..200]);
        Assert.Equal(new byte[] { 9, 10, 11, 12 }, wire[208..212]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, wire[220..224]);
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0 }, wire[224..228]);
        Assert.Equal(new byte[] { 0x34, 0x12, 0, 0 }, wire[232..236]);
        Assert.Equal(new[] { 0u, 1u, 2u }, new[] { U(wire, 244), U(wire, 248), U(wire, 252) });
        Assert.Equal([0u, 1u, 0u, 3u], Enumerable.Range(0, 4).Select(i => U(wire, 260 + (4 * i))).ToArray());
        Assert.Equal((uint)(wire.Length - 276 - 4), U(wire, 276));
    }

    [Fact]
    public void TheJsonTrailer_CarriesTheMaterials_InTheDocumentedShape()
    {
        byte[] wire = GeometryWire.Write(GeometryV2Fixtures.Quad());
        JsonObject json = JsonNode.Parse(wire.AsSpan(288 + 4))!.AsObject();

        JsonArray materials = json["materials"]!.AsArray();
        Assert.Equal(2, materials.Count);
        JsonObject first = materials[0]!.AsObject();
        Assert.Equal("generic", first["kind"]!.GetValue<string>());
        Assert.Equal(1, first["blend"]!.GetValue<int>());
        Assert.Equal(0x1 | 0x4, first["flags"]!.GetValue<int>());
        Assert.Equal(0.5, first["alphaRef"]!.GetValue<double>());
        Assert.Equal([1.0, 0.5, 0.25, 1.0], first["color"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray());
        Assert.Equal("k", first["extra"]!["note"]!.GetValue<string>());
        JsonObject texture = first["textures"]![0]!.AsObject();
        Assert.Equal("client:textures\\x.blp", texture["key"]!.GetValue<string>());
        Assert.Equal(0, texture["slot"]!.GetValue<int>());
        Assert.Equal([0, 1], texture["wrap"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray());
        Assert.Null(texture["replace"]);

        JsonObject second = materials[1]!.AsObject();
        Assert.Equal("m2", second["kind"]!.GetValue<string>());
        Assert.Equal(11, second["textures"]![0]!["replace"]!.GetValue<int>());
        Assert.Null(second["color"]);
        Assert.Null(second["extra"]);
        Assert.Equal("one", json["attributes"]!["1"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void TheModelBlock_IsWrittenAndReadBack()
    {
        GeometryChunk chunk = GeometryV2Fixtures.Quad();
        var withModel = new GeometryChunk
        {
            LayerId = chunk.LayerId,
            Tile = chunk.Tile,
            Origin = chunk.Origin,
            Positions = chunk.Positions,
            Triangles = chunk.Triangles,
            TriangleAttributes = chunk.TriangleAttributes,
            Normals = chunk.Normals,
            Uv0 = chunk.Uv0,
            Color0 = chunk.Color0,
            Submesh = chunk.Submesh,
            Batches = chunk.Batches,
            Materials = chunk.Materials,
            Model = new JsonObject { ["key"] = "m2:creature\\wolf\\wolf.m2", ["kind"] = "m2", ["radius"] = 3.5 },
        };

        GeometryChunk back = GeometryWire.Read(GeometryWire.Write(withModel));

        Assert.Equal("m2:creature\\wolf\\wolf.m2", back.Model!["key"]!.GetValue<string>());
        Assert.Equal(3.5, back.Model["radius"]!.GetValue<double>());
        Assert.Null(GeometryWire.Read(GeometryWire.Write(chunk)).Model);
    }

    [Fact]
    public void RoundTrip_PreservesStreamsBatchesAndMaterials()
    {
        GeometryChunk chunk = GeometryV2Fixtures.Quad();

        GeometryChunk back = GeometryWire.Read(GeometryWire.Write(chunk));

        Assert.True(back.IsV2);
        Assert.Equal(chunk.Positions, back.Positions);
        Assert.Equal(chunk.Triangles, back.Triangles);
        Assert.Equal(chunk.Normals, back.Normals);
        Assert.Equal(chunk.Uv0, back.Uv0);
        Assert.Null(back.Uv1);
        Assert.Equal(chunk.Color0, back.Color0);
        Assert.Null(back.Color1);
        Assert.Equal(chunk.Submesh, back.Submesh);
        Assert.Equal(chunk.Batches, back.Batches);
        Assert.Equal(chunk.Materials.Select(m => m.ToJson().ToJsonString()), back.Materials.Select(m => m.ToJson().ToJsonString()));
        Assert.Equal(1u, back.TriangleAttributes[0]);
        Assert.Equal("one", back.Attributes[1]["name"]!.GetValue<string>());
    }

    [Fact]
    public void RoundTrip_AllStreams()
    {
        GeometryChunk chunk = GeometryV2Fixtures.AllStreams();

        GeometryChunk back = GeometryWire.Read(GeometryWire.Write(chunk));

        Assert.Equal(chunk.Normals, back.Normals);
        Assert.Equal(chunk.Uv0, back.Uv0);
        Assert.Equal(chunk.Uv1, back.Uv1);
        Assert.Equal(chunk.Color0, back.Color0);
        Assert.Equal(chunk.Color1, back.Color1);
        Assert.Equal(chunk.BoneIndices, back.BoneIndices);
        Assert.Equal(chunk.BoneWeights, back.BoneWeights);
        Assert.Equal(chunk.Submesh, back.Submesh);
        Assert.Equal([(ushort)0x1234, (ushort)0, (ushort)5], back.Submesh!);
    }

    [Fact]
    public void Validate_RejectsStreamsThatDoNotMatchTheVertexCount()
    {
        Assert.Throws<InvalidDataException>(() => Raw(normals: new float[11]).Validate());
        Assert.Throws<InvalidDataException>(() => Raw(normals: new float[15]).Validate());
        Assert.Throws<InvalidDataException>(() => Raw(uv0: new float[7]).Validate());
        Assert.Throws<InvalidDataException>(() => Raw(submesh: new ushort[3]).Validate());
        Raw(normals: new float[12], uv0: new float[8], submesh: new ushort[4]).Validate();
    }

    [Fact]
    public void Validate_RejectsNonFiniteNormalsAndUvs()
    {
        float[] normals = new float[12];
        normals[5] = float.NaN;
        float[] uv = new float[8];
        uv[2] = float.PositiveInfinity;

        Assert.Throws<InvalidDataException>(() => Raw(normals: normals).Validate());
        Assert.Throws<InvalidDataException>(() => Raw(uv0: uv).Validate());
    }

    [Fact]
    public void Validate_AcceptsBatchesThatTileTheTriangles()
    {
        // The second batch starts where the first ends, and they use two different materials.
        GeometryChunk chunk = Raw(batches: [new(0, 1, 0, 0), new(1, 1, 1, 0)], materials: [GeometryV2Fixtures.Alpha(), GeometryV2Fixtures.Replaceable()]);

        chunk.Validate();
    }

    [Fact]
    public void Validate_RejectsBatchesThatLeaveAGapOverlapOrOverrun()
    {
        Material[] materials = [GeometryV2Fixtures.Alpha()];

        // covers triangle 0 only: triangle 1 is in no batch ("batches cover every triangle or none")
        Assert.Throws<InvalidDataException>(() => Raw(batches: [new(0, 1, 0, 0)], materials: materials).Validate());
        // gap before the first batch
        Assert.Throws<InvalidDataException>(() => Raw(batches: [new(1, 1, 0, 0)], materials: materials).Validate());
        // overlap
        Assert.Throws<InvalidDataException>(() => Raw(batches: [new(0, 2, 0, 0), new(1, 1, 0, 0)], materials: materials).Validate());
        // overrun past the triangle count
        Assert.Throws<InvalidDataException>(() => Raw(batches: [new(0, 3, 0, 0)], materials: materials).Validate());
        // empty batch
        Assert.Throws<InvalidDataException>(() => Raw(batches: [new(0, 0, 0, 0), new(0, 2, 0, 0)], materials: materials).Validate());
        // out of order
        Assert.Throws<InvalidDataException>(() => Raw(batches: [new(1, 1, 0, 0), new(0, 1, 0, 0)], materials: materials).Validate());
        // whole coverage is valid
        Raw(batches: [new(0, 2, 0, 0)], materials: materials).Validate();
    }

    [Fact]
    public void Validate_RejectsAMaterialIndexPastTheMaterialTable()
    {
        Assert.Throws<InvalidDataException>(() => Raw(batches: [new(0, 2, 1, 0)], materials: [GeometryV2Fixtures.Alpha()]).Validate());
        Assert.Throws<InvalidDataException>(() => Raw(batches: [new(0, 2, 0, 0)], materials: []).Validate());
    }

    [Fact]
    public void Validate_AcceptsChunksWithNoBatchesEvenWhenTheyCarryMaterials()
    {
        // "batches cover every triangle or none": a chunk without batches draws as today.
        Raw(materials: [GeometryV2Fixtures.Alpha()], normals: new float[12]).Validate();
    }

    [Fact]
    public void Read_RefusesEveryTruncationOfAV2Chunk()
    {
        byte[] good = GeometryWire.Write(GeometryV2Fixtures.AllStreams());

        for (int length = 0; length < good.Length; length++)
        {
            byte[] cut = good[..length];
            Assert.Throws<InvalidDataException>(() => GeometryWire.Read(cut));
        }

        Assert.NotNull(GeometryWire.Read(good));
    }

    [Fact]
    public void Read_RefusesCorruptV2Headers()
    {
        byte[] good = GeometryWire.Write(GeometryV2Fixtures.Quad());
        byte[] Patched(Action<byte[]> edit)
        {
            byte[] copy = (byte[])good.Clone();
            edit(copy);
            return copy;
        }

        // an undefined stream bit
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48), 0x100))));
        // a batch count the data cannot hold
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), 1_000_000))));
        // a batch pointing at a material the trailer does not have
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(256 + 8), 9))));
        // a batch that runs past the triangles
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(256 + 4), 5))));
        // an unknown version
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(Patched(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 3))));
        // a v1 header claiming v2 content is a length mismatch
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(Patched(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 1))));
        // a NaN normal
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(Patched(b => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(112), float.NaN))));
    }

    [Fact]
    public void Read_RefusesAMaterialsTrailerThatIsNotAnArrayOfObjects()
    {
        byte[] good = GeometryWire.Write(GeometryV2Fixtures.Quad());
        JsonObject json = JsonNode.Parse(good.AsSpan(288 + 4))!.AsObject();

        foreach (JsonNode bad in new JsonNode[] { new JsonObject(), JsonValue.Create("x")!, new JsonArray(1, 2) })
        {
            json["materials"] = bad;
            byte[] text = JsonSerializer.SerializeToUtf8Bytes(json);
            byte[] wire = [.. good[..288], .. BitConverter.GetBytes((uint)text.Length), .. text];
            Assert.Throws<InvalidDataException>(() => GeometryWire.Read(wire));
        }
    }

    [Fact]
    public void Builder_WithoutExtras_ProducesNoStreamsOrBatches()
    {
        WorldPoint origin = WorldGrid.Origin(GeometryV2Fixtures.Tile);
        var builder = new GeometryBuilder(LayerIds.Terrain, GeometryV2Fixtures.Tile, origin);
        builder.AddTriangle(builder.AddVertex(origin.X - 1, origin.Y, 0), builder.AddVertex(origin.X - 2, origin.Y, 0), builder.AddVertex(origin.X, origin.Y - 2, 0));

        GeometryChunk chunk = builder.Build();

        Assert.Null(chunk.Normals);
        Assert.Null(chunk.Uv0);
        Assert.Null(chunk.Color0);
        Assert.Null(chunk.Submesh);
        Assert.Empty(chunk.Batches);
        Assert.Empty(chunk.Materials);
    }

    [Fact]
    public void Builder_MixingVerticesWithAndWithoutAStream_IsRefused()
    {
        WorldPoint origin = WorldGrid.Origin(GeometryV2Fixtures.Tile);
        var builder = new GeometryBuilder(LayerIds.RefM2Tex, GeometryV2Fixtures.Tile, origin);
        builder.AddVertex(origin.X, origin.Y, 0, new VertexExtras { Uv0 = new Vector2(0, 0) });

        Assert.ThrowsAny<ArgumentException>(() => builder.AddVertex(origin.X, origin.Y, 1));
        Assert.ThrowsAny<ArgumentException>(() => builder.AddVertex(origin.X, origin.Y, 1, new VertexExtras { Normal = Vector3.UnitZ }));
    }

    [Fact]
    public void Builder_BatchesFollowTheTrianglesAddedInsideThem_AndEqualMaterialsShareAnIndex()
    {
        GeometryChunk chunk = BuildThreeBatches();

        Assert.Equal(2, chunk.Materials.Length);
        Assert.Equal(
            [new GeometryBatch(0, 1, 0, 5), new GeometryBatch(1, 2, 1, 6), new GeometryBatch(3, 1, 0, 7)],
            chunk.Batches);
    }

    [Fact]
    public void Builder_RefusesBatchMisuse()
    {
        WorldPoint origin = WorldGrid.Origin(GeometryV2Fixtures.Tile);
        var builder = new GeometryBuilder(LayerIds.RefM2Tex, GeometryV2Fixtures.Tile, origin);
        uint a = builder.AddVertex(origin.X, origin.Y, 0);
        uint b = builder.AddVertex(origin.X - 1, origin.Y, 0);
        uint c = builder.AddVertex(origin.X, origin.Y - 1, 0);

        Assert.Throws<InvalidOperationException>(() => builder.EndBatch());
        builder.BeginBatch(GeometryV2Fixtures.Alpha());
        Assert.Throws<InvalidOperationException>(() => builder.BeginBatch(GeometryV2Fixtures.Alpha()));
        builder.AddTriangle(a, b, c, edges: false);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        builder.EndBatch();
        builder.AddTriangle(a, b, c, edges: false); // a triangle outside every batch while others are in batches
        Assert.Throws<InvalidDataException>(() => builder.Build());
    }

    [Fact]
    public void Builder_EstimatedBytes_CountsTheStreams()
    {
        WorldPoint origin = WorldGrid.Origin(GeometryV2Fixtures.Tile);
        var plain = new GeometryBuilder(LayerIds.RefM2Tex, GeometryV2Fixtures.Tile, origin);
        var rich = new GeometryBuilder(LayerIds.RefM2Tex, GeometryV2Fixtures.Tile, origin);
        for (int i = 0; i < 100; i++)
        {
            plain.AddVertex(origin.X - i, origin.Y, 0);
            rich.AddVertex(origin.X - i, origin.Y, 0, new VertexExtras { Normal = Vector3.UnitZ, Uv0 = Vector2.Zero });
        }

        // normal 12 + uv0 8 bytes per vertex
        Assert.True(rich.EstimatedBytes - plain.EstimatedBytes >= 100 * 20);
    }

    [Fact]
    public void ALayerCache_ServesV2Wire_ForAV2Source()
    {
        var registry = new LayerRegistry();
        var source = new FakeLayerSource(LayerIds.RefM2Tex);
        source.AddChunks(LayerIds.RefM2Tex, GeometryV2Fixtures.Tile, GeometryV2Fixtures.Quad());
        registry.Add(source);
        var cache = new LayerCache(registry);

        IReadOnlyList<byte[]> wire = cache.GetWire(LayerIds.RefM2Tex, GeometryV2Fixtures.Tile, CancellationToken.None);

        Assert.Single(wire);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wire[0].AsSpan(4)));
        Assert.Equal(GeometryWire.Write(GeometryV2Fixtures.Quad()), wire[0]);
    }

    private static GeometryChunk BuildThreeBatches()
    {
        WorldPoint origin = WorldGrid.Origin(GeometryV2Fixtures.Tile);
        var builder = new GeometryBuilder(LayerIds.RefM2Tex, GeometryV2Fixtures.Tile, origin);
        uint a = builder.AddVertex(origin.X, origin.Y, 0);
        uint b = builder.AddVertex(origin.X - 1, origin.Y, 0);
        uint c = builder.AddVertex(origin.X, origin.Y - 1, 0);
        builder.BeginBatch(GeometryV2Fixtures.Alpha(), tag: 5);
        builder.AddTriangle(a, b, c, edges: false);
        builder.EndBatch();
        builder.BeginBatch(GeometryV2Fixtures.Replaceable(), tag: 6);
        builder.AddTriangle(a, b, c, edges: false);
        builder.AddTriangle(b, c, a, edges: false);
        builder.EndBatch();
        builder.BeginBatch(GeometryV2Fixtures.Alpha(), tag: 7);
        builder.AddTriangle(c, b, a, edges: false);
        builder.EndBatch();
        return builder.Build();
    }
}

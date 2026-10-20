using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

public sealed class M2VisualMeshTests
{
    private static readonly WorldPoint Spot = new(-9250, 100, 50);

    private static byte[] BuildM2WithNormals(
        IReadOnlyList<(Vector3 Pos, Vector3 Normal)> vertices,
        IReadOnlyList<ushort> viewLookup,
        IReadOnlyList<ushort> viewTriangles,
        IReadOnlyList<Vector3> boundingVertices,
        IReadOnlyList<ushort> boundingIndices,
        uint version = 256,
        bool useAltHeaderOffsets = false)
    {
        const int headerSize = 0x140;
        var buffer = new List<byte>(new byte[headerSize]);
        int Append(ReadOnlySpan<byte> bytes)
        {
            int at = buffer.Count;
            buffer.AddRange(bytes.ToArray());
            return at;
        }

        byte[] nameBytes = Encoding.Latin1.GetBytes("visual_test\0");
        int nameAt = Append(nameBytes);
        var vertexBytes = new byte[48 * vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            Span<byte> vSpan = vertexBytes.AsSpan(48 * i, 48);
            WriteVec3(vSpan, vertices[i].Pos);
            vSpan[12] = 255;
            WriteVec3(vSpan[20..], vertices[i].Normal);
        }

        int vertexAt = Append(vertexBytes);
        int lookupAt = Append(U16(viewLookup));
        int trianglesAt = Append(U16(viewTriangles));
        var view = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)viewLookup.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(4), (uint)lookupAt);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(8), (uint)viewTriangles.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(12), (uint)trianglesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(40), 256);
        int viewAt = Append(view);
        int hullTrianglesAt = Append(U16(boundingIndices));
        var hullVertexBytes = new byte[12 * boundingVertices.Count];
        for (int i = 0; i < boundingVertices.Count; i++)
        {
            WriteVec3(hullVertexBytes.AsSpan(12 * i), boundingVertices[i]);
        }

        int hullVerticesAt = Append(hullVertexBytes);

        byte[] file = [.. buffer];
        file[0] = (byte)'M';
        file[1] = (byte)'D';
        file[2] = (byte)'2';
        file[3] = (byte)'0';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x08), (uint)nameBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), (uint)nameAt);
        int vertHeaderOffset = useAltHeaderOffsets ? 0x3C : 0x44;
        int viewHeaderOffset = useAltHeaderOffsets ? 0x44 : 0x4C;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(vertHeaderOffset), (uint)vertices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(vertHeaderOffset + 4), (uint)vertexAt);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(viewHeaderOffset), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(viewHeaderOffset + 4), (uint)viewAt);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0xEC), (uint)boundingIndices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0xF0), (uint)hullTrianglesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0xF4), (uint)boundingVertices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0xF8), (uint)hullVerticesAt);
        return file;
    }

    private static byte[] U16(IReadOnlyList<ushort> values)
    {
        var bytes = new byte[2 * values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), values[i]);
        }

        return bytes;
    }

    private static void WriteVec3(Span<byte> span, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(span, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(span[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(span[8..], v.Z);
    }

    [Theory]
    [InlineData(256u, false)]
    [InlineData(257u, false)]
    [InlineData(260u, true)]
    public void Parse_ExtractsVisualVerticesNormalsAndTriangles_AcrossSupportedVersionsAndHeaderLayouts(uint version, bool altLayout)
    {
        (Vector3 Pos, Vector3 Normal)[] verts =
        [
            (new Vector3(0, 0, 0), new Vector3(0, 0, 1)),
            (new Vector3(2, 0, 0), new Vector3(0, 1, 0)),
            (new Vector3(0, 2, 0), new Vector3(1, 0, 0)),
            (new Vector3(2, 2, 1), new Vector3(0.577f, 0.577f, 0.577f)),
        ];
        byte[] data = BuildM2WithNormals(
            verts,
            viewLookup: [3, 1, 2, 0],
            viewTriangles: [3, 1, 2, 1, 0, 2],
            boundingVertices: [],
            boundingIndices: [],
            version: version,
            useAltHeaderOffsets: altLayout);

        M2ModelFile model = M2ModelFile.Parse(data);

        Assert.True(model.HasVisualMesh);
        Assert.False(model.HasCollisionHull);
        Assert.False(model.HasCollision);
        Assert.Equal(4, model.VisualVertices.Length);
        Assert.Equal(4, model.VisualNormals.Length);
        Assert.Equal(new Vector3(0, 0, 1), model.VisualNormals[0]);
        Assert.Equal(new Vector3(0, 1, 0), model.VisualNormals[1]);
        Assert.Equal(new Vector3(1, 0, 0), model.VisualNormals[2]);
        Assert.Equal(2, model.VisualTriangles.Length);
        Assert.Equal(new ModelTriangle(0, 1, 2), model.VisualTriangles[0]);
        Assert.Equal(new ModelTriangle(1, 3, 2), model.VisualTriangles[1]);
        Assert.Equal((0, 1, 2), (model.VisualTriangles[0].Index0, model.VisualTriangles[0].Index1, model.VisualTriangles[0].Index2));
    }

    [Fact]
    public void Parse_OutOfBoundsOrMissingViews_GracefullyReturnsEmptyVisualMesh_WhenBoundingHullIsValid()
    {
        byte[] cube = ModelFixtures.CubeM2(1f, withHull: true);

        // 1. nViews == 0
        byte[] zeroViews = [.. cube];
        BinaryPrimitives.WriteUInt32LittleEndian(zeroViews.AsSpan(0x4C), 0u);
        M2ModelFile mZeroViews = M2ModelFile.Parse(zeroViews);
        Assert.False(mZeroViews.HasVisualMesh);
        Assert.Empty(mZeroViews.VisualTriangles);
        Assert.True(mZeroViews.HasCollisionHull);

        // 2. ofsViews out of bounds
        byte[] badViewOfs = [.. cube];
        BinaryPrimitives.WriteUInt32LittleEndian(badViewOfs.AsSpan(0x50), 0xFFFFFF00u);
        M2ModelFile mBadView = M2ModelFile.Parse(badViewOfs);
        Assert.False(mBadView.HasVisualMesh);
        Assert.Empty(mBadView.VisualTriangles);
        Assert.True(mBadView.HasCollisionHull);

        // 3. ofsVertices out of bounds on a bounding-only synthetic M2
        byte[] badVertOfs = [.. cube];
        BinaryPrimitives.WriteUInt32LittleEndian(badVertOfs.AsSpan(0x48), 0xFFFFFF00u);
        M2ModelFile mBadVert = M2ModelFile.Parse(badVertOfs);
        Assert.False(mBadVert.HasVisualMesh);
        Assert.Empty(mBadVert.VisualVertices);
        Assert.Empty(mBadVert.VisualNormals);
        Assert.Empty(mBadVert.VisualTriangles);
        Assert.True(mBadVert.HasCollisionHull);
    }

    [Fact]
    public void ModelGeometry_VisualFallback_EmitsVisualMeshWhenNoCollision_AndKeepsQueriesStrictlyOnCollision()
    {
        var world = new ModelWorld();
        world.AddCube(withHull: false);
        ClientPlacement placement = ModelWorld.M2(19, Spot, ModelWorld.Goldshire);
        world.Placements.Add(ModelWorld.Goldshire, placement);

        TilePlan plan = ModelPlanner.Plan(0, ModelWorld.Goldshire, [placement], PlanScope.All, world.Files, world.Cache, CancellationToken.None);
        PlannedM2 planned = Assert.Single(plan.M2s);
        Assert.False(planned.Model.HasCollision);
        Assert.True(planned.Model.HasVisualMesh);

        // Default collision layer does not emit visual triangles without fallback.
        var strictCollision = new PartedGeometry(LayerIds.RefM2Collision, ModelWorld.Goldshire, WorldGrid.Origin(ModelWorld.Goldshire));
        Assert.Equal(0, ModelGeometry.AddM2Collision(strictCollision, planned, CancellationToken.None));

        // AppendM2 / AddM2Collision with allowVisualFallback emits visual triangles with subLayer "m2-visual".
        var fallbackGeometry = new PartedGeometry(LayerIds.RefM2Render, ModelWorld.Goldshire, WorldGrid.Origin(ModelWorld.Goldshire));
        int added = ModelGeometry.AppendM2(fallbackGeometry, planned, CancellationToken.None);
        Assert.Equal(12, added);
        GeometryChunk chunk = Assert.Single(fallbackGeometry.Finish(new JsonObject()));
        Assert.Equal(12, chunk.TriangleCount);
        JsonObject attr = ModelWorld.AttributeOf(chunk, 0);
        Assert.Equal("m2-visual", attr["subLayer"]!.GetValue<string>());

        // ReferenceModelQueries model inspection reports visualVertexCount, visualTriangleCount, and hasVisualMesh.
        JsonObject info = world.Queries.ModelInfo(ModelWorld.CubePath);
        Assert.Equal(8, info["visualVertexCount"]!.GetValue<int>());
        Assert.Equal(12, info["visualTriangleCount"]!.GetValue<int>());
        Assert.True(info["hasVisualMesh"]!.GetValue<bool>());
        Assert.False(info["hasCollisionHull"]!.GetValue<bool>());

        // LineOfSight and FloorStack remain strictly on collision geometry.
        JsonObject los = world.Queries.LineOfSight(0, new WorldPoint(-9240, 100, 50), new WorldPoint(-9260, 100, 50), true, CancellationToken.None);
        Assert.True(los["movement"]!["clear"]!.GetValue<bool>());
        JsonObject floor = world.Queries.FloorStack(0, Spot.X, Spot.Y, 0, 100, true, CancellationToken.None);
        Assert.Empty(floor["surfaces"]!.AsArray());
    }
}

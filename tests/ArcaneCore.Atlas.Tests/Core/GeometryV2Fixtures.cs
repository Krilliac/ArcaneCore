using System.Numerics;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Materials;

namespace ArcaneCore.Atlas.Tests.Core;

/// <summary>Hand-built ATG1 v2 chunks shared by the wire tests, the CPU render tests and the JS golden export.</summary>
internal static class GeometryV2Fixtures
{
    public static readonly TileKey Tile = new(0, 49, 31);

    /// <summary>A material that exercises every optional field.</summary>
    public static Material Alpha() => new(
        "generic",
        BlendMode.AlphaKey,
        MaterialFlags.Unlit | MaterialFlags.TwoSided,
        [new TextureRef("client:textures\\x.blp", Slot: 0, WrapS: 0, WrapT: 1)],
        AlphaRef: 0.5,
        Color: [1, 0.5, 0.25, 1],
        Extra: new System.Text.Json.Nodes.JsonObject { ["note"] = "k" });

    /// <summary>A material with a replaceable texture and nothing optional.</summary>
    public static Material Replaceable() => new(
        "m2",
        BlendMode.Opaque,
        MaterialFlags.None,
        [new TextureRef("missing", Slot: 0, WrapS: 0, WrapT: 0, Replace: 11)]);

    /// <summary>
    /// Two triangles over four vertices with normals, uv0, color0 and submesh streams and one batch per triangle (two
    /// materials). No edges, so the arrays are small enough to check by offset.
    /// </summary>
    public static GeometryChunk Quad()
    {
        WorldPoint origin = WorldGrid.Origin(Tile);
        var builder = new GeometryBuilder(LayerIds.RefM2Tex, Tile, origin);
        uint a = builder.AddVertex(origin.X - 1, origin.Y - 1, 10, new VertexExtras { Normal = new Vector3(0, 0, 1), Uv0 = new Vector2(0, 0), Color0 = 0xFF0000FF, Submesh = 7 });
        uint b = builder.AddVertex(origin.X - 2, origin.Y - 1, 10, new VertexExtras { Normal = new Vector3(0, 0, 1), Uv0 = new Vector2(1, 0), Color0 = 0xFF00FF00, Submesh = 7 });
        uint c = builder.AddVertex(origin.X - 2, origin.Y - 2, 10, new VertexExtras { Normal = new Vector3(0, 0, 1), Uv0 = new Vector2(1, 1), Color0 = 0xFFFF0000, Submesh = 8 });
        uint d = builder.AddVertex(origin.X - 1, origin.Y - 2, 10, new VertexExtras { Normal = new Vector3(0, 0, 1), Uv0 = new Vector2(0, 1), Color0 = 0x80FFFFFF, Submesh = 8 });
        builder.BeginBatch(Alpha(), tag: 0x0002_0007);
        builder.AddTriangle(a, b, c, attribute: 1, edges: false);
        builder.EndBatch();
        builder.BeginBatch(Replaceable(), tag: 0x0001_0008);
        builder.AddTriangle(a, c, d, attribute: 2, edges: false);
        builder.EndBatch();
        builder.DescribeAttribute(1, () => new System.Text.Json.Nodes.JsonObject { ["name"] = "one" });
        return builder.Build();
    }

    /// <summary>Two vertices carrying every stream, one triangle (degenerate indices are fine for the wire), one batch.</summary>
    public static GeometryChunk AllStreams()
    {
        WorldPoint origin = WorldGrid.Origin(Tile);
        var builder = new GeometryBuilder(LayerIds.RefM2Tex, Tile, origin);
        uint a = builder.AddVertex(origin.X - 1, origin.Y - 1, 1, new VertexExtras
        {
            Normal = new Vector3(0.25f, 0.5f, 0.75f),
            Uv0 = new Vector2(0.125f, 0.25f),
            Uv1 = new Vector2(0.375f, 0.625f),
            Color0 = 0x04030201,
            Color1 = 0x08070605,
            BoneIndices = 0x0C0B0A09,
            BoneWeights = 0xFF000000,
            Submesh = 0x1234,
        });
        uint b = builder.AddVertex(origin.X - 2, origin.Y - 1, 2, new VertexExtras
        {
            Normal = new Vector3(1, 0, 0),
            Uv0 = new Vector2(1, 1),
            Uv1 = new Vector2(2, 2),
            Color0 = 0xFFFFFFFF,
            Color1 = 0,
            BoneIndices = 0,
            BoneWeights = 0x000000FF,
            Submesh = 0,
        });
        uint c = builder.AddVertex(origin.X - 2, origin.Y - 2, 3, new VertexExtras { Normal = new Vector3(0, 1, 0), Uv0 = new Vector2(0, 0), Uv1 = new Vector2(0, 0), Color0 = 1, Color1 = 2, BoneIndices = 3, BoneWeights = 4, Submesh = 5 });
        builder.BeginBatch(Replaceable(), tag: 3);
        builder.AddTriangle(a, b, c, edges: false);
        builder.EndBatch();
        return builder.Build();
    }

    /// <summary>The quad with a model block, as the model package writes it.</summary>
    public static GeometryChunk QuadWithModel()
    {
        GeometryChunk chunk = Quad();
        return new GeometryChunk
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
            Model = new System.Text.Json.Nodes.JsonObject { ["key"] = "m2:creature\\wolf\\wolf.m2", ["kind"] = "m2", ["radius"] = 3.5 },
        };
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// Accumulates one tile's geometry for a layer as one or more <c>GeometryChunk</c> parts, each kept under a
/// size limit so the wire format's 64 MiB cap is never reached. Attribute ids are unique across the parts; each part
/// describes the ids it uses. Single-threaded (one build).
/// </summary>
internal sealed class PartedGeometry(string layerId, TileKey tile, WorldPoint origin, long partLimitBytes = PartedGeometry.DefaultPartLimitBytes)
{
    public const long DefaultPartLimitBytes = 40L * 1024 * 1024;

    private readonly List<GeometryBuilder> _builders = [];
    private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
    private readonly List<JsonObject> _descriptions = [];

    // Undirected edges already drawn in the current (last) part. GeometryBuilder.AddEdge dedupes through a HashSet<ulong>
    // whose default hash is low ^ high, which collapses edges between nearby vertex indices into a few buckets: a
    // 900,000-triangle tile (Stormwind) took over eight minutes. The key is mixed here instead, and every part starts
    // with an empty set because vertex indices are per part.
    private readonly HashSet<ulong> _edges = new(EdgeKeyComparer.Instance);

    public int Parts => _builders.Count;

    /// <summary>The builder to write to; starts a new part when the current one has no room for roughly <paramref name="extraBytes"/> more.</summary>
    public GeometryBuilder Writable(long extraBytes)
    {
        if (_builders.Count == 0 || (!_builders[^1].IsEmpty && _builders[^1].EstimatedBytes + extraBytes > partLimitBytes))
        {
            StartPart();
        }

        return _builders[^1];
    }

    /// <summary>True when <paramref name="builder"/> is the current part and has grown past the limit.</summary>
    public bool IsFull(GeometryBuilder builder) => builder.EstimatedBytes > partLimitBytes;

    /// <summary>Starts a new part unconditionally and returns it.</summary>
    public GeometryBuilder NextPart()
    {
        StartPart();
        return _builders[^1];
    }

    /// <summary>
    /// Adds a triangle to <paramref name="builder"/> (the current part) with its three edges as lines, each undirected
    /// edge once per part, which is what <c>GeometryBuilder.AddTriangle</c> does with its own edge set.
    /// </summary>
    public void AddTriangle(GeometryBuilder builder, uint a, uint b, uint c, uint attribute)
    {
        if (!ReferenceEquals(builder, _builders[^1]))
        {
            throw new InvalidOperationException($"{layerId} {tile}: triangles go to the current part only.");
        }

        builder.AddTriangle(a, b, c, attribute, edges: false);
        AddEdge(builder, a, b, attribute);
        AddEdge(builder, b, c, attribute);
        AddEdge(builder, c, a, attribute);
    }

    private void StartPart()
    {
        _builders.Add(new GeometryBuilder(layerId, tile, origin, _builders.Count));
        _edges.Clear();
    }

    private void AddEdge(GeometryBuilder builder, uint a, uint b, uint attribute)
    {
        if (a == b)
        {
            return;
        }

        ulong key = a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
        if (_edges.Add(key))
        {
            builder.AddLine(a, b, attribute);
        }
    }

    /// <summary>The id for an attribute key, described by <paramref name="describe"/> the first time.</summary>
    public uint Attribute(string key, Func<JsonObject> describe)
    {
        if (_ids.TryGetValue(key, out uint existing))
        {
            return existing;
        }

        uint id = (uint)_descriptions.Count;
        _ids[key] = id;
        _descriptions.Add(describe());
        return id;
    }

    /// <summary>Makes sure <paramref name="builder"/> carries the description of attribute <paramref name="id"/>.</summary>
    public void Describe(GeometryBuilder builder, uint id)
        => builder.DescribeAttribute(id, () => (JsonObject)_descriptions[(int)id].DeepClone());

    public long TriangleCount => _builders.Sum(b => (long)b.TriangleCount);

    /// <summary>Builds every part; <paramref name="stats"/> goes on the first one.</summary>
    public IReadOnlyList<GeometryChunk> Finish(JsonObject stats)
    {
        if (_builders.Count == 0)
        {
            return [];
        }

        foreach ((string key, JsonNode? value) in stats)
        {
            _builders[0].Stats[key] = value?.DeepClone();
        }

        return [.. _builders.Select(b => b.Build())];
    }
}

/// <summary>Fibonacci hashing of an edge key, so keys that differ only in low bits of both halves spread over the table.</summary>
internal sealed class EdgeKeyComparer : IEqualityComparer<ulong>
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly EdgeKeyComparer Instance = new();

    /// <inheritdoc/>
    public bool Equals(ulong x, ulong y) => x == y;

    /// <summary>The high 32 bits of the key times 2^64 / golden ratio.</summary>
    public int GetHashCode(ulong obj) => unchecked((int)((obj * 0x9E3779B97F4A7C15UL) >> 32));
}

/// <summary>Writes planned instances into geometry: world-space triangles with per-instance attributes.</summary>
internal static class ModelGeometry
{
    private const int CheckEvery = 4096;
    private const long BytesPerTriangle = 48;

    /// <summary>Adds every triangle of every group of a WMO instance (collidable and render-only, kept and skipped groups).</summary>
    public static int AddWmo(PartedGeometry geometry, PlannedWmo wmo, CancellationToken cancellationToken)
    {
        int added = 0;
        WmoModel model = wmo.Model;
        for (int g = 0; g < model.Groups.Count; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WmoGroupFile group = model.Groups[g];
            if (group.TriangleCount == 0)
            {
                continue;
            }

            string? groupSkip = model.SkipReason(g);
            string? skip = groupSkip ?? wmo.SkipReason;
            int collidable = 0;
            foreach (byte flags in group.TriangleFlags)
            {
                if (WmoMaterialFlags.IsCollidable(flags))
                {
                    collidable++;
                }
            }

            int renderOnly = group.TriangleCount - collidable;
            uint collideId = 0;
            uint renderId = 0;
            bool haveCollide = false;
            bool haveRender = false;
            if (collidable > 0)
            {
                collideId = geometry.Attribute(AttributeKey(wmo, g, true), () => WmoAttribute(wmo, g, true, collidable, groupSkip, skip));
                haveCollide = true;
            }

            if (renderOnly > 0)
            {
                renderId = geometry.Attribute(AttributeKey(wmo, g, false), () => WmoAttribute(wmo, g, false, renderOnly, groupSkip, skip));
                haveRender = true;
            }

            GeometryBuilder builder = geometry.Writable(group.TriangleCount * BytesPerTriangle);
            if (haveCollide)
            {
                geometry.Describe(builder, collideId);
            }

            if (haveRender)
            {
                geometry.Describe(builder, renderId);
            }

            uint[] remap = NewRemap(group.Vertices.Length);
            for (int t = 0; t < group.TriangleCount; t++)
            {
                if (t % CheckEvery == 0 && t > 0 && geometry.IsFull(builder))
                {
                    builder = geometry.NextPart();
                    if (haveCollide)
                    {
                        geometry.Describe(builder, collideId);
                    }

                    if (haveRender)
                    {
                        geometry.Describe(builder, renderId);
                    }

                    Array.Fill(remap, uint.MaxValue);
                }

                uint attribute = WmoMaterialFlags.IsCollidable(group.TriangleFlags[t]) ? collideId : renderId;
                uint a = Vertex(builder, remap, group.Vertices, group.Indices[3 * t], wmo.Transform);
                uint b = Vertex(builder, remap, group.Vertices, group.Indices[(3 * t) + 1], wmo.Transform);
                uint c = Vertex(builder, remap, group.Vertices, group.Indices[(3 * t) + 2], wmo.Transform);
                geometry.AddTriangle(builder, a, b, c, attribute);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Adds an M2 instance's bounding (collision) triangles in the client's winding, or optionally falls back to its visual
    /// mesh when <paramref name="allowVisualFallback"/> is true and the model has no collision hull.
    /// </summary>
    public static int AddM2Collision(PartedGeometry geometry, PlannedM2 m2, CancellationToken cancellationToken, bool allowVisualFallback = false)
    {
        M2ModelFile model = m2.Model;
        if (!model.HasCollisionHull)
        {
            if (allowVisualFallback && model.HasVisualMesh)
            {
                return AddM2Visual(geometry, m2, cancellationToken, subLayer: "m2-visual");
            }

            return 0;
        }

        return AddM2(geometry, m2, model.BoundingVertices, model.BoundingIndices.Select(i => (int)i).ToArray(), model.BoundingTriangleCount, cancellationToken, subLayer: "m2");
    }

    /// <summary>Adds an M2 instance's render mesh (view 0).</summary>
    public static int AddM2Render(PartedGeometry geometry, PlannedM2 m2, CancellationToken cancellationToken)
    {
        M2ModelFile model = m2.Model;
        string subLayer = model.HasCollisionHull ? "m2" : "m2-visual";
        return model.RenderTriangleCount == 0
            ? 0
            : AddM2(geometry, m2, model.Vertices, model.RenderIndices, model.RenderTriangleCount, cancellationToken, subLayer);
    }

    /// <summary>
    /// Appends an M2 instance's geometry to <paramref name="geometry"/>: uses the collision hull when present, or falls back
    /// to the visual mesh (<c>VisualVertices</c> / <c>VisualTriangles</c>) with sub-layer <c>"m2-visual"</c> when
    /// <c>HasCollision</c> is false and <c>HasVisualMesh</c> is true.
    /// </summary>
    public static int AppendM2(PartedGeometry geometry, PlannedM2 m2, CancellationToken cancellationToken = default, bool allowVisualFallback = true)
        => AddM2Collision(geometry, m2, cancellationToken, allowVisualFallback);

    /// <summary>Adds an M2 instance's visual mesh (<c>VisualVertices</c> and <c>VisualTriangles</c>).</summary>
    public static int AddM2Visual(PartedGeometry geometry, PlannedM2 m2, CancellationToken cancellationToken = default, string subLayer = "m2-visual")
    {
        M2ModelFile model = m2.Model;
        if (!model.HasVisualMesh)
        {
            return 0;
        }

        return AddM2(geometry, m2, model.VisualVertices, model.RenderIndices, model.VisualTriangles.Length, cancellationToken, subLayer);
    }

    private static int AddM2(
        PartedGeometry geometry,
        PlannedM2 m2,
        Vector3[] vertices,
        int[] indices,
        int triangles,
        CancellationToken cancellationToken,
        string subLayer = "m2")
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint attribute = geometry.Attribute(M2Key(m2, subLayer), () => M2Attribute(m2, triangles, subLayer));
        GeometryBuilder builder = geometry.Writable(triangles * BytesPerTriangle);
        geometry.Describe(builder, attribute);
        uint[] remap = NewRemap(vertices.Length);
        for (int t = 0; t < triangles; t++)
        {
            if (t % CheckEvery == 0 && t > 0 && geometry.IsFull(builder))
            {
                builder = geometry.NextPart();
                geometry.Describe(builder, attribute);
                Array.Fill(remap, uint.MaxValue);
            }

            uint a = Vertex(builder, remap, vertices, indices[3 * t], m2.Transform);
            uint b = Vertex(builder, remap, vertices, indices[(3 * t) + 1], m2.Transform);
            uint c = Vertex(builder, remap, vertices, indices[(3 * t) + 2], m2.Transform);
            geometry.AddTriangle(builder, a, b, c, attribute);
        }

        return triangles;
    }

    private static uint[] NewRemap(int count)
    {
        var remap = new uint[count];
        Array.Fill(remap, uint.MaxValue);
        return remap;
    }

    private static uint Vertex(GeometryBuilder builder, uint[] remap, Vector3[] vertices, int index, InstanceTransform transform)
    {
        uint existing = remap[index];
        if (existing != uint.MaxValue)
        {
            return existing;
        }

        WorldPoint world = transform.ModelToWorld(Vec3d.From(vertices[index]));
        uint added = builder.AddVertex(world);
        remap[index] = added;
        return added;
    }

    private static string AttributeKey(PlannedWmo wmo, int group, bool collide)
        => string.Create(CultureInfo.InvariantCulture, $"wmo:{wmo.Placement.UniqueId}:{wmo.Placement.Tile.X}:{wmo.Placement.Tile.Y}:{group}:{(collide ? 'c' : 'r')}");

    private static string M2Key(PlannedM2 m2, string subLayer)
        => string.Create(CultureInfo.InvariantCulture, $"m2:{subLayer}:{(m2.IsWmoDoodad ? 'd' : 'p')}:{m2.UniqueId}:{m2.DoodadIndex}:{m2.Source.Tile.X}:{m2.Source.Tile.Y}");

    private static JsonObject WmoAttribute(PlannedWmo wmo, int group, bool collide, int triangles, string? groupSkip, string? skip)
    {
        WmoModel model = wmo.Model;
        WmoGroupFile file = model.Groups[group];
        var flags = new JsonArray();
        foreach (byte value in file.TriangleFlags.Where(f => WmoMaterialFlags.IsCollidable(f) == collide).Distinct().Order())
        {
            flags.Add(value);
        }

        return new JsonObject
        {
            ["colorKey"] = collide ? ColorKeys.Collidable : ColorKeys.RenderOnly,
            ["kind"] = "wmo",
            ["model"] = model.Path,
            ["uniqueId"] = wmo.Placement.UniqueId,
            ["group"] = group,
            ["groupName"] = model.GroupName(group),
            ["groupFlags"] = file.Flags,
            ["groupId"] = file.GroupId,
            ["class"] = collide ? "collide" : "render-only",

            // The class is the client's MOPY reading. The server's precision-mode extractor writes every MOVI triangle of
            // a kept group, so the server collides render-only faces too unless the group or the instance is skipped.
            // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:280-354 (precise branch: all MOVI indices and MOVT vertices, nColTriangles = nTriangles)
            // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/vmapexport.cpp:76 (preciseVectorData defaults to true)
            ["serverCollides"] = skip is null,
            ["mopyFlags"] = flags,
            ["triangles"] = triangles,
            ["extractorSkipped"] = skip is not null,
            ["groupSkipReason"] = groupSkip,
            ["skipReason"] = skip,
            ["doodadSet"] = wmo.Placement.DoodadSet,
            ["position"] = Triple(wmo.Position.X, wmo.Position.Y, wmo.Position.Z),
            ["rawRotation"] = Triple(wmo.Placement.Rotation.X, wmo.Placement.Rotation.Y, wmo.Placement.Rotation.Z),
            ["originQuirk"] = wmo.OriginQuirk,
        };
    }

    private static JsonObject M2Attribute(PlannedM2 m2, int triangles, string subLayer)
    {
        var attribute = new JsonObject
        {
            ["kind"] = "m2",
            ["subLayer"] = subLayer,
            ["model"] = m2.ModelPath,
            ["uniqueId"] = m2.UniqueId,
            ["source"] = m2.IsWmoDoodad ? "wmo-doodad" : "mddf",
            ["triangles"] = triangles,
            ["scale"] = m2.Transform.Scale,
            ["position"] = Triple(m2.Position.X, m2.Position.Y, m2.Position.Z),
            ["extractorSkipped"] = m2.SkipReason is not null,
            ["skipReason"] = m2.SkipReason,
        };
        if (m2.IsWmoDoodad)
        {
            attribute["doodadIndex"] = m2.DoodadIndex;
            attribute["doodadSet"] = m2.DoodadSet;
            attribute["serverRotationDeviationDegrees"] = m2.ServerRotationDeviationDegrees;
        }
        else
        {
            attribute["rawRotation"] = Triple(m2.Source.Rotation.X, m2.Source.Rotation.Y, m2.Source.Rotation.Z);
        }

        return attribute;
    }

    private static JsonArray Triple(double x, double y, double z) => new(x, y, z);
}

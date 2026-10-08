using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.VMaps;

/// <summary>A mesh triangle: three vertex indices (vmangos <c>MeshTriangle</c>).</summary>
public readonly record struct MeshTriangle(uint Index0, uint Index1, uint Index2);

/// <summary>
/// One group of a model (vmangos <c>GroupModel</c>): its bound, MOGP flags and WMO group id,
/// the collision mesh with its BIH, and the group's liquid (parsed, kept for a later liquid
/// query; see docs/integration/vmap-los.md). Coordinates are model space.
/// </summary>
public sealed class GroupModel
{
    private readonly Vector3[] _vertices;
    private readonly MeshTriangle[] _triangles;

    public GroupModel(Vector3 boundLow, Vector3 boundHigh, uint mogpFlags, uint groupWmoId, Vector3[] vertices, MeshTriangle[] triangles, BihTree? meshTree = null, WmoLiquid? liquid = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(triangles);
        foreach (MeshTriangle t in triangles)
        {
            if (t.Index0 >= vertices.Length || t.Index1 >= vertices.Length || t.Index2 >= vertices.Length)
            {
                throw new InvalidDataException($"triangle index out of range ({vertices.Length} vertices)");
            }
        }

        BoundLow = boundLow;
        BoundHigh = boundHigh;
        MogpFlags = mogpFlags;
        GroupWmoId = groupWmoId;
        _vertices = vertices;
        _triangles = triangles;
        MeshTree = meshTree ?? BihTree.Build([.. triangles.Select(TriangleBounds)]);
        Liquid = liquid;
    }

    public Vector3 BoundLow { get; }

    public Vector3 BoundHigh { get; }

    public uint MogpFlags { get; }

    public uint GroupWmoId { get; }

    public BihTree MeshTree { get; }

    public WmoLiquid? Liquid { get; }

    public IReadOnlyList<Vector3> Vertices => _vertices;

    public IReadOnlyList<MeshTriangle> Triangles => _triangles;

    /// <summary>Nearest triangle hit along the ray closer than <paramref name="distance"/> (both faces count).</summary>
    public bool IntersectRay(Vector3 origin, Vector3 direction, ref float distance, bool stopAtFirstHit)
    {
        if (_triangles.Length == 0)
        {
            return false;
        }

        bool hit = false;
        MeshTree.IntersectRay(origin, direction, ref distance, (int primitive, ref float max) =>
        {
            if ((uint)primitive >= (uint)_triangles.Length || !IntersectTriangle(_triangles[primitive], origin, direction, ref max))
            {
                return false;
            }

            hit = true;
            return true;
        }, stopAtFirstHit);
        return hit;
    }

    /// <summary>
    /// vmangos <c>GroupModel::IsInsideObject</c>: the point is inside the group's bound and a ray
    /// from it along <paramref name="down"/> meets the mesh; <paramref name="distance"/> is how far.
    /// </summary>
    public bool IsInside(Vector3 point, Vector3 down, out float distance)
    {
        distance = float.PositiveInfinity;
        if (_triangles.Length == 0 || !BihTree.Contains(BoundLow, BoundHigh, point))
        {
            return false;
        }

        return IntersectRay(point, down, ref distance, stopAtFirstHit: false);
    }

    /// <summary>
    /// Ray/triangle intersection (Möller–Trumbore; standard algorithm, both faces): true when the
    /// ray meets the triangle at 0 &lt; t &lt; <paramref name="distance"/>, which becomes t.
    /// </summary>
    internal bool IntersectTriangle(MeshTriangle triangle, Vector3 origin, Vector3 direction, ref float distance)
    {
        Vector3 v0 = _vertices[triangle.Index0];
        Vector3 edge1 = _vertices[triangle.Index1] - v0;
        Vector3 edge2 = _vertices[triangle.Index2] - v0;
        Vector3 p = Vector3.Cross(direction, edge2);
        float det = Vector3.Dot(edge1, p);
        if (MathF.Abs(det) < 1e-10f)
        {
            return false;
        }

        float inv = 1.0f / det;
        Vector3 s = origin - v0;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(s, edge1);
        float v = Vector3.Dot(direction, q) * inv;
        if (v < 0 || u + v > 1)
        {
            return false;
        }

        float t = Vector3.Dot(edge2, q) * inv;
        if (t > 0 && t < distance)
        {
            distance = t;
            return true;
        }

        return false;
    }

    private (Vector3 Low, Vector3 High) TriangleBounds(MeshTriangle t)
    {
        Vector3 a = _vertices[t.Index0];
        Vector3 b = _vertices[t.Index1];
        Vector3 c = _vertices[t.Index2];
        return (Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
    }

    /// <summary>
    /// Group record: f32[6] bound, u32 MOGP flags, u32 group WMO id; "VERT" u32 size u32 count
    /// f32[3]×count (a group with no vertices ends here); "TRIM" u32 size u32 count u32[3]×count;
    /// "MBIH" BIH; "LIQU" u32 size [liquid].
    /// <para>
    /// The LIQU size is only a presence flag: vmangos writes <c>WmoLiquid::GetFileSize()</c>, which
    /// leaves out the u32 liquid type and so is 4 bytes short, and its reader
    /// (<c>GroupModel::readFromFile</c>) ignores it and reads the liquid by its own grid. So does
    /// this reader; the grid is still checked against the bytes left in the file.
    /// </para>
    /// </summary>
    internal static GroupModel Read(ref CollisionDataReader reader)
    {
        Vector3 low = reader.ReadVector3();
        Vector3 high = reader.ReadVector3();
        uint mogpFlags = reader.ReadUInt32();
        uint groupWmoId = reader.ReadUInt32();

        reader.Expect("VERT");
        reader.ReadUInt32();
        int vertexCount = reader.ReadCount(12);
        if (vertexCount == 0)
        {
            return new GroupModel(low, high, mogpFlags, groupWmoId, [], [], BihTree.Empty);
        }

        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i] = reader.ReadVector3();
        }

        reader.Expect("TRIM");
        reader.ReadUInt32();
        int triangleCount = reader.ReadCount(12);
        var triangles = new MeshTriangle[triangleCount];
        for (int i = 0; i < triangleCount; i++)
        {
            triangles[i] = new MeshTriangle(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        }

        reader.Expect("MBIH");
        BihTree meshTree = BihTree.Read(ref reader);

        reader.Expect("LIQU");
        uint liquidSize = reader.ReadUInt32();
        WmoLiquid? liquid = liquidSize > 0 ? WmoLiquid.Read(ref reader) : null;

        return new GroupModel(low, high, mogpFlags, groupWmoId, vertices, triangles, meshTree, liquid);
    }

    internal void Write(BinaryWriter writer)
    {
        ModelSpawn.WriteVector(writer, BoundLow);
        ModelSpawn.WriteVector(writer, BoundHigh);
        writer.Write(MogpFlags);
        writer.Write(GroupWmoId);
        writer.Write("VERT"u8);
        writer.Write((uint)(4 + (12 * _vertices.Length)));
        writer.Write((uint)_vertices.Length);
        if (_vertices.Length == 0)
        {
            return;
        }

        foreach (Vector3 v in _vertices)
        {
            ModelSpawn.WriteVector(writer, v);
        }

        writer.Write("TRIM"u8);
        writer.Write((uint)(4 + (12 * _triangles.Length)));
        writer.Write((uint)_triangles.Length);
        foreach (MeshTriangle t in _triangles)
        {
            writer.Write(t.Index0);
            writer.Write(t.Index1);
            writer.Write(t.Index2);
        }

        writer.Write("MBIH"u8);
        MeshTree.Write(writer);
        writer.Write("LIQU"u8);
        if (Liquid is null)
        {
            writer.Write(0u);
        }
        else
        {
            using var buffer = new MemoryStream();
            using (var inner = new BinaryWriter(buffer, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                Liquid.Write(inner);
            }

            writer.Write(Liquid.VmangosChunkSize);
            writer.Write(buffer.ToArray());
        }
    }
}

/// <summary>WMO liquid of a group (vmangos <c>WmoLiquid</c>): tile grid, corner, type, heights and per-tile flags.</summary>
public sealed record WmoLiquid(uint TilesX, uint TilesY, Vector3 Corner, uint Type, float[] Heights, byte[] Flags)
{
    /// <summary>
    /// The LIQU chunk size vmangos stores (<c>WmoLiquid::GetFileSize</c>): the serialized size
    /// without the u32 type field, i.e. 4 bytes short. Written as-is so files stay byte-identical.
    /// </summary>
    internal uint VmangosChunkSize => (uint)((2 * 4) + 12 + (Heights.Length * 4) + Flags.Length);

    /// <summary>u32 tiles X, u32 tiles Y, f32[3] corner, u32 type, f32 heights[(X+1)(Y+1)], u8 flags[X·Y].</summary>
    internal static WmoLiquid Read(ref CollisionDataReader reader, int chunkEnd = int.MaxValue)
    {
        uint tilesX = reader.ReadUInt32();
        uint tilesY = reader.ReadUInt32();
        Vector3 corner = reader.ReadVector3();
        uint type = reader.ReadUInt32();

        // Compare the counts to what the LIQU chunk can hold BEFORE multiplying: the untrusted
        // grid dimensions must never reach an overflowing product (AC-PI-001).
        int available = Math.Min(reader.Remaining, chunkEnd - reader.Position);
        if (available < 0)
        {
            throw new InvalidDataException("liquid header runs past its chunk");
        }

        long maxHeights = available / 4;
        long width = (long)tilesX + 1;
        long depth = (long)tilesY + 1;
        if (width > maxHeights || depth > maxHeights || width * depth > maxHeights)
        {
            throw new InvalidDataException($"liquid {tilesX}×{tilesY} does not fit in the {available} bytes left");
        }

        long heightCount = width * depth;
        long flagCount = (long)tilesX * tilesY;
        if ((heightCount * 4) + flagCount > available)
        {
            throw new InvalidDataException($"liquid {tilesX}×{tilesY} does not fit in the {available} bytes left");
        }

        var heights = new float[heightCount];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = reader.ReadSingle();
        }

        byte[] flags = reader.ReadBytes((int)flagCount).ToArray();
        return new WmoLiquid(tilesX, tilesY, corner, type, heights, flags);
    }

    internal void Write(BinaryWriter writer)
    {
        writer.Write(TilesX);
        writer.Write(TilesY);
        ModelSpawn.WriteVector(writer, Corner);
        writer.Write(Type);
        foreach (float h in Heights)
        {
            writer.Write(h);
        }

        writer.Write(Flags);
    }
}

/// <summary>
/// A model file (<c>.vmo</c>, vmangos <c>WorldModel</c>): the root WMO id, its groups and the
/// group BIH. Coordinates are model space.
/// </summary>
public sealed class WorldModel
{
    public WorldModel(uint rootWmoId, IReadOnlyList<GroupModel> groups, BihTree? groupTree = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        RootWmoId = rootWmoId;
        Groups = groups;
        GroupTree = groupTree ?? BihTree.Build([.. groups.Select(g => (g.BoundLow, g.BoundHigh))], leafSize: 1);
    }

    public uint RootWmoId { get; }

    public IReadOnlyList<GroupModel> Groups { get; }

    public BihTree GroupTree { get; }

    /// <summary>Nearest hit over every group (a single group is tested directly, as vmangos does).</summary>
    public bool IntersectRay(Vector3 origin, Vector3 direction, ref float distance, bool stopAtFirstHit)
    {
        if (Groups.Count == 0)
        {
            return false;
        }

        if (Groups.Count == 1)
        {
            return Groups[0].IntersectRay(origin, direction, ref distance, stopAtFirstHit);
        }

        bool hit = false;
        GroupTree.IntersectRay(origin, direction, ref distance, (int primitive, ref float max) =>
        {
            if ((uint)primitive >= (uint)Groups.Count || !Groups[primitive].IntersectRay(origin, direction, ref max, stopAtFirstHit))
            {
                return false;
            }

            hit = true;
            return true;
        }, stopAtFirstHit);
        return hit;
    }

    /// <summary>
    /// vmangos <c>WorldModel::IntersectPoint</c>: the group enclosing the point whose floor (along
    /// <paramref name="down"/>) is nearest; false when the point is in no group.
    /// </summary>
    public bool TryFindGroup(Vector3 point, Vector3 down, out GroupModel? group, out float distance)
    {
        GroupModel? best = null;
        float bestDistance = float.PositiveInfinity;
        GroupTree.IntersectPoint(point, primitive =>
        {
            if ((uint)primitive < (uint)Groups.Count && Groups[primitive].IsInside(point, down, out float d) && d < bestDistance)
            {
                bestDistance = d;
                best = Groups[primitive];
            }
        });
        group = best;
        distance = bestDistance;
        return best is not null;
    }

    /// <summary>"VMAP_7.0" "WMOD" u32 size u32 root WMO id, then optionally "GMOD" u32 count, groups, "GBIH" BIH.</summary>
    public static WorldModel Parse(ReadOnlySpan<byte> data)
    {
        var reader = new CollisionDataReader(data);
        reader.Expect(VMapFormat.Magic);
        reader.Expect("WMOD");
        reader.ReadUInt32();
        uint rootId = reader.ReadUInt32();
        if (!reader.TryExpect("GMOD"))
        {
            return new WorldModel(rootId, [], BihTree.Empty);
        }

        int count = reader.ReadCount(32);
        var groups = new GroupModel[count];
        for (int i = 0; i < count; i++)
        {
            groups[i] = GroupModel.Read(ref reader);
        }

        reader.Expect("GBIH");
        BihTree tree = BihTree.Read(ref reader);
        return new WorldModel(rootId, groups, tree);
    }

    /// <summary>Serialize in the same layout (fixtures and tools).</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(VMapFormat.Magic));
            writer.Write("WMOD"u8);
            writer.Write(8u);
            writer.Write(RootWmoId);
            if (Groups.Count > 0)
            {
                writer.Write("GMOD"u8);
                writer.Write((uint)Groups.Count);
                foreach (GroupModel group in Groups)
                {
                    group.Write(writer);
                }

                writer.Write("GBIH"u8);
                GroupTree.Write(writer);
            }
        }

        return stream.ToArray();
    }
}

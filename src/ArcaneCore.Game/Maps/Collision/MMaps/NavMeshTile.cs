using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.MMaps;

/// <summary>A navmesh polygon (<c>dtPoly</c>): vertex indices, neighbour entries, flags, area and type.</summary>
public sealed record NavPoly(ushort[] Vertices, ushort[] Neighbours, ushort Flags, byte Area, byte Type)
{
    public int VertexCount => Vertices.Length;

    public bool IsOffMeshConnection => Type == NavMeshFormat.PolyTypeOffMeshConnection;
}

/// <summary>A polygon's detail sub-mesh (<c>dtPolyDetail</c>).</summary>
public readonly record struct NavPolyDetail(uint VertexBase, uint TriangleBase, byte VertexCount, byte TriangleCount);

/// <summary>
/// One Detour tile (<c>dtMeshHeader</c> and its sections), read for queries: vertices, polygons
/// and detail meshes. Links, the BV tree and off-mesh connections are skipped: this reader
/// derives connectivity from the polygons' neighbour entries (docs/integration/vmap-los.md).
/// Coordinates are Recast space.
/// </summary>
public sealed class NavMeshTile
{
    private const int HeaderSize = 100;
    private const int PolySize = 32;
    private readonly Vector3[] _polyLow;
    private readonly Vector3[] _polyHigh;
    private Dictionary<int, List<BorderEdge>>? _borderEdges;

    private NavMeshTile(int x, int y, int layer, Vector3 low, Vector3 high, float walkableClimb, Vector3[] vertices, NavPoly[] polys, NavPolyDetail[] details, Vector3[] detailVertices, byte[] detailTriangles)
    {
        X = x;
        Y = y;
        Layer = layer;
        BoundLow = low;
        BoundHigh = high;
        WalkableClimb = walkableClimb;
        Vertices = vertices;
        Polys = polys;
        Details = details;
        DetailVertices = detailVertices;
        DetailTriangles = detailTriangles;
        _polyLow = new Vector3[polys.Length];
        _polyHigh = new Vector3[polys.Length];
        for (int i = 0; i < polys.Length; i++)
        {
            Vector3 lo = new(float.MaxValue);
            Vector3 hi = new(float.MinValue);
            foreach (ushort v in polys[i].Vertices)
            {
                lo = Vector3.Min(lo, vertices[v]);
                hi = Vector3.Max(hi, vertices[v]);
            }

            _polyLow[i] = lo;
            _polyHigh[i] = hi;
        }
    }

    /// <summary>Detour tile coordinates (header x, y): the key neighbours are found by.</summary>
    public int X { get; }

    public int Y { get; }

    public int Layer { get; }

    public Vector3 BoundLow { get; }

    public Vector3 BoundHigh { get; }

    public float WalkableClimb { get; }

    public Vector3[] Vertices { get; }

    public NavPoly[] Polys { get; }

    public NavPolyDetail[] Details { get; }

    public Vector3[] DetailVertices { get; }

    /// <summary>Four bytes per detail triangle: three vertex indices and a flags byte.</summary>
    public byte[] DetailTriangles { get; }

    /// <summary>An <c>.mmtile</c>: the generator header, then the Detour tile data.</summary>
    public static NavMeshTile ParseFile(ReadOnlySpan<byte> file)
    {
        var reader = new CollisionDataReader(file);
        MmapTileHeader header = MmapTileHeader.Read(ref reader);
        if (header.Magic != NavMeshFormat.MmapMagic)
        {
            throw new InvalidDataException($"bad mmap magic 0x{header.Magic:x8}");
        }

        if (header.DetourVersion != NavMeshFormat.DetourVersion || header.MmapVersion != NavMeshFormat.MmapVersion)
        {
            throw new InvalidDataException($"mmap tile version {header.DetourVersion}/{header.MmapVersion}, expected {NavMeshFormat.DetourVersion}/{NavMeshFormat.MmapVersion}");
        }

        if (header.Size > reader.Remaining)
        {
            throw new InvalidDataException($"tile data size {header.Size} exceeds the {reader.Remaining} bytes left");
        }

        return Parse(reader.ReadBytes((int)header.Size));
    }

    /// <summary>
    /// Detour tile data: a 100-byte <c>dtMeshHeader</c> then 4-byte aligned sections (vertices,
    /// polygons, links, detail meshes, detail vertices, detail triangles, BV nodes, off-mesh
    /// connections). The link size (12 or 16 bytes, 32- or 64-bit polygon refs) is inferred from
    /// the data size; anything inconsistent is rejected.
    /// </summary>
    public static NavMeshTile Parse(ReadOnlySpan<byte> data)
    {
        var reader = new CollisionDataReader(data);
        if (reader.ReadInt32() != NavMeshFormat.DetourMagic)
        {
            throw new InvalidDataException("not a Detour tile (bad magic)");
        }

        int version = reader.ReadInt32();
        if (version != NavMeshFormat.DetourVersion)
        {
            throw new InvalidDataException($"Detour tile version {version}, expected {NavMeshFormat.DetourVersion}");
        }

        int x = reader.ReadInt32();
        int y = reader.ReadInt32();
        int layer = reader.ReadInt32();
        reader.ReadUInt32(); // userId
        int polyCount = reader.ReadInt32();
        int vertCount = reader.ReadInt32();
        int maxLinkCount = reader.ReadInt32();
        int detailMeshCount = reader.ReadInt32();
        int detailVertCount = reader.ReadInt32();
        int detailTriCount = reader.ReadInt32();
        int bvNodeCount = reader.ReadInt32();
        int offMeshCount = reader.ReadInt32();
        reader.ReadInt32(); // offMeshBase
        reader.ReadSingle(); // walkableHeight
        reader.ReadSingle(); // walkableRadius
        float walkableClimb = reader.ReadSingle();
        Vector3 low = reader.ReadVector3();
        Vector3 high = reader.ReadVector3();
        reader.ReadSingle(); // bvQuantFactor

        int[] counts = [polyCount, vertCount, maxLinkCount, detailMeshCount, detailVertCount, detailTriCount, bvNodeCount, offMeshCount];
        if (counts.Any(c => c < 0 || c > 1 << 20))
        {
            throw new InvalidDataException("Detour tile counts are out of range");
        }

        long fixedSize = HeaderSize + Align4(12L * vertCount) + Align4((long)PolySize * polyCount) + Align4(12L * detailMeshCount)
            + Align4(12L * detailVertCount) + Align4(4L * detailTriCount) + Align4(16L * bvNodeCount) + Align4(36L * offMeshCount);
        int linkSize = fixedSize + Align4(16L * maxLinkCount) == data.Length ? 16
            : fixedSize + Align4(12L * maxLinkCount) == data.Length ? 12
            : throw new InvalidDataException($"Detour tile size {data.Length} does not match its header");

        reader.Seek(HeaderSize);
        var vertices = new Vector3[vertCount];
        for (int i = 0; i < vertCount; i++)
        {
            vertices[i] = reader.ReadVector3();
        }

        reader.Align4();
        var polys = new NavPoly[polyCount];
        for (int i = 0; i < polyCount; i++)
        {
            reader.ReadUInt32(); // firstLink
            ushort[] verts = new ushort[NavMeshFormat.MaxVertsPerPoly];
            ushort[] neis = new ushort[NavMeshFormat.MaxVertsPerPoly];
            for (int j = 0; j < verts.Length; j++)
            {
                verts[j] = reader.ReadUInt16();
            }

            for (int j = 0; j < neis.Length; j++)
            {
                neis[j] = reader.ReadUInt16();
            }

            ushort flags = reader.ReadUInt16();
            byte count = reader.ReadByte();
            byte areaAndType = reader.ReadByte();
            if (count > NavMeshFormat.MaxVertsPerPoly || (count < 3 && (areaAndType >> 6) != NavMeshFormat.PolyTypeOffMeshConnection))
            {
                throw new InvalidDataException($"polygon {i} has {count} vertices");
            }

            for (int j = 0; j < count; j++)
            {
                if (verts[j] >= vertCount)
                {
                    throw new InvalidDataException($"polygon {i} names vertex {verts[j]} of {vertCount}");
                }
            }

            polys[i] = new NavPoly(verts[..count], neis[..count], flags, (byte)(areaAndType & 0x3f), (byte)(areaAndType >> 6));
        }

        reader.Align4();
        reader.Skip((int)Align4((long)linkSize * maxLinkCount));
        var details = new NavPolyDetail[detailMeshCount];
        for (int i = 0; i < detailMeshCount; i++)
        {
            details[i] = new NavPolyDetail(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte(), reader.ReadByte());
            reader.Skip(2);
        }

        reader.Align4();
        var detailVertices = new Vector3[detailVertCount];
        for (int i = 0; i < detailVertCount; i++)
        {
            detailVertices[i] = reader.ReadVector3();
        }

        reader.Align4();
        byte[] detailTriangles = reader.ReadBytes(4 * detailTriCount).ToArray();
        return new NavMeshTile(x, y, layer, low, high, walkableClimb, vertices, polys, details, detailVertices, detailTriangles);
    }

    /// <summary>Whether a query box (centre ± extents) can touch this tile.</summary>
    public bool Overlaps(Vector3 center, Vector3 extents)
        => BoxesOverlap(center - extents, center + extents, BoundLow, BoundHigh);

    /// <summary>The polygon nearest to <paramref name="center"/> within the box, with the closest point on it.</summary>
    public bool TryFindNearestPoly(Vector3 center, Vector3 extents, NavTerrain include, NavTerrain exclude, out int poly, out Vector3 nearest, out float distanceSquared)
    {
        poly = -1;
        nearest = center;
        distanceSquared = float.MaxValue;
        Vector3 low = center - extents;
        Vector3 high = center + extents;
        for (int i = 0; i < Polys.Length; i++)
        {
            if (!Passes(i, include, exclude) || !BoxesOverlap(low, high, _polyLow[i], _polyHigh[i]))
            {
                continue;
            }

            // As Detour's findNearestPoly: over a polygon only the height difference beyond the
            // walkable climb counts, so the polygon under the point wins over a nearer slope.
            Vector3 closest = ClosestPointOnPoly(i, center, out bool over);
            float climbExcess = MathF.Max(0, MathF.Abs(closest.Y - center.Y) - WalkableClimb);
            float d = over ? climbExcess * climbExcess : Vector3.DistanceSquared(closest, center);
            if (d < distanceSquared)
            {
                poly = i;
                nearest = closest;
                distanceSquared = d;
            }
        }

        return poly >= 0;
    }

    /// <summary>Detour <c>dtQueryFilter::passFilter</c>: some include flag, no exclude flag, not an off-mesh link.</summary>
    public bool Passes(int poly, NavTerrain include, NavTerrain exclude)
    {
        NavPoly p = Polys[poly];
        return !p.IsOffMeshConnection && (p.Flags & (ushort)include) != 0 && (p.Flags & (ushort)exclude) == 0;
    }

    /// <summary>The point on the polygon closest to <paramref name="position"/> (inside: straight below or above it).</summary>
    public Vector3 ClosestPointOnPoly(int poly, Vector3 position) => ClosestPointOnPoly(poly, position, out _);

    /// <summary>As <see cref="ClosestPointOnPoly(int, Vector3)"/>; <paramref name="over"/> tells whether the point is inside the polygon in x/z.</summary>
    public Vector3 ClosestPointOnPoly(int poly, Vector3 position, out bool over)
    {
        NavPoly p = Polys[poly];
        over = ContainsXz(p, position);
        if (over)
        {
            return position with { Y = GetPolyHeight(poly, position) };
        }

        Vector3 best = Vertices[p.Vertices[0]];
        float bestDistance = float.MaxValue;
        for (int i = 0, j = p.VertexCount - 1; i < p.VertexCount; j = i++)
        {
            Vector3 a = Vertices[p.Vertices[j]];
            Vector3 b = Vertices[p.Vertices[i]];
            float t = ClosestT(a, b, position);
            Vector3 c = Vector3.Lerp(a, b, t);
            float d = DistanceXzSquared(c, position);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }

        return best;
    }

    /// <summary>Surface height at a point inside the polygon: the detail mesh when present, else a fan over the polygon.</summary>
    public float GetPolyHeight(int poly, Vector3 position)
    {
        NavPoly p = Polys[poly];
        if (poly < Details.Length)
        {
            NavPolyDetail detail = Details[poly];
            for (int t = 0; t < detail.TriangleCount; t++)
            {
                long index = ((long)detail.TriangleBase + t) * 4;
                if (index + 3 > DetailTriangles.Length)
                {
                    break;
                }

                if (TryDetailVertex(p, detail, DetailTriangles[index], out Vector3 a)
                    && TryDetailVertex(p, detail, DetailTriangles[index + 1], out Vector3 b)
                    && TryDetailVertex(p, detail, DetailTriangles[index + 2], out Vector3 c)
                    && TryTriangleHeight(position, a, b, c, out float h))
                {
                    return h;
                }
            }
        }

        for (int i = 1; i + 1 < p.VertexCount; i++)
        {
            if (TryTriangleHeight(position, Vertices[p.Vertices[0]], Vertices[p.Vertices[i]], Vertices[p.Vertices[i + 1]], out float h))
            {
                return h;
            }
        }

        return p.Vertices.Average(v => Vertices[v].Y);
    }

    public Vector3 Center(int poly)
    {
        Vector3 sum = Vector3.Zero;
        foreach (ushort v in Polys[poly].Vertices)
        {
            sum += Vertices[v];
        }

        return sum / Polys[poly].VertexCount;
    }

    /// <summary>The edges of this tile's polygons that lie on a border side, for joining with the neighbour tile.</summary>
    internal List<BorderEdge> GetBorderEdges(int side)
    {
        _borderEdges ??= [];
        if (!_borderEdges.TryGetValue(side, out List<BorderEdge>? edges))
        {
            edges = [];
            for (int i = 0; i < Polys.Length; i++)
            {
                NavPoly p = Polys[i];
                for (int e = 0; e < p.VertexCount; e++)
                {
                    ushort nei = p.Neighbours[e];
                    if ((nei & NavMeshFormat.ExternalLink) != 0 && (nei & 0xff) == side)
                    {
                        edges.Add(new BorderEdge(i, Vertices[p.Vertices[e]], Vertices[p.Vertices[(e + 1) % p.VertexCount]]));
                    }
                }
            }

            _borderEdges[side] = edges;
        }

        return edges;
    }

    internal static bool ContainsXz(IReadOnlyList<Vector3> polygon, Vector3 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector3 a = polygon[i];
            Vector3 b = polygon[j];
            if (((a.Z > p.Z) != (b.Z > p.Z)) && (p.X < ((b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z)) + a.X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    internal static float DistanceXzSquared(Vector3 a, Vector3 b) => ((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Parameter of the point on segment a–b closest (in x/z) to p.</summary>
    internal static float ClosestT(Vector3 a, Vector3 b, Vector3 p)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float length = (dx * dx) + (dz * dz);
        return length <= 1e-12f ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Z - a.Z) * dz)) / length, 0, 1);
    }

    private bool ContainsXz(NavPoly p, Vector3 position)
        => ContainsXz([.. p.Vertices.Select(v => Vertices[v])], position);

    private bool TryDetailVertex(NavPoly p, NavPolyDetail detail, byte index, out Vector3 vertex)
    {
        if (index < p.VertexCount)
        {
            vertex = Vertices[p.Vertices[index]];
            return true;
        }

        long i = (long)detail.VertexBase + index - p.VertexCount;
        vertex = i < DetailVertices.Length ? DetailVertices[i] : default;
        return i < DetailVertices.Length;
    }

    private static bool TryTriangleHeight(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float height)
    {
        height = 0;
        float v0x = c.X - a.X, v0z = c.Z - a.Z, v1x = b.X - a.X, v1z = b.Z - a.Z, v2x = p.X - a.X, v2z = p.Z - a.Z;
        float denominator = (v0x * v1z) - (v0z * v1x);
        if (MathF.Abs(denominator) < 1e-12f)
        {
            return false;
        }

        float u = ((v1z * v2x) - (v1x * v2z)) / denominator;
        float v = ((v0x * v2z) - (v0z * v2x)) / denominator;
        const float eps = 1e-4f;
        if (u < -eps || v < -eps || u + v > 1 + eps)
        {
            return false;
        }

        height = a.Y + ((c.Y - a.Y) * u) + ((b.Y - a.Y) * v);
        return true;
    }

    private static bool BoxesOverlap(Vector3 aLow, Vector3 aHigh, Vector3 bLow, Vector3 bHigh)
        => aLow.X <= bHigh.X && aHigh.X >= bLow.X && aLow.Y <= bHigh.Y && aHigh.Y >= bLow.Y && aLow.Z <= bHigh.Z && aHigh.Z >= bLow.Z;

    private static long Align4(long size) => (size + 3) & ~3L;
}

/// <summary>A polygon edge on a tile border (Recast space).</summary>
internal readonly record struct BorderEdge(int Poly, Vector3 A, Vector3 B);

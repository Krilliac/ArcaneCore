using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.MMaps;

/// <summary>
/// A navmesh polygon (<c>dtPoly</c>): vertex indices, neighbour entries, flags, area and type. A value inside its tile's polygon
/// array: the indices live in one array per tile (<see cref="NavMeshFormat.MaxVertsPerPoly"/> vertex slots, then as many neighbour
/// slots, per polygon). It was a class with two small arrays of its own; with ~1,000 bots on two continents that was 1.6 million
/// polygon objects and 3.3 million index arrays live in gen2 (gcdump 2026-10-17 20:05 CT), all traced by every full collection
/// (multithreading plan W9).
/// </summary>
public readonly struct NavPoly
{
    private readonly ushort[] _indices;
    private readonly int _offset;
    private readonly byte _vertexCount;

    internal NavPoly(ushort[] indices, int offset, byte vertexCount, ushort flags, byte area, byte type)
    {
        _indices = indices;
        _offset = offset;
        _vertexCount = vertexCount;
        Flags = flags;
        Area = area;
        Type = type;
    }

    public ushort Flags { get; }

    public byte Area { get; }

    public byte Type { get; }

    public int VertexCount => _vertexCount;

    /// <summary>The vertex indices (into the tile's vertices), <see cref="VertexCount"/> of them. No copy.</summary>
    public ArraySegment<ushort> Vertices => new(_indices, _offset, _vertexCount);

    /// <summary>The neighbour entries, one per edge (0 wall, internal polygon + 1, or an external-link side). No copy.</summary>
    public ArraySegment<ushort> Neighbours => new(_indices, _offset + NavMeshFormat.MaxVertsPerPoly, _vertexCount);

    /// <summary>Vertex index <paramref name="i"/> (hot paths: no segment).</summary>
    internal ushort Vertex(int i) => _indices[_offset + i];

    /// <summary>The neighbour entry of edge <paramref name="i"/> (hot paths: no segment).</summary>
    internal ushort Neighbour(int i) => _indices[_offset + NavMeshFormat.MaxVertsPerPoly + i];

    public bool IsOffMeshConnection => Type == NavMeshFormat.PolyTypeOffMeshConnection;
}

/// <summary>A polygon's detail sub-mesh (<c>dtPolyDetail</c>).</summary>
public readonly record struct NavPolyDetail(uint VertexBase, uint TriangleBase, byte VertexCount, byte TriangleCount);

/// <summary>
/// One Detour tile (<c>dtMeshHeader</c> and its sections), read for queries: vertices, polygons,
/// detail meshes and the BV tree. Links and off-mesh connections are skipped: this reader
/// derives connectivity from the polygons' neighbour entries (docs/integration/vmap-los.md).
/// Coordinates are Recast space.
/// </summary>
public sealed class NavMeshTile
{
    private const int HeaderSize = 100;
    private const int PolySize = 32;

    /// <summary>The most a leaf's quantized box may fall short of its polygon's own box before the BV tree is not used (see <see cref="TryVerifyBvTree"/>).</summary>
    private const int MaxBvSlack = 4;

    private readonly Vector3[] _polyLow;

    

    /// <summary>Each polygon's vertex average (<see cref="Center"/>), worked out once: every A* expansion asks for it (3.6% of the live world thread, Oct 17).</summary>

    private readonly Vector3[] _polyCenter;
    private readonly Vector3[] _polyHigh;
    private readonly BvNode[]? _bv;
    private readonly int _bvCount; // nodes of _bv in use: the generator writes 2n slots for a 2n-1 node tree, kept rather than copied (LOH)
    private readonly float _bvQuantFactor;
    private readonly int _bvSlack;
    // Lazily built caches. A search fills them, and searches may run on several threads at once (path jobs beside the world thread), so
    // each is published whole: a racing reader builds its own copy and one of them wins; nothing is ever seen half built.
    private readonly List<BorderEdge>?[] _borderEdges = new List<BorderEdge>?[8];
    private ExternalEdges? _externalEdges;

    private NavMeshTile(int x, int y, int layer, Vector3 low, Vector3 high, float walkableClimb, Vector3[] vertices, NavPoly[] polys, NavPolyDetail[] details, Vector3[] detailVertices, byte[] detailTriangles, BvNode[] bvNodes, float bvQuantFactor)
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
        _polyCenter = new Vector3[polys.Length];
        for (int i = 0; i < polys.Length; i++)
        {
            Vector3 lo = new(float.MaxValue);
            Vector3 hi = new(float.MinValue);
            Vector3 sum = Vector3.Zero;
            NavPoly poly = polys[i];
            for (int j = 0; j < poly.VertexCount; j++)
            {
                Vector3 v = vertices[poly.Vertex(j)];
                lo = Vector3.Min(lo, v);
                hi = Vector3.Max(hi, v);
                sum += v;
            }

            _polyLow[i] = lo;
            _polyHigh[i] = hi;
            _polyCenter[i] = sum / poly.VertexCount; // the same sum, in the same order, as the former per-call Center
        }

        if (TryVerifyBvTree(bvNodes, bvQuantFactor, out int slack, out int used))
        {
            _bv = bvNodes;
            _bvCount = used;
            _bvQuantFactor = bvQuantFactor;
            _bvSlack = slack;
        }
        else
        {
            PathSearchCounters.NoteBvTreeRejected(); // nearest-polygon lookups on this tile scan every polygon
        }
    }

    /// <summary>Whether the file's BV tree passed <see cref="TryVerifyBvTree"/> and nearest-polygon queries use it.</summary>
    internal bool HasBvTree => _bv is not null;

    /// <summary>Bytes the BV tree adds to this tile (16 per node), 0 when it is not kept.</summary>
    internal int BvTreeBytes => (_bv is null ? 0 : _bvCount) * 16;

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


    private const uint CmangosMmapVersion = 8;

    /// <summary>An <c>.mmtile</c>: the generator header, then the Detour tile data.</summary>
    public static NavMeshTile ParseFile(ReadOnlySpan<byte> file)
    {
        var reader = new CollisionDataReader(file);
        MmapTileHeader header = MmapTileHeader.Read(ref reader);
        if (header.Magic != NavMeshFormat.MmapMagic)
        {
            throw new InvalidDataException($"bad mmap magic 0x{header.Magic:x8}");
        }

        if (header.MmapVersion != NavMeshFormat.MmapVersion && header.MmapVersion == CmangosMmapVersion)
        {
            // mangos-classic MoveMapSharedDefines.h:26 writes MMAP_VERSION 8 (and 32-bit refs); vmangos reads only 6.
            throw new InvalidDataException($"mmap tile version {header.MmapVersion} comes from the cMaNGOS generator; regenerate with vmangos' MoveMapGen (version {NavMeshFormat.MmapVersion})");
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
        float bvQuantFactor = reader.ReadSingle();

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
        const int stride = 2 * NavMeshFormat.MaxVertsPerPoly;
        ushort[] indices = new ushort[polyCount * stride]; // one array for the tile: dtPoly's verts[6] then neis[6], as on disk
        for (int i = 0; i < polyCount; i++)
        {
            reader.ReadUInt32(); // firstLink
            int at = i * stride;
            for (int j = 0; j < stride; j++)
            {
                indices[at + j] = reader.ReadUInt16();
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
                if (indices[at + j] >= vertCount)
                {
                    throw new InvalidDataException($"polygon {i} names vertex {indices[at + j]} of {vertCount}");
                }
            }

            polys[i] = new NavPoly(indices, at, count, flags, (byte)(areaAndType & 0x3f), (byte)(areaAndType >> 6));
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
        reader.Align4();
        var bvNodes = new BvNode[bvNodeCount];
        for (int i = 0; i < bvNodeCount; i++)
        {
            // dtBVNode: u16 bmin[3], u16 bmax[3], i32 i (polygon index for a leaf, minus the escape distance otherwise).
            ushort minX = reader.ReadUInt16(), minY = reader.ReadUInt16(), minZ = reader.ReadUInt16();
            ushort maxX = reader.ReadUInt16(), maxY = reader.ReadUInt16(), maxZ = reader.ReadUInt16();
            bvNodes[i] = new BvNode(minX, minY, minZ, maxX, maxY, maxZ, reader.ReadInt32());
        }

        return new NavMeshTile(x, y, layer, low, high, walkableClimb, vertices, polys, details, detailVertices, detailTriangles, bvNodes, bvQuantFactor);
    }

    /// <summary>
    /// Whether the file's BV tree can stand in for the linear polygon scan without changing a result. The tree is only a
    /// pre-filter (every candidate still passes the exact polygon-box test), so it must never drop a polygon whose box overlaps
    /// the query. That holds when: the tree is well formed (nodes nest by escape index, each polygon is a leaf once, a node's
    /// box contains its children's); and each leaf's quantized box covers its polygon's box quantized the way a query is
    /// (<see cref="Quantize"/>), give or take <paramref name="slack"/> units. The slack is measured here, at load, as the
    /// largest shortfall over all leaves (float rounding in the generator's quantization can cost a unit); queries widen by it.
    /// Anything else (no tree, a malformed one, slack above <see cref="MaxBvSlack"/>) keeps the linear scan.
    /// </summary>
    private bool TryVerifyBvTree(BvNode[] nodes, float factor, out int slack, out int used)
    {
        slack = 0;
        used = 0;
        if (nodes.Length == 0 || !float.IsFinite(factor) || factor <= 0 || !IsFinite(BoundLow) || !IsFinite(BoundHigh)
            || BoundLow.X > BoundHigh.X || BoundLow.Y > BoundHigh.Y || BoundLow.Z > BoundHigh.Z)
        {
            return false;
        }

        // The generator allocates 2 * polygons nodes but a tree over n polygons has 2n - 1: the root's escape distance is the real length.
        long root = nodes[0].Index < 0 ? -(long)nodes[0].Index : 1;
        if (root > nodes.Length)
        {
            return false;
        }

        used = (int)root;
        var seen = new bool[Polys.Length];
        var open = new int[used]; // indices of the internal nodes whose subtree contains the current node
        int depth = 0;
        for (int index = 0; index < used; index++)
        {
            while (depth > 0 && index >= (long)open[depth - 1] - nodes[open[depth - 1]].Index)
            {
                depth--;
            }

            BvNode node = nodes[index];
            if (index > 0)
            {
                if (depth == 0)
                {
                    return false; // a second root
                }

                BvNode parent = nodes[open[depth - 1]];
                if (node.MinX < parent.MinX || node.MinY < parent.MinY || node.MinZ < parent.MinZ
                    || node.MaxX > parent.MaxX || node.MaxY > parent.MaxY || node.MaxZ > parent.MaxZ
                    || (node.Index < 0 && (long)index - node.Index > (long)open[depth - 1] - parent.Index))
                {
                    return false;
                }
            }

            if (node.Index < 0)
            {
                long end = (long)index - node.Index;
                if (end > used)
                {
                    return false;
                }

                open[depth++] = index;
                continue;
            }

            if (node.Index >= Polys.Length || seen[node.Index])
            {
                return false;
            }

            seen[node.Index] = true;
            if (Polys[node.Index].IsOffMeshConnection)
            {
                continue;
            }

            Vector3 lo = _polyLow[node.Index];
            Vector3 hi = _polyHigh[node.Index];
            if (!IsFinite(lo) || !IsFinite(hi))
            {
                return false;
            }

            slack = Math.Max(slack, Math.Max(node.MinX - Quantize(lo.X, BoundLow.X, BoundHigh.X, factor), Quantize(hi.X, BoundLow.X, BoundHigh.X, factor) - node.MaxX));
            slack = Math.Max(slack, Math.Max(node.MinY - Quantize(lo.Y, BoundLow.Y, BoundHigh.Y, factor), Quantize(hi.Y, BoundLow.Y, BoundHigh.Y, factor) - node.MaxY));
            slack = Math.Max(slack, Math.Max(node.MinZ - Quantize(lo.Z, BoundLow.Z, BoundHigh.Z, factor), Quantize(hi.Z, BoundLow.Z, BoundHigh.Z, factor) - node.MaxZ));
            if (slack > MaxBvSlack)
            {
                return false;
            }
        }

        for (int i = 0; i < Polys.Length; i++)
        {
            if (!seen[i] && !Polys[i].IsOffMeshConnection)
            {
                return false; // a polygon the tree would never offer
            }
        }

        return true;
    }

    /// <summary>
    /// A coordinate in the tree's units: clamped to the tile box, relative to its low corner, scaled by <c>bvQuantFactor</c> and
    /// truncated (Detour <c>queryPolygonsInTile</c>, and the generator's <c>createBVTree</c>, in <c>DetourNavMeshQuery.cpp</c> and
    /// <c>DetourNavMeshBuilder.cpp</c>). Monotone in <paramref name="value"/>, which the tree check relies on. Saturates at 65535.
    /// </summary>
    private static int Quantize(float value, float low, float high, float factor)
    {
        float scaled = (MathF.Min(MathF.Max(value, low), high) - low) * factor;
        return scaled >= 65535f ? 65535 : (int)scaled;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Whether a query box (centre ± extents) can touch this tile.</summary>
    public bool Overlaps(Vector3 center, Vector3 extents)
        => BoxesOverlap(center - extents, center + extents, BoundLow, BoundHigh);

    /// <summary>The polygon nearest to <paramref name="center"/> within the box, with the closest point on it.</summary>
    public bool TryFindNearestPoly(Vector3 center, Vector3 extents, NavTerrain include, NavTerrain exclude, out int poly, out Vector3 nearest, out float distanceSquared)
        => FindNearestPoly(center, extents, include, exclude, _bv is not null, out poly, out nearest, out distanceSquared);

    /// <summary>
    /// The answer of <see cref="TryFindNearestPoly"/> by the plain scan of every polygon's box, with no BV tree: the reference
    /// the tree-assisted query must equal.
    /// </summary>
    internal bool TryFindNearestPolyLinear(Vector3 center, Vector3 extents, NavTerrain include, NavTerrain exclude, out int poly, out Vector3 nearest, out float distanceSquared)
    {
        // The loop as it was before the BV tree, kept independent of FindNearestPoly/ConsiderPoly on purpose.
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

    private bool FindNearestPoly(Vector3 center, Vector3 extents, NavTerrain include, NavTerrain exclude, bool useBv, out int poly, out Vector3 nearest, out float distanceSquared)
    {
        poly = -1;
        nearest = center;
        distanceSquared = float.MaxValue;
        Vector3 low = center - extents;
        Vector3 high = center + extents;
        if (useBv && _bv is { } bv && IsFinite(low) && IsFinite(high))
        {
            // Detour queryPolygonsInTile: quantize the box into the tree's units (the low side rounded down to even, the high side up
            // to odd) and walk the nodes, skipping a subtree by its escape index when its box misses. Widened by the slack measured
            // at load so no polygon whose own box overlaps can be missed; the exact box test below still decides.
            float f = _bvQuantFactor;
            int s = _bvSlack;
            int minX = (Quantize(low.X, BoundLow.X, BoundHigh.X, f) & ~1) - s, maxX = (Quantize(high.X, BoundLow.X, BoundHigh.X, f) | 1) + s;
            int minY = (Quantize(low.Y, BoundLow.Y, BoundHigh.Y, f) & ~1) - s, maxY = (Quantize(high.Y, BoundLow.Y, BoundHigh.Y, f) | 1) + s;
            int minZ = (Quantize(low.Z, BoundLow.Z, BoundHigh.Z, f) & ~1) - s, maxZ = (Quantize(high.Z, BoundLow.Z, BoundHigh.Z, f) | 1) + s;
            int node = 0;
            int count = _bvCount;
            while (node < count)
            {
                ref readonly BvNode n = ref bv[node];
                bool overlap = minX <= n.MaxX && maxX >= n.MinX && minY <= n.MaxY && maxY >= n.MinY && minZ <= n.MaxZ && maxZ >= n.MinZ;
                if (n.Index >= 0)
                {
                    if (overlap)
                    {
                        ConsiderPoly(n.Index, center, low, high, include, exclude, ref poly, ref nearest, ref distanceSquared);
                    }

                    node++;
                }
                else
                {
                    node += overlap ? 1 : -n.Index;
                }
            }

            return poly >= 0;
        }

        for (int i = 0; i < Polys.Length; i++)
        {
            ConsiderPoly(i, center, low, high, include, exclude, ref poly, ref nearest, ref distanceSquared);
        }

        return poly >= 0;
    }

    /// <summary>
    /// One candidate of the nearest-polygon search. The scan visits polygons in index order and keeps the first strictly nearer one;
    /// the tree visits them in its own order, so an equal distance goes to the lower index to give the same winner.
    /// </summary>
    private void ConsiderPoly(int i, Vector3 center, Vector3 low, Vector3 high, NavTerrain include, NavTerrain exclude, ref int poly, ref Vector3 nearest, ref float distanceSquared)
    {
        if (!Passes(i, include, exclude) || !BoxesOverlap(low, high, _polyLow[i], _polyHigh[i]))
        {
            return;
        }

        // As Detour's findNearestPoly: over a polygon only the height difference beyond the
        // walkable climb counts, so the polygon under the point wins over a nearer slope.
        Vector3 closest = ClosestPointOnPoly(i, center, out bool over);
        float climbExcess = MathF.Max(0, MathF.Abs(closest.Y - center.Y) - WalkableClimb);
        float d = over ? climbExcess * climbExcess : Vector3.DistanceSquared(closest, center);
        if (d < distanceSquared || (d == distanceSquared && poly >= 0 && i < poly))
        {
            poly = i;
            nearest = closest;
            distanceSquared = d;
        }
    }

    /// <summary>Detour <c>dtQueryFilter::passFilter</c>: some include flag, no exclude flag, not an off-mesh link.</summary>
    public bool Passes(int poly, NavTerrain include, NavTerrain exclude)
    {
        NavPoly p = Polys[poly];
        return !p.IsOffMeshConnection && (p.Flags & (ushort)include) != 0 && (p.Flags & (ushort)exclude) == 0;
    }

    /// <summary>
    /// Whether <paramref name="position"/> lies inside polygon <paramref name="poly"/> in x/z, and the surface height there
    /// (<see cref="GetPolyHeight"/>). A bounding-box test first, so scanning many polygons is cheap. Allocation-free.
    /// </summary>
    internal bool TryHeightAtXz(int poly, Vector3 position, out float height)
    {
        height = 0;
        if (position.X < _polyLow[poly].X || position.X > _polyHigh[poly].X || position.Z < _polyLow[poly].Z || position.Z > _polyHigh[poly].Z
            || !ContainsXz(Polys[poly], position))
        {
            return false;
        }

        height = GetPolyHeight(poly, position);
        return true;
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

        Vector3 best = Vertices[p.Vertex(0)];
        float bestDistance = float.MaxValue;
        for (int i = 0, j = p.VertexCount - 1; i < p.VertexCount; j = i++)
        {
            Vector3 a = Vertices[p.Vertex(j)];
            Vector3 b = Vertices[p.Vertex(i)];
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
            if (TryTriangleHeight(position, Vertices[p.Vertex(0)], Vertices[p.Vertex(i)], Vertices[p.Vertex(i + 1)], out float h))
            {
                return h;
            }
        }

        double sum = 0;
        foreach (ushort v in p.Vertices)
        {
            sum += Vertices[v].Y;
        }

        return (float)(sum / p.VertexCount);
    }

    public Vector3 Center(int poly) => _polyCenter[poly];

    /// <summary>The edges of this tile's polygons that lie on a border side, for joining with the neighbour tile.</summary>
    internal List<BorderEdge> GetBorderEdges(int side)
    {
        List<BorderEdge>? edges = Volatile.Read(ref _borderEdges[side & 7]);
        if (edges is null)
        {
            edges = [];
            for (int i = 0; i < Polys.Length; i++)
            {
                NavPoly p = Polys[i];
                for (int e = 0; e < p.VertexCount; e++)
                {
                    ushort nei = p.Neighbour(e);
                    if ((nei & NavMeshFormat.ExternalLink) != 0 && (nei & 0xff) == side)
                    {
                        edges.Add(new BorderEdge(i, Vertices[p.Vertex(e)], Vertices[p.Vertex((e + 1) % p.VertexCount)]));
                    }
                }
            }

            edges = Interlocked.CompareExchange(ref _borderEdges[side & 7], edges, null) ?? edges;
        }

        return edges;
    }

    /// <summary>
    /// The polygons of <paramref name="other"/> (the tile across this edge's side) joined to edge <paramref name="edge"/> of
    /// <paramref name="poly"/>, with the shared segment, in the order the scan of <paramref name="other"/>'s opposite border
    /// edges finds them (<see cref="NavMesh.TryOverlap"/>). Built on first use per edge and kept: it depends only on the two tiles'
    /// immutable geometry, so a different <paramref name="other"/> (the neighbour was unloaded and read again) rebuilds it.
    /// The edge must carry an external link. World thread.
    /// </summary>
    internal CrossPortal[] GetCrossPortals(int poly, int edge, NavMeshTile other)
    {
        ExternalEdges table = Volatile.Read(ref _externalEdges) ?? BuildExternalEdgeKeys();
        int slot = Array.BinarySearch(table.Keys, (poly << 3) | edge);
        if (slot < 0)
        {
            return [];
        }

        CrossPortals? cached = Volatile.Read(ref table.Slots[slot]);
        if (cached is null || !ReferenceEquals(cached.Other, other))
        {
            cached = new CrossPortals(other, BuildCrossPortals(poly, edge, other));
            Volatile.Write(ref table.Slots[slot], cached); // immutable entry: a racing builder writes an equal one
        }

        return cached.Items;
    }

    /// <summary>Forget the portals built against <paramref name="other"/> (it was unloaded), so this tile does not keep it alive.</summary>
    internal void DropCrossPortalsTo(NavMeshTile other)
    {
        if (Volatile.Read(ref _externalEdges)?.Slots is not { } portals)
        {
            return;
        }

        for (int i = 0; i < portals.Length; i++)
        {
            if (portals[i] is { } p && ReferenceEquals(p.Other, other))
            {
                portals[i] = null;
            }
        }
    }

    /// <summary>Bytes held by the cross-tile portal tables built so far (keys, slots, and each table's items).</summary>
    internal long CrossPortalBytes
    {
        get
        {
            ExternalEdges? table = Volatile.Read(ref _externalEdges);
            long bytes = (table?.Keys.Length ?? 0) * 4L + (table?.Slots.Length ?? 0) * 8L;
            foreach (CrossPortals? p in table?.Slots ?? [])
            {
                bytes += p is null ? 0 : 32 + (p.Items.Length * 28L);
            }

            return bytes;
        }
    }

    private ExternalEdges BuildExternalEdgeKeys()
    {
        var keys = new List<int>();
        for (int i = 0; i < Polys.Length; i++)
        {
            NavPoly p = Polys[i];
            for (int e = 0; e < p.VertexCount; e++)
            {
                if ((p.Neighbour(e) & NavMeshFormat.ExternalLink) != 0)
                {
                    keys.Add((i << 3) | e); // ascending: polygon, then edge
                }
            }
        }

        var built = new ExternalEdges([.. keys], new CrossPortals?[keys.Count]);
        return Interlocked.CompareExchange(ref _externalEdges, built, null) ?? built;
    }

    private CrossPortal[] BuildCrossPortals(int poly, int edge, NavMeshTile other)
    {
        NavPoly p = Polys[poly];
        int side = p.Neighbour(edge) & 0xff;
        Vector3 a = Vertices[p.Vertex(edge)];
        Vector3 b = Vertices[p.Vertex((edge + 1) % p.VertexCount)];
        bool alongZ = side is 0 or 4; // the border is a line of constant x; edges run along z
        float climb = MathF.Max(MathF.Max(WalkableClimb, other.WalkableClimb), 0.5f);
        List<CrossPortal>? found = null;
        foreach (BorderEdge candidate in other.GetBorderEdges((side + 4) & 7))
        {
            if (NavMesh.TryOverlap(a, b, candidate.A, candidate.B, alongZ, climb, out Vector3 pa, out Vector3 pb))
            {
                (found ??= []).Add(new CrossPortal(candidate.Poly, pa, pb));
            }
        }

        return found is null ? [] : [.. found];
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

    /// <summary>As the list overload (same crossing-number test, same edge order), over the polygon's vertices without building a list.</summary>
    private bool ContainsXz(NavPoly p, Vector3 position)
    {
        bool inside = false;
        for (int i = 0, j = p.VertexCount - 1; i < p.VertexCount; j = i++)
        {
            Vector3 a = Vertices[p.Vertex(i)];
            Vector3 b = Vertices[p.Vertex(j)];
            if (((a.Z > position.Z) != (b.Z > position.Z)) && (position.X < ((b.X - a.X) * (position.Z - a.Z) / (b.Z - a.Z)) + a.X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private bool TryDetailVertex(NavPoly p, NavPolyDetail detail, byte index, out Vector3 vertex)
    {
        if (index < p.VertexCount)
        {
            vertex = Vertices[p.Vertex(index)];
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

/// <summary>A node of the Detour BV tree (<c>dtBVNode</c>): a quantized box, and a polygon index (leaf) or minus the escape distance.</summary>
internal readonly record struct BvNode(ushort MinX, ushort MinY, ushort MinZ, ushort MaxX, ushort MaxY, ushort MaxZ, int Index);

/// <summary>The part of a polygon edge shared with a polygon across a tile border: the neighbour's polygon and the overlap segment (Recast space).</summary>
internal readonly record struct CrossPortal(int Poly, Vector3 A, Vector3 B);

/// <summary>A tile's external (tile-border) edges, sorted (polygon &lt;&lt; 3 | edge), and the portal table built for each.</summary>
internal sealed class ExternalEdges(int[] keys, CrossPortals?[] slots)
{
    public int[] Keys { get; } = keys;

    public readonly CrossPortals?[] Slots = slots;
}

/// <summary>The portals of one tile-border edge against one neighbour tile.</summary>
internal sealed class CrossPortals(NavMeshTile other, CrossPortal[] items)
{
    public NavMeshTile Other { get; } = other;

    public CrossPortal[] Items { get; } = items;
}

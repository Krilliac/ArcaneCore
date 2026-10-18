using System.Numerics;
using ArcaneCore.Game.Maps.Collision.MMaps;

namespace ArcaneCore.Atlas.Tests.Reference.Navmesh;

/// <summary>An off-mesh connection of a synthetic tile (Recast axes: x = world Y, y = height, z = world X).</summary>
internal sealed record OffMeshSpec(Vector3 StartRecast, Vector3 EndRecast, float Radius, bool Bidirectional, ushort Flags = 1, uint UserId = 1);

/// <summary>
/// A synthetic navmesh tile: an N x N grid of square polygons covering one Detour tile, written in the vmangos layout (20-byte generator
/// prefix, Detour v7 data with 64-bit-reference links). Built only at test runtime; nothing derived from client data.
/// </summary>
internal sealed class TileSpec
{
    /// <summary>Terrain tile X (the file name). The Detour coordinates follow from it unless overridden.</summary>
    public required int TerrainX { get; init; }

    /// <summary>Terrain tile Y (the file name).</summary>
    public required int TerrainY { get; init; }

    /// <summary>Polygons per side.</summary>
    public int Cells { get; init; } = 4;

    /// <summary>Height of the flat mesh (Recast y).</summary>
    public float Height { get; init; } = 10f;

    /// <summary>Polygon flags of cell (i, j).</summary>
    public Func<int, int, ushort> Flags { get; init; } = static (_, _) => 1;

    /// <summary>Whether cell (i, j) has a polygon.</summary>
    public Func<int, int, bool> Present { get; init; } = static (_, _) => true;

    /// <summary>Off-mesh connections that start in this tile.</summary>
    public IReadOnlyList<OffMeshSpec> OffMesh { get; init; } = [];

    /// <summary>Write a bounding-volume tree (2n nodes, zero tail) as the generator does.</summary>
    public bool BvTree { get; init; } = true;

    /// <summary>Bytes per link in the file: 16 (64-bit references, vmangos) or 12.</summary>
    public int LinkSize { get; init; } = 16;

    /// <summary>Overrides the header's link capacity (a value that is too small makes DotRecast overrun the table).</summary>
    public int? MaxLinkCount { get; init; }

    /// <summary>The prefix's uses-liquids word.</summary>
    public bool UsesLiquids { get; init; }

    /// <summary>Overrides the header's Detour x (to make two files claim one slot).</summary>
    public int? DetourX { get; init; }

    /// <summary>Overrides the header's Detour y.</summary>
    public int? DetourY { get; init; }
}

/// <summary>Where each section of a built tile starts, in bytes from the start of the whole file.</summary>
internal sealed record TileLayout(int Prefix, int Header, int Verts, int Polys, int Links, int DetailMeshes, int DetailVerts, int DetailTris, int Bv, int OffMesh);

/// <summary>A synthetic tile's bytes and what it was built from.</summary>
internal sealed record BuiltTile(TileSpec Spec, byte[] File, TileLayout Layout, int DetourX, int DetourY, int PolyCount, int TotalPolys, int VertCount, int MaxLinkCount, int BvNodeCount);

/// <summary>The synthetic tile writer and its coordinate helpers.</summary>
internal static class SyntheticNav
{
    /// <summary>Tile edge in yards, as the generator writes it into <c>NNN.mmap</c>.</summary>
    public const float TileSize = 533.33331f;

    /// <summary>The Recast origin of the tile grid (-32 tiles on each axis).</summary>
    public const float OriginXz = -17066.666f;

    private const uint MmapMagic = 0x4d4d4150;
    private const int DetourMagic = ('D' << 24) | ('N' << 16) | ('A' << 8) | 'V';
    private const ushort ExternalLink = 0x8000;

    /// <summary>The Detour tile of a terrain tile: x follows the terrain tile's Y and y its X (the generator's axes are swapped).</summary>
    public static (int X, int Y) DetourOf(int terrainX, int terrainY) => (63 - terrainY, 63 - terrainX);

    /// <summary>The Recast-space minimum corner of a terrain tile's Detour tile (y 0).</summary>
    public static Vector3 TileMinRecast(int terrainX, int terrainY)
    {
        (int x, int y) = DetourOf(terrainX, terrainY);
        return new Vector3(OriginXz + (x * TileSize), 0, OriginXz + (y * TileSize));
    }

    /// <summary>Recast-space position of the centre of cell (<paramref name="i"/>, <paramref name="j"/>) of a tile.</summary>
    public static Vector3 CellCenterRecast(int terrainX, int terrainY, int cells, int i, int j, float height)
    {
        Vector3 min = TileMinRecast(terrainX, terrainY);
        float s = TileSize / cells;
        return new Vector3(min.X + ((i + 0.5f) * s), height, min.Z + ((j + 0.5f) * s));
    }

    /// <summary>World position of the centre of cell (<paramref name="i"/>, <paramref name="j"/>) of a tile.</summary>
    public static Vector3 CellCenterWorld(int terrainX, int terrainY, int cells, int i, int j, float height)
        => NavMeshFormat.ToWorld(CellCenterRecast(terrainX, terrainY, cells, i, j, height));

    /// <summary>The 28-byte <c>NNN.mmap</c> contents (maxPolys 0, as the generator writes with 64-bit references).</summary>
    public static byte[] BuildParams(int maxTiles)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(OriginXz);
            w.Write(0f);
            w.Write(OriginXz);
            w.Write(TileSize);
            w.Write(TileSize);
            w.Write(maxTiles);
            w.Write(0);
        }

        return stream.ToArray();
    }

    /// <summary>Builds the <c>.mmtile</c> bytes for a spec.</summary>
    public static BuiltTile Build(TileSpec spec)
    {
        int n = spec.Cells;
        float s = TileSize / n;
        (int detourX, int detourY) = DetourOf(spec.TerrainX, spec.TerrainY);
        detourX = spec.DetourX ?? detourX;
        detourY = spec.DetourY ?? detourY;
        float minX = OriginXz + (detourX * TileSize), maxX = OriginXz + ((detourX + 1) * TileSize);
        float minZ = OriginXz + (detourY * TileSize), maxZ = OriginXz + ((detourY + 1) * TileSize);
        float h = spec.Height;

        var verts = new List<Vector3>((n + 1) * (n + 1));
        for (int i = 0; i <= n; i++)
        {
            for (int j = 0; j <= n; j++)
            {
                verts.Add(new Vector3(i == n ? maxX : minX + (i * s), h, j == n ? maxZ : minZ + (j * s)));
            }
        }

        int[,] index = new int[n, n];
        int polyCount = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                index[i, j] = spec.Present(i, j) ? polyCount++ : -1;
            }
        }

        var polys = new List<PolyRecord>(polyCount);
        var cellOf = new List<(int I, int J)>(polyCount);
        int edgeCount = 0, portalCount = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (index[i, j] < 0)
                {
                    continue;
                }

                // Corner order (i,j) -> (i,j+1) -> (i+1,j+1) -> (i+1,j); edge k runs from corner k to corner k+1:
                // 0 faces -x, 1 faces +z, 2 faces +x, 3 faces -z (Detour sides 4, 2, 0, 6).
                int Vertex(int a, int b) => (a * (n + 1)) + b;
                ushort Neighbour(int ni, int nj, ushort side)
                    => ni < 0 || nj < 0 || ni >= n || nj >= n ? (ushort)(ExternalLink | side) : index[ni, nj] < 0 ? (ushort)0 : (ushort)(index[ni, nj] + 1);

                var record = new PolyRecord
                {
                    Verts = [(ushort)Vertex(i, j), (ushort)Vertex(i, j + 1), (ushort)Vertex(i + 1, j + 1), (ushort)Vertex(i + 1, j), 0, 0],
                    Neis = [Neighbour(i - 1, j, 4), Neighbour(i, j + 1, 2), Neighbour(i + 1, j, 0), Neighbour(i, j - 1, 6), 0, 0],
                    Flags = spec.Flags(i, j),
                    VertCount = 4,
                    AreaAndType = (byte)(1 + ((i + j) % 4)),
                };
                foreach (ushort nei in record.Neis.Take(4))
                {
                    edgeCount++;
                    if ((nei & ExternalLink) != 0)
                    {
                        portalCount++;
                    }
                }

                polys.Add(record);
                cellOf.Add((i, j));
            }
        }

        // Off-mesh connections: stored as two-vertex polygons after the mesh polygons; the vertices follow the mesh vertices.
        var offMeshPolys = new List<PolyRecord>();
        var offMeshRecords = new List<OffMeshSpec>();
        int offMeshLinks = 0;
        foreach (OffMeshSpec con in spec.OffMesh)
        {
            ushort first = (ushort)verts.Count;
            verts.Add(con.StartRecast);
            verts.Add(con.EndRecast);
            offMeshPolys.Add(new PolyRecord
            {
                Verts = [first, (ushort)(first + 1), 0, 0, 0, 0],
                Neis = [0, 0, 0, 0, 0, 0],
                Flags = con.Flags,
                VertCount = 2,
                AreaAndType = (byte)(1 | (1 << 6)),
            });
            offMeshRecords.Add(con);
            offMeshLinks += (Classify(con.StartRecast, minX, maxX, minZ, maxZ) == 0xff ? 1 : 0) + (Classify(con.EndRecast, minX, maxX, minZ, maxZ) == 0xff ? 1 : 0);
        }

        int maxLinkCount = spec.MaxLinkCount ?? (edgeCount + (portalCount * 2) + (offMeshLinks * 2));
        int detailTriCount = polyCount * 2;
        int bvNodeCount = spec.BvTree ? polyCount * 2 : 0;
        float quant = 1.0f;
        float boundsMinY = h - 10f, boundsMaxY = h + 10f;

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        for (int i = 0; i < 20; i++)
        {
            w.Write((byte)0);
        }

        int headerAt = (int)stream.Position;
        for (int i = 0; i < 100; i++)
        {
            w.Write((byte)0);
        }

        int vertsAt = (int)stream.Position;
        foreach (Vector3 v in verts)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        Align4(stream);
        int polysAt = (int)stream.Position;
        foreach (PolyRecord p in polys.Concat(offMeshPolys))
        {
            w.Write(uint.MaxValue);
            foreach (ushort v in p.Verts)
            {
                w.Write(v);
            }

            foreach (ushort v in p.Neis)
            {
                w.Write(v);
            }

            w.Write(p.Flags);
            w.Write(p.VertCount);
            w.Write(p.AreaAndType);
        }

        Align4(stream);
        int linksAt = (int)stream.Position;
        for (long i = 0; i < (long)maxLinkCount * spec.LinkSize; i++)
        {
            w.Write((byte)0);
        }

        Align4(stream);
        int detailMeshesAt = (int)stream.Position;
        for (int p = 0; p < polyCount; p++)
        {
            w.Write(0u);
            w.Write((uint)(p * 2));
            w.Write((byte)0);
            w.Write((byte)2);
            w.Write((ushort)0);
        }

        Align4(stream);
        int detailVertsAt = (int)stream.Position;
        int detailTrisAt = detailVertsAt;
        for (int p = 0; p < polyCount; p++)
        {
            // Two triangles per quad: (0,1,2) and (0,2,3); the flag bits mark the edges that lie on the polygon's boundary.
            w.Write(new byte[] { 0, 1, 2, 5, 0, 2, 3, 20 });
        }

        Align4(stream);
        int bvAt = (int)stream.Position;
        if (bvNodeCount > 0)
        {
            var items = new List<BvItem>(polyCount);
            for (int p = 0; p < polyCount; p++)
            {
                var (ci, cj) = cellOf[p];
                float x0 = ci == 0 ? minX : minX + (ci * s), x1 = ci + 1 == n ? maxX : minX + ((ci + 1) * s);
                float z0 = cj == 0 ? minZ : minZ + (cj * s), z1 = cj + 1 == n ? maxZ : minZ + ((cj + 1) * s);
                items.Add(new BvItem(
                    p,
                    Quantize(x0, minX, quant, floor: true), Quantize(h, boundsMinY, quant, floor: true), Quantize(z0, minZ, quant, floor: true),
                    Quantize(x1, minX, quant, floor: false), Quantize(h, boundsMinY, quant, floor: false), Quantize(z1, minZ, quant, floor: false)));
            }

            var nodes = new List<BvNode>(bvNodeCount);
            Subdivide(items, 0, items.Count, nodes);
            while (nodes.Count < bvNodeCount)
            {
                nodes.Add(default);
            }

            foreach (BvNode node in nodes)
            {
                w.Write(node.MinX);
                w.Write(node.MinY);
                w.Write(node.MinZ);
                w.Write(node.MaxX);
                w.Write(node.MaxY);
                w.Write(node.MaxZ);
                w.Write(node.Index);
            }
        }

        Align4(stream);
        int offMeshAt = (int)stream.Position;
        for (int c = 0; c < offMeshRecords.Count; c++)
        {
            OffMeshSpec con = offMeshRecords[c];
            w.Write(con.StartRecast.X);
            w.Write(con.StartRecast.Y);
            w.Write(con.StartRecast.Z);
            w.Write(con.EndRecast.X);
            w.Write(con.EndRecast.Y);
            w.Write(con.EndRecast.Z);
            w.Write(con.Radius);
            w.Write((ushort)(polyCount + c));
            w.Write((byte)(con.Bidirectional ? 1 : 0));
            w.Write((byte)Classify(con.EndRecast, minX, maxX, minZ, maxZ));
            w.Write(con.UserId);
        }

        Align4(stream);
        int end = (int)stream.Position;

        // Header and prefix are patched in now that every count is known.
        stream.Position = headerAt;
        w.Write(DetourMagic);
        w.Write(7);
        w.Write(detourX);
        w.Write(detourY);
        w.Write(0);
        w.Write(0u);
        w.Write(polyCount + offMeshPolys.Count);
        w.Write(verts.Count);
        w.Write(maxLinkCount);
        w.Write(polyCount);
        w.Write(0);
        w.Write(detailTriCount);
        w.Write(bvNodeCount);
        w.Write(offMeshPolys.Count);
        w.Write(polyCount);
        w.Write(2.0f);
        w.Write(0.6f);
        w.Write(0.9f);
        w.Write(minX);
        w.Write(boundsMinY);
        w.Write(minZ);
        w.Write(maxX);
        w.Write(boundsMaxY);
        w.Write(maxZ);
        w.Write(quant);
        stream.Position = 0;
        w.Write(MmapMagic);
        w.Write(7u);
        w.Write(6u);
        w.Write((uint)(end - 20));
        w.Write(spec.UsesLiquids ? 1u : 0u);
        w.Flush();

        var layout = new TileLayout(0, headerAt, vertsAt, polysAt, linksAt, detailMeshesAt, detailVertsAt, detailTrisAt, bvAt, offMeshAt);
        return new BuiltTile(spec, stream.ToArray(), layout, detourX, detourY, polyCount, polyCount + offMeshPolys.Count, verts.Count, maxLinkCount, bvNodeCount);
    }

    private sealed class PolyRecord
    {
        public required ushort[] Verts { get; init; }

        public required ushort[] Neis { get; init; }

        public ushort Flags { get; init; }

        public byte VertCount { get; init; }

        public byte AreaAndType { get; init; }
    }

    private readonly record struct BvItem(int Poly, ushort MinX, ushort MinY, ushort MinZ, ushort MaxX, ushort MaxY, ushort MaxZ);

    private readonly record struct BvNode(ushort MinX, ushort MinY, ushort MinZ, ushort MaxX, ushort MaxY, ushort MaxZ, int Index);

    // Detour's dtClassifyPointOutsideBounds (the side an off-mesh end leaves the tile by; 0xff inside).
    private static int Classify(Vector3 p, float minX, float maxX, float minZ, float maxZ)
    {
        if (p.X >= maxX)
        {
            return p.Z >= maxZ ? 1 : p.Z < minZ ? 7 : 0;
        }

        if (p.X < minX)
        {
            return p.Z >= maxZ ? 3 : p.Z < minZ ? 5 : 4;
        }

        return p.Z >= maxZ ? 2 : p.Z < minZ ? 6 : 0xff;
    }

    private static ushort Quantize(float value, float origin, float factor, bool floor)
    {
        double scaled = (value - origin) * factor;
        double rounded = floor ? Math.Floor(scaled) : Math.Ceiling(scaled);
        return (ushort)Math.Clamp(rounded, 0, ushort.MaxValue);
    }

    private static void Align4(MemoryStream stream)
    {
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    // Detour's subdivide (DetourNavMeshBuilder.cpp): leaves hold a polygon index, inner nodes the negative size of their subtree.
    private static void Subdivide(List<BvItem> items, int from, int to, List<BvNode> nodes)
    {
        int count = to - from;
        int at = nodes.Count;
        nodes.Add(default);
        if (count == 1)
        {
            BvItem item = items[from];
            nodes[at] = new BvNode(item.MinX, item.MinY, item.MinZ, item.MaxX, item.MaxY, item.MaxZ, item.Poly);
            return;
        }

        ushort minX = ushort.MaxValue, minY = ushort.MaxValue, minZ = ushort.MaxValue, maxX = 0, maxY = 0, maxZ = 0;
        for (int i = from; i < to; i++)
        {
            minX = Math.Min(minX, items[i].MinX);
            minY = Math.Min(minY, items[i].MinY);
            minZ = Math.Min(minZ, items[i].MinZ);
            maxX = Math.Max(maxX, items[i].MaxX);
            maxY = Math.Max(maxY, items[i].MaxY);
            maxZ = Math.Max(maxZ, items[i].MaxZ);
        }

        int dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        Comparison<BvItem> order;
        if (dx >= dy && dx >= dz)
        {
            order = static (a, b) => a.MinX.CompareTo(b.MinX);
        }
        else if (dy >= dz)
        {
            order = static (a, b) => a.MinY.CompareTo(b.MinY);
        }
        else
        {
            order = static (a, b) => a.MinZ.CompareTo(b.MinZ);
        }

        items.Sort(from, count, Comparer<BvItem>.Create(order));
        int split = from + (count / 2);
        Subdivide(items, from, split, nodes);
        Subdivide(items, split, to, nodes);
        nodes[at] = new BvNode(minX, minY, minZ, maxX, maxY, maxZ, -(nodes.Count - at));
    }
}

/// <summary>
/// A temporary terrain data root with an <c>mmaps</c> folder holding synthetic navmesh files; deleted on dispose.
/// </summary>
internal sealed class SyntheticMap : IDisposable
{
    /// <summary>Creates the folder and, unless <paramref name="writeParams"/> is false, the map's <c>.mmap</c> file.</summary>
    public SyntheticMap(uint mapId = 0, int maxTiles = 256, bool writeParams = true)
    {
        MapId = mapId;
        Root = Path.Combine(Path.GetTempPath(), "arcane-atlas-n-" + Guid.NewGuid().ToString("N"));
        Mmaps = Path.Combine(Root, "mmaps");
        Directory.CreateDirectory(Mmaps);
        if (writeParams)
        {
            File.WriteAllBytes(Path.Combine(Mmaps, NavMeshFormat.ParamsFileName(mapId)), SyntheticNav.BuildParams(maxTiles));
        }
    }

    /// <summary>The map id the files are named for.</summary>
    public uint MapId { get; }

    /// <summary>The terrain data root (holds <c>mmaps</c>).</summary>
    public string Root { get; }

    /// <summary>The <c>mmaps</c> folder.</summary>
    public string Mmaps { get; }

    /// <summary>Writes a built tile's file.</summary>
    public BuiltTile Add(TileSpec spec)
    {
        BuiltTile built = SyntheticNav.Build(spec);
        WriteRaw(spec.TerrainX, spec.TerrainY, built.File);
        return built;
    }

    /// <summary>Writes arbitrary bytes as a tile file.</summary>
    public void WriteRaw(int terrainX, int terrainY, byte[] bytes)
        => File.WriteAllBytes(TilePath(terrainX, terrainY), bytes);

    /// <summary>Path of a tile's file.</summary>
    public string TilePath(int terrainX, int terrainY) => Path.Combine(Mmaps, NavMeshFormat.TileFileName(MapId, terrainX, terrainY));

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may hold a file for a moment; the folder is in the temp directory either way.
        }
    }
}

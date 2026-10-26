using System.Buffers.Binary;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// One <c>.mmtile</c> as DotRecast reads it: the generator's prefix plus the Detour tile data. <see cref="Data"/> is mutated by
/// <see cref="DtNavMesh.AddTile(DtMeshData, int, long, out long)"/> (it snaps off-mesh endpoints onto the mesh and links polygons),
/// so the vertices as they were in the file are kept in <see cref="RawVertices"/>.
/// </summary>
/// <remarks>Ownership: the instance owns <see cref="Data"/>; it is handed to at most one <see cref="DtNavMesh"/> at a time.</remarks>
public sealed class ParsedNavTile
{
    /// <summary>The 20-byte generator prefix (magic, Detour version, mmap version, data size, uses-liquids).</summary>
    public required MmapTileHeader Prefix { get; init; }

    /// <summary>The Detour tile, parsed by DotRecast's <see cref="DtMeshDataReader"/>.</summary>
    public required DtMeshData Data { get; init; }

    /// <summary>Copy of <see cref="DtMeshData.verts"/> taken before <c>AddTile</c> (Recast axes: x = world Y, y = height, z = world X).</summary>
    public required float[] RawVertices { get; init; }

    /// <summary>Bytes per <c>dtLink</c> in the file: 16 with 64-bit polygon references (the generator's build), 12 with 32-bit ones.</summary>
    public required int LinkSize { get; init; }

    /// <summary>The whole file's length.</summary>
    public required long FileBytes { get; init; }
}

/// <summary>
/// Reads a vmangos <c>.mmtile</c> with the vendored DotRecast reader after checking everything the reader itself does not:
/// the generator prefix, the section sizes against the file length, and the indices DotRecast would otherwise follow out of
/// range. Every refusal is an <see cref="InvalidDataException"/> with the reason; nothing is read as if it were valid.
/// </summary>
public static class MmapTileReader
{
    /// <summary>Size of <c>dtMeshHeader</c> in bytes.</summary>
    // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:261-289 (5 ints, uint userId, 9 ints, 3 floats,
    // 2 x float[3], float bvQuantFactor: 25 four-byte fields, no padding).
    public const int DetourHeaderSize = 100;

    // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:167-200 (dtPoly: uint firstLink, ushort verts[6],
    // ushort neis[6], ushort flags, uchar vertCount, uchar areaAndtype = 32 bytes).
    private const int PolyBytes = 32;

    // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:203-209 (dtPolyDetail: uint vertBase, uint triBase, uchar vertCount, uchar triCount, 2 bytes padding = 12).
    private const int PolyDetailBytes = 12;

    // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:227-232 (dtBVNode: ushort bmin[3], ushort bmax[3], int i = 16).
    private const int BvNodeBytes = 16;

    // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:236-257 (dtOffMeshConnection: float pos[6], float rad, ushort poly, uchar flags, uchar side, uint userId = 36).
    private const int OffMeshBytes = 36;

    // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:28 (#define DT_POLYREF64 1) and :214-222 (dtLink: dtPolyRef ref, uint next, 4 uchars): 8 + 4 + 4 = 16 bytes
    // with a 64-bit reference, 4 + 4 + 4 = 12 with a 32-bit one.
    private const int LinkBytes64 = 16;
    private const int LinkBytes32 = 12;

    private const int MaxCount = 1 << 20;

    /// <summary>Parses a whole <c>.mmtile</c> file.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable vmangos navmesh tile; the message says why.</exception>
    public static ParsedNavTile Parse(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < MmapTileHeader.ByteSize + DetourHeaderSize)
        {
            throw new InvalidDataException($"navmesh tile is {file.Length} bytes, shorter than its {MmapTileHeader.ByteSize + DetourHeaderSize}-byte headers");
        }

        // ref: vmangos@0e3ff01 src/game/Maps/MoveMapSharedDefines.h:28-38 (MmapTileHeader: five uint32) and
        // src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshFormat.cs:92-98.
        var prefix = new MmapTileHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0)),
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12)),
            (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)) & 1) != 0);

        // ref: vmangos@0e3ff01 src/game/Maps/MoveMap.cpp:163 (mmapMagic != MMAP_MAGIC) and :170 (mmapVersion != MMAP_VERSION).
        if (prefix.Magic != NavMeshFormat.MmapMagic)
        {
            throw new InvalidDataException($"bad mmap magic 0x{prefix.Magic:x8}, expected 0x{NavMeshFormat.MmapMagic:x8}");
        }

        if (prefix.MmapVersion != NavMeshFormat.MmapVersion)
        {
            throw new InvalidDataException($"mmap tile version {prefix.MmapVersion}, expected {NavMeshFormat.MmapVersion} (version 8 is cMaNGOS' generator)");
        }

        // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:913-916 (addTile refuses another magic or version).
        if (prefix.DetourVersion != NavMeshFormat.DetourVersion)
        {
            throw new InvalidDataException($"Detour version {prefix.DetourVersion} in the prefix, expected {NavMeshFormat.DetourVersion}");
        }

        long remaining = file.Length - MmapTileHeader.ByteSize;
        if (prefix.Size > remaining)
        {
            throw new InvalidDataException($"tile data size {prefix.Size} exceeds the {remaining} bytes left in the file");
        }

        byte[] data = file.AsSpan(MmapTileHeader.ByteSize, (int)prefix.Size).ToArray();
        int linkSize = ValidateLayout(data);
        DtMeshData mesh;
        try
        {
            // The vmangos generator builds Detour with 64-bit references, so links are 16 bytes (is32Bit false); the size check above
            // decided which of the two the file really has.
            mesh = new DtMeshDataReader().Read(new RcByteBuffer(data), NavMeshFormat.MaxVertsPerPoly, linkSize == LinkBytes32);
        }
        catch (IOException e)
        {
            throw new InvalidDataException($"DotRecast refused the tile: {e.Message}", e);
        }

        if (mesh.header.version != NavMeshFormat.DetourVersion)
        {
            throw new InvalidDataException($"Detour tile version {mesh.header.version}, expected {NavMeshFormat.DetourVersion}");
        }

        ValidateIndices(mesh);
        return new ParsedNavTile
        {
            Prefix = prefix,
            Data = mesh,
            RawVertices = (float[])mesh.verts.Clone(),
            LinkSize = linkSize,
            FileBytes = file.Length,
        };
    }

    /// <summary>
    /// Checks the header counts against the data length and returns the link size (16 or 12 bytes). The section sizes are the
    /// builder's (each aligned to 4 bytes); the link table is the one section whose element size varies.
    /// </summary>
    private static int ValidateLayout(byte[] data)
    {
        if (data.Length < DetourHeaderSize)
        {
            throw new InvalidDataException($"Detour tile is {data.Length} bytes, shorter than its {DetourHeaderSize}-byte header");
        }

        // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:261-289 field order; byte offsets: magic 0, version 4, x 8, y 12, layer 16, userId 20, polyCount 24,
        // vertCount 28, maxLinkCount 32, detailMeshCount 36, detailVertCount 40, detailTriCount 44, bvNodeCount 48, offMeshConCount 52.
        int magic = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
        if (magic != NavMeshFormat.DetourMagic)
        {
            throw new InvalidDataException("not a Detour tile (bad magic)");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (version != NavMeshFormat.DetourVersion)
        {
            throw new InvalidDataException($"Detour tile version {version}, expected {NavMeshFormat.DetourVersion}");
        }

        long polyCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
        long vertCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28));
        long maxLinkCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(32));
        long detailMeshCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(36));
        long detailVertCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(40));
        long detailTriCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(44));
        long bvNodeCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(48));
        long offMeshCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(52));
        long[] counts = [polyCount, vertCount, maxLinkCount, detailMeshCount, detailVertCount, detailTriCount, bvNodeCount, offMeshCount];
        if (counts.Any(c => c < 0 || c > MaxCount))
        {
            throw new InvalidDataException("Detour tile counts are out of range");
        }

        // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:997 (tile->links[header->maxLinkCount-1].next = DT_NULL_LINK)
        // and tests/ThirdParty/DotRecast/DotRecast.Detour/DtNavMesh.cs:457: addTile writes the last link of the table, so a tile with no
        // links (a tile without polygons) is not one either engine can take.
        if (maxLinkCount == 0)
        {
            throw new InvalidDataException($"Detour tile has a link table of 0 links ({polyCount} polygons); addTile indexes links[maxLinkCount - 1], so the tile cannot be added");
        }

        // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMeshBuilder.cpp:427-459 (the writer's section sizes)
        // and vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:970-989 (the reader's): header, verts, polys,
        // links, detail meshes, detail verts, detail tris, BV tree, off-mesh connections, each dtAlign4'ed. The writer stores
        // bvNodeCount = 2 * polyCount (DetourNavMeshBuilder.cpp:483), so the tree's tail is allocated and zero.
        long fixedSize = DetourHeaderSize + Align4(12L * vertCount) + Align4(PolyBytes * polyCount) + Align4(PolyDetailBytes * detailMeshCount)
            + Align4(12L * detailVertCount) + Align4(4L * detailTriCount) + Align4(BvNodeBytes * bvNodeCount) + Align4(OffMeshBytes * offMeshCount);
        if (fixedSize + Align4(LinkBytes64 * maxLinkCount) == data.Length)
        {
            return LinkBytes64;
        }

        if (fixedSize + Align4(LinkBytes32 * maxLinkCount) == data.Length)
        {
            return LinkBytes32;
        }

        throw new InvalidDataException($"Detour tile size {data.Length} does not match its header (expected {fixedSize + Align4(LinkBytes64 * maxLinkCount)} with 64-bit links)");
    }

    /// <summary>
    /// DotRecast follows polygon, vertex, detail and BV indices without bounds checks (an out-of-range one throws deep inside a
    /// later <c>AddTile</c> or query, or loops), so a tile whose indices leave their arrays is refused here, with the first offender named.
    /// </summary>
    private static void ValidateIndices(DtMeshData mesh)
    {
        DtMeshHeader h = mesh.header;
        if (!float.IsFinite(h.bmin.X) || !float.IsFinite(h.bmin.Y) || !float.IsFinite(h.bmin.Z)
            || !float.IsFinite(h.bmax.X) || !float.IsFinite(h.bmax.Y) || !float.IsFinite(h.bmax.Z)
            || h.bmin.X > h.bmax.X || h.bmin.Y > h.bmax.Y || h.bmin.Z > h.bmax.Z
            || !float.IsFinite(h.bvQuantFactor) || h.bvQuantFactor < 0
            || !float.IsFinite(h.walkableHeight) || !float.IsFinite(h.walkableRadius) || !float.IsFinite(h.walkableClimb))
        {
            throw new InvalidDataException("Detour tile header bounds or parameters are invalid");
        }

        for (int i = 0; i < mesh.polys.Length; i++)
        {
            DtPoly p = mesh.polys[i];
            bool offMesh = p.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION;
            if (p.vertCount > NavMeshFormat.MaxVertsPerPoly || p.vertCount < (offMesh ? 2 : 3))
            {
                throw new InvalidDataException($"polygon {i} has {p.vertCount} vertices");
            }

            for (int j = 0; j < p.vertCount; j++)
            {
                if (p.verts[j] >= h.vertCount)
                {
                    throw new InvalidDataException($"polygon {i} names vertex {p.verts[j]} of {h.vertCount}");
                }

                // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:543-549 (connectIntLinks skips a neis of 0 or
                // with DT_EXT_LINK, otherwise links to polygon neis - 1) and DetourNavMesh.h:97 (DT_EXT_LINK = 0x8000).
                int nei = p.neis[j];
                if ((nei & NavMeshFormat.ExternalLink) == 0 && nei > h.polyCount)
                {
                    throw new InvalidDataException($"polygon {i} edge {j} names neighbour {nei} of {h.polyCount} polygons");
                }
            }
        }

        for (int i = 0; i < mesh.detailMeshes.Length; i++)
        {
            DtPolyDetail d = mesh.detailMeshes[i];
            if ((long)d.vertBase + d.vertCount > h.detailVertCount || (long)d.triBase + d.triCount > h.detailTriCount)
            {
                throw new InvalidDataException($"detail mesh {i} reaches past the tile's detail vertices or triangles");
            }

            int ownVerts = i < mesh.polys.Length ? mesh.polys[i].vertCount : 0;
            for (int t = d.triBase; t < d.triBase + d.triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    if (mesh.detailTris[(t * 4) + k] >= ownVerts + d.vertCount)
                    {
                        throw new InvalidDataException($"detail triangle {t} names vertex {mesh.detailTris[(t * 4) + k]} outside its sub-mesh");
                    }
                }
            }
        }

        // The builder allocates 2n nodes for a tree of 2n - 1; the unused tail is zero, a leaf of polygon 0 (Detour reads it as one).
        DtBVNode[]? bv = mesh.bvTree;
        for (int i = 0; bv is not null && i < bv.Length; i++)
        {
            int index = bv[i].i;
            if (index >= h.polyCount || (index < 0 && (long)i - index > bv.Length))
            {
                throw new InvalidDataException($"BV node {i} names polygon {index}, or an escape past the {bv.Length} nodes, of {h.polyCount} polygons");
            }
        }

        foreach (DtOffMeshConnection con in mesh.offMeshCons)
        {
            if (con.poly < 0 || con.poly >= h.polyCount || mesh.polys[con.poly].GetPolyType() != DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
            {
                throw new InvalidDataException($"off-mesh connection {con.userId} names polygon {con.poly}, which is not an off-mesh polygon");
            }
        }
    }

    private static long Align4(long size) => (size + 3) & ~3L;
}

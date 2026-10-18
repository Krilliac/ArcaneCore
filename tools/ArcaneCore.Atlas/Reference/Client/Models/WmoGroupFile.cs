using System.Buffers.Binary;
using System.Numerics;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>MOPY per-triangle flag bits (the first byte of each MOPY entry).</summary>
public static class WmoMaterialFlags
{
    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.h:35-42 (WMO_MATERIAL_* values)
    public const byte NoCameraCollision = 0x02;
    public const byte Detail = 0x04;
    public const byte Collision = 0x08;
    public const byte Render = 0x20;

    /// <summary>
    /// The extractor's non-precise rule: a triangle is collidable when it carries the collision bit, or when it is a
    /// render face that is not a detail face.
    /// </summary>
    public static bool IsCollidable(byte flags)
    {
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:381-383
        bool renderFace = (flags & Render) != 0 && (flags & Detail) == 0;
        return (flags & Collision) != 0 || renderFace;
    }
}

/// <summary>MOGP flag bits the extractor looks at.</summary>
public static class WmoGroupFlags
{
    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:515 (mogpFlags & 0x80: unreachable group)
    public const uint Unreachable = 0x80;

    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:519 (mogpFlags & 0x4000000: antiportal)
    public const uint Antiportal = 0x4000000;
}

/// <summary>The MLIQ chunk of a group: a liquid height grid with per-tile flag bytes.</summary>
/// <param name="VerticesX">Vertices along x.</param>
/// <param name="VerticesY">Vertices along y.</param>
/// <param name="TilesX">Tiles along x.</param>
/// <param name="TilesY">Tiles along y.</param>
/// <param name="Corner">Group-local position of the grid's base corner.</param>
/// <param name="MaterialId">The header's u16 material (liquid type) field.</param>
/// <param name="Heights">One height per vertex, x fastest.</param>
/// <param name="TileFlags">One flag byte per tile (low nibble 15 means no liquid), x fastest.</param>
public sealed record WmoLiquidGrid(int VerticesX, int VerticesY, int TilesX, int TilesY, Vector3 Corner, ushort MaterialId, float[] Heights, byte[] TileFlags);

/// <summary>
/// A parsed WMO group file (<c>Building_NNN.wmo</c>): the MOGP header and the geometry sub-chunks the collision
/// pipeline reads (MOPY, MOVI, MOVT, MODR, MLIQ). Triangles are in WMO-local space exactly as stored; nothing is
/// reordered or filtered. Instances are immutable after <see cref="Parse"/> and safe to share between threads.
/// </summary>
public sealed class WmoGroupFile
{
    /// <summary>Bytes of the MOGP header before its sub-chunks begin.</summary>
    public const int MogpHeaderSize = 68;

    private readonly (Vector3 Min, Vector3 Max) _vertexBounds;

    private WmoGroupFile(
        uint nameOffset,
        uint descriptionOffset,
        uint flags,
        Vector3 boundsMin,
        Vector3 boundsMax,
        ushort portalStart,
        ushort portalCount,
        ushort transitionBatchCount,
        ushort interiorBatchCount,
        ushort exteriorBatchCount,
        uint fogIds,
        uint liquidType,
        uint groupId,
        byte[] triangleFlags,
        byte[] triangleMaterials,
        ushort[] indices,
        Vector3[] vertices,
        ushort[] doodadReferences,
        WmoLiquidGrid? liquid)
    {
        NameOffset = nameOffset;
        DescriptionOffset = descriptionOffset;
        Flags = flags;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        PortalStart = portalStart;
        PortalCount = portalCount;
        TransitionBatchCount = transitionBatchCount;
        InteriorBatchCount = interiorBatchCount;
        ExteriorBatchCount = exteriorBatchCount;
        FogIds = fogIds;
        LiquidType = liquidType;
        GroupId = groupId;
        TriangleFlags = triangleFlags;
        TriangleMaterials = triangleMaterials;
        Indices = indices;
        Vertices = vertices;
        DoodadReferences = doodadReferences;
        Liquid = liquid;
        _vertexBounds = Bounds(vertices);
    }

    /// <summary>Byte offset into the root's MOGN blob of this group's name.</summary>
    public uint NameOffset { get; }

    public uint DescriptionOffset { get; }

    public uint Flags { get; }

    public Vector3 BoundsMin { get; }

    public Vector3 BoundsMax { get; }

    public ushort PortalStart { get; }

    public ushort PortalCount { get; }

    public ushort TransitionBatchCount { get; }

    public ushort InteriorBatchCount { get; }

    public ushort ExteriorBatchCount { get; }

    /// <summary>The four packed fog index bytes.</summary>
    public uint FogIds { get; }

    public uint LiquidType { get; }

    /// <summary>The group's WMOAreaTable key (vmangos <c>groupWMOID</c>).</summary>
    public uint GroupId { get; }

    /// <summary>MOPY flags byte per triangle.</summary>
    public byte[] TriangleFlags { get; }

    /// <summary>MOPY material id byte per triangle (0xFF: no material).</summary>
    public byte[] TriangleMaterials { get; }

    /// <summary>MOVI vertex indices, three per triangle, one triangle per MOPY entry.</summary>
    public ushort[] Indices { get; }

    /// <summary>MOVT vertices in WMO-local space.</summary>
    public Vector3[] Vertices { get; }

    /// <summary>MODR doodad indices into the root's MODD list.</summary>
    public ushort[] DoodadReferences { get; }

    public WmoLiquidGrid? Liquid { get; }

    public int TriangleCount => TriangleFlags.Length;

    /// <summary>Smallest corner of the box that holds the stored vertices (not the header's bounds), or zero without vertices.</summary>
    public Vector3 VertexBoundsMin => _vertexBounds.Min;

    /// <summary>Largest corner of the box that holds the stored vertices, or zero without vertices.</summary>
    public Vector3 VertexBoundsMax => _vertexBounds.Max;

    /// <summary>Approximate heap size, for the model cache.</summary>
    public long EstimatedBytes => 256 + (TriangleFlags.Length * 2L) + (Indices.Length * 2L) + (Vertices.Length * 12L)
        + (DoodadReferences.Length * 2L) + (Liquid is null ? 0 : (Liquid.Heights.Length * 4L) + Liquid.TileFlags.Length);

    /// <summary>Parses a group file. Throws <see cref="InvalidDataException"/> for an empty, truncated or inconsistent file.</summary>
    public static WmoGroupFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            throw new InvalidDataException("WMO group file is empty (0 bytes).");
        }

        List<WowChunk> top = WowChunks.Scan(data, 0, data.Length);
        WowChunk? mogp = null;
        foreach (WowChunk chunk in top)
        {
            if (chunk.Tag == "MVER")
            {
                ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO group");
                if (payload.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != WmoRootFile.SupportedVersion)
                {
                    throw new InvalidDataException($"WMO group: version is not the 1.12.1 version {WmoRootFile.SupportedVersion}.");
                }
            }
            else if (chunk.Tag == "MOGP")
            {
                mogp = chunk;
                break;
            }
        }

        if (mogp is not { } group)
        {
            throw new InvalidDataException("WMO group: no MOGP chunk.");
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:188-190 (the extractor forces MOGP's size to 68
        //      and reads the geometry sub-chunks as if they were top-level chunks until the end of the file)
        // ref: WoW-RE-Research/formats/wmo.md:153-175 (MVER then one MOGP wrapping a 68-byte header and the sub-chunks)
        if (data.Length - group.Offset < MogpHeaderSize)
        {
            throw new InvalidDataException($"WMO group: MOGP header needs {MogpHeaderSize} bytes, the file has {data.Length - group.Offset}.");
        }

        ReadOnlySpan<byte> header = data.Slice(group.Offset, MogpHeaderSize);

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:197-212 (groupName, descGroupName, mogpFlags, bbcorn1, bbcorn2, moprIdx, moprNItems, nBatchA, nBatchB, nBatchC(u32), fogIdx(u32), liquidType, groupWMOID)
        // ref: WoW-RE-Research/formats/wmo.md:157-172 (portalStart, portalCount, trans/int/ext batch counts, pad, fogIds[4], groupLiquid, uniqueID)
        // ref: Worldforge@1f73a08/src/wmo.cpp:188-195 (68-byte header; flags at 0x08, bbox at 0x0C, portal range at 0x24)
        uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint descriptionOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        Vector3 boundsMin = ReadVector(header[12..]);
        Vector3 boundsMax = ReadVector(header[24..]);
        ushort portalStart = BinaryPrimitives.ReadUInt16LittleEndian(header[36..]);
        ushort portalCount = BinaryPrimitives.ReadUInt16LittleEndian(header[38..]);
        ushort transitionBatches = BinaryPrimitives.ReadUInt16LittleEndian(header[40..]);
        ushort interiorBatches = BinaryPrimitives.ReadUInt16LittleEndian(header[42..]);
        ushort exteriorBatches = BinaryPrimitives.ReadUInt16LittleEndian(header[44..]);
        uint fogIds = BinaryPrimitives.ReadUInt32LittleEndian(header[48..]);
        uint liquidType = BinaryPrimitives.ReadUInt32LittleEndian(header[52..]);
        uint groupId = BinaryPrimitives.ReadUInt32LittleEndian(header[56..]);

        List<WowChunk> subs = WowChunks.Scan(data, group.Offset + MogpHeaderSize, data.Length);
        byte[] triangleFlags = [];
        byte[] triangleMaterials = [];
        ushort[] allIndices = [];
        Vector3[] vertices = [];
        ushort[] doodadReferences = [];
        WmoLiquidGrid? liquid = null;

        foreach (WowChunk chunk in subs)
        {
            switch (chunk.Tag)
            {
                case "MOPY":
                    {
                        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:214-220 (nTriangles = size / 2)
                        // ref: WoW-RE-Research/formats/wmo.md:180 (2 bytes per triangle: u8 flags, u8 materialId)
                        ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO group");
                        int count = payload.Length / 2;
                        triangleFlags = new byte[count];
                        triangleMaterials = new byte[count];
                        for (int i = 0; i < count; i++)
                        {
                            triangleFlags[i] = payload[2 * i];
                            triangleMaterials[i] = payload[(2 * i) + 1];
                        }

                        break;
                    }

                case "MOVI":
                    {
                        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:221-225 (u16 array, size / 2 entries)
                        // ref: WoW-RE-Research/formats/wmo.md:181 (u16 indices, three per triangle)
                        ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO group");
                        allIndices = new ushort[payload.Length / 2];
                        for (int i = 0; i < allIndices.Length; i++)
                        {
                            allIndices[i] = BinaryPrimitives.ReadUInt16LittleEndian(payload[(2 * i)..]);
                        }

                        break;
                    }

                case "MOVT":
                    {
                        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:226-231 (float array, nVertices = size / 12)
                        // ref: WoW-RE-Research/formats/wmo.md:182 (12-byte vertices in WMO-local space)
                        ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO group");
                        vertices = new Vector3[payload.Length / 12];
                        for (int i = 0; i < vertices.Length; i++)
                        {
                            vertices[i] = ReadVector(payload[(12 * i)..]);
                        }

                        break;
                    }

                case "MODR":
                    {
                        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:244-248 (u16 doodad indices, size / 2)
                        // ref: WoW-RE-Research/formats/wmo.md:187
                        ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO group");
                        doodadReferences = new ushort[payload.Length / 2];
                        for (int i = 0; i < doodadReferences.Length; i++)
                        {
                            doodadReferences[i] = BinaryPrimitives.ReadUInt16LittleEndian(payload[(2 * i)..]);
                        }

                        break;
                    }

                case "MLIQ":
                    liquid = ReadLiquid(WowChunks.Required(data, chunk, "WMO group"));
                    break;
            }
        }

        // The extractor writes 3 * nTriangles indices from MOVI (vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:301-323, precise mode); fewer would read past the buffer.
        int expectedIndices = triangleFlags.Length * 3;
        if (allIndices.Length < expectedIndices)
        {
            throw new InvalidDataException($"WMO group: MOVI holds {allIndices.Length} indices but MOPY lists {triangleFlags.Length} triangles ({expectedIndices} needed).");
        }

        ushort[] indices = allIndices.Length == expectedIndices ? allIndices : allIndices[..expectedIndices];
        foreach (ushort index in indices)
        {
            if (index >= vertices.Length)
            {
                throw new InvalidDataException($"WMO group: a triangle index ({index}) points past the {vertices.Length} vertices.");
            }
        }

        foreach (Vector3 v in vertices)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            {
                throw new InvalidDataException("WMO group: a vertex is not finite.");
            }
        }

        return new WmoGroupFile(
            nameOffset,
            descriptionOffset,
            flags,
            boundsMin,
            boundsMax,
            portalStart,
            portalCount,
            transitionBatches,
            interiorBatches,
            exteriorBatches,
            fogIds,
            liquidType,
            groupId,
            triangleFlags,
            triangleMaterials,
            indices,
            vertices,
            doodadReferences,
            liquid);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(Vector3[] vertices)
    {
        if (vertices.Length == 0)
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        Vector3 min = vertices[0];
        Vector3 max = vertices[0];
        foreach (Vector3 v in vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        return (min, max);
    }

    private static WmoLiquidGrid ReadLiquid(ReadOnlySpan<byte> payload)
    {
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.h:101-117 (header: i32 xverts, yverts, xtiles, ytiles, f32 pos x/y/z, u16 type = 30 bytes; vertex: u16, u16, f32 height)
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:249-262 (header, xverts*yverts vertices, xtiles*ytiles flag bytes)
        // ref: Worldforge@1f73a08/src/wmo.cpp:243-267 (30-byte header, 8-byte vertices with height last, tile flag bytes)
        const int headerSize = 30;
        if (payload.Length < headerSize)
        {
            throw new InvalidDataException($"WMO group: MLIQ chunk is {payload.Length} bytes, shorter than its {headerSize}-byte header.");
        }

        int xverts = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int yverts = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        int xtiles = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        int ytiles = BinaryPrimitives.ReadInt32LittleEndian(payload[12..]);
        if (xverts < 0 || yverts < 0 || xtiles < 0 || ytiles < 0)
        {
            throw new InvalidDataException("WMO group: MLIQ has a negative grid size.");
        }

        long vertexCount = (long)xverts * yverts;
        long tileCount = (long)xtiles * ytiles;
        if (headerSize + (vertexCount * 8) + tileCount > payload.Length)
        {
            throw new InvalidDataException($"WMO group: MLIQ grid ({xverts}x{yverts} vertices, {xtiles}x{ytiles} tiles) needs more bytes than the chunk's {payload.Length}.");
        }

        Vector3 corner = ReadVector(payload[16..]);
        ushort material = BinaryPrimitives.ReadUInt16LittleEndian(payload[28..]);
        var heights = new float[vertexCount];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = BinaryPrimitives.ReadSingleLittleEndian(payload[(headerSize + (8 * i) + 4)..]);
        }

        byte[] flags = payload.Slice(headerSize + (int)(vertexCount * 8), (int)tileCount).ToArray();
        return new WmoLiquidGrid(xverts, yverts, xtiles, ytiles, corner, material, heights, flags);
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> span)
        => new(
            BinaryPrimitives.ReadSingleLittleEndian(span),
            BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(span[8..]));
}

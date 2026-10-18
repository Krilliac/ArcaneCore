using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// A parsed 1.12.1 M2 model (<c>MD20</c>, version 256): the render vertices, the first embedded view's triangles and the
/// bounding (collision) triangles and vertices the vmap extractor keeps. Positions are in model space exactly as stored.
/// Instances are immutable after <see cref="Parse"/> and safe to share between threads.
/// </summary>
public sealed class M2ModelFile
{
    /// <summary>The M2 version of every 1.12.1 model.</summary>
    public const uint SupportedVersion = 256;

    // Header field offsets. The bounding-array offsets are counted from the vmangos ModelHeader struct, whose first
    // field is at 0 and which is packed to 1 byte, and were confirmed on a real file (elfbed01.m2: nBoundingTriangles 294
    // at 0xEC, nBoundingVertices 65 at 0xF4, both ranges inside the file).
    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/modelheaders.h:31-102 (ModelHeader field order)
    // ref: Worldforge@1f73a08/src/m2.cpp:10-13 (name 0x08, vertices 0x44, views 0x4C)
    private const int OffsetName = 0x08;
    private const int OffsetVertices = 0x44;
    private const int OffsetViews = 0x4C;
    private const int OffsetBoundingTriangles = 0xEC;
    private const int OffsetBoundingVertices = 0xF4;
    private const int MinimumHeaderBytes = 0x104;

    // ref: Worldforge@1f73a08/src/m2.cpp:15 (48-byte vertices) and :112-121 (position, bone weights, bone indices, normal, uv)
    private const int VertexSize = 48;

    // ref: Worldforge@1f73a08/src/m2.cpp:144 (view header: five M2Arrays plus u32 = 44 bytes) and :147-148 (vertex lookup, then triangles)
    private const int ViewSize = 44;

    private M2ModelFile(
        string name,
        Vector3[] vertices,
        int viewCount,
        ushort[] viewLookup,
        int[] renderIndices,
        ushort[] boundingIndices,
        Vector3[] boundingVertices)
    {
        Name = name;
        Vertices = vertices;
        ViewCount = viewCount;
        ViewLookup = viewLookup;
        RenderIndices = renderIndices;
        BoundingIndices = boundingIndices;
        BoundingVertices = boundingVertices;
    }

    public string Name { get; }

    /// <summary>Render vertex positions (the global vertex pool).</summary>
    public Vector3[] Vertices { get; }

    /// <summary>Number of embedded views (level-of-detail meshes) in the file.</summary>
    public int ViewCount { get; }

    /// <summary>View 0's local-to-global vertex lookup.</summary>
    public ushort[] ViewLookup { get; }

    /// <summary>View 0's triangles as indices into <see cref="Vertices"/>, three per triangle; empty without a view.</summary>
    public int[] RenderIndices { get; }

    /// <summary>Bounding (collision) triangle corner indices into <see cref="BoundingVertices"/>, three per triangle, as stored.</summary>
    public ushort[] BoundingIndices { get; }

    public Vector3[] BoundingVertices { get; }

    /// <summary>
    /// True when the extractor would keep this model: it needs at least one bounding triangle and one bounding vertex.
    /// </summary>
    public bool HasCollisionHull
    {
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:49 (nBoundingTriangles > 0, else the model is not extracted)
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:183 (a spawn whose model has nVertices == 0 is dropped)
        get
        {
            return BoundingIndices.Length > 0 && BoundingVertices.Length > 0;
        }
    }

    /// <summary>Box around the bounding (collision) vertices, or zero without a hull.</summary>
    public (Vector3 Min, Vector3 Max) BoundingBox => Box(BoundingVertices);

    /// <summary>Box around the render vertices, or zero without vertices.</summary>
    public (Vector3 Min, Vector3 Max) RenderBox => Box(Vertices);

    public int RenderTriangleCount => RenderIndices.Length / 3;

    public int BoundingTriangleCount => BoundingIndices.Length / 3;

    /// <summary>Approximate heap size, for the model cache.</summary>
    public long EstimatedBytes => 256 + (Vertices.Length * 12L) + (ViewLookup.Length * 2L) + (RenderIndices.Length * 4L)
        + (BoundingIndices.Length * 2L) + (BoundingVertices.Length * 12L);

    /// <summary>Parses a model. Throws <see cref="InvalidDataException"/> for an empty, truncated, wrong-version or inconsistent file.</summary>
    public static M2ModelFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            throw new InvalidDataException("M2 file is empty (0 bytes).");
        }

        // ref: Worldforge@1f73a08/src/m2.cpp:88-99 (MD20 magic, version at 4, newer-than-profile versions refused)
        if (data.Length < MinimumHeaderBytes || data[0] != (byte)'M' || data[1] != (byte)'D' || data[2] != (byte)'2' || data[3] != (byte)'0')
        {
            throw new InvalidDataException("M2: not an MD20 model (missing magic or header truncated).");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (version != SupportedVersion)
        {
            throw new InvalidDataException($"M2: version {version} is not the 1.12.1 version {SupportedVersion}.");
        }

        string name = ReadName(data);

        (uint vertexCount, uint vertexOffset) = ReadArray(data, OffsetVertices);
        RequireRange(data, vertexOffset, vertexCount, VertexSize, "vertices");
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = ReadVector(data[(int)(vertexOffset + (uint)(i * VertexSize))..]);
        }

        (uint viewCount, uint viewOffset) = ReadArray(data, OffsetViews);
        ushort[] lookup = [];
        int[] renderIndices = [];
        if (viewCount > 0)
        {
            RequireRange(data, viewOffset, 1, ViewSize, "view header");
            ReadOnlySpan<byte> view = data[(int)viewOffset..];
            (uint lookupCount, uint lookupOffset) = ReadArray(view, 0);
            (uint triangleCount, uint triangleOffset) = ReadArray(view, 8);
            RequireRange(data, lookupOffset, lookupCount, 2, "view vertex lookup");
            RequireRange(data, triangleOffset, triangleCount, 2, "view triangles");
            lookup = new ushort[lookupCount];
            for (int i = 0; i < lookup.Length; i++)
            {
                lookup[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(lookupOffset + (uint)(2 * i))..]);
                if (lookup[i] >= vertices.Length)
                {
                    throw new InvalidDataException($"M2: view vertex lookup entry {i} ({lookup[i]}) points past the {vertices.Length} vertices.");
                }
            }

            if (triangleCount % 3 != 0)
            {
                throw new InvalidDataException($"M2: view 0 lists {triangleCount} triangle indices, not a multiple of 3.");
            }

            renderIndices = new int[triangleCount];
            for (int i = 0; i < renderIndices.Length; i++)
            {
                ushort local = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(triangleOffset + (uint)(2 * i))..]);
                if (local >= lookup.Length)
                {
                    throw new InvalidDataException($"M2: view triangle index {i} ({local}) points past the {lookup.Length} lookup entries.");
                }

                renderIndices[i] = lookup[local];
            }
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:49-63 (nBoundingTriangles counts u16 indices, not triangles; vertices are Vec3D at ofsBoundingVertices)
        (uint boundingIndexCount, uint boundingIndexOffset) = ReadArray(data, OffsetBoundingTriangles);
        (uint boundingVertexCount, uint boundingVertexOffset) = ReadArray(data, OffsetBoundingVertices);
        RequireRange(data, boundingIndexOffset, boundingIndexCount, 2, "bounding triangles");
        RequireRange(data, boundingVertexOffset, boundingVertexCount, 12, "bounding vertices");
        var boundingIndices = new ushort[boundingIndexCount];
        for (int i = 0; i < boundingIndices.Length; i++)
        {
            boundingIndices[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(boundingIndexOffset + (uint)(2 * i))..]);
        }

        var boundingVertices = new Vector3[boundingVertexCount];
        for (int i = 0; i < boundingVertices.Length; i++)
        {
            boundingVertices[i] = ReadVector(data[(int)(boundingVertexOffset + (uint)(12 * i))..]);
        }

        if (boundingVertices.Length > 0)
        {
            if (boundingIndices.Length % 3 != 0)
            {
                throw new InvalidDataException($"M2: {boundingIndices.Length} bounding triangle indices is not a multiple of 3.");
            }

            foreach (ushort index in boundingIndices)
            {
                if (index >= boundingVertices.Length)
                {
                    throw new InvalidDataException($"M2: a bounding triangle index ({index}) points past the {boundingVertices.Length} bounding vertices.");
                }
            }
        }

        foreach (Vector3 v in vertices.Concat(boundingVertices))
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            {
                throw new InvalidDataException("M2: a vertex is not finite.");
            }
        }

        return new M2ModelFile(name, vertices, (int)viewCount, lookup, renderIndices, boundingIndices, boundingVertices);
    }

    private static (Vector3 Min, Vector3 Max) Box(Vector3[] vertices)
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

    private static string ReadName(ReadOnlySpan<byte> data)
    {
        (uint count, uint offset) = ReadArray(data, OffsetName);
        if (count == 0 || offset >= (uint)data.Length || count > (uint)data.Length - offset)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> raw = data.Slice((int)offset, (int)count);
        int nul = raw.IndexOf((byte)0);
        return Encoding.Latin1.GetString(nul < 0 ? raw : raw[..nul]);
    }

    private static (uint Count, uint Offset) ReadArray(ReadOnlySpan<byte> data, int at)
        => (BinaryPrimitives.ReadUInt32LittleEndian(data[at..]), BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]));

    private static void RequireRange(ReadOnlySpan<byte> data, uint offset, uint count, int elementSize, string what)
    {
        if (count == 0)
        {
            return;
        }

        ulong end = offset + ((ulong)count * (uint)elementSize);
        if (end > (ulong)data.Length)
        {
            throw new InvalidDataException($"M2: {what} ({count} x {elementSize} bytes at {offset}) end past the {data.Length}-byte file.");
        }
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> span)
        => new(
            BinaryPrimitives.ReadSingleLittleEndian(span),
            BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(span[8..]));
}

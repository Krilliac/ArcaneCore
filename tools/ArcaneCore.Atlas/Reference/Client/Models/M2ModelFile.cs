using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// A triangle corner index triple into a model's vertex array.
/// </summary>
public readonly record struct ModelTriangle(int A, int B, int C)
{
    public int Index0 => A;

    public int Index1 => B;

    public int Index2 => C;
}

/// <summary>
/// A parsed 1.12.1 M2 model (<c>MD20</c>, version 256..260): the render vertices and normals, the first embedded view's triangles and the
/// bounding (collision) triangles and vertices the vmap extractor keeps. Positions are in model space exactly as stored.
/// Instances are immutable after <c>Parse</c> and safe to share between threads.
/// </summary>
public sealed class M2ModelFile
{
    /// <summary>The primary M2 version of 1.12.1 models.</summary>
    public const uint SupportedVersion = 256;

    /// <summary>Minimum supported MD20 version in 1.12.1.</summary>
    public const uint MinSupportedVersion = 256;

    /// <summary>Maximum supported MD20 version in 1.12.1.</summary>
    public const uint MaxSupportedVersion = 260;

    // Header field offsets. The bounding-array offsets are counted from the vmangos ModelHeader struct, whose first
    // field is at 0 and which is packed to 1 byte, and were confirmed on a real file (elfbed01.m2: nBoundingTriangles 294
    // at 0xEC, nBoundingVertices 65 at 0xF4, both ranges inside the file).
    // Note: Vanilla 1.12.1 MD20 places vertices at 0x44 and views at 0x4C; some synthetic headers place vertices at 0x3C and views at 0x44.
    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/modelheaders.h:31-102 (ModelHeader field order)
    // ref: Worldforge@1f73a08/src/m2.cpp:10-13 (name 0x08, vertices 0x44, views 0x4C)
    private const int OffsetName = 0x08;
    private const int OffsetAltVertices = 0x3C;
    private const int OffsetVertices = 0x44;
    private const int OffsetAltViews = 0x44;
    private const int OffsetViews = 0x4C;
    private const int OffsetBoundingTriangles = 0xEC;
    private const int OffsetBoundingVertices = 0xF4;
    private const int MinimumHeaderBytes = 0x104;

    // ref: Worldforge@1f73a08/src/m2.cpp:15 (48-byte vertices) and :112-121 (position 12B, bone weights 4B, bone indices 4B, normal 12B at +20, uv 8B at +32, unknown 8B at +40)
    private const int VertexSize = 48;
    private const int VertexNormalOffset = 20;

    // ref: Worldforge@1f73a08/src/m2.cpp:144 (view header: five M2Arrays plus u32 = 44 bytes) and :147-148 (vertex lookup, then triangles)
    private const int ViewSize = 44;

    private M2ModelFile(
        string name,
        Vector3[] vertices,
        Vector3[] normals,
        int viewCount,
        ushort[] viewLookup,
        int[] renderIndices,
        ModelTriangle[] visualTriangles,
        ushort[] boundingIndices,
        Vector3[] boundingVertices)
    {
        Name = name;
        Vertices = vertices;
        VisualNormals = normals;
        ViewCount = viewCount;
        ViewLookup = viewLookup;
        RenderIndices = renderIndices;
        VisualTriangles = visualTriangles;
        BoundingIndices = boundingIndices;
        BoundingVertices = boundingVertices;
    }

    public string Name { get; }

    /// <summary>Render vertex positions (the global vertex pool).</summary>
    public Vector3[] Vertices { get; }

    /// <summary>Render vertex positions (alias for <c>Vertices</c>).</summary>
    public Vector3[] VisualVertices => Vertices;

    /// <summary>Render vertex normals in model space, one per entry in <c>VisualVertices</c>.</summary>
    public Vector3[] VisualNormals { get; }

    /// <summary>Number of embedded views (level-of-detail meshes) in the file.</summary>
    public int ViewCount { get; }

    /// <summary>View 0's local-to-global vertex lookup.</summary>
    public ushort[] ViewLookup { get; }

    /// <summary>View 0's triangles as indices into <c>Vertices</c>, three per triangle; empty without a view.</summary>
    public int[] RenderIndices { get; }

    /// <summary>View 0's triangles as <c>ModelTriangle</c> triples into <c>VisualVertices</c>; empty without a valid view.</summary>
    public ModelTriangle[] VisualTriangles { get; }

    /// <summary>True when the model has at least one visual vertex and one visual triangle from an embedded view.</summary>
    public bool HasVisualMesh => Vertices.Length > 0 && VisualTriangles.Length > 0;

    /// <summary>Bounding (collision) triangle corner indices into <c>BoundingVertices</c>, three per triangle, as stored.</summary>
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

    /// <summary>Alias for <c>HasCollisionHull</c>.</summary>
    public bool HasCollision => HasCollisionHull;

    /// <summary>Box around the bounding (collision) vertices, or zero without a hull.</summary>
    public (Vector3 Min, Vector3 Max) BoundingBox => Box(BoundingVertices);

    /// <summary>Box around the render vertices, or zero without vertices.</summary>
    public (Vector3 Min, Vector3 Max) RenderBox => Box(Vertices);

    public int RenderTriangleCount => RenderIndices.Length / 3;

    public int BoundingTriangleCount => BoundingIndices.Length / 3;

    /// <summary>Approximate heap size, for the model cache.</summary>
    public long EstimatedBytes => 256 + (Vertices.Length * 24L) + (ViewLookup.Length * 2L) + (RenderIndices.Length * 4L)
        + (VisualTriangles.Length * 12L) + (BoundingIndices.Length * 2L) + (BoundingVertices.Length * 12L);

    /// <summary>Parses a model. Throws <c>InvalidDataException</c> for an empty, truncated, wrong-version or inconsistent file.</summary>
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
        if (version < MinSupportedVersion || version > MaxSupportedVersion)
        {
            throw new InvalidDataException($"M2: version {version} is not the 1.12.1 version {SupportedVersion}.");
        }

        string name = ReadName(data);

        // Support both canonical 1.12.1 offsets (0x44 vertices, 0x4C views) and alternate header layout (0x3C vertices, 0x44 views).
        (uint vertexCount, uint vertexOffset) = ReadArray(data, OffsetVertices);
        (uint viewCount, uint viewOffset) = ReadArray(data, OffsetViews);
        (uint altVertCount, uint altVertOffset) = ReadArray(data, OffsetAltVertices);
        (uint altViewCount, uint altViewOffset) = ReadArray(data, OffsetAltViews);
        bool primaryValid = (vertexCount == 0 && vertexOffset == 0)
            || (IsRangeInBounds(data, vertexOffset, vertexCount, VertexSize)
                && (viewCount == 0 || IsRangeInBounds(data, viewOffset, 1, ViewSize)));
        if ((!primaryValid || (vertexCount == 0 && vertexOffset == 0) || (viewCount == 0 && viewOffset == 0 && altVertCount > 0))
            && (altVertCount > 0 || altViewCount > 0)
            && IsRangeInBounds(data, altVertOffset, altVertCount, VertexSize))
        {
            vertexCount = altVertCount;
            vertexOffset = altVertOffset;
            viewCount = altViewCount;
            viewOffset = altViewOffset;
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:49-63 (nBoundingTriangles counts u16 indices, not triangles; vertices are Vec3D at ofsBoundingVertices)
        (uint boundingIndexCount, uint boundingIndexOffset) = ReadArray(data, OffsetBoundingTriangles);
        (uint boundingVertexCount, uint boundingVertexOffset) = ReadArray(data, OffsetBoundingVertices);
        RequireRange(data, boundingIndexOffset, boundingIndexCount, 2, "bounding triangles");
        RequireRange(data, boundingVertexOffset, boundingVertexCount, 12, "bounding vertices");

        if (boundingIndexCount == 0 && boundingVertexCount == 0)
        {
            RequireRange(data, vertexOffset, vertexCount, VertexSize, "vertices");
        }

        Vector3[] vertices = [];
        Vector3[] normals = [];
        if (vertexCount > 0 && IsRangeInBounds(data, vertexOffset, vertexCount, VertexSize))
        {
            var parsedVertices = new Vector3[vertexCount];
            var parsedNormals = new Vector3[vertexCount];
            bool allFinite = true;
            for (int i = 0; i < parsedVertices.Length; i++)
            {
                int baseOffset = (int)(vertexOffset + (uint)(i * VertexSize));
                Vector3 pos = ReadVector(data[baseOffset..]);
                Vector3 norm = ReadVector(data[(baseOffset + VertexNormalOffset)..]);
                if (!IsFinite(pos))
                {
                    throw new InvalidDataException("M2: a vertex is not finite.");
                }

                if (!IsFinite(norm))
                {
                    allFinite = false;
                    norm = Vector3.Zero;
                }

                parsedVertices[i] = pos;
                parsedNormals[i] = norm;
            }

            _ = allFinite;
            vertices = parsedVertices;
            normals = parsedNormals;
        }

        ushort[] lookup = [];
        int[] renderIndices = [];
        ModelTriangle[] visualTriangles = [];
        if (viewCount > 0 && vertices.Length > 0 && IsRangeInBounds(data, viewOffset, 1, ViewSize))
        {
            ReadOnlySpan<byte> view = data[(int)viewOffset..];
            (uint lookupCount, uint lookupOffset) = ReadArray(view, 0);
            (uint triangleCount, uint triangleOffset) = ReadArray(view, 8);
            if (lookupCount > 0
                && triangleCount > 0
                && IsRangeInBounds(data, lookupOffset, lookupCount, 2)
                && IsRangeInBounds(data, triangleOffset, triangleCount, 2))
            {
                if (triangleCount % 3 != 0)
                {
                    throw new InvalidDataException($"M2: view 0 lists {triangleCount} triangle indices, not a multiple of 3.");
                }

                var parsedLookup = new ushort[lookupCount];
                for (int i = 0; i < parsedLookup.Length; i++)
                {
                    parsedLookup[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(lookupOffset + (uint)(2 * i))..]);
                    if (parsedLookup[i] >= vertices.Length)
                    {
                        throw new InvalidDataException($"M2: view vertex lookup entry {i} ({parsedLookup[i]}) points past the {vertices.Length} vertices.");
                    }
                }

                var parsedRenderIndices = new int[triangleCount];
                for (int i = 0; i < parsedRenderIndices.Length; i++)
                {
                    ushort local = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(triangleOffset + (uint)(2 * i))..]);
                    if (local >= parsedLookup.Length)
                    {
                        throw new InvalidDataException($"M2: view triangle index {i} ({local}) points past the {parsedLookup.Length} lookup entries.");
                    }

                    parsedRenderIndices[i] = parsedLookup[local];
                }

                int triStructCount = (int)(triangleCount / 3);
                var parsedTriangles = new ModelTriangle[triStructCount];
                for (int t = 0; t < triStructCount; t++)
                {
                    parsedTriangles[t] = new ModelTriangle(
                        parsedRenderIndices[3 * t],
                        parsedRenderIndices[(3 * t) + 1],
                        parsedRenderIndices[(3 * t) + 2]);
                }

                lookup = parsedLookup;
                renderIndices = parsedRenderIndices;
                visualTriangles = parsedTriangles;
            }
        }


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

        foreach (Vector3 v in boundingVertices)
        {
            if (!IsFinite(v))
            {
                throw new InvalidDataException("M2: a vertex is not finite.");
            }
        }

        return new M2ModelFile(
            name,
            vertices,
            normals,
            (int)viewCount,
            lookup,
            renderIndices,
            visualTriangles,
            boundingIndices,
            boundingVertices);
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

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

    private static bool IsRangeInBounds(ReadOnlySpan<byte> data, uint offset, uint count, int elementSize)
    {
        if (count == 0)
        {
            return offset <= (uint)data.Length;
        }

        ulong end = offset + ((ulong)count * (uint)elementSize);
        return end <= (ulong)data.Length;
    }

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

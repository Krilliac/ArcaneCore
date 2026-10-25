using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Materials;

namespace ArcaneCore.Atlas.Core;

/// <summary>
/// Optional per-vertex streams of ATG1 version 2. A field left null is not written for this vertex; every vertex of a chunk must set
/// the same fields. Packed colour words are the four bytes R, G, B, A in memory order (R is the least significant byte); bone
/// indices and weights are four bytes each in the same way.
/// </summary>
public struct VertexExtras
{
    public Vector3? Normal { get; set; }

    public Vector2? Uv0 { get; set; }

    public Vector2? Uv1 { get; set; }

    public uint? Color0 { get; set; }

    public uint? Color1 { get; set; }

    public uint? BoneIndices { get; set; }

    public uint? BoneWeights { get; set; }

    public ushort? Submesh { get; set; }

    /// <summary>The ATG1 stream-mask bits of the fields that are set.</summary>
    internal readonly uint Mask => (Normal.HasValue ? GeometryWire.StreamNormal : 0u)
        | (Uv0.HasValue ? GeometryWire.StreamUv0 : 0u)
        | (Uv1.HasValue ? GeometryWire.StreamUv1 : 0u)
        | (Color0.HasValue ? GeometryWire.StreamColor0 : 0u)
        | (Color1.HasValue ? GeometryWire.StreamColor1 : 0u)
        | (BoneIndices.HasValue ? GeometryWire.StreamBoneIndices : 0u)
        | (BoneWeights.HasValue ? GeometryWire.StreamBoneWeights : 0u)
        | (Submesh.HasValue ? GeometryWire.StreamSubmesh : 0u);
}

/// <summary>
/// Renderable geometry for one layer on one tile: vertices relative to a double-precision <see cref="Origin"/>,
/// triangles (for fills, depth and picking), line segments (the wireframe actually drawn) and points. Every
/// primitive carries an attribute id; <see cref="Attributes"/> describes each id (the inspector shows it, and its
/// <c>colorKey</c> field picks a colour from <see cref="LayerInfo.AttributeColors"/>).
/// A chunk may also carry the optional version 2 streams (normals, texture coordinates, colours, skinning, submesh ids), draw
/// <see cref="Batches"/> and the <see cref="Materials"/> they index; a chunk with any of those is written as ATG1 version 2.
/// </summary>
public sealed class GeometryChunk
{
    public required string LayerId { get; init; }

    public required TileKey Tile { get; init; }

    /// <summary>Index of this chunk when a tile's layer is split to stay under <see cref="GeometryWire.MaxChunkBytes"/>.</summary>
    public int Part { get; init; }

    public required WorldPoint Origin { get; init; }

    /// <summary>x, y, z per vertex, relative to <see cref="Origin"/>.</summary>
    public required float[] Positions { get; init; }

    /// <summary>Three vertex indices per triangle.</summary>
    public uint[] Triangles { get; init; } = [];

    /// <summary>One attribute id per triangle.</summary>
    public uint[] TriangleAttributes { get; init; } = [];

    /// <summary>Two vertex indices per segment.</summary>
    public uint[] Lines { get; init; } = [];

    /// <summary>One attribute id per segment.</summary>
    public uint[] LineAttributes { get; init; } = [];

    public uint[] Points { get; init; } = [];

    public uint[] PointAttributes { get; init; } = [];

    /// <summary>One normal per vertex, or null.</summary>
    public Vector3[]? Normals { get; init; }

    /// <summary>One texture coordinate pair per vertex, or null.</summary>
    public Vector2[]? Uv0 { get; init; }

    /// <summary>A second texture coordinate pair per vertex, or null.</summary>
    public Vector2[]? Uv1 { get; init; }

    /// <summary>One packed RGBA colour per vertex (R in the least significant byte), or null.</summary>
    public uint[]? Color0 { get; init; }

    public uint[]? Color1 { get; init; }

    /// <summary>Four bone indices per vertex packed as bytes, or null.</summary>
    public uint[]? BoneIndices { get; init; }

    /// <summary>Four bone weights per vertex packed as bytes (summing to 255), or null.</summary>
    public uint[]? BoneWeights { get; init; }

    /// <summary>The model submesh id of each vertex, or null.</summary>
    public ushort[]? Submesh { get; init; }

    /// <summary>Draw ranges. Either empty (the chunk draws as one) or covering every triangle in order.</summary>
    public GeometryBatch[] Batches { get; init; } = [];

    /// <summary>The materials the <see cref="Batches"/> index.</summary>
    public Material[] Materials { get; init; } = [];

    /// <summary>The model package block of the JSON trailer (<c>"model"</c>), or null.</summary>
    public JsonObject? Model { get; init; }

    /// <summary>Attribute id to descriptive fields (any JSON object; <c>colorKey</c> is reserved for colouring).</summary>
    public IReadOnlyDictionary<uint, JsonObject> Attributes { get; init; } = new Dictionary<uint, JsonObject>();

    /// <summary>Free-form statistics the source wants surfaced (counts, timings, parse notes).</summary>
    public JsonObject Stats { get; init; } = [];

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Triangles.Length / 3;

    public int LineCount => Lines.Length / 2;

    public int PointCount => Points.Length;

    /// <summary>True when the chunk needs ATG1 version 2: it has a vertex stream, batches, materials or a model block.</summary>
    public bool IsV2 => StreamMask != 0 || Batches.Length > 0 || Materials.Length > 0 || Model is not null;

    /// <summary>The ATG1 stream-mask bits of the streams this chunk carries.</summary>
    public uint StreamMask => (Normals is not null ? GeometryWire.StreamNormal : 0u)
        | (Uv0 is not null ? GeometryWire.StreamUv0 : 0u)
        | (Uv1 is not null ? GeometryWire.StreamUv1 : 0u)
        | (Color0 is not null ? GeometryWire.StreamColor0 : 0u)
        | (Color1 is not null ? GeometryWire.StreamColor1 : 0u)
        | (BoneIndices is not null ? GeometryWire.StreamBoneIndices : 0u)
        | (BoneWeights is not null ? GeometryWire.StreamBoneWeights : 0u)
        | (Submesh is not null ? GeometryWire.StreamSubmesh : 0u);

    /// <summary>World position of vertex <paramref name="index"/>.</summary>
    public WorldPoint Vertex(uint index)
    {
        int i = checked((int)index * 3);
        return new WorldPoint(Origin.X + Positions[i], Origin.Y + Positions[i + 1], Origin.Z + Positions[i + 2]);
    }

    /// <summary>Throws when counts disagree or an index points past the vertices.</summary>
    public void Validate()
    {
        if (Positions.Length % 3 != 0 || Triangles.Length % 3 != 0 || Lines.Length % 2 != 0)
        {
            throw new InvalidDataException($"{LayerId} {Tile}: array lengths are not whole primitives.");
        }

        if (TriangleAttributes.Length != TriangleCount || LineAttributes.Length != LineCount || PointAttributes.Length != PointCount)
        {
            throw new InvalidDataException($"{LayerId} {Tile}: attribute arrays do not match the primitive counts.");
        }

        uint vertices = (uint)VertexCount;
        if (Triangles.Any(i => i >= vertices) || Lines.Any(i => i >= vertices) || Points.Any(i => i >= vertices))
        {
            throw new InvalidDataException($"{LayerId} {Tile}: an index points past the {vertices} vertices.");
        }

        if (!Origin.IsFinite || Positions.Any(p => !float.IsFinite(p)))
        {
            throw new InvalidDataException($"{LayerId} {Tile}: a position is not finite.");
        }

        ValidateStreams();
        ValidateBatches();
    }

    private void ValidateStreams()
    {
        int vertices = VertexCount;
        if ((Normals is not null && Normals.Length != vertices)
            || (Uv0 is not null && Uv0.Length != vertices)
            || (Uv1 is not null && Uv1.Length != vertices)
            || (Color0 is not null && Color0.Length != vertices)
            || (Color1 is not null && Color1.Length != vertices)
            || (BoneIndices is not null && BoneIndices.Length != vertices)
            || (BoneWeights is not null && BoneWeights.Length != vertices)
            || (Submesh is not null && Submesh.Length != vertices))
        {
            throw new InvalidDataException($"{LayerId} {Tile}: a vertex stream does not have one entry per vertex ({vertices}).");
        }

        if ((Normals is not null && Normals.Any(n => !float.IsFinite(n.X) || !float.IsFinite(n.Y) || !float.IsFinite(n.Z)))
            || (Uv0 is not null && Uv0.Any(t => !float.IsFinite(t.X) || !float.IsFinite(t.Y)))
            || (Uv1 is not null && Uv1.Any(t => !float.IsFinite(t.X) || !float.IsFinite(t.Y))))
        {
            throw new InvalidDataException($"{LayerId} {Tile}: a normal or texture coordinate is not finite.");
        }
    }

    private void ValidateBatches()
    {
        if (Batches.Length == 0)
        {
            return;
        }

        long next = 0;
        foreach (GeometryBatch batch in Batches)
        {
            if (batch.TriangleCount == 0 || batch.FirstTriangle != next)
            {
                throw new InvalidDataException($"{LayerId} {Tile}: batches must be non-empty, in order and contiguous.");
            }

            next += batch.TriangleCount;
            if (next > TriangleCount)
            {
                throw new InvalidDataException($"{LayerId} {Tile}: a batch runs past the {TriangleCount} triangles.");
            }

            if (batch.Material >= (uint)Materials.Length)
            {
                throw new InvalidDataException($"{LayerId} {Tile}: a batch uses material {batch.Material} but there are {Materials.Length}.");
            }
        }

        if (next != TriangleCount)
        {
            throw new InvalidDataException($"{LayerId} {Tile}: batches cover {next} of {TriangleCount} triangles; they must cover all or none.");
        }
    }
}

/// <summary>
/// Accumulates a <see cref="GeometryChunk"/>. Vertices are given in world coordinates and stored relative to the
/// origin. <see cref="AddTriangle"/> with <c>edges: true</c> also adds the triangle's three edges as wireframe
/// segments, each shared edge once. Vertices may carry <see cref="VertexExtras"/> (all vertices of a chunk the same), and
/// triangles added between <see cref="BeginBatch"/> and <see cref="EndBatch"/> form one draw batch.
/// </summary>
public sealed class GeometryBuilder(string layerId, TileKey tile, WorldPoint origin, int part = 0)
{
    private readonly List<float> _positions = [];
    private readonly List<uint> _triangles = [];
    private readonly List<uint> _triangleAttributes = [];
    private readonly List<uint> _lines = [];
    private readonly List<uint> _lineAttributes = [];
    private readonly List<uint> _points = [];
    private readonly List<uint> _pointAttributes = [];
    private readonly HashSet<ulong> _edges = new(EdgeKeyComparer.Instance);
    private readonly Dictionary<uint, JsonObject> _attributes = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<Vector2> _uv0 = [];
    private readonly List<Vector2> _uv1 = [];
    private readonly List<uint> _color0 = [];
    private readonly List<uint> _color1 = [];
    private readonly List<uint> _boneIndices = [];
    private readonly List<uint> _boneWeights = [];
    private readonly List<ushort> _submesh = [];
    private readonly List<Material> _materials = [];
    private readonly Dictionary<string, uint> _materialIndex = new(StringComparer.Ordinal);
    private readonly List<GeometryBatch> _batches = [];
    private uint _streamMask;
    private Material? _openMaterial;
    private uint _openTag;
    private int _openFirst;

    public string LayerId { get; } = layerId;

    public TileKey Tile { get; } = tile;

    public WorldPoint Origin { get; } = origin;

    public int VertexCount => _positions.Count / 3;

    public int TriangleCount => _triangles.Count / 3;

    public int LineCount => _lines.Count / 2;

    /// <summary>Approximate wire size so far, for splitting large tiles.</summary>
    public long EstimatedBytes => (_positions.Count * 4L) + (_triangles.Count * 4L) + (_triangleAttributes.Count * 4L)
        + (_lines.Count * 4L) + (_lineAttributes.Count * 4L) + (_points.Count * 8L)
        + (_normals.Count * 12L) + ((_uv0.Count + _uv1.Count) * 8L)
        + ((_color0.Count + _color1.Count + _boneIndices.Count + _boneWeights.Count) * 4L) + (_submesh.Count * 4L)
        + (_batches.Count * 16L);

    public JsonObject Stats { get; } = [];

    /// <summary>The "model" block of the built chunk (model packages only); null for every other layer.</summary>
    public JsonObject? Model { get; set; }

    public uint AddVertex(double x, double y, double z) => AddVertex(x, y, z, default(VertexExtras));

    public uint AddVertex(double x, double y, double z, in VertexExtras extras)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            throw new ArgumentException($"{LayerId} {Tile}: vertex ({x}, {y}, {z}) is not finite.");
        }

        uint index = (uint)VertexCount;
        uint mask = extras.Mask;
        if (index == 0)
        {
            _streamMask = mask;
        }
        else if (mask != _streamMask)
        {
            throw new ArgumentException($"{LayerId} {Tile}: vertex {index} sets streams 0x{mask:X} but earlier vertices set 0x{_streamMask:X}; every vertex must carry the same streams.");
        }

        _positions.Add((float)(x - Origin.X));
        _positions.Add((float)(y - Origin.Y));
        _positions.Add((float)(z - Origin.Z));
        if (mask != 0)
        {
            if (extras.Normal is Vector3 normal)
            {
                _normals.Add(normal);
            }

            if (extras.Uv0 is Vector2 uv0)
            {
                _uv0.Add(uv0);
            }

            if (extras.Uv1 is Vector2 uv1)
            {
                _uv1.Add(uv1);
            }

            if (extras.Color0 is uint color0)
            {
                _color0.Add(color0);
            }

            if (extras.Color1 is uint color1)
            {
                _color1.Add(color1);
            }

            if (extras.BoneIndices is uint boneIndices)
            {
                _boneIndices.Add(boneIndices);
            }

            if (extras.BoneWeights is uint boneWeights)
            {
                _boneWeights.Add(boneWeights);
            }

            if (extras.Submesh is ushort submesh)
            {
                _submesh.Add(submesh);
            }
        }

        return index;
    }

    public uint AddVertex(WorldPoint p) => AddVertex(p.X, p.Y, p.Z);

    public void AddTriangle(uint a, uint b, uint c, uint attribute = 0, bool edges = true)
    {
        _triangles.Add(a);
        _triangles.Add(b);
        _triangles.Add(c);
        _triangleAttributes.Add(attribute);
        if (edges)
        {
            AddEdge(a, b, attribute);
            AddEdge(b, c, attribute);
            AddEdge(c, a, attribute);
        }
    }

    /// <summary>Starts a draw batch; the triangles added until <see cref="EndBatch"/> use <paramref name="material"/>.</summary>
    /// <exception cref="InvalidOperationException">A batch is already open.</exception>
    public void BeginBatch(Material material, uint tag = 0)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (_openMaterial is not null)
        {
            throw new InvalidOperationException($"{LayerId} {Tile}: a batch is already open.");
        }

        _openMaterial = material;
        _openTag = tag;
        _openFirst = TriangleCount;
    }

    /// <summary>Closes the open batch. A batch that received no triangles is dropped. Equal materials share one table entry.</summary>
    /// <exception cref="InvalidOperationException">No batch is open.</exception>
    public void EndBatch()
    {
        Material material = _openMaterial ?? throw new InvalidOperationException($"{LayerId} {Tile}: no batch is open.");
        _openMaterial = null;
        int count = TriangleCount - _openFirst;
        if (count == 0)
        {
            return;
        }

        string identity = material.Identity;
        if (!_materialIndex.TryGetValue(identity, out uint index))
        {
            index = (uint)_materials.Count;
            _materials.Add(material);
            _materialIndex[identity] = index;
        }

        _batches.Add(new GeometryBatch((uint)_openFirst, (uint)count, index, _openTag));
    }

    /// <summary>Adds a segment unless the same undirected edge was already added through this method.</summary>
    public void AddEdge(uint a, uint b, uint attribute = 0)
    {
        if (a == b)
        {
            return;
        }

        ulong key = a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;
        if (_edges.Add(key))
        {
            AddLine(a, b, attribute);
        }
    }

    /// <summary>Adds a segment unconditionally.</summary>
    public void AddLine(uint a, uint b, uint attribute = 0)
    {
        _lines.Add(a);
        _lines.Add(b);
        _lineAttributes.Add(attribute);
    }

    public void AddPoint(uint vertex, uint attribute = 0)
    {
        _points.Add(vertex);
        _pointAttributes.Add(attribute);
    }

    /// <summary>Describes an attribute id once; later calls for the same id are ignored.</summary>
    public void DescribeAttribute(uint attribute, Func<JsonObject> describe)
    {
        if (!_attributes.ContainsKey(attribute))
        {
            _attributes[attribute] = describe();
        }
    }

    public bool HasAttribute(uint attribute) => _attributes.ContainsKey(attribute);

    public bool IsEmpty => _triangles.Count == 0 && _lines.Count == 0 && _points.Count == 0;

    /// <exception cref="InvalidOperationException">A batch is still open.</exception>
    /// <exception cref="InvalidDataException">The batches do not cover the triangles, or a stream is inconsistent.</exception>
    public GeometryChunk Build()
    {
        if (_openMaterial is not null)
        {
            throw new InvalidOperationException($"{LayerId} {Tile}: a batch is still open.");
        }

        var chunk = new GeometryChunk
        {
            LayerId = LayerId,
            Tile = Tile,
            Part = part,
            Origin = Origin,
            Positions = [.. _positions],
            Triangles = [.. _triangles],
            TriangleAttributes = [.. _triangleAttributes],
            Lines = [.. _lines],
            LineAttributes = [.. _lineAttributes],
            Points = [.. _points],
            PointAttributes = [.. _pointAttributes],
            Normals = (_streamMask & GeometryWire.StreamNormal) != 0 ? [.. _normals] : null,
            Uv0 = (_streamMask & GeometryWire.StreamUv0) != 0 ? [.. _uv0] : null,
            Uv1 = (_streamMask & GeometryWire.StreamUv1) != 0 ? [.. _uv1] : null,
            Color0 = (_streamMask & GeometryWire.StreamColor0) != 0 ? [.. _color0] : null,
            Color1 = (_streamMask & GeometryWire.StreamColor1) != 0 ? [.. _color1] : null,
            BoneIndices = (_streamMask & GeometryWire.StreamBoneIndices) != 0 ? [.. _boneIndices] : null,
            BoneWeights = (_streamMask & GeometryWire.StreamBoneWeights) != 0 ? [.. _boneWeights] : null,
            Submesh = (_streamMask & GeometryWire.StreamSubmesh) != 0 ? [.. _submesh] : null,
            Batches = [.. _batches],
            Materials = [.. _materials],
            Attributes = new Dictionary<uint, JsonObject>(_attributes),
            Stats = Stats,
            Model = Model,
        };
        chunk.Validate();
        return chunk;
    }

    private sealed class EdgeKeyComparer : IEqualityComparer<ulong>
    {
        public static readonly EdgeKeyComparer Instance = new();

        public bool Equals(ulong x, ulong y) => x == y;

        public int GetHashCode(ulong key) => unchecked((int)(((key ^ (key >> 30)) * 0xBF58476D1CE4E5B9UL) >> 32));
    }
}

/// <summary>
/// The "ATG1" binary form of a <see cref="GeometryChunk"/> served to the UI (all little-endian):
/// <code>
/// u32 magic 0x31475441 ("ATG1") | u16 version 1 | u16 flags (bit0 triangles, bit1 lines, bit2 points)
/// f64 originX | f64 originY | f64 originZ
/// u32 vertexCount | u32 triangleCount | u32 lineCount | u32 pointCount        (header ends at byte 48)
/// f32[3*vertexCount] positions | u32[3*triangleCount] | u32[triangleCount] attributes
/// u32[2*lineCount] | u32[lineCount] attributes | u32[pointCount] | u32[pointCount] attributes
/// u32 jsonBytes | UTF-8 JSON {"layer","tile":[map,x,y],"part","attributes":{"id":{...}},"stats":{...}}
/// </code>
/// Version 2 (written only for chunks with vertex streams, batches, materials or a model block) has a 64-byte header: the 48
/// bytes above, then <c>u32 streamMask</c>, <c>u32 batchCount</c> and two reserved words. After the positions come the present
/// streams in bit order (0 normal f32x3, 1 uv0 f32x2, 2 uv1 f32x2, 3 color0 u8x4, 4 color1 u8x4, 5 boneIndices u8x4,
/// 6 boneWeights u8x4, 7 submesh u16 plus 2 pad bytes), then the arrays above, then the batches (4 words each: firstTriangle,
/// triangleCount, material, tag), then the JSON, which also carries <c>"materials"</c> and <c>"model"</c> when present.
/// Every array starts on a 4-byte boundary so the browser can view it in place.
/// </summary>
public static class GeometryWire
{
    public const uint Magic = 0x31475441;
    public const ushort Version = 1;
    public const ushort VersionV2 = 2;
    public const int HeaderBytes = 48;
    public const int HeaderBytesV2 = 64;

    public const uint StreamNormal = 1u << 0;
    public const uint StreamUv0 = 1u << 1;
    public const uint StreamUv1 = 1u << 2;
    public const uint StreamColor0 = 1u << 3;
    public const uint StreamColor1 = 1u << 4;
    public const uint StreamBoneIndices = 1u << 5;
    public const uint StreamBoneWeights = 1u << 6;
    public const uint StreamSubmesh = 1u << 7;

    private const uint KnownStreams = 0xFF;

    /// <summary>Upper bound for one chunk on the wire; sources split larger tiles into parts.</summary>
    public const long MaxChunkBytes = 64L * 1024 * 1024;

    public static byte[] Write(GeometryChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        chunk.Validate();
        bool v2 = chunk.IsV2;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(Metadata(chunk));
        int vertices = chunk.VertexCount;
        long size = (v2 ? HeaderBytesV2 : HeaderBytes)
            + (chunk.Positions.Length * 4L)
            + (chunk.Triangles.Length * 4L) + (chunk.TriangleAttributes.Length * 4L)
            + (chunk.Lines.Length * 4L) + (chunk.LineAttributes.Length * 4L)
            + (chunk.Points.Length * 4L) + (chunk.PointAttributes.Length * 4L)
            + 4 + json.Length;
        if (v2)
        {
            size += (chunk.Normals is null ? 0 : vertices * 12L) + (chunk.Uv0 is null ? 0 : vertices * 8L) + (chunk.Uv1 is null ? 0 : vertices * 8L)
                + (chunk.Color0 is null ? 0 : vertices * 4L) + (chunk.Color1 is null ? 0 : vertices * 4L)
                + (chunk.BoneIndices is null ? 0 : vertices * 4L) + (chunk.BoneWeights is null ? 0 : vertices * 4L)
                + (chunk.Submesh is null ? 0 : vertices * 4L) + (chunk.Batches.Length * 16L);
        }

        if (size > MaxChunkBytes)
        {
            throw new InvalidOperationException($"{chunk.LayerId} {chunk.Tile}: chunk is {size} bytes, over {MaxChunkBytes}.");
        }

        byte[] buffer = new byte[size];
        Span<byte> span = buffer;
        ushort flags = (ushort)((chunk.TriangleCount > 0 ? 1 : 0) | (chunk.LineCount > 0 ? 2 : 0) | (chunk.PointCount > 0 ? 4 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], v2 ? VersionV2 : Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], flags);
        BinaryPrimitives.WriteDoubleLittleEndian(span[8..], chunk.Origin.X);
        BinaryPrimitives.WriteDoubleLittleEndian(span[16..], chunk.Origin.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(span[24..], chunk.Origin.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], (uint)chunk.VertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], (uint)chunk.TriangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)chunk.LineCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], (uint)chunk.PointCount);
        int offset = HeaderBytes;
        if (v2)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[48..], chunk.StreamMask);
            BinaryPrimitives.WriteUInt32LittleEndian(span[52..], (uint)chunk.Batches.Length);
            offset = HeaderBytesV2;
        }

        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<float>(chunk.Positions));
        if (v2)
        {
            if (chunk.Normals is not null)
            {
                offset = WriteArray(span, offset, MemoryMarshal.AsBytes<Vector3>(chunk.Normals));
            }

            if (chunk.Uv0 is not null)
            {
                offset = WriteArray(span, offset, MemoryMarshal.AsBytes<Vector2>(chunk.Uv0));
            }

            if (chunk.Uv1 is not null)
            {
                offset = WriteArray(span, offset, MemoryMarshal.AsBytes<Vector2>(chunk.Uv1));
            }

            if (chunk.Color0 is not null)
            {
                offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.Color0));
            }

            if (chunk.Color1 is not null)
            {
                offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.Color1));
            }

            if (chunk.BoneIndices is not null)
            {
                offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.BoneIndices));
            }

            if (chunk.BoneWeights is not null)
            {
                offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.BoneWeights));
            }

            if (chunk.Submesh is not null)
            {
                foreach (ushort id in chunk.Submesh)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], id);
                    offset += 4;
                }
            }
        }

        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.Triangles));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.TriangleAttributes));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.Lines));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.LineAttributes));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.Points));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.PointAttributes));
        if (v2)
        {
            foreach (GeometryBatch batch in chunk.Batches)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], batch.FirstTriangle);
                BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], batch.TriangleCount);
                BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 8)..], batch.Material);
                BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 12)..], batch.Tag);
                offset += 16;
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)json.Length);
        json.CopyTo(span[(offset + 4)..]);
        return buffer;
    }

    public static GeometryChunk Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes + 4)
        {
            throw new InvalidDataException("ATG1 chunk is shorter than its header.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic || (version != Version && version != VersionV2))
        {
            throw new InvalidDataException("Not an ATG1 version 1 or 2 chunk.");
        }

        bool v2 = version == VersionV2;
        int headerBytes = v2 ? HeaderBytesV2 : HeaderBytes;
        if (data.Length < headerBytes + 4)
        {
            throw new InvalidDataException("ATG1 chunk is shorter than its header.");
        }

        var origin = new WorldPoint(
            BinaryPrimitives.ReadDoubleLittleEndian(data[8..]),
            BinaryPrimitives.ReadDoubleLittleEndian(data[16..]),
            BinaryPrimitives.ReadDoubleLittleEndian(data[24..]));
        uint verticesU = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        uint trianglesU = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        uint linesU = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);
        uint pointsU = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        uint streamMask = 0;
        uint batchesU = 0;
        if (v2)
        {
            streamMask = BinaryPrimitives.ReadUInt32LittleEndian(data[48..]);
            batchesU = BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
            if ((streamMask & ~KnownStreams) != 0)
            {
                throw new InvalidDataException($"ATG1 chunk declares unknown vertex streams 0x{streamMask & ~KnownStreams:X}.");
            }
        }

        uint maxWords = (uint)(data.Length - headerBytes) / 4u;
        if (verticesU > maxWords / 3u || trianglesU > maxWords / 4u || linesU > maxWords / 3u || pointsU > maxWords / 2u || batchesU > maxWords / 4u)
        {
            throw new InvalidDataException("ATG1 chunk is truncated.");
        }

        int vertices = (int)verticesU;
        int triangles = (int)trianglesU;
        int lines = (int)linesU;
        int points = (int)pointsU;
        int batchCount = (int)batchesU;
        int offset = headerBytes;
        float[] positions = ReadFloats(data, ref offset, vertices * 3);
        Vector3[]? normals = null;
        Vector2[]? uv0 = null;
        Vector2[]? uv1 = null;
        uint[]? color0 = null;
        uint[]? color1 = null;
        uint[]? boneIndices = null;
        uint[]? boneWeights = null;
        ushort[]? submesh = null;
        if (v2)
        {
            if ((streamMask & StreamNormal) != 0)
            {
                normals = [.. MemoryMarshal.Cast<float, Vector3>(ReadFloats(data, ref offset, vertices * 3))];
            }

            if ((streamMask & StreamUv0) != 0)
            {
                uv0 = [.. MemoryMarshal.Cast<float, Vector2>(ReadFloats(data, ref offset, vertices * 2))];
            }

            if ((streamMask & StreamUv1) != 0)
            {
                uv1 = [.. MemoryMarshal.Cast<float, Vector2>(ReadFloats(data, ref offset, vertices * 2))];
            }

            if ((streamMask & StreamColor0) != 0)
            {
                color0 = ReadUInts(data, ref offset, vertices);
            }

            if ((streamMask & StreamColor1) != 0)
            {
                color1 = ReadUInts(data, ref offset, vertices);
            }

            if ((streamMask & StreamBoneIndices) != 0)
            {
                boneIndices = ReadUInts(data, ref offset, vertices);
            }

            if ((streamMask & StreamBoneWeights) != 0)
            {
                boneWeights = ReadUInts(data, ref offset, vertices);
            }

            if ((streamMask & StreamSubmesh) != 0)
            {
                uint[] words = ReadUInts(data, ref offset, vertices);
                submesh = new ushort[vertices];
                for (int i = 0; i < vertices; i++)
                {
                    submesh[i] = (ushort)(words[i] & 0xFFFF);
                }
            }
        }

        uint[] tri = ReadUInts(data, ref offset, triangles * 3);
        uint[] triAttr = ReadUInts(data, ref offset, triangles);
        uint[] lin = ReadUInts(data, ref offset, lines * 2);
        uint[] linAttr = ReadUInts(data, ref offset, lines);
        uint[] pts = ReadUInts(data, ref offset, points);
        uint[] ptsAttr = ReadUInts(data, ref offset, points);
        GeometryBatch[] batches = [];
        if (batchCount > 0)
        {
            uint[] words = ReadUInts(data, ref offset, batchCount * 4);
            batches = new GeometryBatch[batchCount];
            for (int i = 0; i < batchCount; i++)
            {
                batches[i] = new GeometryBatch(words[i * 4], words[(i * 4) + 1], words[(i * 4) + 2], words[(i * 4) + 3]);
            }
        }

        uint jsonBytesU = ReadUInts(data, ref offset, 1)[0];
        if (jsonBytesU > (uint)(data.Length - offset) || offset + (int)jsonBytesU != data.Length)
        {
            throw new InvalidDataException("ATG1 metadata length does not match the chunk size.");
        }

        int jsonBytes = (int)jsonBytesU;
        GeometryChunk chunk;
        try
        {
            JsonObject meta = JsonNode.Parse(data.Slice(offset, jsonBytes))?.AsObject()
                ?? throw new InvalidDataException("ATG1 metadata is not a JSON object.");
            JsonArray tile = meta["tile"]?.AsArray() ?? throw new InvalidDataException("ATG1 metadata has no tile.");
            if (tile.Count < 3 || tile[0] is null || tile[1] is null || tile[2] is null)
            {
                throw new InvalidDataException("ATG1 metadata tile must have 3 elements.");
            }

            var attributes = new Dictionary<uint, JsonObject>();
            if (meta["attributes"] is JsonObject table)
            {
                foreach ((string key, JsonNode? value) in table)
                {
                    attributes[uint.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] = value?.AsObject().DeepClone().AsObject() ?? [];
                }
            }
            else if (meta["attributes"] is not null)
            {
                throw new InvalidDataException("ATG1 metadata attributes must be a JSON object.");
            }

            Material[] materials = [];
            if (meta["materials"] is not null)
            {
                if (meta["materials"] is not JsonArray list)
                {
                    throw new InvalidDataException("ATG1 metadata materials must be an array.");
                }

                materials = new Material[list.Count];
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = Material.FromJson(list[i] as JsonObject ?? throw new InvalidDataException("ATG1 metadata materials must be objects."));
                }
            }

            JsonObject? model = null;
            if (meta["model"] is not null)
            {
                model = (meta["model"] as JsonObject ?? throw new InvalidDataException("ATG1 metadata model must be an object.")).DeepClone().AsObject();
            }

            chunk = new GeometryChunk
            {
                LayerId = meta["layer"]?.GetValue<string>() ?? throw new InvalidDataException("ATG1 metadata has no layer."),
                Tile = new TileKey(tile[0]!.GetValue<uint>(), tile[1]!.GetValue<int>(), tile[2]!.GetValue<int>()),
                Part = meta["part"]?.GetValue<int>() ?? 0,
                Origin = origin,
                Positions = positions,
                Triangles = tri,
                TriangleAttributes = triAttr,
                Lines = lin,
                LineAttributes = linAttr,
                Points = pts,
                PointAttributes = ptsAttr,
                Normals = normals,
                Uv0 = uv0,
                Uv1 = uv1,
                Color0 = color0,
                Color1 = color1,
                BoneIndices = boneIndices,
                BoneWeights = boneWeights,
                Submesh = submesh,
                Batches = batches,
                Materials = materials,
                Model = model,
                Attributes = attributes,
                Stats = meta["stats"]?.DeepClone().AsObject() ?? [],
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("ATG1 metadata is malformed.", ex);
        }

        chunk.Validate();
        return chunk;
    }

    /// <summary>The JSON trailer of a chunk (also what <c>export.json</c> writes next to the arrays).</summary>
    public static JsonObject Metadata(GeometryChunk chunk)
    {
        var attributes = new JsonObject();
        foreach ((uint id, JsonObject value) in chunk.Attributes.OrderBy(p => p.Key))
        {
            attributes[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = value.DeepClone();
        }

        var meta = new JsonObject
        {
            ["layer"] = chunk.LayerId,
            ["tile"] = new JsonArray(chunk.Tile.MapId, chunk.Tile.X, chunk.Tile.Y),
            ["part"] = chunk.Part,
            ["attributes"] = attributes,
            ["stats"] = chunk.Stats.DeepClone(),
        };
        if (chunk.Materials.Length > 0)
        {
            meta["materials"] = new JsonArray([.. chunk.Materials.Select(m => (JsonNode)m.ToJson())]);
        }

        if (chunk.Model is not null)
        {
            meta["model"] = chunk.Model.DeepClone();
        }

        return meta;
    }

    private static int WriteArray(Span<byte> destination, int offset, ReadOnlySpan<byte> bytes)
    {
        // The arrays are copied as raw memory, which is only the little-endian wire form on a little-endian host.
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("ATG1 chunks are written by little-endian hosts only.");
        }

        bytes.CopyTo(destination[offset..]);
        return offset + bytes.Length;
    }

    private static float[] ReadFloats(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        ReadOnlySpan<byte> bytes = Slice(data, ref offset, count);
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    private static uint[] ReadUInts(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        ReadOnlySpan<byte> bytes = Slice(data, ref offset, count);
        return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("ATG1 chunks are read by little-endian hosts only.");
        }

        long length = count * 4L;
        if (count < 0 || offset + length > data.Length)
        {
            throw new InvalidDataException("ATG1 chunk is truncated.");
        }

        ReadOnlySpan<byte> bytes = data.Slice(offset, (int)length);
        offset += (int)length;
        return bytes;
    }
}

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Core;

/// <summary>
/// Renderable geometry for one layer on one tile: vertices relative to a double-precision <see cref="Origin"/>,
/// triangles (for fills, depth and picking), line segments (the wireframe actually drawn) and points. Every
/// primitive carries an attribute id; <see cref="Attributes"/> describes each id (the inspector shows it, and its
/// <c>colorKey</c> field picks a colour from <see cref="LayerInfo.AttributeColors"/>).
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

    /// <summary>Attribute id to descriptive fields (any JSON object; <c>colorKey</c> is reserved for colouring).</summary>
    public IReadOnlyDictionary<uint, JsonObject> Attributes { get; init; } = new Dictionary<uint, JsonObject>();

    /// <summary>Free-form statistics the source wants surfaced (counts, timings, parse notes).</summary>
    public JsonObject Stats { get; init; } = [];

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Triangles.Length / 3;

    public int LineCount => Lines.Length / 2;

    public int PointCount => Points.Length;

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
    }
}

/// <summary>
/// Accumulates a <see cref="GeometryChunk"/>. Vertices are given in world coordinates and stored relative to the
/// origin. <see cref="AddTriangle"/> with <c>edges: true</c> also adds the triangle's three edges as wireframe
/// segments, each shared edge once.
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

    public string LayerId { get; } = layerId;

    public TileKey Tile { get; } = tile;

    public WorldPoint Origin { get; } = origin;

    public int VertexCount => _positions.Count / 3;

    public int TriangleCount => _triangles.Count / 3;

    public int LineCount => _lines.Count / 2;

    /// <summary>Approximate wire size so far, for splitting large tiles.</summary>
    public long EstimatedBytes => (_positions.Count * 4L) + (_triangles.Count * 4L) + (_triangleAttributes.Count * 4L)
        + (_lines.Count * 4L) + (_lineAttributes.Count * 4L) + (_points.Count * 8L);

    public JsonObject Stats { get; } = [];

    public uint AddVertex(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            throw new ArgumentException($"{LayerId} {Tile}: vertex ({x}, {y}, {z}) is not finite.");
        }

        uint index = (uint)VertexCount;
        _positions.Add((float)(x - Origin.X));
        _positions.Add((float)(y - Origin.Y));
        _positions.Add((float)(z - Origin.Z));
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

    public GeometryChunk Build()
    {
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
            Attributes = new Dictionary<uint, JsonObject>(_attributes),
            Stats = Stats,
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
/// Every array starts on a 4-byte boundary so the browser can view it in place.
/// </summary>
public static class GeometryWire
{
    public const uint Magic = 0x31475441;
    public const ushort Version = 1;
    public const int HeaderBytes = 48;

    /// <summary>Upper bound for one chunk on the wire; sources split larger tiles into parts.</summary>
    public const long MaxChunkBytes = 64L * 1024 * 1024;

    public static byte[] Write(GeometryChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        chunk.Validate();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(Metadata(chunk));
        long size = HeaderBytes
            + (chunk.Positions.Length * 4L)
            + (chunk.Triangles.Length * 4L) + (chunk.TriangleAttributes.Length * 4L)
            + (chunk.Lines.Length * 4L) + (chunk.LineAttributes.Length * 4L)
            + (chunk.Points.Length * 4L) + (chunk.PointAttributes.Length * 4L)
            + 4 + json.Length;
        if (size > MaxChunkBytes)
        {
            throw new InvalidOperationException($"{chunk.LayerId} {chunk.Tile}: chunk is {size} bytes, over {MaxChunkBytes}.");
        }

        byte[] buffer = new byte[size];
        Span<byte> span = buffer;
        ushort flags = (ushort)((chunk.TriangleCount > 0 ? 1 : 0) | (chunk.LineCount > 0 ? 2 : 0) | (chunk.PointCount > 0 ? 4 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], flags);
        BinaryPrimitives.WriteDoubleLittleEndian(span[8..], chunk.Origin.X);
        BinaryPrimitives.WriteDoubleLittleEndian(span[16..], chunk.Origin.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(span[24..], chunk.Origin.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], (uint)chunk.VertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], (uint)chunk.TriangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)chunk.LineCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], (uint)chunk.PointCount);
        int offset = HeaderBytes;
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<float>(chunk.Positions));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.Triangles));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.TriangleAttributes));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.Lines));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.LineAttributes));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.Points));
        offset = WriteArray(span, offset, MemoryMarshal.AsBytes<uint>(chunk.PointAttributes));
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

        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != Version)
        {
            throw new InvalidDataException("Not an ATG1 version 1 chunk.");
        }

        var origin = new WorldPoint(
            BinaryPrimitives.ReadDoubleLittleEndian(data[8..]),
            BinaryPrimitives.ReadDoubleLittleEndian(data[16..]),
            BinaryPrimitives.ReadDoubleLittleEndian(data[24..]));
        uint verticesU = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        uint trianglesU = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        uint linesU = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);
        uint pointsU = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        uint maxWords = (uint)(data.Length - HeaderBytes) / 4u;
        if (verticesU > maxWords / 3u || trianglesU > maxWords / 4u || linesU > maxWords / 3u || pointsU > maxWords / 2u)
        {
            throw new InvalidDataException("ATG1 chunk is truncated.");
        }

        int vertices = (int)verticesU;
        int triangles = (int)trianglesU;
        int lines = (int)linesU;
        int points = (int)pointsU;
        int offset = HeaderBytes;
        float[] positions = ReadFloats(data, ref offset, vertices * 3);
        uint[] tri = ReadUInts(data, ref offset, triangles * 3);
        uint[] triAttr = ReadUInts(data, ref offset, triangles);
        uint[] lin = ReadUInts(data, ref offset, lines * 2);
        uint[] linAttr = ReadUInts(data, ref offset, lines);
        uint[] pts = ReadUInts(data, ref offset, points);
        uint[] ptsAttr = ReadUInts(data, ref offset, points);
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

        return new JsonObject
        {
            ["layer"] = chunk.LayerId,
            ["tile"] = new JsonArray(chunk.Tile.MapId, chunk.Tile.X, chunk.Tile.Y),
            ["part"] = chunk.Part,
            ["attributes"] = attributes,
            ["stats"] = chunk.Stats.DeepClone(),
        };
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

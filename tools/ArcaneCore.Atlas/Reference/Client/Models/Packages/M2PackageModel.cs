using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>One texture entry of an M2: type 0 names a file, any other type is replaceable (resolved per display).</summary>
internal readonly record struct M2TextureEntry(uint Type, uint Flags, string Name);

/// <summary>
/// One submesh: a range of its view's triangle-index array. <see cref="StartIndex"/> is the 16-bit index start at +0x08 plus the
/// submesh's level (+0x02) shifted left 16 bits, which is how a view with more than 65535 indices addresses its tail.
/// </summary>
internal readonly record struct M2SubmeshEntry(ushort Id, int StartIndex, ushort IndexCount);

/// <summary>
/// One texture unit (a batch): the submesh it draws, its priority plane (the signed byte at +0x01; +0x02 is the shader id, which this
/// reader does not use), render-flags row and texture-lookup row.
/// </summary>
internal readonly record struct M2UnitEntry(ushort Submesh, sbyte Priority, ushort RenderFlags, ushort TextureLookup);

/// <summary>One embedded view: vertex lookup, triangle indices (into the lookup), submeshes and texture units.</summary>
internal sealed record M2ViewData(ushort[] Lookup, ushort[] Triangles, M2SubmeshEntry[] Submeshes, M2UnitEntry[] Units);

/// <summary>
/// The tables of a 1.12.1 M2 that a model package needs: render vertices with normals and the first uv set, textures, render flags,
/// the texture lookup and every embedded view. Offsets are the vanilla header positions (vertices 0x44, views 0x4C, textures 0x5C,
/// render flags 0x84, texture lookup 0x94); per-record layouts: vertex 48 bytes (normal +20, uv0 +32), view 44 bytes (lookup
/// +0, triangles +8, submeshes +24, texture units +32), submesh 32 bytes, texture unit 24 bytes, texture 16 bytes, render flags
/// 4 bytes. Immutable after <see cref="Parse"/>; safe to share between threads. The real-data tests check the offsets against the
/// client's own files.
/// </summary>
internal sealed class M2PackageModel
{
    private const int OffsetName = 0x08;
    private const int OffsetVertices = 0x44;
    private const int OffsetViews = 0x4C;
    private const int OffsetTextures = 0x5C;
    private const int OffsetRenderFlags = 0x84;
    private const int OffsetTextureLookup = 0x94;
    private const int MinimumHeaderBytes = 0x9C;
    private const int VertexSize = 48;
    private const int ViewSize = 44;
    private const int SubmeshSize = 32;
    private const int UnitSize = 24;
    private const int TextureSize = 16;
    private const int MaxNameBytes = 260;

    private M2PackageModel()
    {
    }

    public uint Version { get; private init; }

    public string Name { get; private init; } = string.Empty;

    public Vector3[] Positions { get; private init; } = [];

    public Vector3[] Normals { get; private init; } = [];

    public Vector2[] Uv0 { get; private init; } = [];

    public M2TextureEntry[] Textures { get; private init; } = [];

    public (ushort Flags, ushort Blend)[] RenderFlags { get; private init; } = [];

    public ushort[] TextureLookup { get; private init; } = [];

    public M2ViewData[] Views { get; private init; } = [];

    /// <exception cref="InvalidDataException">The file is truncated, not an MD20 of version 256..260, or a table points outside it.</exception>
    public static M2PackageModel Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumHeaderBytes || data[0] != (byte)'M' || data[1] != (byte)'D' || data[2] != (byte)'2' || data[3] != (byte)'0')
        {
            throw new InvalidDataException("M2: not an MD20 model (missing magic or header truncated).");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (version is < M2ModelFile.MinSupportedVersion or > M2ModelFile.MaxSupportedVersion)
        {
            throw new InvalidDataException($"M2: version {version} is not the 1.12.1 version {M2ModelFile.SupportedVersion}.");
        }

        (uint vertexCount, int vertexAt) = Table(data, OffsetVertices, VertexSize, "vertices");
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uv0 = new Vector2[vertexCount];
        for (int i = 0; i < positions.Length; i++)
        {
            ReadOnlySpan<byte> v = data[(vertexAt + (i * VertexSize))..];
            positions[i] = new Vector3(Single(v, 0), Single(v, 4), Single(v, 8));
            normals[i] = new Vector3(Single(v, 20), Single(v, 24), Single(v, 28));
            uv0[i] = new Vector2(Single(v, 32), Single(v, 36));
        }

        (uint viewCount, int viewAt) = Table(data, OffsetViews, ViewSize, "views");
        var views = new M2ViewData[viewCount];
        for (int i = 0; i < views.Length; i++)
        {
            views[i] = ReadView(data, viewAt + (i * ViewSize), i);
        }

        (uint textureCount, int textureAt) = Table(data, OffsetTextures, TextureSize, "textures");
        var textures = new M2TextureEntry[textureCount];
        for (int i = 0; i < textures.Length; i++)
        {
            ReadOnlySpan<byte> t = data[(textureAt + (i * TextureSize))..];
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(t);
            string name = string.Empty;
            if (type == 0)
            {
                name = ReadString(data, BinaryPrimitives.ReadUInt32LittleEndian(t[12..]), BinaryPrimitives.ReadUInt32LittleEndian(t[8..]), $"texture {i} name");
            }

            textures[i] = new M2TextureEntry(type, BinaryPrimitives.ReadUInt32LittleEndian(t[4..]), name);
        }

        (uint flagCount, int flagAt) = Table(data, OffsetRenderFlags, 4, "render flags");
        var renderFlags = new (ushort, ushort)[flagCount];
        for (int i = 0; i < renderFlags.Length; i++)
        {
            renderFlags[i] = (
                BinaryPrimitives.ReadUInt16LittleEndian(data[(flagAt + (i * 4))..]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[(flagAt + (i * 4) + 2)..]));
        }

        (uint lookupCount, int lookupAt) = Table(data, OffsetTextureLookup, 2, "texture lookup");
        ushort[] lookup = ReadU16(data, lookupAt, lookupCount);

        return new M2PackageModel
        {
            Version = version,
            Name = ReadOptionalName(data),
            Positions = positions,
            Normals = normals,
            Uv0 = uv0,
            Textures = textures,
            RenderFlags = renderFlags,
            TextureLookup = lookup,
            Views = views,
        };
    }

    private static M2ViewData ReadView(ReadOnlySpan<byte> data, int at, int index)
    {
        ReadOnlySpan<byte> v = data[at..];
        (uint lookupCount, int lookupAt) = Range(data, v, 0, 2, $"view {index} vertex lookup");
        (uint triangleCount, int triangleAt) = Range(data, v, 8, 2, $"view {index} triangles");
        (uint submeshCount, int submeshAt) = Range(data, v, 24, SubmeshSize, $"view {index} submeshes");
        (uint unitCount, int unitAt) = Range(data, v, 32, UnitSize, $"view {index} texture units");
        var submeshes = new M2SubmeshEntry[submeshCount];
        for (int i = 0; i < submeshes.Length; i++)
        {
            ReadOnlySpan<byte> s = data[(submeshAt + (i * SubmeshSize))..];
            submeshes[i] = new M2SubmeshEntry(
                BinaryPrimitives.ReadUInt16LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s[8..]) | (BinaryPrimitives.ReadUInt16LittleEndian(s[2..]) << 16),
                BinaryPrimitives.ReadUInt16LittleEndian(s[10..]));
        }

        var units = new M2UnitEntry[unitCount];
        for (int i = 0; i < units.Length; i++)
        {
            ReadOnlySpan<byte> u = data[(unitAt + (i * UnitSize))..];
            units[i] = new M2UnitEntry(
                BinaryPrimitives.ReadUInt16LittleEndian(u[4..]),
                (sbyte)u[1],
                BinaryPrimitives.ReadUInt16LittleEndian(u[10..]),
                BinaryPrimitives.ReadUInt16LittleEndian(u[16..]));
        }

        return new M2ViewData(ReadU16(data, lookupAt, lookupCount), ReadU16(data, triangleAt, triangleCount), submeshes, units);
    }

    private static (uint Count, int At) Table(ReadOnlySpan<byte> data, int headerOffset, int recordSize, string what)
        => Range(data, data, headerOffset, recordSize, what);

    /// <summary>Reads the (count, offset) pair at <paramref name="pairOffset"/> of <paramref name="holder"/> and checks the table lies inside the file.</summary>
    private static (uint Count, int At) Range(ReadOnlySpan<byte> file, ReadOnlySpan<byte> holder, int pairOffset, int recordSize, string what)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(holder[pairOffset..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(holder[(pairOffset + 4)..]);
        if (count == 0)
        {
            return (0, 0);
        }

        if (offset > file.Length || (long)count * recordSize > file.Length - offset)
        {
            throw new InvalidDataException($"M2: the {what} table ({count} x {recordSize} bytes at {offset}) lies outside the {file.Length}-byte file.");
        }

        return (count, (int)offset);
    }

    private static ushort[] ReadU16(ReadOnlySpan<byte> data, int at, uint count)
    {
        var values = new ushort[count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + (2 * i))..]);
        }

        return values;
    }

    private static float Single(ReadOnlySpan<byte> bytes, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
        return float.IsFinite(value) ? value : throw new InvalidDataException("M2: a vertex value is not finite.");
    }

    private static string ReadString(ReadOnlySpan<byte> data, uint offset, uint length, string what)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        if (offset > data.Length || length > MaxNameBytes || length > data.Length - offset)
        {
            throw new InvalidDataException($"M2: the {what} lies outside the file or is longer than {MaxNameBytes} bytes.");
        }

        ReadOnlySpan<byte> raw = data.Slice((int)offset, (int)length);
        int end = raw.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end >= 0 ? raw[..end] : raw);
    }

    private static string ReadOptionalName(ReadOnlySpan<byte> data)
    {
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetName..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data[(OffsetName + 4)..]);
        try
        {
            return ReadString(data, offset, length, "model name");
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
    }
}

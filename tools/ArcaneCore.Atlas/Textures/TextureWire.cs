using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Textures;

/// <summary>Pixel format of an ATX1 texture.</summary>
public enum AtxFormat : ushort
{
    Rgba8 = 1,
    Dxt1 = 2,
    Dxt3 = 3,
    Dxt5 = 4,
    R8 = 5,
}

/// <summary>ATX1 flag bits.</summary>
[Flags]
public enum AtxFlags : byte
{
    None = 0,
    HasAlpha = 1,
    Alpha1Bit = 2,
    Generated = 4,
    SrgbHint = 8,
}

/// <summary>
/// A texture ready for the wire: a pixel format, a size, <see cref="Layers"/> array layers (more than one means a
/// <c>TEXTURE_2D_ARRAY</c>) and the mip chain. Each entry of <see cref="Levels"/> holds all layers of that level, layer-major, in
/// the size <see cref="TextureWire.LevelBytes"/> gives. <see cref="Meta"/> is the JSON object that travels with it.
/// </summary>
public sealed record AtxTexture(
    AtxFormat Format,
    int Width,
    int Height,
    int Layers,
    AtxFlags Flags,
    IReadOnlyList<byte[]> Levels,
    JsonObject Meta);

/// <summary>
/// The "ATX1" texture wire (design section 2.2), little-endian:
/// <code>
/// u32 magic 0x31585441 ("ATX1") | u16 version 1 | u16 format (1 RGBA8, 2 DXT1, 3 DXT3, 4 DXT5, 5 R8)
/// u16 width | u16 height | u16 layers | u8 levels | u8 flags (bit0 hasAlpha, bit1 alpha1bit, bit2 generated, bit3 srgbHint)
/// u32 jsonBytes                                                                   (header = 20 bytes)
/// per level: u32 byteLength (unpadded) + payload, padded to 4 bytes
/// then the JSON object (jsonBytes bytes, UTF-8)
/// </code>
/// </summary>
public static class TextureWire
{
    public const uint Magic = 0x31585441;
    public const ushort Version = 1;
    public const int HeaderBytes = 20;

    /// <summary>Largest ATX1 payload accepted or written.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    public const int MaxLevels = 16;
    private const int MaxLayers = 4096;

    /// <summary>
    /// Metadata is JSON read only by <c>JSON.parse</c> and <see cref="JsonNode"/>, never put into HTML, so non-ASCII text is written as
    /// UTF-8 like <c>JSON.stringify</c> does (the browser's test writer is held byte-identical to this one).
    /// </summary>
    private static readonly JsonSerializerOptions MetaOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Bytes of one level of the given texture size, all layers included.</summary>
    public static long LevelBytes(AtxFormat format, int width, int height, int layers, int level)
    {
        long w = Math.Max(1, width >> level);
        long h = Math.Max(1, height >> level);
        long perLayer = format switch
        {
            AtxFormat.Rgba8 => w * h * 4,
            AtxFormat.R8 => w * h,
            AtxFormat.Dxt1 => ((w + 3) / 4) * ((h + 3) / 4) * 8,
            AtxFormat.Dxt3 or AtxFormat.Dxt5 => ((w + 3) / 4) * ((h + 3) / 4) * 16,
            _ => throw new InvalidDataException($"Unknown ATX1 format {(int)format}."),
        };
        return perLayer * layers;
    }

    public static byte[] Write(AtxTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Validate(texture);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(texture.Meta, MetaOptions);
        long size = HeaderBytes + json.Length;
        foreach (byte[] level in texture.Levels)
        {
            size += 4 + Padded(level.Length);
        }

        if (size > MaxBytes)
        {
            throw new InvalidDataException($"ATX1 texture is {size} bytes, over the {MaxBytes} limit.");
        }

        byte[] buffer = new byte[size];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)texture.Format);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)texture.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)texture.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)texture.Layers);
        span[14] = (byte)texture.Levels.Count;
        span[15] = (byte)texture.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)json.Length);
        int offset = HeaderBytes;
        foreach (byte[] level in texture.Levels)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)level.Length);
            level.CopyTo(span[(offset + 4)..]);
            offset += 4 + Padded(level.Length);
        }

        json.CopyTo(span[offset..]);
        return buffer;
    }

    public static AtxTexture Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes)
        {
            throw new InvalidDataException("ATX1 texture is shorter than its header.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException("Not an ATX1 texture (bad magic).");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != Version)
        {
            throw new InvalidDataException("Unsupported ATX1 version.");
        }

        var format = (AtxFormat)BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (format is < AtxFormat.Rgba8 or > AtxFormat.R8)
        {
            throw new InvalidDataException("Unknown ATX1 format.");
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        int layers = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        int levelCount = data[14];
        byte flagBits = data[15];
        uint jsonBytes = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (width < 1 || height < 1 || layers < 1 || layers > MaxLayers || levelCount < 1 || levelCount > MaxLevels || (flagBits & ~0xF) != 0)
        {
            throw new InvalidDataException("ATX1 header has an invalid size, layer count, level count or flags.");
        }

        var levels = new List<byte[]>(levelCount);
        long offset = HeaderBytes;
        for (int i = 0; i < levelCount; i++)
        {
            if (offset + 4 > data.Length)
            {
                throw new InvalidDataException("ATX1 texture is truncated in a level header.");
            }

            long length = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)offset..]);
            if (length != LevelBytes(format, width, height, layers, i))
            {
                throw new InvalidDataException($"ATX1 level {i} is {length} bytes, not the {LevelBytes(format, width, height, layers, i)} its format implies.");
            }

            if (offset + 4 + Padded(length) > data.Length)
            {
                throw new InvalidDataException("ATX1 texture is truncated in a level.");
            }

            levels.Add(data.Slice((int)offset + 4, (int)length).ToArray());
            offset += 4 + Padded(length);
        }

        if (jsonBytes != data.Length - offset)
        {
            throw new InvalidDataException("ATX1 metadata length does not match the texture size.");
        }

        JsonObject meta;
        try
        {
            meta = JsonNode.Parse(data[(int)offset..]) as JsonObject ?? throw new InvalidDataException("ATX1 metadata is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("ATX1 metadata is malformed.", ex);
        }

        return new AtxTexture(format, width, height, layers, (AtxFlags)flagBits, levels, meta);
    }

    private static void Validate(AtxTexture texture)
    {
        if (!Enum.IsDefined(texture.Format))
        {
            throw new InvalidDataException("Unknown ATX1 format.");
        }

        if (texture.Width is < 1 or > ushort.MaxValue || texture.Height is < 1 or > ushort.MaxValue || texture.Layers is < 1 or > MaxLayers)
        {
            throw new InvalidDataException($"ATX1 size {texture.Width}x{texture.Height}x{texture.Layers} is out of range.");
        }

        if (texture.Levels.Count is < 1 or > MaxLevels)
        {
            throw new InvalidDataException($"ATX1 needs 1 to {MaxLevels} levels, not {texture.Levels.Count}.");
        }

        if (((byte)texture.Flags & ~0xF) != 0)
        {
            throw new InvalidDataException("ATX1 flags use undefined bits.");
        }

        for (int i = 0; i < texture.Levels.Count; i++)
        {
            long expected = LevelBytes(texture.Format, texture.Width, texture.Height, texture.Layers, i);
            if (texture.Levels[i].Length != expected)
            {
                throw new InvalidDataException($"ATX1 level {i} is {texture.Levels[i].Length} bytes but a {texture.Format} level of {texture.Width}x{texture.Height}x{texture.Layers} is {expected}.");
            }
        }
    }

    private static int Padded(int length) => (length + 3) & ~3;

    private static long Padded(long length) => (length + 3) & ~3L;
}

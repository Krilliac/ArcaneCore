using System.Buffers.Binary;

namespace ArcaneCore.Atlas.Tests.Textures;

/// <summary>
/// Builds BLP2 files from explicit bytes, so a decoder test states the expected pixels from the format rules and never needs a
/// client file. Layout (design section 2.1, wowdev.wiki "BLP" BLP2): <c>char[4] "BLP2"</c>, <c>u32 type</c> (1 = direct),
/// <c>u8 compression</c> (1 palettized, 2 DXT, 3 raw BGRA), <c>u8 alphaDepth</c>, <c>u8 alphaType</c>, <c>u8 hasMips</c>, <c>u32 width</c>,
/// <c>u32 height</c>, <c>u32 mipOffsets[16]</c>, <c>u32 mipSizes[16]</c> (148 bytes), then for compression 1 a <c>u32 palette[256]</c>
/// (B, G, R, A bytes), then the levels.
/// </summary>
internal static class BlpTestWriter
{
    public const int HeaderBytes = 148;
    public const int PaletteBytes = 1024;

    /// <summary>Decoded straight-alpha pixel, the form every expectation is written in.</summary>
    public readonly record struct Px(byte R, byte G, byte B, byte A = 255);

    /// <summary>One mip level of a palettized file: one palette index per pixel and, depending on alphaDepth, the alpha plane.</summary>
    public sealed record PaletteLevel(byte[] Indices, byte[]? AlphaPlane);

    /// <summary>
    /// A palettized BLP2. <paramref name="alphaPlane"/> is the already packed alpha plane of level 0 (1-bit: 8 px per byte LSB first,
    /// 4-bit: 2 px per byte low nibble first, 8-bit: one byte per pixel) and is null for alphaDepth 0. The palette's own alpha byte
    /// is written as <paramref name="paletteAlphaByte"/>: the format says alpha comes from the plane, so decoders must ignore it.
    /// </summary>
    public static byte[] Palettized(
        int width,
        int height,
        Px[] palette,
        byte[] indices,
        byte alphaDepth,
        byte[]? alphaPlane,
        byte alphaType = 8,
        byte hasMips = 0,
        byte paletteAlphaByte = 0,
        IReadOnlyList<PaletteLevel>? moreLevels = null)
    {
        var levels = new List<byte[]> { Concat(indices, alphaPlane ?? []) };
        foreach (PaletteLevel level in moreLevels ?? [])
        {
            levels.Add(Concat(level.Indices, level.AlphaPlane ?? []));
        }

        byte[] paletteBytes = new byte[PaletteBytes];
        for (int i = 0; i < palette.Length; i++)
        {
            paletteBytes[(4 * i) + 0] = palette[i].B;
            paletteBytes[(4 * i) + 1] = palette[i].G;
            paletteBytes[(4 * i) + 2] = palette[i].R;
            paletteBytes[(4 * i) + 3] = paletteAlphaByte;
        }

        return Assemble(width, height, compression: 1, alphaDepth, alphaType, hasMips, paletteBytes, levels);
    }

    /// <summary>A DXT BLP2 whose levels are the given block bytes, level 0 first. alphaType 0 = DXT1, 1 = DXT3, 7 = DXT5.</summary>
    public static byte[] Dxt(int width, int height, byte alphaType, byte alphaDepth, IReadOnlyList<byte[]> levelBlocks, byte hasMips = 0)
        => Assemble(width, height, compression: 2, alphaDepth, alphaType, hasMips, palette: null, levelBlocks);

    /// <summary>A raw BLP2 (compression 3): each level is width x height pixels of B, G, R, A bytes.</summary>
    public static byte[] Raw(int width, int height, byte[] bgra, byte alphaDepth = 8, byte hasMips = 0, IReadOnlyList<byte[]>? moreLevels = null)
    {
        var levels = new List<byte[]> { bgra };
        levels.AddRange(moreLevels ?? []);
        return Assemble(width, height, compression: 3, alphaDepth, alphaType: 0, hasMips, palette: null, levels);
    }

    /// <summary>The 4x4 block count in bytes for a DXT level of the given size: 8 bytes per block for DXT1, 16 for DXT3/5.</summary>
    public static int DxtLevelBytes(int width, int height, int bytesPerBlock)
        => ((Math.Max(1, width) + 3) / 4) * ((Math.Max(1, height) + 3) / 4) * bytesPerBlock;

    /// <summary>Overwrites the header's mip offset and size for level <paramref name="level"/> (to make a level point outside the file, say).</summary>
    public static byte[] WithMipEntry(byte[] file, int level, uint offset, uint size)
    {
        byte[] copy = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(20 + (4 * level)), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(84 + (4 * level)), size);
        return copy;
    }

    /// <summary>A header-only copy field patch: sets the u32 type (0 = JPEG) at offset 4.</summary>
    public static byte[] WithType(byte[] file, uint type)
    {
        byte[] copy = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(4), type);
        return copy;
    }

    private static byte[] Assemble(int width, int height, byte compression, byte alphaDepth, byte alphaType, byte hasMips, byte[]? palette, IReadOnlyList<byte[]> levels)
    {
        int dataStart = HeaderBytes + (palette?.Length ?? 0);
        int total = dataStart + levels.Sum(l => l.Length);
        byte[] file = new byte[total];
        file[0] = (byte)'B';
        file[1] = (byte)'L';
        file[2] = (byte)'P';
        file[3] = (byte)'2';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 1);
        file[8] = compression;
        file[9] = alphaDepth;
        file[10] = alphaType;
        file[11] = hasMips;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)height);
        palette?.CopyTo(file, HeaderBytes);
        int at = dataStart;
        for (int i = 0; i < levels.Count && i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20 + (4 * i)), (uint)at);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(84 + (4 * i)), (uint)levels[i].Length);
            levels[i].CopyTo(file, at);
            at += levels[i].Length;
        }

        return file;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}

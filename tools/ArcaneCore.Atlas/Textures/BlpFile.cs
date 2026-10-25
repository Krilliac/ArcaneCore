using System.Buffers.Binary;

namespace ArcaneCore.Atlas.Textures;

/// <summary>
/// The header and level table of a BLP2 file (design section 2.1; wowdev.wiki BLP2), validated without decoding any pixels. Layout
/// (little-endian): <c>char[4] "BLP2"</c>, <c>u32 type</c> (0 JPEG, 1 direct), <c>u8 compression</c> (1 palettized, 2 DXT, 3 raw BGRA),
/// <c>u8 alphaDepth</c> (0, 1, 4, 8), <c>u8 alphaType</c> (DXT: 0 DXT1, 1 DXT3, 7 DXT5), <c>u8 hasMips</c>, <c>u32 width</c>,
/// <c>u32 height</c>, <c>u32 mipOffsets[16]</c>, <c>u32 mipSizes[16]</c> (148 bytes), then <c>u32 palette[256]</c> (B, G, R, A bytes) when
/// the file is palettized. Everything is bounds-checked in <see cref="Parse"/>; a bad file is an <see cref="InvalidDataException"/>
/// with the reason, never another exception type, and no image-sized allocation happens before the data proves it exists.
/// <para>
/// A level is valid only when it lies inside the file, starts after the header and has exactly the size its encoding implies for
/// <c>max(1, w &gt;&gt; i) x max(1, h &gt;&gt; i)</c>. <see cref="ValidLevelCount"/> is the length of the valid prefix of the chain
/// (1 when <see cref="HasMips"/> is 0); level 0 must be valid. Instances are immutable and may be shared between threads.
/// </para>
/// </summary>
public sealed class BlpFile
{
    public const int HeaderBytes = 148;
    public const int PaletteBytes = 1024;
    public const int MaxDimension = 4096;
    public const int MaxListedLevels = 16;

    /// <summary>Largest decoded size (all levels of the chain, RGBA8) a file may have.</summary>
    public const long MaxDecodedBytes = 64L * 1024 * 1024;

    private readonly byte[] _file;
    private readonly uint[] _offsets = new uint[MaxListedLevels];
    private readonly uint[] _sizes = new uint[MaxListedLevels];

    private BlpFile(byte[] file)
    {
        _file = file;
    }

    /// <summary>1 palettized, 2 DXT, 3 raw BGRA.</summary>
    public int Compression { get; private set; }

    /// <summary>0, 1, 4 or 8 for palettized files; informational for the others.</summary>
    public int AlphaDepth { get; private set; }

    /// <summary>For DXT files 0 (DXT1), 1 (DXT3) or 7 (DXT5); ignored for palettized files.</summary>
    public int AlphaType { get; private set; }

    /// <summary>0, 1 or 2 in the real client; any non-zero value means the mip levels are listed.</summary>
    public int HasMips { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Levels in a full chain down to 1x1.</summary>
    public int FullLevelCount { get; private set; }

    /// <summary>Levels at the start of the chain that the file really holds.</summary>
    public int ValidLevelCount { get; private set; }

    public bool IsDxt => Compression == 2;

    /// <summary>True for DXT1 (alphaType 0).</summary>
    public bool IsDxt1 => IsDxt && AlphaType == 0;

    /// <summary>The block size of a DXT file: 8 for DXT1, 16 for DXT3 and DXT5.</summary>
    public int DxtBlockBytes => IsDxt1 ? DxtCodec.Dxt1BlockBytes : DxtCodec.Dxt35BlockBytes;

    /// <summary>True when the image has per-pixel transparency (a DXT1 file only when its alphaDepth is 1).</summary>
    public bool HasAlpha => AlphaDepth != 0 && (!IsDxt1 || AlphaDepth == 1);

    /// <summary>True when the alpha is a single bit (cut-out textures).</summary>
    public bool Alpha1Bit => AlphaDepth == 1;

    public static BlpFile Parse(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < HeaderBytes)
        {
            throw new InvalidDataException($"BLP is {file.Length} bytes, shorter than its {HeaderBytes}-byte header.");
        }

        if (file[0] != (byte)'B' || file[1] != (byte)'L' || file[2] != (byte)'P' || file[3] != (byte)'2')
        {
            throw new InvalidDataException("Not a BLP2 file (bad magic).");
        }

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        if (type == 0)
        {
            throw new InvalidDataException("jpeg BLP not supported.");
        }

        if (type != 1)
        {
            throw new InvalidDataException($"Unknown BLP2 type {type}.");
        }

        uint width = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
        {
            throw new InvalidDataException($"BLP size {width}x{height} is outside 1..{MaxDimension}.");
        }

        var blp = new BlpFile(file)
        {
            Compression = file[8],
            AlphaDepth = file[9],
            AlphaType = file[10],
            HasMips = file[11],
            Width = (int)width,
            Height = (int)height,
        };
        blp.ValidateEncoding();
        for (int i = 0; i < MaxListedLevels; i++)
        {
            blp._offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20 + (4 * i)));
            blp._sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(84 + (4 * i)));
        }

        blp.FullLevelCount = 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(blp.Width, blp.Height));
        long decoded = 0;
        for (int i = 0; i < blp.FullLevelCount; i++)
        {
            (int w, int h) = blp.LevelSize(i);
            decoded += (long)w * h * 4;
        }

        if (decoded > MaxDecodedBytes)
        {
            throw new InvalidDataException($"BLP {blp.Width}x{blp.Height} would decode to {decoded} bytes, over the {MaxDecodedBytes} limit.");
        }

        if (!blp.IsLevelValid(0))
        {
            throw new InvalidDataException("BLP level 0 is outside the file or has the wrong size for its encoding.");
        }

        int valid = 1;
        if (blp.HasMips != 0)
        {
            int limit = Math.Min(blp.FullLevelCount, MaxListedLevels);
            while (valid < limit && blp.IsLevelValid(valid))
            {
                valid++;
            }
        }

        blp.ValidLevelCount = valid;
        return blp;
    }

    /// <summary>Width and height of level <paramref name="level"/> of the chain.</summary>
    public (int Width, int Height) LevelSize(int level)
        => (Math.Max(1, Width >> level), Math.Max(1, Height >> level));

    /// <summary>The bytes level <paramref name="level"/> must have for this encoding.</summary>
    public long ExpectedLevelBytes(int level)
    {
        (int w, int h) = LevelSize(level);
        long pixels = (long)w * h;
        return Compression switch
        {
            1 => pixels + AlphaPlaneBytes(pixels),
            2 => DxtCodec.LevelBytes(w, h, DxtBlockBytes),
            _ => pixels * 4,
        };
    }

    /// <summary>A copy of the file's own bytes for a valid level (the DXT blocks, the palette indices plus alpha plane, or the BGRA pixels).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not below <see cref="ValidLevelCount"/>.</exception>
    public byte[] LevelData(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, ValidLevelCount);
        return _file.AsSpan((int)_offsets[level], (int)_sizes[level]).ToArray();
    }

    /// <summary>A view of a valid level's bytes, without copying.</summary>
    internal ReadOnlySpan<byte> LevelSpan(int level)
        => _file.AsSpan((int)_offsets[level], (int)_sizes[level]);

    /// <summary>The palette entry <paramref name="index"/> as (R, G, B); the palette's own alpha byte is never used.</summary>
    internal (byte R, byte G, byte B) PaletteColour(int index)
    {
        int at = HeaderBytes + (4 * index);
        return (_file[at + 2], _file[at + 1], _file[at]);
    }

    /// <summary>Bytes of the alpha plane that follows the palette indices of a palettized level with <paramref name="pixels"/> pixels.</summary>
    internal long AlphaPlaneBytes(long pixels) => AlphaDepth switch
    {
        1 => (pixels + 7) / 8,
        4 => (pixels + 1) / 2,
        8 => pixels,
        _ => 0,
    };

    private void ValidateEncoding()
    {
        switch (Compression)
        {
            case 1:
                if (_file.Length < HeaderBytes + PaletteBytes)
                {
                    throw new InvalidDataException("BLP palette is cut off.");
                }

                if (AlphaDepth is not (0 or 1 or 4 or 8))
                {
                    throw new InvalidDataException($"Palettized BLP alpha depth {AlphaDepth} is not 0, 1, 4 or 8.");
                }

                break;
            case 2:
                if (AlphaType is not (0 or 1 or 7))
                {
                    throw new InvalidDataException($"DXT BLP alpha type {AlphaType} is not 0 (DXT1), 1 (DXT3) or 7 (DXT5).");
                }

                break;
            case 3:
                break;
            default:
                throw new InvalidDataException($"Unknown BLP compression {Compression}.");
        }
    }

    private bool IsLevelValid(int level)
    {
        long offset = _offsets[level];
        long size = _sizes[level];
        long dataStart = Compression == 1 ? HeaderBytes + PaletteBytes : HeaderBytes;
        return size == ExpectedLevelBytes(level) && offset >= dataStart && offset + size <= _file.Length;
    }
}

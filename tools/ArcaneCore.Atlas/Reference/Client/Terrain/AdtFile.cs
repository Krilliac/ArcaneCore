using System.Numerics;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>Which liquid an MCNK liquid layer holds (the MCNK flag that selects the layer).</summary>
public enum AdtLiquidKind
{
    /// <summary>Flag 0x04: river or lake water.</summary>
    River,

    /// <summary>Flag 0x08: ocean.</summary>
    Ocean,

    /// <summary>Flag 0x10: magma.</summary>
    Magma,

    /// <summary>Flag 0x20: slime (no 1.12.1 ADT sets it, but the flag is part of the format).</summary>
    Slime,
}

/// <summary>
/// One MCLQ liquid layer of one MCNK: a 9x9 grid of surface heights plus an 8x8 grid of per-square flags. Layers are
/// kept exactly as stored and never merged; the extractor's merge is reproduced only by the oracle tests.
/// </summary>
public sealed class AdtLiquidLayer
{
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:60-66 (one layer per set MCNK liquid flag, in this bit order)
    // ref: Worldforge src/terrain.cpp:487-497 (river, ocean, magma, slime order; 804 bytes per layer)

    /// <summary>MCNK flag selecting a river layer.</summary>
    public const uint McnkRiver = 0x04;

    /// <summary>MCNK flag selecting an ocean layer.</summary>
    public const uint McnkOcean = 0x08;

    /// <summary>MCNK flag selecting a magma layer.</summary>
    public const uint McnkMagma = 0x10;

    /// <summary>MCNK flag selecting a slime layer.</summary>
    public const uint McnkSlime = 0x20;

    /// <summary>All four liquid flags.</summary>
    public const uint McnkMask = McnkRiver | McnkOcean | McnkMagma | McnkSlime;

    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:69 (0x0F: sub-cell not shown)
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:70 (0x80: dark water)

    /// <summary>Square flag byte of a square the client does not draw.</summary>
    public const byte HiddenSquare = 0x0F;

    /// <summary>Square flag bit marking dark (deep) water.</summary>
    public const byte DarkWaterSquare = 0x80;

    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:72-74 and :99 (804 bytes per layer, static_assert)
    // ref: Worldforge src/terrain.cpp:478-486 (8 + 81*8 + 64 + 4 + 2*40 = 804)
    internal const int LayerBytes = 804;

    private readonly float[] _heights;
    private readonly byte[] _flags;

    internal AdtLiquidLayer(AdtLiquidKind kind, float minHeight, float maxHeight, float[] heights, byte[] flags)
    {
        Kind = kind;
        MinHeight = minHeight;
        MaxHeight = maxHeight;
        _heights = heights;
        _flags = flags;
    }

    /// <summary>The liquid, from the MCNK flag that selects this layer.</summary>
    public AdtLiquidKind Kind { get; }

    /// <summary>The layer's stored lowest surface height.</summary>
    public float MinHeight { get; }

    /// <summary>The layer's stored highest surface height.</summary>
    public float MaxHeight { get; }

    /// <summary>The MCNK flag that selects a layer of this kind.</summary>
    public static uint FlagOf(AdtLiquidKind kind) => kind switch
    {
        AdtLiquidKind.River => McnkRiver,
        AdtLiquidKind.Ocean => McnkOcean,
        AdtLiquidKind.Magma => McnkMagma,
        _ => McnkSlime,
    };

    /// <summary>The shared colour key of the liquid (the same keys the server liquid layer uses).</summary>
    public static string ColorKeyOf(AdtLiquidKind kind) => kind switch
    {
        AdtLiquidKind.River => ColorKeys.Water,
        AdtLiquidKind.Ocean => ColorKeys.Ocean,
        AdtLiquidKind.Magma => ColorKeys.Magma,
        _ => ColorKeys.Slime,
    };

    /// <summary>Surface height at grid vertex (<paramref name="y"/>, <paramref name="x"/>), both 0..8; y advances toward -X, x toward -Y.</summary>
    public float Height(int y, int x)
    {
        CheckRange(y, 9, nameof(y));
        CheckRange(x, 9, nameof(x));
        return _heights[(y * 9) + x];
    }

    /// <summary>The raw flag byte of square (<paramref name="y"/>, <paramref name="x"/>), both 0..7.</summary>
    public byte SquareFlags(int y, int x)
    {
        CheckRange(y, 8, nameof(y));
        CheckRange(x, 8, nameof(x));
        return _flags[(y * 8) + x];
    }

    /// <summary>True unless the square's flag byte is <see cref="HiddenSquare"/>.</summary>
    public bool IsVisible(int y, int x) => SquareFlags(y, x) != HiddenSquare;

    /// <summary>True when a visible square carries the dark-water bit.</summary>
    public bool IsDarkWater(int y, int x)
    {
        byte flags = SquareFlags(y, x);
        return flags != HiddenSquare && (flags & DarkWaterSquare) != 0;
    }

    /// <summary>Number of visible squares.</summary>
    public int VisibleSquares
    {
        get
        {
            int count = 0;
            foreach (byte flags in _flags)
            {
                count += flags != HiddenSquare ? 1 : 0;
            }

            return count;
        }
    }

    internal static AdtLiquidLayer Read(ReadOnlySpan<byte> data, int start, AdtLiquidKind kind)
    {
        // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:79-97 (f32 height1, f32 height2, 9x9 {u32, f32 height}, u8 flags[8][8], u8 data[84])
        // ref: Worldforge src/terrain.cpp:506-519 (the height is always the last four bytes of the 8-byte vertex)
        float min = ClientChunks.F32(data, start);
        float max = ClientChunks.F32(data, start + 4);
        var heights = new float[81];
        for (int i = 0; i < 81; i++)
        {
            heights[i] = ClientChunks.F32(data, start + 8 + (i * 8) + 4);
        }

        byte[] flags = data.Slice(start + 8 + (81 * 8), 64).ToArray();
        return new AdtLiquidLayer(kind, min, max, heights, flags);
    }

    private static void CheckRange(int value, int exclusiveMax, string name)
    {
        if ((uint)value >= (uint)exclusiveMax)
        {
            throw new ArgumentOutOfRangeException(name, value, $"must be 0..{exclusiveMax - 1}.");
        }
    }
}

/// <summary>
/// One MCNK: a 9x9 + 8x8 height sample block covering 8x8 squares (33.33 yd), with its area id, flags, hole bits and
/// liquid layers. Row and column are the chunk's place in the 16x16 grid of the tile: rows advance toward -X, columns
/// toward -Y.
/// </summary>
public sealed class AdtChunk
{
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:46-58 (MCVT: 9x9 + 8x8 = 145 floats)
    public const int HeightCount = 145;

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:440 and :450 (outer sample [y][x] at y*17+x, inner sample [y][x] at y*17+9+x)
    // ref: WoW-RE-Research formats/terrain-adt.md:269-276 (17 alternating rows of 9 and 8)
    // ref: Worldforge src/terrain.cpp:827-828 (the same split: 81 outer samples, then 64 inner)
    private const int Stride = 17;

    private readonly float[] _mcvt;
    private readonly AdtLiquidLayer[] _layers;

    internal AdtChunk(
        int index,
        uint flags,
        uint areaId,
        ushort holes,
        uint textureLayers,
        float baseX,
        float baseY,
        float baseHeight,
        bool hasHeights,
        float[] mcvt,
        AdtLiquidLayer[] layers)
    {
        Index = index;
        Flags = flags;
        AreaId = areaId;
        Holes = holes;
        TextureLayerCount = textureLayers;
        BaseX = baseX;
        BaseY = baseY;
        BaseHeight = baseHeight;
        HasHeights = hasHeights;
        _mcvt = mcvt;
        _layers = layers;
    }

    /// <summary>Position in the MCIN table, 0..255: <c>Row * 16 + Column</c>.</summary>
    public int Index { get; }

    /// <summary>Chunk row 0..15; advances toward -X (world north to south).</summary>
    public int Row => Index / AdtFile.ChunksPerSide;

    /// <summary>Chunk column 0..15; advances toward -Y (world west to east).</summary>
    public int Column => Index % AdtFile.ChunksPerSide;

    /// <summary>MCNK flag word.</summary>
    public uint Flags { get; }

    /// <summary>AreaTable.dbc id stored for the chunk (the extractor turns it into an explore flag).</summary>
    public uint AreaId { get; }

    /// <summary>The 4x4 hole bitmap: bit <c>(y / 2) * 4 + (x / 2)</c> covers squares y, x (0..7) of the chunk.</summary>
    public ushort Holes { get; }

    /// <summary>Number of MCLY texture layers the chunk declares.</summary>
    public uint TextureLayerCount { get; }

    /// <summary>The header position's world X (north) of the chunk's corner.</summary>
    public float BaseX { get; }

    /// <summary>The header position's world Y (west) of the chunk's corner.</summary>
    public float BaseY { get; }

    /// <summary>The height every MCVT sample is relative to.</summary>
    public float BaseHeight { get; }

    /// <summary>False when the chunk stores no MCVT, in which case every sample equals <see cref="BaseHeight"/>.</summary>
    public bool HasHeights { get; }

    /// <summary>The 145 stored MCVT values (relative to <see cref="BaseHeight"/>).</summary>
    public ReadOnlySpan<float> RelativeHeights => _mcvt;

    /// <summary>Liquid layers in storage order (river, ocean, magma, slime), unmerged.</summary>
    public IReadOnlyList<AdtLiquidLayer> LiquidLayers => _layers;

    /// <summary>World height of corner sample (<paramref name="y"/>, <paramref name="x"/>), both 0..8.</summary>
    public float OuterHeight(int y, int x)
    {
        if ((uint)y > 8 || (uint)x > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(y), $"outer samples are 0..8, got ({y}, {x}).");
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:417 and :440 (V9 = base height + MCVT value, as float addition)
        return BaseHeight + _mcvt[(y * Stride) + x];
    }

    /// <summary>World height of the square-centre sample (<paramref name="y"/>, <paramref name="x"/>), both 0..7.</summary>
    public float InnerHeight(int y, int x)
    {
        if ((uint)y > 7 || (uint)x > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(y), $"inner samples are 0..7, got ({y}, {x}).");
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:426 and :450 (V8 = base height + MCVT value)
        return BaseHeight + _mcvt[(y * Stride) + 9 + x];
    }

    /// <summary>True when square (<paramref name="y"/>, <paramref name="x"/>), both 0..7, is cut out by the hole bitmap.</summary>
    public bool IsHoleSquare(int y, int x)
    {
        if ((uint)y > 7 || (uint)x > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(y), $"squares are 0..7, got ({y}, {x}).");
        }

        // ref: Worldforge src/terrain.cpp:60-63 (bit (row / 2) * 4 + (col / 2))
        // ref: vmangos@0e3ff01 src/game/Maps/GridMap.cpp:41-42 and :274-283 (holetab_h x holetab_v select that same bit)
        return (Holes & (1 << (((y / 2) * 4) + (x / 2)))) != 0;
    }

    /// <summary>Number of squares the hole bitmap cuts out (each set bit covers four squares).</summary>
    public int HoleSquares => BitOperations.PopCount(Holes) * 4;

    /// <summary>True when any raw MCVT or header position value is not a finite number.</summary>
    internal static bool AnyNonFinite(ReadOnlySpan<float> values)
    {
        foreach (float value in values)
        {
            if (!float.IsFinite(value))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A parsed 1.12.1 ADT terrain tile. Read-only after <see cref="Parse"/> and safe to share between threads.
/// </summary>
/// <remarks>
/// The ADT <c>World\Maps\&lt;Directory&gt;\&lt;Directory&gt;_&lt;a&gt;_&lt;b&gt;.adt</c> belongs to the server tile with
/// X = b and Y = a (see <see cref="ClientMapPaths"/>). MCNK, MCVT and MCLQ are read through the offsets in the MCNK
/// header, never by walking the sub-chunks: real files carry 13 uncounted bytes after MCNR.
/// </remarks>
public sealed class AdtFile
{
    /// <summary>MVER value of every 1.12.1 ADT.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/loadlib.h:33 (FILE_FORMAT_VERSION 18); loadlib.cpp:52-55
    public const uint FormatVersion = 18;

    /// <summary>MCNK chunks per tile edge.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:39 (ADT_CELLS_PER_GRID 16)
    public const int ChunksPerSide = 16;

    /// <summary>MCNK chunks per tile.</summary>
    public const int ChunkTotal = ChunksPerSide * ChunksPerSide;

    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.cpp:60 (MHDR payload is sizeof(adt_MHDR) - 8 = 64 bytes)
    private const int HeaderPayloadBytes = 64;

    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:317-347 (MHDR: flags, then offsets relative to the payload start)
    private const int HeaderMcin = 1;
    private const int HeaderMtex = 2;
    private const int HeaderMmdx = 3;
    private const int HeaderMmid = 4;
    private const int HeaderMwmo = 5;
    private const int HeaderMwid = 6;
    private const int HeaderMddf = 7;
    private const int HeaderModf = 8;
    private const int HeaderMh2o = 10;

    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.cpp:121-130 (MCVT payload is 145 floats = 580 bytes)
    private const int McvtBytes = AdtChunk.HeightCount * 4;

    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:121-162 (MCNK header is 128 bytes; offsets below are inside it)
    private const int McnkHeaderBytes = 128;

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:294-300 (a layer per set flag, in river, ocean, magma, slime order)
    private static readonly AdtLiquidKind[] LayerOrder = [AdtLiquidKind.River, AdtLiquidKind.Ocean, AdtLiquidKind.Magma, AdtLiquidKind.Slime];

    private AdtFile(
        int byteLength,
        string[] textures,
        string[] doodadNames,
        uint[] doodadNameOffsets,
        string[] wmoNames,
        uint[] wmoNameOffsets,
        AdtDoodad[] doodads,
        AdtWmoObject[] wmoObjects,
        AdtChunk[] chunks)
    {
        ByteLength = byteLength;
        Textures = textures;
        DoodadNames = doodadNames;
        DoodadNameOffsets = doodadNameOffsets;
        WmoNames = wmoNames;
        WmoNameOffsets = wmoNameOffsets;
        Doodads = doodads;
        WmoObjects = wmoObjects;
        Chunks = chunks;
    }

    /// <summary>Size of the parsed file in bytes.</summary>
    public int ByteLength { get; }

    /// <summary>MTEX texture paths.</summary>
    public IReadOnlyList<string> Textures { get; }

    /// <summary>MMDX doodad model paths, in storage order.</summary>
    public IReadOnlyList<string> DoodadNames { get; }

    /// <summary>MMID: byte offset into MMDX of each doodad name.</summary>
    public IReadOnlyList<uint> DoodadNameOffsets { get; }

    /// <summary>MWMO WMO paths, in storage order.</summary>
    public IReadOnlyList<string> WmoNames { get; }

    /// <summary>MWID: byte offset into MWMO of each WMO name.</summary>
    public IReadOnlyList<uint> WmoNameOffsets { get; }

    /// <summary>MDDF entries in file order.</summary>
    public IReadOnlyList<AdtDoodad> Doodads { get; }

    /// <summary>MODF entries in file order.</summary>
    public IReadOnlyList<AdtWmoObject> WmoObjects { get; }

    /// <summary>The 256 MCNK chunks in MCIN order (<see cref="AdtChunk.Index"/> = position).</summary>
    public IReadOnlyList<AdtChunk> Chunks { get; }

    /// <summary>The chunk at row <paramref name="row"/>, column <paramref name="column"/> (each 0..15).</summary>
    public AdtChunk Chunk(int row, int column)
    {
        if ((uint)row >= ChunksPerSide || (uint)column >= ChunksPerSide)
        {
            throw new ArgumentOutOfRangeException(nameof(row), $"chunks are 0..15 per axis, got ({row}, {column}).");
        }

        return Chunks[(row * ChunksPerSide) + column];
    }

    /// <summary>
    /// Parses an ADT. Throws <see cref="InvalidDataException"/> with the reason when the file is empty, truncated, not
    /// version 18, or inconsistent (offsets that miss their chunk, an MCNK that disagrees with its MCIN slot, a liquid
    /// layer count that disagrees with the MCNK flags, MH2O liquid that 1.12.1 never had).
    /// </summary>
    public static AdtFile Parse(ReadOnlySpan<byte> data)
    {
        const string context = "ADT";
        if (data.Length == 0)
        {
            throw new InvalidDataException($"{context}: the file is empty.");
        }

        ClientChunks.Chunk mver = ClientChunks.Expect(data, 0, "MVER", context);
        if (mver.Size < 4)
        {
            throw new InvalidDataException($"{context}: MVER holds {mver.Size} bytes, a version needs 4.");
        }

        uint version = ClientChunks.U32(data, mver.DataStart);
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"{context}: MVER is {version}, 1.12.1 ADTs are {FormatVersion}.");
        }

        // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.cpp:48 (MHDR is the chunk right after MVER)
        ClientChunks.Chunk mhdr = ClientChunks.Expect(data, mver.End, "MHDR", context);
        if (mhdr.Size != HeaderPayloadBytes)
        {
            throw new InvalidDataException($"{context}: MHDR holds {mhdr.Size} bytes, expected {HeaderPayloadBytes}.");
        }

        var header = new uint[HeaderPayloadBytes / 4];
        for (int i = 0; i < header.Length; i++)
        {
            header[i] = ClientChunks.U32(data, mhdr.DataStart + (i * 4));
        }

        if (header[HeaderMh2o] != 0)
        {
            throw new InvalidDataException($"{context}: MHDR points at an MH2O liquid chunk, which build 5875 never writes (WotLK data?).");
        }

        ClientChunks.Chunk mcin = Locate(data, mhdr, header, HeaderMcin, "MCIN", context)
            ?? throw new InvalidDataException($"{context}: MHDR has no MCIN.");
        if (mcin.Size != ChunkTotal * 16)
        {
            throw new InvalidDataException($"{context}: MCIN holds {mcin.Size} bytes, expected {ChunkTotal * 16}.");
        }

        string[] textures = [];
        if (Locate(data, mhdr, header, HeaderMtex, "MTEX", context) is { } mtex)
        {
            textures = [.. ClientChunks.Strings(data.Slice(mtex.DataStart, mtex.Size), $"{context} MTEX").Names];
        }

        (string[] doodadNames, uint[] doodadOffsets, string[] doodadPaths) = ReadNames(data, mhdr, header, HeaderMmdx, "MMDX", HeaderMmid, "MMID", context);
        (string[] wmoNames, uint[] wmoOffsets, string[] wmoPaths) = ReadNames(data, mhdr, header, HeaderMwmo, "MWMO", HeaderMwid, "MWID", context);

        AdtDoodad[] doodads = [];
        if (Locate(data, mhdr, header, HeaderMddf, "MDDF", context) is { } mddf)
        {
            doodads = [.. AdtPlacements.ReadDoodads(data.Slice(mddf.DataStart, mddf.Size), doodadPaths, context)];
        }

        AdtWmoObject[] wmoObjects = [];
        if (Locate(data, mhdr, header, HeaderModf, "MODF", context) is { } modf)
        {
            wmoObjects = [.. AdtPlacements.ReadWmoObjects(data.Slice(modf.DataStart, modf.Size), wmoPaths, context)];
        }

        var chunks = new AdtChunk[ChunkTotal];
        for (int index = 0; index < ChunkTotal; index++)
        {
            // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:205-211 (MCIN entry: u32 offset, u32 size, u32 flags, u32 async id)
            int entry = mcin.DataStart + (index * 16);
            uint offset = ClientChunks.U32(data, entry);
            uint size = ClientChunks.U32(data, entry + 4);
            if (offset > (uint)data.Length)
            {
                throw new InvalidDataException($"{context}: MCIN entry {index} points at byte {offset}, past the {data.Length}-byte file.");
            }

            ClientChunks.Chunk mcnk = ClientChunks.Expect(data, (int)offset, "MCNK", $"{context} MCIN entry {index}");
            if (size != (uint)mcnk.Size + ClientChunks.HeaderBytes)
            {
                throw new InvalidDataException($"{context}: MCIN entry {index} says {size} bytes but its MCNK spans {mcnk.Size + ClientChunks.HeaderBytes}.");
            }

            chunks[index] = ReadChunk(data, index, mcnk);
        }

        return new AdtFile(data.Length, textures, doodadNames, doodadOffsets, wmoNames, wmoOffsets, doodads, wmoObjects, chunks);
    }

    private static ClientChunks.Chunk? Locate(ReadOnlySpan<byte> data, ClientChunks.Chunk mhdr, uint[] header, int field, string id, string context)
    {
        // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:344-345 (offsets count from the MHDR payload; zero means absent)
        uint relative = header[field];
        if (relative == 0)
        {
            return null;
        }

        long start = (long)mhdr.DataStart + relative;
        if (start > data.Length - ClientChunks.HeaderBytes)
        {
            throw new InvalidDataException($"{context}: MHDR offset {relative} for {id} points past the {data.Length}-byte file.");
        }

        return ClientChunks.Expect(data, (int)start, id, $"{context} MHDR");
    }

    private static (string[] Names, uint[] Offsets, string[] Paths) ReadNames(
        ReadOnlySpan<byte> data,
        ClientChunks.Chunk mhdr,
        uint[] header,
        int nameField,
        string nameId,
        int indexField,
        string indexId,
        string context)
    {
        ClientChunks.Chunk? names = Locate(data, mhdr, header, nameField, nameId, context);
        ClientChunks.Chunk? index = Locate(data, mhdr, header, indexField, indexId, context);
        if (names is null && index is null)
        {
            return ([], [], []);
        }

        if (names is not { } nameChunk || index is not { } indexChunk)
        {
            throw new InvalidDataException($"{context}: {nameId} and {indexId} must both be present or both absent.");
        }

        if (indexChunk.Size % 4 != 0)
        {
            throw new InvalidDataException($"{context}: {indexId} holds {indexChunk.Size} bytes, not a whole number of u32 offsets.");
        }

        ReadOnlySpan<byte> payload = data.Slice(nameChunk.DataStart, nameChunk.Size);
        (List<string> list, _) = ClientChunks.Strings(payload, $"{context} {nameId}");
        var offsets = new uint[indexChunk.Size / 4];
        var paths = new string[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = ClientChunks.U32(data, indexChunk.DataStart + (i * 4));
            paths[i] = ClientChunks.StringAt(payload, offsets[i], $"{context} {indexId}[{i}]");
        }

        return ([.. list], offsets, paths);
    }

    private static AdtChunk ReadChunk(ReadOnlySpan<byte> data, int index, ClientChunks.Chunk mcnk)
    {
        string context = $"ADT MCNK {index}";
        if (mcnk.Size < McnkHeaderBytes)
        {
            throw new InvalidDataException($"{context}: holds {mcnk.Size} bytes, the header alone is {McnkHeaderBytes}.");
        }

        // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:130-162 (field order; offsets below are payload-relative)
        // ref: Worldforge src/terrain.cpp:23-45 (the same header offsets, checked against real tiles)
        int h = mcnk.DataStart;
        uint flags = ClientChunks.U32(data, h);
        uint indexX = ClientChunks.U32(data, h + 0x04);
        uint indexY = ClientChunks.U32(data, h + 0x08);
        uint textureLayers = ClientChunks.U32(data, h + 0x0C);
        uint offsetMcvt = ClientChunks.U32(data, h + 0x14);
        uint areaId = ClientChunks.U32(data, h + 0x34);
        ushort holes = ClientChunks.U16(data, h + 0x3C);
        uint offsetMclq = ClientChunks.U32(data, h + 0x60);
        uint sizeMclq = ClientChunks.U32(data, h + 0x64);
        float baseX = ClientChunks.F32(data, h + 0x68);
        float baseY = ClientChunks.F32(data, h + 0x6C);
        float baseHeight = ClientChunks.F32(data, h + 0x70);

        // The MCIN slot is the position the extractor uses; the MCNK's own IndexX (column) and IndexY (row) must agree.
        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:391 (cells->getMCNK(i, j): i is the row, j the column)
        // ref: WoW-RE-Research formats/terrain-adt.md:203-208 (IndexX = column, IndexY = row)
        if (indexX != (uint)(index % ChunksPerSide) || indexY != (uint)(index / ChunksPerSide))
        {
            throw new InvalidDataException($"{context}: header says column {indexX}, row {indexY} but its MCIN slot is column {index % ChunksPerSide}, row {index / ChunksPerSide}.");
        }

        if (!float.IsFinite(baseX) || !float.IsFinite(baseY) || !float.IsFinite(baseHeight))
        {
            throw new InvalidDataException($"{context}: the header position ({baseX}, {baseY}, {baseHeight}) is not finite.");
        }

        // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:165-170 (MCVT offset counts from the MCNK chunk start; zero = none)
        var mcvt = new float[AdtChunk.HeightCount];
        bool hasHeights = offsetMcvt != 0;
        if (hasHeights)
        {
            long start = (long)mcnk.Start + offsetMcvt;
            if (start > data.Length - ClientChunks.HeaderBytes)
            {
                throw new InvalidDataException($"{context}: MCVT offset {offsetMcvt} points past the file.");
            }

            ClientChunks.Chunk chunk = ClientChunks.Expect(data, (int)start, "MCVT", context);
            if (chunk.Size != McvtBytes)
            {
                throw new InvalidDataException($"{context}: MCVT holds {chunk.Size} bytes, expected {McvtBytes}.");
            }

            for (int i = 0; i < mcvt.Length; i++)
            {
                mcvt[i] = ClientChunks.F32(data, chunk.DataStart + (i * 4));
            }

            if (AdtChunk.AnyNonFinite(mcvt))
            {
                throw new InvalidDataException($"{context}: MCVT holds a height that is not finite.");
            }
        }

        AdtLiquidLayer[] layers = ReadLiquid(data, mcnk, flags, offsetMclq, sizeMclq, context);
        return new AdtChunk(index, flags, areaId, holes, textureLayers, baseX, baseY, baseHeight, hasHeights, mcvt, layers);
    }

    private static AdtLiquidLayer[] ReadLiquid(ReadOnlySpan<byte> data, ClientChunks.Chunk mcnk, uint flags, uint offsetMclq, uint sizeMclq, string context)
    {
        // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:171-190 (layers = (sizeMCLQ - 8) / 804; the size counts the 8-byte chunk header)
        // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.cpp:110-116 (an MCLQ chunk must be there when the offset is set)
        // ref: Worldforge src/terrain.cpp:478-486 (the MCLQ chunk's own size field is unreliable; the MCNK header drives the layout)
        int expected = BitOperations.PopCount(flags & AdtLiquidLayer.McnkMask);
        int stored = 0;
        long layersStart = 0;
        if (offsetMclq != 0)
        {
            long start = (long)mcnk.Start + offsetMclq;
            ClientChunks.Need(data, start, ClientChunks.HeaderBytes, context);
            string tag = ClientChunks.Tag(data.Slice((int)start, 4));
            if (!string.Equals(tag, "MCLQ", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{context}: the liquid offset points at {tag}, not MCLQ.");
            }

            if (sizeMclq > ClientChunks.HeaderBytes)
            {
                uint payload = sizeMclq - ClientChunks.HeaderBytes;
                if (payload % AdtLiquidLayer.LayerBytes != 0)
                {
                    throw new InvalidDataException($"{context}: the MCLQ size {sizeMclq} is not 8 plus a whole number of {AdtLiquidLayer.LayerBytes}-byte layers.");
                }

                stored = (int)(payload / AdtLiquidLayer.LayerBytes);
                layersStart = start + ClientChunks.HeaderBytes;
                ClientChunks.Need(data, layersStart, (long)stored * AdtLiquidLayer.LayerBytes, context);
            }
        }

        if (stored != expected)
        {
            throw new InvalidDataException($"{context}: the flags 0x{flags:X} select {expected} liquid layer(s) but MCLQ stores {stored}.");
        }

        var layers = new AdtLiquidLayer[stored];
        int layer = 0;
        foreach (AdtLiquidKind kind in LayerOrder)
        {
            if ((flags & AdtLiquidLayer.FlagOf(kind)) == 0)
            {
                continue;
            }

            layers[layer] = AdtLiquidLayer.Read(data, (int)(layersStart + ((long)layer * AdtLiquidLayer.LayerBytes)), kind);
            layer++;
        }

        return layers;
    }
}

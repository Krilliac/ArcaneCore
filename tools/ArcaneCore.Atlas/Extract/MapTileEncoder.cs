using System.Buffers.Binary;
using ArcaneCore.Atlas.Reference.Client.Terrain;

namespace ArcaneCore.Atlas.Extract;

/// <summary>
/// Turns one parsed 1.12.1 ADT into the bytes of the server's <c>maps/MMMXXYY.map</c> file ("z1.4"), the way the
/// reference map extractor does with its defaults: heights stored as floats, heights below -500 raised to -500.
/// The layout the server reads is described on <c>ArcaneCore.Game.Maps.Terrain.TerrainTile</c>. Stateless and thread-safe.
/// </summary>
/// <remarks>
/// Not produced: heights packed into 8 or 16 bits (the reference's <c>-f 1</c>) and unclamped heights (<c>-h 0</c>).
/// </remarks>
public static class MapTileEncoder
{
    /// <summary>Height squares per tile edge (16 chunks of 8).</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/adt.h:39-41 (ADT_CELLS_PER_GRID 16, ADT_CELL_SIZE 8, ADT_GRID_SIZE 128)
    public const int Squares = 128;

    /// <summary>The lowest height stored; also the level written for liquid vertices no shown square reaches.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:92 (CONF_use_minHeight = -500.0f) and :91 (the limit is on by default)
    public const float MinHeight = -500f;

    private const int Chunks = AdtFile.ChunksPerSide;
    private const int ChunkSquares = 8;
    private const int Vertices = Squares + 1;

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:256-260 ("MAPS", "z1.4", "AREA", "MHGT", "MLIQ" read as little-endian u32)
    private const uint MapMagic = 0x5350414D;
    private const uint VersionMagic = 0x342E317A;
    private const uint AreaMagic = 0x41455241;
    private const uint HeightMagic = 0x5447484D;
    private const uint LiquidMagic = 0x51494C4D;

    // ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:5-17 (file header: ten u32), :21-26 (area header), :32-38 (height header), :43-54 (liquid header)
    private const int FileHeaderBytes = 40;
    private const int AreaHeaderBytes = 8;
    private const int HeightHeaderBytes = 16;
    private const int LiquidHeaderBytes = 16;

    // ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:19, :28, :40-41
    private const ushort AreaNoArea = 0x0001;
    private const uint HeightNoHeight = 0x0001;
    private const byte LiquidNoType = 0x01;
    private const byte LiquidNoHeight = 0x02;

    // ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:74
    private const byte LiquidDeepWater = 0x10;

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:294-300 (per MCNK liquid flag: LiquidType.dbc entry and map liquid type)
    // ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:67-70 (magma 0x01, ocean 0x02, slime 0x04, water 0x08)
    private static (ushort Entry, byte MapFlag) LayerType(AdtLiquidKind kind) => kind switch
    {
        AdtLiquidKind.River => (1, 0x08),
        AdtLiquidKind.Ocean => (2, 0x02),
        AdtLiquidKind.Magma => (3, 0x01),
        _ => (4, 0x04),
    };

    /// <summary>The <c>.map</c> bytes of <paramref name="adt"/>, with chunk area ids turned into explore flags by <paramref name="areas"/>.</summary>
    public static byte[] Encode(AdtFile adt, AreaExploreFlags areas)
    {
        ArgumentNullException.ThrowIfNull(adt);
        ArgumentNullException.ThrowIfNull(areas);

        // Area: one explore flag per chunk; a tile with one flag everywhere stores only that flag.
        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:330-381
        var areaFlags = new ushort[Chunks * Chunks];
        for (int i = 0; i < Chunks; i++)
        {
            for (int j = 0; j < Chunks; j++)
            {
                areaFlags[(i * Chunks) + j] = areas.FlagOf(adt.Chunk(i, j).AreaId);
            }
        }

        bool fullAreaData = false;
        foreach (ushort flag in areaFlags)
        {
            fullAreaData |= flag != areaFlags[0];
        }

        int areaOffset = FileHeaderBytes;
        int areaSize = AreaHeaderBytes + (fullAreaData ? areaFlags.Length * sizeof(ushort) : 0);

        // Heights.
        (float[] v9, float[] v8) = Heights(adt);
        (float minHeight, float maxHeight) = Range(v9, v8);
        if (minHeight < MinHeight)
        {
            // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:479-494
            Raise(v8);
            Raise(v9);
            minHeight = MinHeight;
            if (maxHeight < MinHeight)
            {
                maxHeight = MinHeight;
            }
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:496-506 (a tile whose lowest and highest sample are equal stores no samples)
        bool flat = maxHeight == minHeight;
        int heightOffset = areaOffset + areaSize;
        int heightSize = HeightHeaderBytes + (flat ? 0 : (v9.Length + v8.Length) * sizeof(float));

        // Liquid.
        Liquid liquid = MergeLiquid(adt);
        int liquidOffset = 0;
        int liquidSize = 0;
        if (liquid.Stored)
        {
            // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:710-741
            liquidOffset = heightOffset + heightSize;
            liquidSize = LiquidHeaderBytes
                + (liquid.NoType ? 0 : (liquid.Entry.Length * sizeof(ushort)) + liquid.Flags.Length)
                + (liquid.NoHeight ? 0 : sizeof(float) * liquid.Columns * liquid.Rows);
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:744-752 (holes follow the liquid section, or the heights when there is none)
        int holesOffset = liquid.Stored ? liquidOffset + liquidSize : heightOffset + heightSize;
        int holesSize = Chunks * Chunks * sizeof(ushort);

        var file = new byte[holesOffset + holesSize];
        var w = new Writer(file);

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:326-329 and :773 (the file header)
        w.U32(MapMagic);
        w.U32(VersionMagic);
        w.U32((uint)areaOffset);
        w.U32((uint)areaSize);
        w.U32((uint)heightOffset);
        w.U32((uint)heightSize);
        w.U32((uint)liquidOffset);
        w.U32((uint)liquidSize);
        w.U32((uint)holesOffset);
        w.U32((uint)holesSize);

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:369-381 and :774-777
        w.U32(AreaMagic);
        w.U16(fullAreaData ? (ushort)0 : AreaNoArea);
        w.U16(fullAreaData ? (ushort)0 : areaFlags[0]);
        if (fullAreaData)
        {
            foreach (ushort flag in areaFlags)
            {
                w.U16(flag);
            }
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:499-506 and :779-797 (V9 first, then V8)
        w.U32(HeightMagic);
        w.U32(flat ? HeightNoHeight : 0);
        w.F32(minHeight);
        w.F32(maxHeight);
        if (!flat)
        {
            w.F32(v9);
            w.F32(v8);
        }

        if (liquid.Stored)
        {
            // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:712-735 and :800-814
            w.U32(LiquidMagic);
            w.U8((byte)((liquid.NoType ? LiquidNoType : 0) | (liquid.NoHeight ? LiquidNoHeight : 0)));
            w.U8(liquid.NoType ? liquid.Flags[0] : (byte)0);
            w.U16(liquid.NoType ? liquid.Entry[0] : (ushort)0);
            w.U8((byte)liquid.OffsetColumn);
            w.U8((byte)liquid.OffsetRow);
            w.U8((byte)liquid.Columns);
            w.U8((byte)liquid.Rows);
            w.F32(liquid.Level);
            if (!liquid.NoType)
            {
                foreach (ushort entry in liquid.Entry)
                {
                    w.U16(entry);
                }

                w.Bytes(liquid.Flags);
            }

            if (!liquid.NoHeight)
            {
                for (int y = 0; y < liquid.Rows; y++)
                {
                    w.F32(liquid.Grid.AsSpan(((y + liquid.OffsetRow) * Vertices) + liquid.OffsetColumn, liquid.Columns));
                }
            }
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:754-764 and :817 (the low 16 bits of each MCNK's hole word)
        for (int i = 0; i < Chunks; i++)
        {
            for (int j = 0; j < Chunks; j++)
            {
                w.U16(adt.Chunk(i, j).Holes);
            }
        }

        return file;
    }

    /// <summary>
    /// The 129 x 129 corner grid (V9) and the 128 x 128 square-centre grid (V8). Chunks are visited row by row and each
    /// writes all nine of its corner rows and columns, so a corner two chunks share keeps the value of the chunk visited last.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:385-454
    private static (float[] V9, float[] V8) Heights(AdtFile adt)
    {
        var v9 = new float[Vertices * Vertices];
        var v8 = new float[Squares * Squares];
        for (int i = 0; i < Chunks; i++)
        {
            for (int j = 0; j < Chunks; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                for (int y = 0; y <= ChunkSquares; y++)
                {
                    int row = ((i * ChunkSquares) + y) * Vertices;
                    for (int x = 0; x <= ChunkSquares; x++)
                    {
                        // A chunk without MCVT keeps the base height itself; with MCVT the sample is the float sum base + value.
                        v9[row + (j * ChunkSquares) + x] = chunk.HasHeights ? chunk.OuterHeight(y, x) : chunk.BaseHeight;
                    }
                }

                for (int y = 0; y < ChunkSquares; y++)
                {
                    int row = ((i * ChunkSquares) + y) * Squares;
                    for (int x = 0; x < ChunkSquares; x++)
                    {
                        v8[row + (j * ChunkSquares) + x] = chunk.HasHeights ? chunk.InnerHeight(y, x) : chunk.BaseHeight;
                    }
                }
            }
        }

        return (v9, v8);
    }

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:456-477 (V8 first, then V9; strict comparisons from -20000 and 20000)
    private static (float Min, float Max) Range(float[] v9, float[] v8)
    {
        float max = -20000f;
        float min = 20000f;
        foreach (float h in v8)
        {
            if (max < h)
            {
                max = h;
            }

            if (min > h)
            {
                min = h;
            }
        }

        foreach (float h in v9)
        {
            if (max < h)
            {
                max = h;
            }

            if (min > h)
            {
                min = h;
            }
        }

        return (min, max);
    }

    private static void Raise(float[] heights)
    {
        for (int i = 0; i < heights.Length; i++)
        {
            if (heights[i] < MinHeight)
            {
                heights[i] = MinHeight;
            }
        }
    }

    /// <summary>What the file stores for the tile's liquid.</summary>
    /// <param name="Stored">False when the file has no liquid section.</param>
    /// <param name="NoType">One type covers the whole tile: only the first chunk's entry and flags are stored.</param>
    /// <param name="NoHeight">One level covers the stored rectangle: no per-vertex levels are stored.</param>
    /// <param name="Entry">LiquidType.dbc entry per chunk, row-major.</param>
    /// <param name="Flags">Map liquid type flags per chunk, row-major.</param>
    /// <param name="OffsetRow">First stored vertex row.</param>
    /// <param name="OffsetColumn">First stored vertex column.</param>
    /// <param name="Rows">Stored vertex rows.</param>
    /// <param name="Columns">Stored vertex columns.</param>
    /// <param name="Level">The lowest level of the stored rectangle.</param>
    /// <param name="Grid">The 129 x 129 vertex levels.</param>
    private readonly record struct Liquid(
        bool Stored,
        bool NoType,
        bool NoHeight,
        ushort[] Entry,
        byte[] Flags,
        int OffsetRow,
        int OffsetColumn,
        int Rows,
        int Columns,
        float Level,
        float[] Grid);

    /// <summary>
    /// Merges each chunk's stacked MCLQ layers into the one surface the format holds: a square is flooded when any layer
    /// shows it, a vertex takes the highest surface among the shown squares that touch it, and the chunk's type is that of
    /// the layer showing the most squares (the higher layer on a tie). Then finds the rectangle of shown squares to store.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:557-741
    private static Liquid MergeLiquid(AdtFile adt)
    {
        var show = new bool[Squares * Squares];
        var grid = new float[Vertices * Vertices];
        Array.Fill(grid, MinHeight);
        var entry = new ushort[Chunks * Chunks];
        var flags = new byte[Chunks * Chunks];
        var cellHeight = new float[(ChunkSquares + 1) * (ChunkSquares + 1)];
        var cellSet = new bool[(ChunkSquares + 1) * (ChunkSquares + 1)];

        for (int i = 0; i < Chunks; i++)
        {
            for (int j = 0; j < Chunks; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                if (chunk.LiquidLayers.Count == 0)
                {
                    continue;
                }

                int cell = (i * Chunks) + j;
                Array.Clear(cellSet);
                int count = 0;
                int bestCells = 0;
                float bestHeight = 0f;
                foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
                {
                    int layerCells = 0;
                    float layerHeight = -20000f;
                    for (int y = 0; y < ChunkSquares; y++)
                    {
                        for (int x = 0; x < ChunkSquares; x++)
                        {
                            byte square = layer.SquareFlags(y, x);
                            if (square == AdtLiquidLayer.HiddenSquare)
                            {
                                continue;
                            }

                            show[((((i * ChunkSquares) + y) * Squares) + (j * ChunkSquares)) + x] = true;
                            if ((square & AdtLiquidLayer.DarkWaterSquare) != 0)
                            {
                                flags[cell] |= LiquidDeepWater;
                            }

                            layerCells++;
                            count++;

                            // Only the corners of a shown square are read: the client leaves the others uninitialised.
                            for (int vy = y; vy <= y + 1; vy++)
                            {
                                for (int vx = x; vx <= x + 1; vx++)
                                {
                                    float h = layer.Height(vy, vx);
                                    int at = (vy * (ChunkSquares + 1)) + vx;
                                    if (!cellSet[at] || cellHeight[at] < h)
                                    {
                                        cellHeight[at] = h;
                                        cellSet[at] = true;
                                    }

                                    if (layerHeight < h)
                                    {
                                        layerHeight = h;
                                    }
                                }
                            }
                        }
                    }

                    if (layerCells > bestCells || (layerCells == bestCells && layerHeight > bestHeight))
                    {
                        bestCells = layerCells;
                        bestHeight = layerHeight;
                        (ushort layerEntry, byte mapFlag) = LayerType(layer.Kind);
                        entry[cell] = layerEntry;
                        flags[cell] = (byte)((flags[cell] & LiquidDeepWater) | mapFlag);
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                for (int y = 0; y <= ChunkSquares; y++)
                {
                    for (int x = 0; x <= ChunkSquares; x++)
                    {
                        int at = (y * (ChunkSquares + 1)) + x;
                        if (cellSet[at])
                        {
                            grid[((((i * ChunkSquares) + y) * Vertices) + (j * ChunkSquares)) + x] = cellHeight[at];
                        }
                    }
                }
            }
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:656-683 (no section when every chunk has liquid type 0)
        bool fullType = false;
        for (int c = 0; c < entry.Length && !fullType; c++)
        {
            fullType = entry[c] != entry[0] || flags[c] != flags[0];
        }

        if (flags[0] == 0 && !fullType)
        {
            return new Liquid(false, false, false, entry, flags, 0, 0, 0, 0, 0f, grid);
        }

        // The rectangle scan visits the 128 x 128 squares and reads each square's own (top-left) vertex; a square that is
        // not shown has that vertex written back to the floor. Row and column 128 are never visited.
        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:685-709
        int minX = 255;
        int minY = 255;
        int maxX = 0;
        int maxY = 0;
        float maxLevel = -20000f;
        float minLevel = 20000f;
        for (int y = 0; y < Squares; y++)
        {
            for (int x = 0; x < Squares; x++)
            {
                int at = (y * Vertices) + x;
                if (show[(y * Squares) + x])
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    float h = grid[at];
                    if (maxLevel < h)
                    {
                        maxLevel = h;
                    }

                    if (minLevel > h)
                    {
                        minLevel = h;
                    }
                }
                else
                {
                    grid[at] = MinHeight;
                    if (minLevel > MinHeight)
                    {
                        minLevel = MinHeight;
                    }
                }
            }
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:716-719 (width and height count the closing vertex)
        return new Liquid(
            Stored: true,
            NoType: !fullType,
            NoHeight: maxLevel == minLevel,
            Entry: entry,
            Flags: flags,
            OffsetRow: minY,
            OffsetColumn: minX,
            Rows: maxY - minY + 1 + 1,
            Columns: maxX - minX + 1 + 1,
            Level: minLevel,
            Grid: grid);
    }

    private ref struct Writer(byte[] buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _at;

        public void U8(byte value) => _buffer[_at++] = value;

        public void U16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer[_at..], value);
            _at += sizeof(ushort);
        }

        public void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_at..], value);
            _at += sizeof(uint);
        }

        public void F32(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buffer[_at..], value);
            _at += sizeof(float);
        }

        public void F32(ReadOnlySpan<float> values)
        {
            foreach (float value in values)
            {
                F32(value);
            }
        }

        public void Bytes(ReadOnlySpan<byte> values)
        {
            values.CopyTo(_buffer[_at..]);
            _at += values.Length;
        }
    }
}

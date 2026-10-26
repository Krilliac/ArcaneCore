using ArcaneCore.Atlas.Reference.Client.Terrain;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

/// <summary>
/// What the vmangos map extractor does to an ADT on its way to a server <c>.map</c> file, written from
/// <c>contrib/extractor/System.cpp</c> (cited per rule) so the real-data tests can compare the client file with the
/// server's data sample by sample. Nothing here is used by the Atlas product code: the product keeps the client's own
/// unmerged layers.
/// </summary>
internal static class ExtractorEmulation
{
    /// <summary>Samples per tile edge in the server's height grids.</summary>
    public const int Squares = 128;

    /// <summary>Extractor <c>CONF_use_minHeight</c>.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:92 (CONF_use_minHeight = -500.0f)
    public const float MinHeight = -500f;

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:286-300 (liquid layer order, LiquidType.dbc entry and map type flag)
    private static readonly (AdtLiquidKind Kind, ushort Entry, byte MapFlag)[] LayerTypes =
    [
        (AdtLiquidKind.River, 1, 8),
        (AdtLiquidKind.Ocean, 2, 2),
        (AdtLiquidKind.Magma, 3, 1),
        (AdtLiquidKind.Slime, 4, 4),
    ];

    /// <summary>The 129 x 129 corner grid and 128 x 128 centre grid with the extractor's last-writer-wins rule, unclamped.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:410-454 (chunk i, j writes V9[i*8+y][j*8+x] for y, x in 0..8; later chunks overwrite)
    public static (float[] V9, float[] V8) RawHeights(AdtFile adt)
    {
        var v9 = new float[(Squares + 1) * (Squares + 1)];
        var v8 = new float[Squares * Squares];
        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                for (int y = 0; y <= 8; y++)
                {
                    for (int x = 0; x <= 8; x++)
                    {
                        v9[((((i * 8) + y) * (Squares + 1)) + (j * 8)) + x] = chunk.OuterHeight(y, x);
                    }
                }

                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        v8[((((i * 8) + y) * Squares) + (j * 8)) + x] = chunk.InnerHeight(y, x);
                    }
                }
            }
        }

        return (v9, v8);
    }

    /// <summary>Lowest and highest sample over both grids.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:456-477
    public static (float Min, float Max) Range(float[] v9, float[] v8)
    {
        float min = 20000f;
        float max = -20000f;
        foreach (float h in v8)
        {
            max = Math.Max(max, h);
            min = Math.Min(min, h);
        }

        foreach (float h in v9)
        {
            max = Math.Max(max, h);
            min = Math.Min(min, h);
        }

        return (min, max);
    }

    /// <summary>Clamps every sample below <see cref="MinHeight"/> to it when the lowest sample is below it; returns whether it did.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:479-494 (CONF_allow_height_limit is on by default, System.cpp:91)
    public static bool ClampLow(float[] v9, float[] v8, ref float min, ref float max)
    {
        if (!(min < MinHeight))
        {
            return false;
        }

        for (int i = 0; i < v8.Length; i++)
        {
            v8[i] = Math.Max(v8[i], MinHeight);
        }

        for (int i = 0; i < v9.Length; i++)
        {
            v9[i] = Math.Max(v9[i], MinHeight);
        }

        min = MinHeight;
        max = Math.Max(max, MinHeight);
        return true;
    }

    /// <summary>What the extractor stores for a tile's liquid.</summary>
    /// <param name="HasLiquid">False when the .map file has no liquid section.</param>
    /// <param name="NoType">True when one type covers the whole tile (global flags and entry).</param>
    /// <param name="NoHeight">True when the stored surface is one level.</param>
    /// <param name="Entry">Per-chunk LiquidType.dbc entry (16 x 16, row-major by chunk row).</param>
    /// <param name="Flags">Per-chunk liquid type flags.</param>
    /// <param name="OffsetRow">First stored vertex row.</param>
    /// <param name="OffsetColumn">First stored vertex column.</param>
    /// <param name="Rows">Stored vertex rows.</param>
    /// <param name="Columns">Stored vertex columns.</param>
    /// <param name="Level">Lowest stored height (the single level when <paramref name="NoHeight"/>).</param>
    /// <param name="Grid">129 x 129 vertex heights after the extractor's reset of hidden squares.</param>
    /// <param name="ReadButReset">Vertices the extractor read for a shown square and then reset because their own square is hidden.</param>
    /// <param name="StackedChunks">Chunks with more than one liquid layer.</param>
    internal sealed record LiquidModel(
        bool HasLiquid,
        bool NoType,
        bool NoHeight,
        ushort[] Entry,
        byte[] Flags,
        int OffsetRow,
        int OffsetColumn,
        int Rows,
        int Columns,
        float Level,
        float[] Grid,
        int ReadButReset,
        int StackedChunks);

    /// <summary>Merges an ADT's liquid layers the way the extractor does and packs the result the way it stores it.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:557-741
    public static LiquidModel Liquid(AdtFile adt)
    {
        const int vertices = Squares + 1;
        var show = new bool[Squares * Squares];
        var grid = new float[vertices * vertices];
        Array.Fill(grid, MinHeight);
        var entry = new ushort[256];
        var flags = new byte[256];
        int stacked = 0;
        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                if (chunk.LiquidLayers.Count > 1)
                {
                    stacked++;
                }

                var cellHeight = new float[9 * 9];
                var cellSet = new bool[9 * 9];
                int bestCells = 0;
                float bestHeight = 0f;
                int count = 0;
                foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
                {
                    (AdtLiquidKind _, ushort layerEntry, byte mapFlag) = LayerTypes[(int)layer.Kind];
                    int layerCells = 0;
                    float layerHeight = -20000f;
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            if (!layer.IsVisible(y, x))
                            {
                                continue;
                            }

                            show[(((i * 8) + y) * Squares) + (j * 8) + x] = true;
                            if ((layer.SquareFlags(y, x) & AdtLiquidLayer.DarkWaterSquare) != 0)
                            {
                                flags[(i * 16) + j] |= 0x10;
                            }

                            layerCells++;
                            count++;
                            for (int vy = y; vy <= y + 1; vy++)
                            {
                                for (int vx = x; vx <= x + 1; vx++)
                                {
                                    float h = layer.Height(vy, vx);
                                    int at = (vy * 9) + vx;
                                    if (!cellSet[at] || cellHeight[at] < h)
                                    {
                                        cellHeight[at] = h;
                                        cellSet[at] = true;
                                    }

                                    layerHeight = Math.Max(layerHeight, h);
                                }
                            }
                        }
                    }

                    if (layerCells > bestCells || (layerCells == bestCells && layerHeight > bestHeight))
                    {
                        bestCells = layerCells;
                        bestHeight = layerHeight;
                        entry[(i * 16) + j] = layerEntry;
                        flags[(i * 16) + j] = (byte)((flags[(i * 16) + j] & 0x10) | mapFlag);
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                for (int y = 0; y <= 8; y++)
                {
                    for (int x = 0; x <= 8; x++)
                    {
                        if (cellSet[(y * 9) + x])
                        {
                            grid[((((i * 8) + y) * vertices) + (j * 8)) + x] = cellHeight[(y * 9) + x];
                        }
                    }
                }
            }
        }

        ushort firstEntry = entry[0];
        byte firstFlag = flags[0];
        bool fullType = false;
        for (int c = 0; c < 256 && !fullType; c++)
        {
            fullType = entry[c] != firstEntry || flags[c] != firstFlag;
        }

        if (firstFlag == 0 && !fullType)
        {
            return new LiquidModel(false, false, false, entry, flags, 0, 0, 0, 0, 0f, grid, 0, stacked);
        }

        int minColumn = 255;
        int minRow = 255;
        int maxColumn = 0;
        int maxRow = 0;
        float maxLevel = -20000f;
        float minLevel = 20000f;
        int resetVertices = 0;
        for (int y = 0; y < Squares; y++)
        {
            for (int x = 0; x < Squares; x++)
            {
                int at = (y * vertices) + x;
                if (show[(y * Squares) + x])
                {
                    minColumn = Math.Min(minColumn, x);
                    maxColumn = Math.Max(maxColumn, x);
                    minRow = Math.Min(minRow, y);
                    maxRow = Math.Max(maxRow, y);
                    maxLevel = Math.Max(maxLevel, grid[at]);
                    minLevel = Math.Min(minLevel, grid[at]);
                }
                else
                {
                    // A vertex some shown square read, whose own square is hidden: the extractor writes the floor over it.
                    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:703-706
                    resetVertices += grid[at] != MinHeight ? 1 : 0;
                    grid[at] = MinHeight;
                    minLevel = Math.Min(minLevel, MinHeight);
                }
            }
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:716-720 (offset = first shown square, width and height count the closing vertex)
        return new LiquidModel(
            HasLiquid: true,
            NoType: !fullType,
            NoHeight: maxLevel == minLevel,
            Entry: entry,
            Flags: flags,
            OffsetRow: minRow,
            OffsetColumn: minColumn,
            Rows: maxRow - minRow + 1 + 1,
            Columns: maxColumn - minColumn + 1 + 1,
            Level: minLevel,
            Grid: grid,
            ReadButReset: resetVertices,
            StackedChunks: stacked);
    }
}

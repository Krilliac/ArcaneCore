using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// A parsed 1.12.1 WDT: which of the 64x64 ADT tiles the client loads for a map, plus the single global WMO of maps
/// that are one building (most dungeons). Read-only after <see cref="Parse"/> and safe to share between threads.
/// </summary>
public sealed class WdtFile
{
    /// <summary>MVER value of every 1.12.1 WDT.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/loadlib.h:33 and loadlib.cpp:52-55 (the same FILE_FORMAT_VERSION 18 check)
    public const uint FormatVersion = 18;

    /// <summary>MPHD flag: the map has no terrain and is one global WMO.</summary>
    // ref: D:\WoW-RE-Research\formats\terrain-adt.md:68-71 (MPHD flag 0x0001 = global WMO)
    public const uint GlobalWmoFlag = 0x1;

    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/wdt.h:40-59 (MPHD: eight u32; the first is the flag word)
    private const int MphdBytes = 32;

    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/wdt.h:61-78 (MAIN: adt_list[64][64] of {u32 exist, u32 data1})
    private const int MainBytes = 64 * 64 * 8;

    private readonly uint[] _main;

    private WdtFile(uint flags, uint[] main, string[] wmoNames, AdtWmoObject[] wmoObjects)
    {
        Flags = flags;
        _main = main;
        WmoNames = wmoNames;
        WmoObjects = wmoObjects;
        int count = 0;
        foreach (uint entry in main)
        {
            count += (entry & 1) != 0 ? 1 : 0;
        }

        TileCount = count;
    }

    /// <summary>The MPHD flag word.</summary>
    public uint Flags { get; }

    /// <summary>True when <see cref="GlobalWmoFlag"/> is set.</summary>
    public bool IsGlobalWmo => (Flags & GlobalWmoFlag) != 0;

    /// <summary>Number of tiles MAIN lists.</summary>
    public int TileCount { get; }

    /// <summary>MWMO names, in storage order (the global WMO's path for a global-WMO map).</summary>
    public IReadOnlyList<string> WmoNames { get; }

    /// <summary>MODF entries (the global WMO placement; empty for terrain maps).</summary>
    public IReadOnlyList<AdtWmoObject> WmoObjects { get; }

    /// <summary>
    /// True when MAIN lists the ADT of server tile (<paramref name="tileX"/>, <paramref name="tileY"/>). MAIN is indexed
    /// <c>[second file number][first file number]</c> and the second number is the server tile X (see <see cref="ClientMapPaths"/>).
    /// </summary>
    public bool HasTile(int tileX, int tileY)
    {
        if ((uint)tileX >= WorldGrid.TilesPerSide || (uint)tileY >= WorldGrid.TilesPerSide)
        {
            return false;
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:857-861 (adt_list[y][x] names file _x_y and the server tile (y, x))
        // ref: D:\WoW-RE-Research\formats\terrain-adt.md:87-90 (row-major, y outer then x inner)
        return (_main[(tileX * WorldGrid.TilesPerSide) + tileY] & 1) != 0;
    }

    /// <summary>The raw MAIN flag word of a tile (0 outside the grid).</summary>
    public uint TileFlags(int tileX, int tileY)
        => (uint)tileX >= WorldGrid.TilesPerSide || (uint)tileY >= WorldGrid.TilesPerSide ? 0 : _main[(tileX * WorldGrid.TilesPerSide) + tileY];

    /// <summary>The tiles MAIN lists for <paramref name="mapId"/>, in tile X then tile Y order.</summary>
    public IReadOnlyList<TileKey> Tiles(uint mapId)
    {
        var tiles = new List<TileKey>(TileCount);
        for (int x = 0; x < WorldGrid.TilesPerSide; x++)
        {
            for (int y = 0; y < WorldGrid.TilesPerSide; y++)
            {
                if (HasTile(x, y))
                {
                    tiles.Add(new TileKey(mapId, x, y));
                }
            }
        }

        return tiles;
    }

    /// <summary>
    /// Parses a WDT. Throws <see cref="InvalidDataException"/> with the reason when the file is empty, truncated, not
    /// version 18, lacks MPHD or MAIN, or has a MODF entry whose name index is outside MWMO.
    /// </summary>
    public static WdtFile Parse(ReadOnlySpan<byte> data)
    {
        const string context = "WDT";
        if (data.Length == 0)
        {
            throw new InvalidDataException($"{context}: the file is empty.");
        }

        List<ClientChunks.Chunk> chunks = ClientChunks.Walk(data, 0, context);

        // ref: vmangos@0e3ff01 contrib/extractor/loadlib/wdt.cpp:47-63 (MVER, MPHD, MAIN, MWMO follow each other)
        ClientChunks.Chunk mver = chunks.Count > 0 && chunks[0].Id == "MVER"
            ? chunks[0]
            : throw new InvalidDataException($"{context}: the first chunk is not MVER.");
        if (mver.Size < 4 || ClientChunks.U32(data, mver.DataStart) != FormatVersion)
        {
            throw new InvalidDataException($"{context}: MVER is not {FormatVersion}.");
        }

        ClientChunks.Chunk mphd = Single(chunks, "MPHD", context);
        if (mphd.Size < MphdBytes)
        {
            throw new InvalidDataException($"{context}: MPHD holds {mphd.Size} bytes, expected {MphdBytes}.");
        }

        ClientChunks.Chunk main = Single(chunks, "MAIN", context);
        if (main.Size != MainBytes)
        {
            throw new InvalidDataException($"{context}: MAIN holds {main.Size} bytes, expected {MainBytes}.");
        }

        uint flags = ClientChunks.U32(data, mphd.DataStart);
        var table = new uint[WorldGrid.TilesPerSide * WorldGrid.TilesPerSide];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = ClientChunks.U32(data, main.DataStart + (i * 8));
        }

        string[] names = [];
        if (chunks.Any(c => c.Id == "MWMO"))
        {
            ClientChunks.Chunk mwmo = Single(chunks, "MWMO", context);
            names = [.. ClientChunks.Strings(data.Slice(mwmo.DataStart, mwmo.Size), $"{context} MWMO").Names];
        }

        AdtWmoObject[] objects = [];
        if (chunks.Any(c => c.Id == "MODF"))
        {
            // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wdtfile.cpp:92-108 (a WDT MODF entry's id is the ordinal of its name in MWMO)
            // ref: D:\WoW-RE-Research\formats\terrain-adt.md:61 (the WDT MODF has the same 64-byte layout as the ADT one)
            ClientChunks.Chunk modf = Single(chunks, "MODF", context);
            objects = [.. AdtPlacements.ReadWmoObjects(data.Slice(modf.DataStart, modf.Size), names, context)];
        }

        return new WdtFile(flags, table, names, objects);
    }

    private static ClientChunks.Chunk Single(List<ClientChunks.Chunk> chunks, string id, string context)
    {
        ClientChunks.Chunk? found = null;
        foreach (ClientChunks.Chunk chunk in chunks)
        {
            if (!string.Equals(chunk.Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                throw new InvalidDataException($"{context}: chunk {id} appears twice.");
            }

            found = chunk;
        }

        return found ?? throw new InvalidDataException($"{context}: chunk {id} is missing.");
    }
}

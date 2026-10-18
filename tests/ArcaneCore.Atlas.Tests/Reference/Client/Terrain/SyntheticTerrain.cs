using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

/// <summary>One MCLQ layer of a synthetic chunk.</summary>
/// <param name="Kind">Which liquid flag selects the layer.</param>
/// <param name="Min">Stored lowest height.</param>
/// <param name="Max">Stored highest height.</param>
/// <param name="Height">Surface height at grid vertex (y, x), both 0..8.</param>
/// <param name="Flags">Flag byte of square (y, x), both 0..7 (0x0F hides the square).</param>
internal sealed record LiquidLayerSpec(AdtLiquidKind Kind, float Min, float Max, Func<int, int, float> Height, Func<int, int, byte> Flags);

/// <summary>One MDDF entry of a synthetic ADT.</summary>
internal sealed record DoodadSpec(string Path, uint UniqueId, WorldPoint Position, WorldPoint Rotation, ushort Scale, ushort Flags);

/// <summary>One MODF entry of a synthetic ADT or WDT.</summary>
internal sealed record WmoSpec(string Path, uint UniqueId, WorldPoint Position, WorldPoint Rotation, WorldPoint Min, WorldPoint Max, ushort Flags, ushort DoodadSet, ushort NameSet);

/// <summary>What a synthetic ADT holds. Heights are given relative to the chunk's base height, as MCVT stores them.</summary>
internal sealed class AdtSpec
{
    /// <summary>The server tile whose naming the chunk header positions follow.</summary>
    public TileKey Tile { get; init; } = new(0, 32, 32);

    /// <summary>MVER value.</summary>
    public uint Version { get; init; } = 18;

    /// <summary>Base height of the chunk at (row, column).</summary>
    public Func<int, int, float> BaseHeight { get; init; } = (_, _) => 0f;

    /// <summary>MCVT corner sample at (chunk row, chunk column, y, x), y and x in 0..8.</summary>
    public Func<int, int, int, int, float> Outer { get; init; } = (_, _, _, _) => 0f;

    /// <summary>MCVT square-centre sample at (chunk row, chunk column, y, x), y and x in 0..7.</summary>
    public Func<int, int, int, int, float> Inner { get; init; } = (_, _, _, _) => 0f;

    /// <summary>Chunk indices written without an MCVT sub-chunk.</summary>
    public HashSet<int> WithoutHeights { get; } = [];

    /// <summary>Hole bitmap by chunk index.</summary>
    public Dictionary<int, ushort> Holes { get; } = [];

    /// <summary>Area id by chunk index.</summary>
    public Dictionary<int, uint> Areas { get; } = [];

    /// <summary>Liquid layers by chunk index, in the order given (the MCNK flags are set from their kinds).</summary>
    public Dictionary<int, List<LiquidLayerSpec>> Liquid { get; } = [];

    /// <summary>MTEX paths.</summary>
    public List<string> Textures { get; } = [];

    /// <summary>MDDF entries.</summary>
    public List<DoodadSpec> Doodads { get; } = [];

    /// <summary>MODF entries.</summary>
    public List<WmoSpec> Wmos { get; } = [];
}

/// <summary>A built synthetic ADT plus the offsets tests patch.</summary>
/// <param name="Bytes">The file.</param>
/// <param name="MhdrStart">Offset of the MHDR chunk header.</param>
/// <param name="McinStart">Offset of the MCIN chunk header.</param>
/// <param name="McnkStarts">Offset of each MCNK chunk header, in MCIN order.</param>
internal sealed record BuiltAdt(byte[] Bytes, int MhdrStart, int McinStart, int[] McnkStarts)
{
    /// <summary>Offset of the MHDR payload (MHDR offsets count from here).</summary>
    public int MhdrData => MhdrStart + 8;

    /// <summary>Offset of the MCIN payload.</summary>
    public int McinData => McinStart + 8;

    /// <summary>Offset of the payload of chunk <paramref name="index"/>'s MCNK (its 128-byte header starts here).</summary>
    public int McnkData(int index) => McnkStarts[index] + 8;

    /// <summary>A copy with a u32 written at <paramref name="offset"/>.</summary>
    public byte[] WithU32(int offset, uint value)
    {
        byte[] copy = (byte[])Bytes.Clone();
        BitConverter.GetBytes(value).CopyTo(copy, offset);
        return copy;
    }

    /// <summary>A copy with a float written at <paramref name="offset"/>.</summary>
    public byte[] WithF32(int offset, float value)
    {
        byte[] copy = (byte[])Bytes.Clone();
        BitConverter.GetBytes(value).CopyTo(copy, offset);
        return copy;
    }

    /// <summary>A copy with the four ASCII bytes of a (reversed on disk) tag written at <paramref name="offset"/>.</summary>
    public byte[] WithTag(int offset, string tag)
    {
        byte[] copy = (byte[])Bytes.Clone();
        for (int i = 0; i < 4; i++)
        {
            copy[offset + i] = (byte)tag[3 - i];
        }

        return copy;
    }
}

/// <summary>
/// Builds 1.12.1 WDT and ADT files in memory from a description. The layout follows the readers' cited sources; nothing
/// here is derived from a client file. Chunk tags are stored reversed; every MCNK is followed by its MCVT, an MCNR with
/// the 13 uncounted bytes real files carry, an MCLY and (with liquid) an MCLQ.
/// </summary>
internal static class SyntheticTerrain
{
    /// <summary>MCNK header size in bytes.</summary>
    public const int McnkHeaderBytes = 128;

    private const int ChunkEdge = 16;

    /// <summary>The edge of one chunk in yards, as the server's grid constant divides it.</summary>
    public static double ChunkSize => WorldGrid.TileSize / ChunkEdge;

    /// <summary>Builds the WDT of a map.</summary>
    /// <param name="tiles">Server tiles (X, Y) MAIN lists.</param>
    /// <param name="flags">MPHD flag word.</param>
    /// <param name="wmos">MODF entries (the global WMO); their names are written to MWMO.</param>
    /// <param name="version">MVER value.</param>
    public static byte[] Wdt(IEnumerable<(int X, int Y)> tiles, uint flags = 0, IReadOnlyList<WmoSpec>? wmos = null, uint version = 18)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        Chunk(w, "MVER", p => p.Write(version));
        Chunk(w, "MPHD", p =>
        {
            p.Write(flags);
            for (int i = 0; i < 7; i++)
            {
                p.Write(0u);
            }
        });

        var main = new uint[64 * 64 * 2];
        foreach ((int x, int y) in tiles)
        {
            main[((x * 64) + y) * 2] = 1;
        }

        Chunk(w, "MAIN", p =>
        {
            foreach (uint value in main)
            {
                p.Write(value);
            }
        });

        if (wmos is { Count: > 0 })
        {
            string[] names = [.. wmos.Select(o => o.Path).Distinct(StringComparer.Ordinal)];
            Chunk(w, "MWMO", p => WriteNames(p, names));
            Chunk(w, "MODF", p =>
            {
                foreach (WmoSpec o in wmos)
                {
                    WriteWmo(p, o, (uint)Array.IndexOf(names, o.Path));
                }
            });
        }

        w.Flush();
        return stream.ToArray();
    }

    /// <summary>Builds an ADT from <paramref name="spec"/>.</summary>
    public static BuiltAdt Adt(AdtSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);

        string[] doodadNames = [.. spec.Doodads.Select(d => d.Path).Distinct(StringComparer.Ordinal)];
        string[] wmoNames = [.. spec.Wmos.Select(o => o.Path).Distinct(StringComparer.Ordinal)];

        Chunk(w, "MVER", p => p.Write(spec.Version));

        // MHDR comes next; its offsets are filled in after the later chunks are placed.
        int mhdrStart = (int)stream.Position;
        Chunk(w, "MHDR", p => p.Write(new byte[64]));
        int mhdrData = mhdrStart + 8;

        var offsets = new uint[16];
        int mcinStart = (int)stream.Position;
        offsets[1] = (uint)(mcinStart - mhdrData);
        Chunk(w, "MCIN", p => p.Write(new byte[256 * 16]));

        if (spec.Textures.Count > 0)
        {
            offsets[2] = (uint)((int)stream.Position - mhdrData);
            Chunk(w, "MTEX", p => WriteNames(p, [.. spec.Textures]));
        }

        if (doodadNames.Length > 0)
        {
            offsets[3] = (uint)((int)stream.Position - mhdrData);
            Chunk(w, "MMDX", p => WriteNames(p, doodadNames));
            offsets[4] = (uint)((int)stream.Position - mhdrData);
            Chunk(w, "MMID", p => WriteNameOffsets(p, doodadNames));
        }

        if (wmoNames.Length > 0)
        {
            offsets[5] = (uint)((int)stream.Position - mhdrData);
            Chunk(w, "MWMO", p => WriteNames(p, wmoNames));
            offsets[6] = (uint)((int)stream.Position - mhdrData);
            Chunk(w, "MWID", p => WriteNameOffsets(p, wmoNames));
        }

        if (spec.Doodads.Count > 0)
        {
            offsets[7] = (uint)((int)stream.Position - mhdrData);
            Chunk(w, "MDDF", p =>
            {
                foreach (DoodadSpec d in spec.Doodads)
                {
                    p.Write((uint)Array.IndexOf(doodadNames, d.Path));
                    p.Write(d.UniqueId);
                    WriteVec(p, d.Position);
                    WriteVec(p, d.Rotation);
                    p.Write(d.Scale);
                    p.Write(d.Flags);
                }
            });
        }

        if (spec.Wmos.Count > 0)
        {
            offsets[8] = (uint)((int)stream.Position - mhdrData);
            Chunk(w, "MODF", p =>
            {
                foreach (WmoSpec o in spec.Wmos)
                {
                    WriteWmo(p, o, (uint)Array.IndexOf(wmoNames, o.Path));
                }
            });
        }

        var mcnkStarts = new int[256];
        var mcnkSizes = new int[256];
        for (int index = 0; index < 256; index++)
        {
            mcnkStarts[index] = (int)stream.Position;
            WriteMcnk(w, spec, index);
            mcnkSizes[index] = (int)stream.Position - mcnkStarts[index];
        }

        w.Flush();
        byte[] bytes = stream.ToArray();

        for (int i = 0; i < offsets.Length; i++)
        {
            BitConverter.GetBytes(offsets[i]).CopyTo(bytes, mhdrData + (i * 4));
        }

        for (int index = 0; index < 256; index++)
        {
            int entry = mcinStart + 8 + (index * 16);
            BitConverter.GetBytes((uint)mcnkStarts[index]).CopyTo(bytes, entry);
            BitConverter.GetBytes((uint)mcnkSizes[index]).CopyTo(bytes, entry + 4);
        }

        return new BuiltAdt(bytes, mhdrStart, mcinStart, mcnkStarts);
    }

    /// <summary>An ADT whose terrain is the plane <c>z = a + slopeX * worldX + slopeY * worldY</c> sampled at every corner and centre.</summary>
    public static AdtSpec Plane(TileKey tile, float a, float slopeX, float slopeY)
    {
        WorldPoint origin = WorldGrid.Origin(tile);
        double sq = WorldGrid.SquareSize;

        // Outer sample (y, x) of chunk (row, col) sits at origin - (row * 8 + y) * sq; the centre samples half a square on.
        float Height(int row, int col, int y, int x, double offset)
        {
            double wx = origin.X - (((row * 8) + y + offset) * sq);
            double wy = origin.Y - (((col * 8) + x + offset) * sq);
            return (float)(a + (slopeX * wx) + (slopeY * wy));
        }

        return new AdtSpec
        {
            Tile = tile,
            BaseHeight = (_, _) => 0f,
            Outer = (row, col, y, x) => Height(row, col, y, x, 0),
            Inner = (row, col, y, x) => Height(row, col, y, x, 0.5),
        };
    }

    /// <summary>A Map.dbc-like directory over a fixed list of maps.</summary>
    public static FakeMapDirectory Maps(params ClientMap[] maps) => new(maps);

    /// <summary>Puts the WDT and ADTs of one map into a client file source at the in-archive paths the reader uses.</summary>
    public static void AddMap(MemoryClientFiles files, string directory, byte[] wdt, params (TileKey Tile, byte[] Adt)[] adts)
    {
        ArgumentNullException.ThrowIfNull(files);
        files.Add(ClientMapPaths.WdtPath(directory), wdt);
        foreach ((TileKey tile, byte[] adt) in adts)
        {
            files.Add(ClientMapPaths.AdtPath(directory, tile), adt);
        }
    }

    private static void WriteMcnk(BinaryWriter w, AdtSpec spec, int index)
    {
        int row = index / 16;
        int col = index % 16;
        spec.Liquid.TryGetValue(index, out List<LiquidLayerSpec>? layers);
        layers ??= [];

        uint flags = 0;
        foreach (LiquidLayerSpec layer in layers)
        {
            flags |= AdtLiquidLayer.FlagOf(layer.Kind);
        }

        bool heights = !spec.WithoutHeights.Contains(index);
        using var body = new MemoryStream();
        using var b = new BinaryWriter(body);

        // The payload starts with the 128-byte header; sub-chunk offsets count from the MCNK chunk start (header + 8).
        int mcvtOffset = 0;
        int mclqOffset = 0;
        int mclqSize = 0;
        b.Write(new byte[McnkHeaderBytes]);
        if (heights)
        {
            mcvtOffset = 8 + (int)body.Position;
            Chunk(b, "MCVT", p =>
            {
                for (int y = 0; y < 9; y++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        p.Write(spec.Outer(row, col, y, x));
                    }

                    if (y < 8)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            p.Write(spec.Inner(row, col, y, x));
                        }
                    }
                }
            });
        }

        // MCNR: 145 packed normals, then 13 bytes the chunk size does not count (real files have them).
        Chunk(b, "MCNR", p => p.Write(new byte[145 * 3]));
        b.Write(new byte[13]);
        Chunk(b, "MCLY", p => p.Write(new byte[16]));
        if (layers.Count > 0)
        {
            mclqOffset = 8 + (int)body.Position;
            Chunk(b, "MCLQ", p =>
            {
                foreach (LiquidLayerSpec layer in layers)
                {
                    p.Write(layer.Min);
                    p.Write(layer.Max);
                    for (int y = 0; y < 9; y++)
                    {
                        for (int x = 0; x < 9; x++)
                        {
                            p.Write(0u);
                            p.Write(layer.Height(y, x));
                        }
                    }

                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            p.Write(layer.Flags(y, x));
                        }
                    }

                    p.Write(new byte[84]);
                }
            });
            mclqSize = 8 + (layers.Count * AdtLiquidLayerBytes);
        }

        b.Flush();
        byte[] payload = body.ToArray();

        WorldPoint origin = WorldGrid.Origin(spec.Tile);
        PutU32(payload, 0x00, flags);
        PutU32(payload, 0x04, (uint)col);
        PutU32(payload, 0x08, (uint)row);
        PutU32(payload, 0x0C, 1);
        PutU32(payload, 0x14, (uint)mcvtOffset);
        PutU32(payload, 0x34, spec.Areas.GetValueOrDefault(index));
        BitConverter.GetBytes(spec.Holes.GetValueOrDefault(index)).CopyTo(payload, 0x3C);
        PutU32(payload, 0x60, (uint)mclqOffset);
        PutU32(payload, 0x64, (uint)mclqSize);
        PutF32(payload, 0x68, (float)(origin.X - (row * ChunkSize)));
        PutF32(payload, 0x6C, (float)(origin.Y - (col * ChunkSize)));
        PutF32(payload, 0x70, spec.BaseHeight(row, col));

        Chunk(w, "MCNK", p => p.Write(payload));
    }

    private const int AdtLiquidLayerBytes = 804;

    private static void WriteNames(BinaryWriter p, string[] names)
    {
        foreach (string name in names)
        {
            p.Write(Encoding.Latin1.GetBytes(name));
            p.Write((byte)0);
        }
    }

    private static void WriteNameOffsets(BinaryWriter p, string[] names)
    {
        uint offset = 0;
        foreach (string name in names)
        {
            p.Write(offset);
            offset += (uint)Encoding.Latin1.GetByteCount(name) + 1;
        }
    }

    private static void WriteVec(BinaryWriter p, WorldPoint v)
    {
        p.Write((float)v.X);
        p.Write((float)v.Y);
        p.Write((float)v.Z);
    }

    private static void WriteWmo(BinaryWriter p, WmoSpec o, uint nameIndex)
    {
        p.Write(nameIndex);
        p.Write(o.UniqueId);
        WriteVec(p, o.Position);
        WriteVec(p, o.Rotation);
        WriteVec(p, o.Min);
        WriteVec(p, o.Max);
        p.Write(o.Flags);
        p.Write(o.DoodadSet);
        p.Write(o.NameSet);
        p.Write((ushort)0);
    }

    private static void PutU32(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);

    private static void PutF32(byte[] bytes, int offset, float value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);

    /// <summary>Writes one chunk: the tag reversed, the payload size, the payload.</summary>
    private static void Chunk(BinaryWriter w, string tag, Action<BinaryWriter> payload)
    {
        using var inner = new MemoryStream();
        using var p = new BinaryWriter(inner);
        payload(p);
        p.Flush();
        for (int i = 3; i >= 0; i--)
        {
            w.Write((byte)tag[i]);
        }

        w.Write((uint)inner.Length);
        w.Write(inner.ToArray());
    }
}

/// <summary>A fixed <see cref="IMapDirectory"/>.</summary>
internal sealed class FakeMapDirectory(IReadOnlyList<ClientMap> maps) : IMapDirectory
{
    private readonly Dictionary<uint, string> _areas = [];

    /// <inheritdoc />
    public IReadOnlyList<ClientMap> Maps { get; } = maps;

    /// <summary>Names an area id.</summary>
    public FakeMapDirectory WithArea(uint id, string name)
    {
        _areas[id] = name;
        return this;
    }

    /// <inheritdoc />
    public bool TryGetMap(uint mapId, out ClientMap map)
    {
        map = Maps.FirstOrDefault(m => m.Id == mapId)!;
        return map is not null;
    }

    /// <inheritdoc />
    public string? AreaName(uint areaId) => _areas.GetValueOrDefault(areaId);
}

/// <summary>A file source that counts reads and can be told to fail or hide files.</summary>
internal sealed class CountingFiles(IClientFileSource inner) : IClientFileSource
{
    private int _reads;

    /// <summary>Paths whose reads throw <see cref="IOException"/>.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of <see cref="Read"/> calls so far.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <inheritdoc />
    public string Description => inner.Description;

    /// <inheritdoc />
    public bool Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    public byte[]? Read(string path)
    {
        Interlocked.Increment(ref _reads);
        return Failing.Contains(path) ? throw new IOException($"{path} is locked.") : inner.Read(path);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string prefix) => inner.List(prefix);

    /// <inheritdoc />
    public string? ArchiveOf(string path) => inner.ArchiveOf(path);
}

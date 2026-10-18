using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>
/// A fact that needs the extracted 1.12.1 client files (<see cref="DirectoryClientFiles.TestEnvironmentVariable"/>) and the
/// server's extracted terrain data (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, with <c>vmaps/</c>). Skipped, visibly, when either
/// is missing.
/// </summary>
public sealed class RealClientModelsFactAttribute : FactAttribute
{
    public RealClientModelsFactAttribute()
    {
        if (RealClientModels.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Shared access to the real client files and server vmaps for the model oracles (read only).</summary>
internal static class RealClientModels
{
    public const string TerrainVariable = "ARCANECORE_TEST_TERRAIN_DIR";

    private static readonly Lazy<DirectoryClientFiles> SharedFiles = new(
        () => new DirectoryClientFiles(Environment.GetEnvironmentVariable(DirectoryClientFiles.TestEnvironmentVariable)!),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string? SkipReason()
    {
        string? client = Environment.GetEnvironmentVariable(DirectoryClientFiles.TestEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(client) || !Directory.Exists(client) || !DirectoryClientFiles.LooksExtracted(client))
        {
            return $"{DirectoryClientFiles.TestEnvironmentVariable} is not set to an extracted 1.12.1 client file tree.";
        }

        string? terrain = Environment.GetEnvironmentVariable(TerrainVariable);
        return string.IsNullOrWhiteSpace(terrain) || !Directory.Exists(Path.Combine(terrain, "vmaps"))
            ? $"{TerrainVariable} is not set to a terrain data root with vmaps/ (server-extracted from the 1.12.1 client)."
            : null;
    }

    public static DirectoryClientFiles Files => SharedFiles.Value;

    public static string VmapDirectory => Path.Combine(Environment.GetEnvironmentVariable(TerrainVariable)!, "vmaps");
}

/// <summary>
/// Test-only reader of ADT MDDF/MODF placements (the stand-in for the terrain lane's placement source), returning the raw
/// fields as <see cref="ClientPlacement"/> records.
/// </summary>
internal sealed class AdtPlacementReader(IClientFileSource files) : IClientPlacementSource
{
    // Map directories of the two continents. The paths are checked against the real files: a wrong name reads as a missing
    // ADT and fails the oracles' counts.
    private static readonly Dictionary<uint, string> MapDirectories = new() { [0] = "Azeroth", [1] = "Kalimdor" };

    /// <summary>Placements whose MMID/MWID offset named a different string than the vmap extractor's running name index.</summary>
    public int NameIndexDisagreements { get; private set; }

    public static string AdtPath(TileKey tile)
    {
        string directory = MapDirectories[tile.MapId];

        // ref: vmangos@0e3ff01/contrib/extractor/System.cpp:859-861 (World\Maps\<Map>\<Map>_<x>_<y>.adt is written as %03u%02u%02u.map with (map, y, x), so server tile (tx, ty) reads ADT <Map>_<ty>_<tx>)
        return $"World\\Maps\\{directory}\\{directory}_{tile.Y}_{tile.X}.adt";
    }

    public IReadOnlyList<ClientPlacement>? Placements(TileKey tile)
    {
        byte[]? data = files.Read(AdtPath(tile));
        if (data is null)
        {
            return null;
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/adtfile.cpp:125-131 and :239 (top-level chunks: reversed fourcc, u32 size, next chunk at the end of the payload)
        List<WowChunk> chunks = WowChunks.Scan(data, 0, data.Length);
        ReadOnlySpan<byte> mmdx = Find(data, chunks, "MMDX");
        ReadOnlySpan<byte> mmid = Find(data, chunks, "MMID");
        ReadOnlySpan<byte> mwmo = Find(data, chunks, "MWMO");
        ReadOnlySpan<byte> mwid = Find(data, chunks, "MWID");
        ReadOnlySpan<byte> mddf = Find(data, chunks, "MDDF");
        ReadOnlySpan<byte> modf = Find(data, chunks, "MODF");
        List<string> m2Names = Sequence(mmdx);
        List<string> wmoNames = Sequence(mwmo);
        var placements = new List<ClientPlacement>();

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/adtfile.cpp:205-216 (MDDF: 36-byte records, the first u32 indexes the model names)
        // ref: WoW-RE-Research/formats/terrain-adt.md:160-170 (u32 mmidIndex, u32 uniqueId, f32 pos[3], f32 rot[3], u16 scale (1024 = 1.0), u16 flags)
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:152-163 (id, position, rotation, u16 scale, u16 flags, in that order)
        for (int offset = 0; offset + 36 <= mddf.Length; offset += 36)
        {
            ReadOnlySpan<byte> record = mddf.Slice(offset, 36);
            uint nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(record);
            string name = Name(mmdx, mmid, m2Names, nameIndex);
            placements.Add(new ClientPlacement(
                PlacementKind.M2,
                name,
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                Point(record[8..]),
                Point(record[20..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[32..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[34..]),
                0,
                tile));
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/adtfile.cpp:218-229 (MODF: 64-byte records, the first u32 indexes the WMO names)
        // ref: WoW-RE-Research/formats/terrain-adt.md:172-185 (u32 mwidIndex, u32 uniqueId, f32 pos[3], f32 rot[3], f32 extents[6], u16 flags, u16 doodadSet, u16 nameSet, u16 padding)
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:544-558 (uniqueId, position, rotation, bounds min/max, u16 flags, u16 doodad set, u16 name set, u16 pad)
        for (int offset = 0; offset + 64 <= modf.Length; offset += 64)
        {
            ReadOnlySpan<byte> record = modf.Slice(offset, 64);
            uint nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(record);
            string name = Name(mwmo, mwid, wmoNames, nameIndex);
            placements.Add(new ClientPlacement(
                PlacementKind.Wmo,
                name,
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                Point(record[8..]),
                Point(record[20..]),
                1,
                BinaryPrimitives.ReadUInt16LittleEndian(record[56..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[58..]),
                tile));
        }

        return placements;
    }

    private static ReadOnlySpan<byte> Find(byte[] data, List<WowChunk> chunks, string tag)
    {
        foreach (WowChunk chunk in chunks)
        {
            if (chunk.Tag == tag)
            {
                return data.AsSpan(chunk.Offset, chunk.Size);
            }
        }

        return [];
    }

    // The vmap extractor indexes the n-th NUL-terminated name of the blob directly.
    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/adtfile.cpp:162-181 and :209-214 (names pushed in blob order; ModelInstanceNames[id])
    private static List<string> Sequence(ReadOnlySpan<byte> blob)
    {
        var names = new List<string>();
        int start = 0;
        while (start < blob.Length)
        {
            int nul = blob[start..].IndexOf((byte)0);
            int end = nul < 0 ? blob.Length : start + nul;
            names.Add(Encoding.Latin1.GetString(blob[start..end]));
            start = end + 1;
        }

        return names;
    }

    // The client resolves the index through the MMID/MWID offset table instead.
    // ref: WoW-RE-Research/formats/terrain-adt.md:148-154 (index -> MMID/MWID byte offset -> string in MMDX/MWMO)
    private string Name(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> offsets, List<string> sequence, uint index)
    {
        string? byOffset = null;
        if ((long)(index + 1) * 4 <= offsets.Length)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(offsets[(int)(index * 4)..]);
            byOffset = WowChunks.StringAt(blob, offset);
        }

        string? bySequence = index < sequence.Count ? sequence[(int)index] : null;
        if (byOffset != bySequence)
        {
            NameIndexDisagreements++;
        }

        return byOffset ?? bySequence ?? throw new InvalidDataException($"placement name index {index} resolves to no name");
    }

    private static WorldPoint Point(ReadOnlySpan<byte> span) => new(
        BinaryPrimitives.ReadSingleLittleEndian(span),
        BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(span[8..]));
}

/// <summary>One spawn record of a server <c>.vmtile</c> with the BIH slot that follows it.</summary>
internal sealed record ServerTileSpawn(ModelSpawn Spawn, uint Slot)
{
    /// <summary>The spawn's model name up to the first NUL (the part the server resolves a file from).</summary>
    public string ModelName => Spawn.Name.Split('\0')[0];
}

/// <summary>
/// Test-only reader of the server's <c>.vmtile</c> spawn list (the server's own parser is internal to ArcaneCore.Game). The
/// count of distinct slots is cross-checked against the server's <see cref="VMapManager"/> loading the same tile.
/// </summary>
internal static class ServerTileReader
{
    public static List<ServerTileSpawn>? Read(string vmapDirectory, TileKey tile)
    {
        string path = Path.Combine(vmapDirectory, VMapFormat.TileFileName(tile.MapId, tile.X, tile.Y));
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] data = File.ReadAllBytes(path);
        int position = 0;

        // ref: src/ArcaneCore.Game/Maps/Collision/VMaps/VMapTree.cs:63-76 ("VMAP_7.0", u32 count, count x {spawn record, u32 slot})
        Assert.Equal(VMapFormat.Magic, Encoding.ASCII.GetString(data, 0, 8));
        position += 8;
        uint count = U32(data, ref position);
        var spawns = new List<ServerTileSpawn>((int)count);
        for (uint i = 0; i < count; i++)
        {
            // ref: src/ArcaneCore.Game/Maps/Collision/VMaps/VMapFormat.cs:86-115 (u32 flags, u16 adt id, u32 id, f32[3] position, f32[3] rotation, f32 scale, bounds when MOD_HAS_BOUND, u32 name length, name)
            uint flags = U32(data, ref position);
            ushort adtId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            position += 2;
            uint id = U32(data, ref position);
            Vector3 spawnPosition = V3(data, ref position);
            Vector3 rotation = V3(data, ref position);
            float scale = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(position));
            position += 4;
            Vector3 low = Vector3.Zero;
            Vector3 high = Vector3.Zero;
            if ((flags & VMapFormat.ModHasBound) != 0)
            {
                low = V3(data, ref position);
                high = V3(data, ref position);
            }

            uint nameLength = U32(data, ref position);
            Assert.InRange(nameLength, 1u, (uint)VMapFormat.MaxNameLength);
            string name = Encoding.ASCII.GetString(data, position, (int)nameLength);
            position += (int)nameLength;
            uint slot = U32(data, ref position);
            spawns.Add(new ServerTileSpawn(new ModelSpawn(flags, adtId, id, spawnPosition, rotation, scale, low, high, name), slot));
        }

        Assert.Equal(data.Length, position);
        return spawns;
    }

    private static uint U32(byte[] data, ref int position)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
        position += 4;
        return value;
    }

    private static Vector3 V3(byte[] data, ref int position)
    {
        var value = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(position)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(position + 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(position + 8)));
        position += 12;
        return value;
    }
}

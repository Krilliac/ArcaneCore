using System.Buffers.Binary;
using System.Numerics;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>The MOHD chunk: counts, ambient colour, area id, bounds and flags of a WMO root.</summary>
public sealed record WmoHeader(
    uint TextureCount,
    uint GroupCount,
    uint PortalCount,
    uint LightCount,
    uint DoodadNameCount,
    uint DoodadDefinitionCount,
    uint DoodadSetCount,
    uint AmbientColor,
    uint WmoId,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    uint Flags);

/// <summary>One MOGI entry: the root's view of a group file.</summary>
/// <param name="NameOffset">Byte offset into the MOGN blob, or -1 for none.</param>
public sealed record WmoGroupInfo(uint Flags, Vector3 BoundsMin, Vector3 BoundsMax, int NameOffset);

/// <summary>One MODS entry: a named run of MODD doodads.</summary>
public sealed record WmoDoodadSet(string Name, uint FirstDoodad, uint DoodadCount);

/// <summary>One MODD entry: a doodad model placed in WMO-local space.</summary>
/// <param name="NameOffset">Byte offset (low 24 bits of the packed field) into the MODN blob.</param>
/// <param name="Flags">The high 8 bits of the packed field.</param>
/// <param name="ModelPath">The MODN string at <paramref name="NameOffset"/>, empty when the offset points outside MODN.</param>
/// <param name="Rotation">Orientation quaternion (x, y, z, w) in WMO-local space (not Euler angles).</param>
public sealed record WmoDoodad(uint NameOffset, byte Flags, string ModelPath, Vector3 Position, Quaternion Rotation, float Scale, uint Color);

/// <summary>
/// A parsed 1.12.1 WMO root file (<c>Building.wmo</c>): version, MOHD header, group table, group names, doodad sets,
/// doodad names and doodad placements. The root holds no triangles; those live in the group files
/// (<see cref="WmoGroupFile"/>). Instances are immutable after <see cref="Parse"/> and safe to share between threads.
/// </summary>
public sealed class WmoRootFile
{
    /// <summary>The WMO version of every 1.12.1 file.</summary>
    public const uint SupportedVersion = 17;

    private const int HeaderSize = 64;
    private const int GroupInfoSize = 32;
    private const int DoodadSetSize = 32;
    private const int DoodadSize = 40;

    private readonly byte[] _groupNames;

    private WmoRootFile(
        uint version,
        WmoHeader header,
        IReadOnlyList<WmoGroupInfo> groups,
        byte[] groupNames,
        IReadOnlyList<WmoDoodadSet> doodadSets,
        IReadOnlyList<WmoDoodad> doodads)
    {
        Version = version;
        Header = header;
        Groups = groups;
        _groupNames = groupNames;
        DoodadSets = doodadSets;
        Doodads = doodads;
    }

    public uint Version { get; }

    public WmoHeader Header { get; }

    /// <summary>MOGI entries (the file's count is chunk size / 32; it normally equals <see cref="WmoHeader.GroupCount"/>).</summary>
    public IReadOnlyList<WmoGroupInfo> Groups { get; }

    public IReadOnlyList<WmoDoodadSet> DoodadSets { get; }

    /// <summary>MODD entries (the count is chunk size / 40, not the header's nominal count).</summary>
    public IReadOnlyList<WmoDoodad> Doodads { get; }

    /// <summary>Approximate heap size, for the model cache.</summary>
    public long EstimatedBytes => 512 + _groupNames.Length + (Groups.Count * 64L) + (DoodadSets.Count * 96L) + (Doodads.Count * 160L);

    /// <summary>
    /// The MOGN string at a byte offset (a group's name), or null when the offset is outside the blob.
    /// </summary>
    public string? GroupNameAt(long offset)
        => WowChunks.StringAt(_groupNames, offset);

    /// <summary>
    /// <c>Dir\Building.wmo</c> → <c>Dir\Building_007.wmo</c>: the group file name for a zero-based group index.
    /// </summary>
    public static string GroupFilePath(string rootPath, int groupIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentOutOfRangeException.ThrowIfNegative(groupIndex);

        // ref: WoW-RE-Research/formats/wmo.md:200-203 (strip .wmo, append _%03u.wmo, index in [0, nGroups))
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/vmapexport.cpp:193-199 (temp[len-4]=0, "%s_%03d.wmo")
        string stem = rootPath.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) ? rootPath[..^4] : rootPath;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{stem}_{groupIndex:D3}.wmo");
    }

    /// <summary>Parses a root file. Throws <see cref="InvalidDataException"/> for an empty, truncated or unsupported file.</summary>
    public static WmoRootFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            throw new InvalidDataException("WMO root file is empty (0 bytes).");
        }

        List<WowChunk> chunks = WowChunks.Scan(data, 0, data.Length);
        uint version = 0;
        bool haveVersion = false;
        WmoHeader? header = null;
        byte[] groupNames = [];
        var groups = new List<WmoGroupInfo>();
        var sets = new List<WmoDoodadSet>();
        byte[] doodadNames = [];
        WowChunk? doodadChunk = null;

        foreach (WowChunk chunk in chunks)
        {
            switch (chunk.Tag)
            {
                case "MVER":
                    {
                        // ref: WoW-RE-Research/formats/wmo.md:58-60 (u32 version = 17)
                        // ref: Worldforge@1f73a08/src/wmo.cpp:41-42 (MVER -> u32 version)
                        ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO root");
                        if (payload.Length < 4)
                        {
                            throw new InvalidDataException("WMO root: MVER chunk is shorter than 4 bytes.");
                        }

                        version = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        haveVersion = true;
                        break;
                    }

                case "MOHD":
                    {
                        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:69-82 (nTextures, nGroups, nPortals, nLights, nDoodadNames, nDoodadDefs, nDoodadSets, color, RootWMOID, bbcorn1, bbcorn2, flags)
                        // ref: WoW-RE-Research/formats/wmo.md:62-76 (64-byte MOHD table)
                        // ref: Worldforge@1f73a08/src/wmo.cpp:43-55
                        ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO root");
                        if (payload.Length < HeaderSize)
                        {
                            throw new InvalidDataException($"WMO root: MOHD chunk is {payload.Length} bytes, expected at least {HeaderSize}.");
                        }

                        header = new WmoHeader(
                            BinaryPrimitives.ReadUInt32LittleEndian(payload),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[24..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[28..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[32..]),
                            ReadVector(payload[36..]),
                            ReadVector(payload[48..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[60..]));
                        break;
                    }

                case "MOGN":
                    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:112-116 (GroupNames = raw blob)
                    // ref: WoW-RE-Research/formats/wmo.md:92-93
                    groupNames = WowChunks.Required(data, chunk, "WMO root").ToArray();
                    break;

                case "MOGI":
                    {
                        // ref: WoW-RE-Research/formats/wmo.md:95-101 (32 bytes: flags, bboxMin, bboxMax, i32 nameOffset)
                        // ref: Worldforge@1f73a08/src/wmo.cpp:70-78
                        ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO root");
                        groups.Clear();
                        for (int offset = 0; offset + GroupInfoSize <= payload.Length; offset += GroupInfoSize)
                        {
                            groups.Add(new WmoGroupInfo(
                                BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]),
                                ReadVector(payload[(offset + 4)..]),
                                ReadVector(payload[(offset + 16)..]),
                                BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 28)..])));
                        }

                        break;
                    }

                case "MODS":
                    {
                        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.h:50-56 (char Name[20], u32 StartIndex, u32 Count, 4 pad bytes)
                        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:84-88
                        // ref: WoW-RE-Research/formats/wmo.md:116-124
                        ReadOnlySpan<byte> payload = WowChunks.Required(data, chunk, "WMO root");
                        sets.Clear();
                        for (int offset = 0; offset + DoodadSetSize <= payload.Length; offset += DoodadSetSize)
                        {
                            ReadOnlySpan<byte> name = payload.Slice(offset, 20);
                            int nul = name.IndexOf((byte)0);
                            sets.Add(new WmoDoodadSet(
                                System.Text.Encoding.Latin1.GetString(nul < 0 ? name : name[..nul]),
                                BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 20)..]),
                                BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 24)..])));
                        }

                        break;
                    }

                case "MODN":
                    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:89-106 (blob of NUL-terminated paths, indexed by byte offset)
                    // ref: WoW-RE-Research/formats/wmo.md:126-127
                    doodadNames = WowChunks.Required(data, chunk, "WMO root").ToArray();
                    break;

                case "MODD":
                    // Parsed after the loop so MODN may come in either order.
                    doodadChunk = chunk;
                    break;
            }
        }

        if (!haveVersion)
        {
            throw new InvalidDataException("WMO root: no MVER chunk.");
        }

        if (version != SupportedVersion)
        {
            // ref: Worldforge@1f73a08/src/wmo.cpp:173-177 (a version newer than the profile's is refused, not mis-parsed)
            throw new InvalidDataException($"WMO root: version {version} is not the 1.12.1 version {SupportedVersion}.");
        }

        if (header is null)
        {
            throw new InvalidDataException("WMO root: no MOHD chunk.");
        }

        var doodads = new List<WmoDoodad>();
        if (doodadChunk is { } md)
        {
            // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.h:58-65 (u32 NameIndex:24, Vec3D Position, Quaternion Rotation, float Scale, u32 Color)
            // ref: WoW-RE-Research/formats/wmo.md:129-139 (40 bytes; count = chunk size / 40; quaternion x, y, z, w)
            // ref: Worldforge@1f73a08/src/wmo.cpp:108-119
            ReadOnlySpan<byte> payload = WowChunks.Required(data, md, "WMO root");
            for (int offset = 0; offset + DoodadSize <= payload.Length; offset += DoodadSize)
            {
                uint packed = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
                uint nameOffset = packed & 0x00FFFFFF;
                doodads.Add(new WmoDoodad(
                    nameOffset,
                    (byte)(packed >> 24),
                    WowChunks.StringAt(doodadNames, nameOffset) ?? string.Empty,
                    ReadVector(payload[(offset + 4)..]),
                    new Quaternion(
                        ReadSingle(payload[(offset + 16)..]),
                        ReadSingle(payload[(offset + 20)..]),
                        ReadSingle(payload[(offset + 24)..]),
                        ReadSingle(payload[(offset + 28)..])),
                    ReadSingle(payload[(offset + 32)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 36)..])));
            }
        }

        return new WmoRootFile(version, header, groups, groupNames, sets, doodads);
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> span)
        => new(ReadSingle(span), ReadSingle(span[4..]), ReadSingle(span[8..]));

    private static float ReadSingle(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadSingleLittleEndian(span);
}

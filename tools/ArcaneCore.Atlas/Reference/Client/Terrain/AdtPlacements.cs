using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// One MDDF entry: an M2 doodad as stored in an ADT. <see cref="Position"/> and <see cref="Rotation"/> are the raw file
/// fields (placement space, rotation in degrees), not world coordinates; the model lanes own that transform.
/// </summary>
/// <param name="NameIndex">Index into the ADT's MMID table.</param>
/// <param name="ModelPath">The path MMID/MMDX resolve to, exactly as stored (often a legacy .mdx name).</param>
/// <param name="UniqueId">Instance id (the same object listed in two ADTs shares it).</param>
/// <param name="Position">Raw position.</param>
/// <param name="Rotation">Raw rotation in degrees.</param>
/// <param name="ScaleRaw">Fixed-point scale; 1024 is 1.0.</param>
/// <param name="Flags">Raw flag word.</param>
public sealed record AdtDoodad(
    uint NameIndex,
    string ModelPath,
    uint UniqueId,
    WorldPoint Position,
    WorldPoint Rotation,
    ushort ScaleRaw,
    ushort Flags)
{
    /// <summary>The scale as a factor (<see cref="ScaleRaw"/> / 1024).</summary>
    public double Scale => ScaleRaw / 1024.0;
}

/// <summary>
/// One MODF entry: a WMO as stored in an ADT or in a WDT. <see cref="Position"/> and <see cref="Rotation"/> are the raw
/// file fields (placement space, rotation in degrees); <see cref="BoundsMin"/>/<see cref="BoundsMax"/> are the two
/// stored extent vectors in the same space.
/// </summary>
/// <param name="NameIndex">Index into the ADT's MWID table, or the ordinal of the name in a WDT's MWMO.</param>
/// <param name="ModelPath">The WMO path as stored.</param>
/// <param name="UniqueId">Instance id.</param>
/// <param name="Position">Raw position.</param>
/// <param name="Rotation">Raw rotation in degrees.</param>
/// <param name="BoundsMin">First stored extent vector, exactly as stored (the on-disk order is documented inconsistently in <c>terrain-adt.md</c> and no code here assumes it; <c>ClientTerrainRealDataTests.Placements_ResolveRealModelPaths</c> verifies <c>BoundsMin &lt;= BoundsMax</c> componentwise across all 4,871 MODF entries in the 1.12.1 WDT and ADT files).</param>
/// <param name="BoundsMax">Second stored extent vector.</param>
/// <param name="Flags">Raw flag word (bit 0 marks a destructible WMO).</param>
/// <param name="DoodadSet">Doodad set index.</param>
/// <param name="NameSet">Name set index.</param>
public sealed record AdtWmoObject(
    uint NameIndex,
    string ModelPath,
    uint UniqueId,
    WorldPoint Position,
    WorldPoint Rotation,
    WorldPoint BoundsMin,
    WorldPoint BoundsMax,
    ushort Flags,
    ushort DoodadSet,
    ushort NameSet);

/// <summary>Readers for the two placement tables.</summary>
internal static class AdtPlacements
{
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/adtfile.cpp:209 (MDDF entries are size / 36)
    public const int DoodadBytes = 36;

    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/adtfile.cpp:222 and wdtfile.cpp:97 (MODF entries are size / 64)
    public const int WmoBytes = 64;

    /// <summary>
    /// MDDF: u32 name index, u32 unique id, f32[3] position, f32[3] rotation, u16 scale (1024 = 1.0), u16 flags.
    /// </summary>
    /// <param name="mddf">The MDDF payload.</param>
    /// <param name="paths">The resolved model path for every name index.</param>
    /// <param name="context">Prefix for error messages.</param>
    public static List<AdtDoodad> ReadDoodads(ReadOnlySpan<byte> mddf, IReadOnlyList<string> paths, string context)
    {
        if (mddf.Length % DoodadBytes != 0)
        {
            throw new InvalidDataException($"{context}: MDDF is {mddf.Length} bytes, not a whole number of {DoodadBytes}-byte entries.");
        }

        var result = new List<AdtDoodad>(mddf.Length / DoodadBytes);
        for (int offset = 0; offset < mddf.Length; offset += DoodadBytes)
        {
            // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/model.cpp:155 id, :156-157 position, :158-159 rotation,
            // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/model.cpp:162 scale (u16), :163 flags (u16), :166 scale / 1024
            // ref: WoW-RE-Research formats/terrain-adt.md:160-170 (MDDF layout)
            uint index = ClientChunks.U32(mddf, offset);
            if (index >= (uint)paths.Count)
            {
                throw new InvalidDataException($"{context}: MDDF entry {offset / DoodadBytes} names model {index} but the name table has {paths.Count}.");
            }

            result.Add(new AdtDoodad(
                index,
                paths[(int)index],
                ClientChunks.U32(mddf, offset + 4),
                ClientChunks.Vec3(mddf, offset + 8),
                ClientChunks.Vec3(mddf, offset + 20),
                ClientChunks.U16(mddf, offset + 32),
                ClientChunks.U16(mddf, offset + 34)));
        }

        return result;
    }

    /// <summary>
    /// MODF: u32 name index, u32 unique id, f32[3] position, f32[3] rotation, f32[3] first extent, f32[3] second extent,
    /// u16 flags, u16 doodad set, u16 name set, u16 padding.
    /// </summary>
    /// <param name="modf">The MODF payload.</param>
    /// <param name="paths">The resolved WMO path for every name index.</param>
    /// <param name="context">Prefix for error messages.</param>
    public static List<AdtWmoObject> ReadWmoObjects(ReadOnlySpan<byte> modf, IReadOnlyList<string> paths, string context)
    {
        if (modf.Length % WmoBytes != 0)
        {
            throw new InvalidDataException($"{context}: MODF is {modf.Length} bytes, not a whole number of {WmoBytes}-byte entries.");
        }

        var result = new List<AdtWmoObject>(modf.Length / WmoBytes);
        for (int offset = 0; offset < modf.Length; offset += WmoBytes)
        {
            // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:544 unique id, :545-546 position, :547-548 rotation,
            // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:549-550 bounds.min, :551-552 bounds.max, :553 flags,
            // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wmo.cpp:554 doodad set, :556-557 name set and padding
            // ref: WoW-RE-Research formats/terrain-adt.md:172-190 (MODF layout; extent order flagged as unconfirmed there)
            uint index = ClientChunks.U32(modf, offset);
            if (index >= (uint)paths.Count)
            {
                throw new InvalidDataException($"{context}: MODF entry {offset / WmoBytes} names WMO {index} but the name table has {paths.Count}.");
            }

            result.Add(new AdtWmoObject(
                index,
                paths[(int)index],
                ClientChunks.U32(modf, offset + 4),
                ClientChunks.Vec3(modf, offset + 8),
                ClientChunks.Vec3(modf, offset + 20),
                ClientChunks.Vec3(modf, offset + 32),
                ClientChunks.Vec3(modf, offset + 44),
                ClientChunks.U16(modf, offset + 56),
                ClientChunks.U16(modf, offset + 58),
                ClientChunks.U16(modf, offset + 60)));
        }

        return result;
    }
}

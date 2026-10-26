using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision.MMaps;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// Reads a vmangos <c>NNN.mmap</c> (one raw <c>dtNavMeshParams</c>) without the server's reader, so the reference view does not
/// inherit a server parsing defect. The server's <see cref="NavMeshParams"/> record is only the carrier of the five values.
/// </summary>
/// <remarks>Stateless and thread-safe.</remarks>
public static class MmapParamsReader
{
    /// <summary>Size of <c>dtNavMeshParams</c> in bytes.</summary>
    // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:339-346 (float orig[3], float tileWidth,
    // float tileHeight, int maxTiles, int maxPolys: seven four-byte fields, no padding).
    public const int ByteSize = 28;

    /// <summary>The most tiles one map can have: the generator makes one Detour tile per terrain tile of the 64 x 64 grid.</summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:450 (maxTiles = tiles.size(), the map's terrain tiles).
    public const int MaxTilesPerMap = 64 * 64;

    /// <summary>Parses the whole <c>.mmap</c> file.</summary>
    /// <exception cref="InvalidDataException">The file is not a usable parameter block; the message says why.</exception>
    public static NavMeshParams Parse(ReadOnlySpan<byte> file)
    {
        // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:487 (the generator fwrites the struct raw) and
        // vmangos@0e3ff01 src/game/Maps/MoveMap.cpp:86-87 (the server freads sizeof(dtNavMeshParams) raw), so the file is the
        // struct and nothing else; the extractor ran on little-endian x86.
        if (file.Length != ByteSize)
        {
            throw new InvalidDataException($"navmesh parameters are {file.Length} bytes; a dtNavMeshParams is {ByteSize} bytes");
        }

        var origin = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(file),
            BinaryPrimitives.ReadSingleLittleEndian(file[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(file[8..]));
        float tileWidth = BinaryPrimitives.ReadSingleLittleEndian(file[12..]);
        float tileHeight = BinaryPrimitives.ReadSingleLittleEndian(file[16..]);
        int maxTiles = BinaryPrimitives.ReadInt32LittleEndian(file[20..]);
        int maxPolys = BinaryPrimitives.ReadInt32LittleEndian(file[24..]);
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
        {
            throw new InvalidDataException("navmesh origin is not finite");
        }

        if (!(tileWidth > 0) || !float.IsFinite(tileWidth) || !(tileHeight > 0) || !float.IsFinite(tileHeight))
        {
            throw new InvalidDataException($"navmesh tile size {tileWidth} x {tileHeight} is not a positive finite size");
        }

        if (maxTiles <= 0 || maxTiles > MaxTilesPerMap)
        {
            throw new InvalidDataException($"navmesh maxTiles {maxTiles} is outside 1..{MaxTilesPerMap}");
        }

        // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:462 (maxPolys = 0, unused with 64-bit polygon references).
        if (maxPolys < 0)
        {
            throw new InvalidDataException($"navmesh maxPolys {maxPolys} is negative");
        }

        return new NavMeshParams(origin, tileWidth, tileHeight, maxTiles, maxPolys);
    }
}

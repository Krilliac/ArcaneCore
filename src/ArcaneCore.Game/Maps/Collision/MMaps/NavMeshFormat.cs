using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.MMaps;

/// <summary>
/// Conventions of the cMaNGOS / vmangos mmap files (navmeshes built by their Recast-based
/// <c>MoveMapGen</c>), implemented from the format description (docs/integration/vmap-los.md).
/// <para>
/// <c>NNN.mmap</c> holds the map's Detour navmesh parameters; <c>NNNXXYY.mmtile</c> holds one
/// tile: an <see cref="MmapTileHeader"/> followed by a Detour tile (<see cref="NavMeshTile"/>).
/// Detour works in Recast space, whose axes are (world Y, world Z, world X): Y is up.
/// </para>
/// </summary>
public static class NavMeshFormat
{
    /// <summary><c>MMAP_MAGIC</c>: "MMAP" read as a little-endian u32.</summary>
    public const uint MmapMagic = 0x4d4d4150;

    /// <summary>Detour navmesh data version the generator writes (<c>DT_NAVMESH_VERSION</c>).</summary>
    public const uint DetourVersion = 7;

    /// <summary>The generator's own format version (<c>MMAP_VERSION</c>).</summary>
    public const uint MmapVersion = 6;

    /// <summary><c>DT_NAVMESH_MAGIC</c>: 'D' 'N' 'A' 'V' packed big-end first into an int.</summary>
    public const int DetourMagic = ('D' << 24) | ('N' << 16) | ('A' << 8) | 'V';

    /// <summary><c>DT_VERTS_PER_POLYGON</c>.</summary>
    public const int MaxVertsPerPoly = 6;

    /// <summary><c>DT_EXT_LINK</c>: a neighbour entry that crosses into the adjacent tile (low byte: side).</summary>
    public const ushort ExternalLink = 0x8000;

    /// <summary><c>DT_POLYTYPE_OFFMESH_CONNECTION</c>.</summary>
    public const int PolyTypeOffMeshConnection = 1;

    /// <summary><c>NNN.mmap</c>.</summary>
    public static string ParamsFileName(uint mapId) => $"{mapId:D3}.mmap";

    /// <summary>
    /// <c>NNNXXYY.mmtile</c> for terrain tile (<paramref name="tileX"/>, <paramref name="tileY"/>):
    /// the same index order as the terrain <c>.map</c> files (<see cref="Terrain.TerrainTile.FileName"/>).
    /// </summary>
    public static string TileFileName(uint mapId, int tileX, int tileY) => $"{mapId:D3}{tileX:D2}{tileY:D2}.mmtile";

    /// <summary>World position → Recast position.</summary>
    public static Vector3 ToRecast(Vector3 world) => new(world.Y, world.Z, world.X);

    /// <summary>Recast position → world position.</summary>
    public static Vector3 ToWorld(Vector3 recast) => new(recast.Z, recast.X, recast.Y);

    /// <summary>The adjacent Detour tile across a border side (0: +x, 2: +z, 4: −x, 6: −z; diagonals have none).</summary>
    public static (int Dx, int Dy)? SideOffset(int side) => side switch
    {
        0 => (1, 0),
        2 => (0, 1),
        4 => (-1, 0),
        6 => (0, -1),
        _ => null,
    };
}

/// <summary><c>dtNavMeshParams</c> from <c>NNN.mmap</c>: f32[3] origin, f32 tile width, f32 tile height, i32 max tiles, i32 max polys.</summary>
public readonly record struct NavMeshParams(Vector3 Origin, float TileWidth, float TileHeight, int MaxTiles, int MaxPolys)
{
    public const int Size = 28;

    public static NavMeshParams Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size)
        {
            throw new InvalidDataException($"navmesh parameters are {Size} bytes, not {data.Length}");
        }

        var reader = new CollisionDataReader(data);
        var result = new NavMeshParams(reader.ReadVector3(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadInt32(), reader.ReadInt32());
        if (!float.IsFinite(result.Origin.X) || !float.IsFinite(result.Origin.Y) || !float.IsFinite(result.Origin.Z)
            || !(result.TileWidth > 0) || !(result.TileHeight > 0) || result.MaxTiles <= 0 || result.MaxPolys <= 0)
        {
            throw new InvalidDataException("navmesh parameters are out of range");
        }

        return result;
    }
}

/// <summary>The generator's tile prefix: u32 magic, u32 Detour version, u32 mmap version, u32 data size, u32 uses-liquids.</summary>
public readonly record struct MmapTileHeader(uint Magic, uint DetourVersion, uint MmapVersion, uint Size, bool UsesLiquids)
{
    public const int ByteSize = 20;

    internal static MmapTileHeader Read(ref CollisionDataReader reader)
        => new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), (reader.ReadUInt32() & 1) != 0);
}

using System.Buffers.Binary;
using ArcaneCore.Atlas.Core;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Server;

/// <summary>
/// Writes a synthetic <c>.map</c> file in the layout <c>TerrainTile.Parse</c> reads, so the server-view tests run on data built here at
/// runtime and never on client data. Only the fields the tests set are written; everything else is the reader's "absent" form.
/// </summary>
internal sealed class SyntheticMapFile
{
    // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:38 "MAPS" / :39 "z1.4" (vmangos vmangos@0e3ff01 src/game/Maps/GridMap.cpp:35-36)
    private const uint MapMagic = 0x5350414D;
    private const uint VersionMagic = 0x342E317A;

    // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:40-42 section magics (GridMap.cpp:37-39 "AREA", "MHGT", "MLIQ")
    private const uint AreaMagic = 0x41455241;
    private const uint HeightMagic = 0x5447484D;
    private const uint LiquidMagic = 0x51494C4D;

    /// <summary>The area of the whole tile when <see cref="AreaFlags"/> is null.</summary>
    public ushort GridArea { get; set; }

    /// <summary>16 x 16 area flags (row from world X, column from world Y), or null for one area per tile.</summary>
    public ushort[]? AreaFlags { get; set; }

    /// <summary>The grid height (a flat tile's only height).</summary>
    public float GridHeight { get; set; }

    /// <summary>The top of the packed range (u16 samples scale onto GridHeight..GridMaxHeight).</summary>
    public float GridMaxHeight { get; set; }

    /// <summary>129 x 129 float corner heights, or null.</summary>
    public float[]? V9 { get; set; }

    /// <summary>128 x 128 float centre heights, or null.</summary>
    public float[]? V8 { get; set; }

    /// <summary>129 x 129 u16 corner samples (packed storage), or null.</summary>
    public ushort[]? V9Packed { get; set; }

    /// <summary>128 x 128 u16 centre samples (packed storage), or null.</summary>
    public ushort[]? V8Packed { get; set; }

    /// <summary>16 x 16 hole masks, or null for none.</summary>
    public ushort[]? Holes { get; set; }

    /// <summary>Per cell liquid type bytes (16 x 16), or null for no liquid section.</summary>
    public byte[]? LiquidTypes { get; set; }

    /// <summary>The level of every liquid square (written as the global level, no height map).</summary>
    public float LiquidLevel { get; set; }

    /// <summary>The file bytes.</summary>
    public byte[] Build()
    {
        var area = new List<byte>();
        U32(area, AreaMagic);

        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:44 MapAreaNoArea = 1, :105-110 flags, grid area, then 16x16 u16
        U16(area, (ushort)(AreaFlags is null ? 1 : 0));
        U16(area, GridArea);
        foreach (ushort a in AreaFlags ?? [])
        {
            U16(area, a);
        }

        var height = new List<byte>();
        U32(height, HeightMagic);

        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:45-47 NoHeight 1, AsInt16 2, AsInt8 4; :125-150 flags, grid height, max, V9 then V8
        uint heightFlags = V9 is not null ? 0u : V9Packed is not null ? 2u : 1u;
        U32(height, heightFlags);
        F32(height, GridHeight);
        F32(height, GridMaxHeight);
        foreach (float f in V9 ?? [])
        {
            F32(height, f);
        }

        foreach (float f in V8 ?? [])
        {
            F32(height, f);
        }

        foreach (ushort s in V9Packed ?? [])
        {
            U16(height, s);
        }

        foreach (ushort s in V8Packed ?? [])
        {
            U16(height, s);
        }

        var liquid = new List<byte>();
        if (LiquidTypes is not null)
        {
            U32(liquid, LiquidMagic);

            // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:48-49 NoType 1, NoHeight 2; :163-181 flags, global type, global entry,
            // offX, offY, width, height, level, then 16x16 u16 entries and 16x16 u8 types when typed
            liquid.Add(0x02);
            liquid.Add(0);
            U16(liquid, 0);
            liquid.Add(0);
            liquid.Add(0);
            liquid.Add(128);
            liquid.Add(128);
            F32(liquid, LiquidLevel);
            for (int i = 0; i < 256; i++)
            {
                U16(liquid, (ushort)(LiquidTypes[i] == 0 ? 0 : 1));
            }

            liquid.AddRange(LiquidTypes);
        }

        var holes = new List<byte>();
        foreach (ushort h in Holes ?? new ushort[256])
        {
            U16(holes, h);
        }

        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:458-468 40-byte header: magic, version, then offset/size of area, height, liquid, holes
        uint areaOffset = 40;
        uint heightOffset = areaOffset + (uint)area.Count;
        uint liquidOffset = heightOffset + (uint)height.Count;
        uint holesOffset = liquidOffset + (uint)liquid.Count;
        var file = new List<byte>();
        U32(file, MapMagic);
        U32(file, VersionMagic);
        U32(file, areaOffset);
        U32(file, (uint)area.Count);
        U32(file, heightOffset);
        U32(file, (uint)height.Count);
        U32(file, liquid.Count == 0 ? 0 : liquidOffset);
        U32(file, (uint)liquid.Count);
        U32(file, holesOffset);
        U32(file, (uint)holes.Count);
        file.AddRange(area);
        file.AddRange(height);
        file.AddRange(liquid);
        file.AddRange(holes);
        return [.. file];
    }

    /// <summary>Fills an n x n grid with <paramref name="value"/>(row, col).</summary>
    public static float[] Grid(int n, Func<int, int, float> value)
    {
        var a = new float[n * n];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                a[(r * n) + c] = value(r, c);
            }
        }

        return a;
    }

    private static void U16(List<byte> list, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        list.AddRange(b);
    }

    private static void U32(List<byte> list, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        list.AddRange(b);
    }

    private static void F32(List<byte> list, float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        list.AddRange(b);
    }
}

/// <summary>A temporary terrain data root (maps/, vmaps/, mmaps/) deleted on dispose.</summary>
internal sealed class TempTerrainRoot : IDisposable
{
    public TempTerrainRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "atlas-server-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "maps"));
        Directory.CreateDirectory(Path.Combine(Root, "vmaps"));
        Directory.CreateDirectory(Path.Combine(Root, "mmaps"));
    }

    public string Root { get; }

    public AtlasPaths Paths => new(Root, null);

    /// <summary>Writes <c>maps/{map:D3}{tx:D2}{ty:D2}.map</c>.</summary>
    public void WriteMap(uint mapId, int tileX, int tileY, byte[] bytes)
        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:303 file name {map:D3}{x:D2}{y:D2}.map
        => File.WriteAllBytes(Path.Combine(Root, "maps", $"{mapId:D3}{tileX:D2}{tileY:D2}.map"), bytes);

    /// <summary>Writes an arbitrary file under the root.</summary>
    public void WriteRaw(string relative, byte[] bytes) => File.WriteAllBytes(Path.Combine(Root, relative), bytes);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A reader still holding a handle on Windows: the temp directory is cleaned by the OS later.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}

/// <summary>
/// A fact that runs only when <c>ARCANECORE_TEST_TERRAIN_DIR</c> names a terrain data root with <c>maps/</c>; otherwise it is a visible skip.
/// </summary>
public sealed class AtlasServerRealTerrainFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_TERRAIN_DIR";

    public AtlasServerRealTerrainFactAttribute()
    {
        if (AtlasServerRealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class AtlasServerRealTerrain
{
    public static string Root => Environment.GetEnvironmentVariable(AtlasServerRealTerrainFactAttribute.Variable)!;

    public static string? SkipReason()
    {
        string? dir = Environment.GetEnvironmentVariable(AtlasServerRealTerrainFactAttribute.Variable);
        return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "maps"))
            ? $"{AtlasServerRealTerrainFactAttribute.Variable} is not set to a terrain data root (maps/, vmaps/, mmaps/ from the 1.12.1 client)."
            : null;
    }
}

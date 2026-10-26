using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// Where the client keeps a map's terrain, and how those files line up with the server's tile indices.
/// </summary>
/// <remarks>
/// <para>
/// A map's folder is the <c>Directory</c> column of Map.dbc. The WDT is <c>World\Maps\&lt;Directory&gt;\&lt;Directory&gt;.wdt</c>
/// and the ADT of server tile (X, Y) is <c>World\Maps\&lt;Directory&gt;\&lt;Directory&gt;_&lt;Y&gt;_&lt;X&gt;.adt</c>: the first
/// number in the file name is the server tile Y (it runs west, like world Y) and the second is the server tile X (it
/// runs north, like world X). The extractor reads <c>&lt;Map&gt;_&lt;x&gt;_&lt;y&gt;.adt</c> and writes <c>%03u%02u%02u.map</c> of
/// (map id, y, x), so the .map name <c>0004931.map</c> (tile X 49, tile Y 31) comes from <c>Azeroth_31_49.adt</c>.
/// </para>
/// <para>
/// Proof on real files (Atlas tests): the heights of <c>Azeroth_31_49.adt</c> equal the server's <c>0004931.map</c> for
/// every sample, the transposed file does not, and each MCNK header position equals the position computed from this
/// naming for every chunk of every ADT.
/// </para>
/// </remarks>
public static class ClientMapPaths
{
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:845 (World\Maps\<name>\<name>.wdt)
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:853-861 (loop y then x; file _<x>_<y>.adt; output (map id, y, x))
    // ref: vmangos@0e3ff01 contrib/vmap_extractor/vmapextract/wdtfile.cpp:132 (the same World\Maps\<name>\<name>_<x>_<z>.adt pattern)
    // ref: WoW-RE-Research formats/terrain-adt.md:372-386 (first index = west, second index = north)
    // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:215 (server .map name: map, tile X, tile Y)
    private const string MapsRoot = "World\\Maps";

    /// <summary>The in-archive path of a map's WDT.</summary>
    public static string WdtPath(string directory)
    {
        CheckDirectory(directory);
        return $"{MapsRoot}\\{directory}\\{directory}.wdt";
    }

    /// <summary>The in-archive path of the ADT for server tile <paramref name="tile"/> (X = second number, Y = first number).</summary>
    public static string AdtPath(string directory, TileKey tile)
    {
        CheckDirectory(directory);
        if (!tile.IsInGrid)
        {
            throw new ArgumentException($"Tile {tile} is not inside the 64x64 grid, so it has no ADT.", nameof(tile));
        }

        return $"{MapsRoot}\\{directory}\\{directory}_{tile.Y}_{tile.X}.adt";
    }

    /// <summary>The server tile an ADT named <c>&lt;Directory&gt;_&lt;first&gt;_&lt;second&gt;.adt</c> belongs to.</summary>
    public static TileKey TileOfAdt(uint mapId, int first, int second) => new(mapId, second, first);

    private static void CheckDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (directory.AsSpan().IndexOfAny('\\', '/', ':') >= 0 || directory is "." or "..")
        {
            throw new ArgumentException($"Map directory '{directory}' is not a single folder name.", nameof(directory));
        }
    }
}

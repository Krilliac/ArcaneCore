using System.Collections.Concurrent;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Extract;

/// <summary>What happened to one map during a <see cref="MapExtractor"/> run.</summary>
/// <param name="Id">Map.dbc id.</param>
/// <param name="Directory">The map's folder under <c>World\Maps</c>.</param>
/// <param name="HasWdt">False when the client has no WDT for the map (it has no terrain of its own; nothing is written).</param>
/// <param name="Listed">Tiles the WDT lists.</param>
/// <param name="Written">Tiles written in this run.</param>
/// <param name="Existing">Tiles left alone because their file was already there.</param>
/// <param name="Absent">Tiles the WDT lists whose ADT is not in the client files.</param>
/// <param name="Failed">Tiles whose ADT could not be read or written.</param>
/// <param name="Bytes">Bytes written in this run.</param>
public sealed record MapExtractMapReport(uint Id, string Directory, bool HasWdt, int Listed, int Written, int Existing, int Absent, int Failed, long Bytes);

/// <summary>One tile (or one map's WDT, with tile -1, -1) a run could not extract.</summary>
/// <param name="Path">The client file.</param>
/// <param name="Reason">Why it was refused.</param>
public sealed record MapExtractFailure(uint Map, int TileX, int TileY, string Path, string Reason);

/// <summary>The outcome of a <see cref="MapExtractor"/> run.</summary>
/// <param name="MapsDirectory">The folder the <c>.map</c> files are in.</param>
/// <param name="Maps">Per map, in map id order.</param>
/// <param name="Failures">Every file that was refused, in map then tile order.</param>
public sealed record MapExtractReport(string MapsDirectory, IReadOnlyList<MapExtractMapReport> Maps, IReadOnlyList<MapExtractFailure> Failures)
{
    /// <summary>Tiles written over all maps.</summary>
    public int Written => Maps.Sum(m => m.Written);

    /// <summary>Tiles left alone because their file was already there.</summary>
    public int Existing => Maps.Sum(m => m.Existing);

    /// <summary>Tiles the WDTs list whose ADT is not in the client files.</summary>
    public int Absent => Maps.Sum(m => m.Absent);

    /// <summary>Bytes written over all maps.</summary>
    public long Bytes => Maps.Sum(m => m.Bytes);
}

/// <summary>
/// Writes the server's terrain files (<c>maps/MMMXXYY.map</c>) from a 1.12.1 client: for every map of Map.dbc that has a
/// WDT, every tile the WDT lists is read from its ADT and encoded by <see cref="MapTileEncoder"/>. The output is what the
/// reference map extractor writes with its default settings and is read by the world server's
/// <see cref="TerrainTile"/>. Tiles are independent, so they are converted in parallel.
/// </summary>
/// <remarks>
/// Differences from the reference tool, none of which change a written file: a tile whose ADT fails validation is
/// reported and not written (the reference trusts the file), and an existing output file is kept unless overwriting is
/// asked for (the reference always rewrites).
/// </remarks>
public sealed class MapExtractor
{
    /// <summary>The folder under the output root that holds the files, as the server expects it.</summary>
    public const string MapsFolder = "maps";

    private readonly IClientFileSource _files;
    private readonly IMapDirectory _maps;
    private readonly AreaExploreFlags _areas;

    /// <param name="files">The client files.</param>
    /// <param name="maps">The client's Map.dbc.</param>
    /// <param name="areas">The client's AreaTable.dbc explore flags.</param>
    public MapExtractor(IClientFileSource files, IMapDirectory maps, AreaExploreFlags areas)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _areas = areas ?? throw new ArgumentNullException(nameof(areas));
    }

    /// <summary>Extracts into <c>&lt;outputRoot&gt;/maps</c>.</summary>
    /// <param name="outputRoot">The server data root (the folder the world server's <c>Maps:DataDirectory</c> names).</param>
    /// <param name="onlyMaps">Map ids to extract; null or empty extracts every map.</param>
    /// <param name="overwrite">Rewrite tiles whose file exists; otherwise they are kept and counted as existing.</param>
    /// <param name="threads">Tiles converted at once (at least 1).</param>
    /// <exception cref="ArgumentException">A requested map id is not in Map.dbc.</exception>
    public MapExtractReport Extract(string outputRoot, IReadOnlyCollection<uint>? onlyMaps, bool overwrite, int threads, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);

        var selected = new List<ClientMap>();
        if (onlyMaps is { Count: > 0 })
        {
            foreach (uint id in onlyMaps.Distinct().Order())
            {
                selected.Add(_maps.TryGetMap(id, out ClientMap map) ? map : throw new ArgumentException($"Map {id} is not in the client's Map.dbc.", nameof(onlyMaps)));
            }
        }
        else
        {
            selected.AddRange(_maps.Maps.OrderBy(m => m.Id));
        }

        string mapsDirectory = Path.Combine(outputRoot, MapsFolder);
        Directory.CreateDirectory(mapsDirectory);

        var reports = new List<MapExtractMapReport>(selected.Count);
        var failures = new List<MapExtractFailure>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken };
        foreach (ClientMap map in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reports.Add(ExtractMap(map, mapsDirectory, overwrite, options, failures));
        }

        return new MapExtractReport(mapsDirectory, reports, failures);
    }

    private MapExtractMapReport ExtractMap(ClientMap map, string mapsDirectory, bool overwrite, ParallelOptions options, List<MapExtractFailure> failures)
    {
        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:845-851 (a map without a WDT is skipped without a message)
        string wdtPath = ClientMapPaths.WdtPath(map.Directory);
        WdtFile wdt;
        try
        {
            byte[]? bytes = _files.Read(wdtPath);
            if (bytes is null)
            {
                return new MapExtractMapReport(map.Id, map.Directory, HasWdt: false, 0, 0, 0, 0, 0, 0);
            }

            wdt = WdtFile.Parse(bytes);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            failures.Add(new MapExtractFailure(map.Id, -1, -1, wdtPath, e.Message));
            return new MapExtractMapReport(map.Id, map.Directory, HasWdt: true, 0, 0, 0, 0, Failed: 1, 0);
        }

        IReadOnlyList<TileKey> tiles = wdt.Tiles(map.Id);
        int written = 0;
        int existing = 0;
        int absent = 0;
        long totalBytes = 0;
        var failed = new ConcurrentBag<MapExtractFailure>();
        Parallel.ForEach(tiles, options, tile =>
        {
            // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:853-861 (file <Map>_<x>_<y>.adt becomes maps/%03u%02u%02u.map of map id, y, x)
            string adtPath = ClientMapPaths.AdtPath(map.Directory, tile);
            string target = Path.Combine(mapsDirectory, TerrainTile.FileName(map.Id, tile.X, tile.Y));
            if (!overwrite && File.Exists(target))
            {
                Interlocked.Increment(ref existing);
                return;
            }

            try
            {
                byte[]? bytes = _files.Read(adtPath);
                if (bytes is null)
                {
                    // The reference fails to open the file and writes nothing for the tile.
                    Interlocked.Increment(ref absent);
                    return;
                }

                byte[] encoded = MapTileEncoder.Encode(AdtFile.Parse(bytes), _areas);
                WriteWhole(target, encoded);
                Interlocked.Increment(ref written);
                Interlocked.Add(ref totalBytes, encoded.Length);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                failed.Add(new MapExtractFailure(map.Id, tile.X, tile.Y, adtPath, e.Message));
            }
        });

        failures.AddRange(failed.OrderBy(f => f.TileX).ThenBy(f => f.TileY));
        return new MapExtractMapReport(map.Id, map.Directory, HasWdt: true, tiles.Count, written, existing, absent, failed.Count, totalBytes);
    }

    /// <summary>Writes the whole file beside its name first, so a stopped run never leaves a cut-off tile under the real name.</summary>
    private static void WriteWhole(string target, byte[] bytes)
    {
        string staging = target + ".part";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, target, overwrite: true);
    }
}

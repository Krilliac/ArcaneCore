using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;

namespace ArcaneCore.Atlas.Extract;

/// <summary>The <c>extract.*</c> commands: write the server's world data files from the open 1.12.1 client.</summary>
internal static class ExtractCommands
{
    private const int MaxThreads = 64;
    private const int MaxReportedFailures = 200;

    /// <summary>Adds <c>extract.maps</c>, <c>extract.vmaps</c> and <c>extract.mmaps</c>.</summary>
    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new CommandDefinition(
            "extract.maps",
            "Write the server's terrain files (maps/MMMXXYY.map: heights, area flags, liquid, holes) from the client's WDT and ADT files, as the reference map extractor does with its defaults.",
            [
                new CommandArg("out", ArgType.String, "Absolute path of the server data root; the files go into its maps folder. Refused inside a git working tree or the client folder.", Required: true),
                new CommandArg("maps", ArgType.StringList, "Map ids to extract (for example 0,1). Empty extracts every map of Map.dbc."),
                new CommandArg("overwrite", ArgType.Boolean, "Rewrite tiles whose file already exists. Off, an existing file is kept, so a stopped run can be continued.", Default: JsonValue.Create(false)),
                new CommandArg("threads", ArgType.Integer, $"Tiles converted at once (1 to {MaxThreads}). Default: the processor count.", Default: JsonValue.Create(0)),
            ],
            MapsAsync,
            CommandSurfaces.Headless | CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "extract.vmaps",
            "Write the server's static collision files (vmaps/NNN.vmtree, NNN_YY_XX.vmtile and a model file per placed WMO and M2, format VMAP_7.0) from the client's WDT, ADT, WMO and M2 files, as the reference vmap extractor and assembler do together.",
            [
                new CommandArg("out", ArgType.String, "Absolute path of the server data root; the files go into its vmaps folder, replacing the files of the maps written. Refused inside a git working tree or the client folder.", Required: true),
                new CommandArg("maps", ArgType.StringList, "Map ids to extract (for example 0,1). Empty extracts every map of Map.dbc."),
                new CommandArg("threads", ArgType.Integer, $"Model files built at once (1 to {MaxThreads}). Default: the processor count.", Default: JsonValue.Create(0)),
            ],
            VmapsAsync,
            CommandSurfaces.Headless | CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "extract.mmaps",
            "Build the server's navigation meshes (mmaps/NNN.mmap and NNNXXYY.mmtile, mmap version 6 over Detour version 7) from the maps and vmaps of a server data root, with the reference generator's parameters and filters. Needs no client.",
            [
                new CommandArg("data", ArgType.String, "Absolute path of the server data root holding maps and vmaps; the files go into its mmaps folder. Default: the configured data directory. Refused inside a git working tree or the client folder."),
                new CommandArg("maps", ArgType.StringList, "Map ids to build (for example 0,1). Empty builds every map that has data, except the unused maps 42 and 169."),
                new CommandArg("tile", ArgType.StringList, "Build one tile only: the server's tile X and Y (for example 49,31). Needs exactly one map in 'maps'."),
                new CommandArg("overwrite", ArgType.Boolean, "Rebuild tiles whose current file already exists. Off, such a tile is kept, so a stopped run can be continued.", Default: JsonValue.Create(false)),
                new CommandArg("quick", ArgType.Boolean, "Skip removing the terrain inside models and the model triangles under the terrain: faster, with unusable polygons left under buildings.", Default: JsonValue.Create(false)),
                new CommandArg("threads", ArgType.Integer, $"Tiles built at once (1 to {MaxThreads}). Default: the processor count.", Default: JsonValue.Create(0)),
            ],
            MmapsAsync,
            CommandSurfaces.Headless | CommandSurfaces.Instance));
    }

    private static Task<JsonNode?> MmapsAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        string name = call.Definition.Name;
        var paths = call.Services.GetService(typeof(AtlasPaths)) as AtlasPaths;
        string? requested = call.OptionalString("data") ?? paths?.TerrainData;
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new CommandException(CommandException.BadArguments, $"{name}: argument 'data' is required when no data directory is configured.");
        }

        var only = new List<uint>();
        foreach (string text in call.GetStringList("maps"))
        {
            only.Add(uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
                ? id
                : throw new CommandException(CommandException.BadArguments, $"{name}: argument 'maps' must list map ids; '{text}' is not one."));
        }

        (int X, int Y)? tile = null;
        IReadOnlyList<string> tileArg = call.GetStringList("tile");
        if (tileArg.Count != 0)
        {
            if (tileArg.Count != 2 || only.Count != 1
                || !int.TryParse(tileArg[0], NumberStyles.None, CultureInfo.InvariantCulture, out int tileX)
                || !int.TryParse(tileArg[1], NumberStyles.None, CultureInfo.InvariantCulture, out int tileY))
            {
                throw new CommandException(CommandException.BadArguments, $"{name}: argument 'tile' must be the tile's X and Y (for example 49,31) and needs exactly one map in 'maps'.");
            }

            tile = (tileX, tileY);
        }

        int threads = Threads(call);
        bool overwrite = call.GetBool("overwrite", false) || tile is not null;
        bool quick = call.GetBool("quick", false);
        string[] protectedRoots = paths?.ClientRoot is { } root ? [root] : [];
        string dataRoot = ClientOutputPath.CheckDirectory(name, requested, protectedRoots);

        // The check on the data root resolves links only along it; a link at 'mmaps' would bypass it, so check the child too.
        ClientOutputPath.CheckDirectory(name, Path.Combine(dataRoot, MmapExtractor.MmapsFolder), protectedRoots);
        if (!Directory.Exists(Path.Combine(dataRoot, MmapExtractor.MapsFolder)) && !Directory.Exists(Path.Combine(dataRoot, MmapExtractor.VmapsFolder)))
        {
            throw new CommandException(CommandException.BadArguments, $"{name}: '{dataRoot}' holds neither a maps nor a vmaps folder.");
        }

        return Task.Run<JsonNode?>(
            () =>
            {
                MmapExtractReport report;
                try
                {
                    report = new MmapExtractor(dataRoot).Extract(only, overwrite, quick, threads, cancellationToken, tile);
                }
                catch (ArgumentException e)
                {
                    throw new CommandException(CommandException.BadArguments, $"{name}: {e.Message}", e);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new CommandException(CommandException.Failed, $"{name}: cannot write to '{dataRoot}': {e.Message}", e);
                }

                var list = new JsonArray();
                foreach (MmapExtractMapReport map in report.Maps)
                {
                    list.Add(new JsonObject
                    {
                        ["id"] = map.Id,
                        ["tiles"] = map.Tiles,
                        ["written"] = map.Written,
                        ["existing"] = map.Existing,
                        ["empty"] = map.Empty,
                        ["failed"] = map.Failed,
                        ["polygons"] = map.Polygons,
                    });
                }

                return new JsonObject
                {
                    ["out"] = report.MmapsDirectory,
                    ["written"] = report.Maps.Sum(m => m.Written),
                    ["existing"] = report.Maps.Sum(m => m.Existing),
                    ["empty"] = report.Maps.Sum(m => m.Empty),
                    ["failed"] = report.Maps.Sum(m => m.Failed),
                    ["polygons"] = report.Maps.Sum(m => m.Polygons),
                    ["bytes"] = report.Bytes,
                    ["maps"] = list,
                    ["problems"] = new JsonArray([.. report.Problems.Take(MaxReportedFailures).Select(p => (JsonNode?)JsonValue.Create(p))]),
                    ["problemCount"] = report.Problems.Count,
                };
            },
            cancellationToken);
    }

    private static int Threads(CommandInvocation call)
    {
        long threads = call.OptionalInt("threads") ?? 0;
        if (threads is < 0 or > MaxThreads)
        {
            throw new CommandException(CommandException.BadArguments, $"{call.Definition.Name}: argument 'threads' must be between 1 and {MaxThreads}.");
        }

        return threads == 0 ? Math.Clamp(Environment.ProcessorCount, 1, MaxThreads) : (int)threads;
    }

    private static Task<JsonNode?> VmapsAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        string name = call.Definition.Name;
        (IClientFileSource files, IMapDirectory maps, List<uint> only, int threads, string output) = Common(call, VmapExtractor.VmapsFolder);
        return Task.Run<JsonNode?>(
            () =>
            {
                VmapExtractReport report;
                try
                {
                    report = new VmapExtractor(files, maps).Extract(output, only, threads, cancellationToken);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new CommandException(CommandException.Failed, $"{name}: cannot write to '{output}': {e.Message}", e);
                }

                var list = new JsonArray();
                foreach (VmapExtractMapReport map in report.Maps)
                {
                    list.Add(new JsonObject
                    {
                        ["id"] = map.Id,
                        ["directory"] = map.Directory,
                        ["hasWdt"] = map.HasWdt,
                        ["tiled"] = map.Tiled,
                        ["spawns"] = map.Spawns,
                        ["tileFiles"] = map.TileFiles,
                        ["failed"] = map.Failed,
                    });
                }

                return new JsonObject
                {
                    ["out"] = report.VmapsDirectory,
                    ["mapsWithModels"] = report.Maps.Count(m => m.Spawns > 0),
                    ["spawns"] = report.Maps.Sum(m => (long)m.Spawns),
                    ["tileFiles"] = report.Maps.Sum(m => m.TileFiles),
                    ["modelFiles"] = report.ModelFiles,
                    ["bytes"] = report.Bytes,
                    ["maps"] = list,
                    ["problems"] = new JsonArray([.. report.Problems.Take(MaxReportedFailures).Select(p => (JsonNode?)JsonValue.Create(p))]),
                    ["problemCount"] = report.Problems.Count,
                };
            },
            cancellationToken);
    }

    /// <summary>The arguments both commands share, checked before anything is read or written.</summary>
    private static (IClientFileSource Files, IMapDirectory Maps, List<uint> Only, int Threads, string Output) Common(CommandInvocation call, string childFolder)
    {
        string name = call.Definition.Name;
        IClientFileSource files = call.Services.GetService(typeof(IClientFileSource)) as IClientFileSource
            ?? throw new CommandException(CommandException.Unavailable, $"{name}: no client files are open (configure the client directory; see client.status).");
        IMapDirectory maps = call.Services.GetService(typeof(IMapDirectory)) as IMapDirectory
            ?? throw new CommandException(CommandException.Unavailable, $"{name}: the client's Map.dbc is not loaded (see client.status).");
        var paths = call.Services.GetService(typeof(AtlasPaths)) as AtlasPaths;

        var only = new List<uint>();
        foreach (string text in call.GetStringList("maps"))
        {
            if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            {
                throw new CommandException(CommandException.BadArguments, $"{name}: argument 'maps' must list map ids; '{text}' is not one.");
            }

            if (!maps.TryGetMap(id, out _))
            {
                throw new CommandException(CommandException.BadArguments, $"{name}: map {id} is not in the client's Map.dbc (see client.maps).");
            }

            only.Add(id);
        }

        int threads = Threads(call);
        string[] protectedRoots = paths?.ClientRoot is { } root ? [root] : [];
        string output = ClientOutputPath.CheckDirectory(name, call.GetString("out"), protectedRoots);

        // The check on 'out' resolves links only along 'out'; a link at the child folder would bypass it, so check the child too.
        ClientOutputPath.CheckDirectory(name, Path.Combine(output, childFolder), protectedRoots);
        return (files, maps, only, threads, output);
    }

    private static Task<JsonNode?> MapsAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        string name = call.Definition.Name;
        (IClientFileSource files, IMapDirectory maps, List<uint> only, int threads, string output) = Common(call, MapExtractor.MapsFolder);
        bool overwrite = call.GetBool("overwrite", false);

        return Task.Run<JsonNode?>(
            () =>
            {
                AreaExploreFlags areas;
                try
                {
                    areas = AreaExploreFlags.Load(files);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
                {
                    throw new CommandException(CommandException.Failed, $"{name}: {e.Message}", e);
                }

                MapExtractReport report;
                try
                {
                    report = new MapExtractor(files, maps, areas).Extract(output, only, overwrite, threads, cancellationToken);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new CommandException(CommandException.Failed, $"{name}: cannot write to '{output}': {e.Message}", e);
                }

                return ToJson(report);
            },
            cancellationToken);
    }

    private static JsonObject ToJson(MapExtractReport report)
    {
        var maps = new JsonArray();
        foreach (MapExtractMapReport map in report.Maps)
        {
            maps.Add(new JsonObject
            {
                ["id"] = map.Id,
                ["directory"] = map.Directory,
                ["hasWdt"] = map.HasWdt,
                ["listed"] = map.Listed,
                ["written"] = map.Written,
                ["existing"] = map.Existing,
                ["absent"] = map.Absent,
                ["failed"] = map.Failed,
                ["bytes"] = map.Bytes,
            });
        }

        var failures = new JsonArray();
        foreach (MapExtractFailure failure in report.Failures.Take(MaxReportedFailures))
        {
            failures.Add(new JsonObject
            {
                ["map"] = failure.Map,
                ["tile"] = failure.TileX < 0 ? null : new JsonArray(failure.TileX, failure.TileY),
                ["path"] = failure.Path,
                ["reason"] = failure.Reason,
            });
        }

        return new JsonObject
        {
            ["out"] = report.MapsDirectory,
            ["written"] = report.Written,
            ["existing"] = report.Existing,
            ["absent"] = report.Absent,
            ["failed"] = report.Failures.Count,
            ["bytes"] = report.Bytes,
            ["mapsWithTerrain"] = report.Maps.Count(m => m.Listed > 0),
            ["maps"] = maps,
            ["failures"] = failures,
            ["failuresTruncated"] = report.Failures.Count > MaxReportedFailures,
        };
    }
}

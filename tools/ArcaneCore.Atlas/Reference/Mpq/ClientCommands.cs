using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>The <c>client.*</c> commands: look at the client files Atlas has open, and copy one file out.</summary>
internal static class ClientCommands
{
    private const int DefaultFileLimit = 100;
    private const int MaxFileLimit = 10000;

    /// <summary>Adds <c>client.status</c>, <c>client.files</c>, <c>client.read</c> and <c>client.maps</c>.</summary>
    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new CommandDefinition(
            "client.status",
            "The client files that are open: archives in load order, file counts and compression statistics.",
            [
                new CommandArg(
                    "deep",
                    ArgType.Boolean,
                    "Also read every sector of every archive for per-method compression and checksum statistics. Reads the whole client (tens of seconds).",
                    Default: JsonValue.Create(false)),
            ],
            StatusAsync));

        registry.Register(new CommandDefinition(
            "client.files",
            "List client files whose path starts with a prefix, with the archive each comes from.",
            [
                new CommandArg("prefix", ArgType.String, "Path prefix, either slash, any case (for example World/Maps/Azeroth). Empty lists everything.", Default: JsonValue.Create(string.Empty)),
                new CommandArg("limit", ArgType.Integer, $"Most files to return (1 to {MaxFileLimit}).", Default: JsonValue.Create(DefaultFileLimit)),
            ],
            FilesAsync));

        registry.Register(new CommandDefinition(
            "client.read",
            "Copy one client file to an absolute path outside any git working tree and outside the client folder.",
            [
                new CommandArg("path", ArgType.String, "In-archive path of the file, either slash, any case.", Required: true),
                new CommandArg("out", ArgType.String, "Absolute path to write. Refused inside a git working tree or the client folder.", Required: true),
                new CommandArg("overwrite", ArgType.Boolean, "Replace the file when it already exists.", Default: JsonValue.Create(false)),
            ],
            ReadAsync,
            CommandSurfaces.Headless | CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "client.maps",
            "The maps in the client's Map.dbc: id, directory under World/Maps, name and instance type.",
            [],
            MapsAsync));
    }

    private static Task<JsonNode?> StatusAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        bool deep = call.GetBool("deep", false);
        var files = call.Services.GetService(typeof(IClientFileSource)) as IClientFileSource;
        var paths = call.Services.GetService(typeof(AtlasPaths)) as AtlasPaths;
        return Task.Run<JsonNode?>(() => BuildStatus(files, paths, deep, cancellationToken), cancellationToken);
    }

    private static JsonObject BuildStatus(IClientFileSource? files, AtlasPaths? paths, bool deep, CancellationToken cancellationToken)
    {
        var status = new JsonObject
        {
            ["clientRoot"] = paths?.ClientRoot,
            ["dataDirectory"] = paths?.ClientDataDirectory,
        };

        switch (files)
        {
            case null:
                status["source"] = "none";
                status["reason"] = paths?.ClientRoot is null
                    ? "No client directory is configured."
                    : $"'{paths.ClientRoot}' holds neither Data/*.MPQ archives nor an extracted World or DBFilesClient folder.";
                break;
            case MpqFileSystem mpq:
                status["source"] = "mpq";
                status["description"] = mpq.Description;
                AddMpqStatus(status, mpq, deep, cancellationToken);
                break;
            default:
                status["source"] = "directory";
                status["description"] = files.Description;
                status["files"] = files.List(string.Empty).Count;
                break;
        }

        return status;
    }

    private static void AddMpqStatus(JsonObject status, MpqFileSystem mpq, bool deep, CancellationToken cancellationToken)
    {
        IReadOnlyList<MpqScanReport> reports = mpq.Scan(deep, cancellationToken);
        var archives = new JsonArray();
        for (int i = 0; i < reports.Count; i++)
        {
            JsonObject entry = reports[i].ToJson();
            entry["order"] = i;
            entry["path"] = mpq.Archives[i].Path;
            entry["bytes"] = mpq.Archives[i].FileLength;
            archives.Add(entry);
        }

        status["archives"] = archives;
        status["skipped"] = new JsonArray([.. mpq.Skipped.Select(s => (JsonNode?)new JsonObject { ["path"] = s.Path, ["reason"] = s.Reason })]);
        status["listedFiles"] = mpq.ListedFileCount;
        status["totals"] = new JsonObject
        {
            ["archives"] = reports.Count,
            ["existingBlocks"] = reports.Sum(r => (long)r.ExistingBlocks),
            ["fileBytes"] = reports.Sum(r => r.FileBytes),
            ["storedBytes"] = reports.Sum(r => r.StoredBytes),
            ["sectorsScanned"] = deep,
            ["flagCombinations"] = Sum(reports.Select(r => r.FlagCombinations)),
            ["locales"] = Sum(reports.Select(r => r.Locales)),
            ["sectorMethods"] = Sum(reports.Select(r => r.SectorMethods)),
            ["checksumTableMethods"] = Sum(reports.Select(r => r.ChecksumTableMethods)),
            ["checksumSectorsVerified"] = reports.Sum(r => r.ChecksumSectorsVerified),
            ["checksumMismatches"] = reports.Sum(r => r.ChecksumMismatches),
        };
    }

    private static JsonObject Sum(IEnumerable<SortedDictionary<string, long>> sources)
    {
        var sums = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (SortedDictionary<string, long> source in sources)
        {
            foreach ((string key, long count) in source)
            {
                sums[key] = sums.GetValueOrDefault(key) + count;
            }
        }

        var result = new JsonObject();
        foreach ((string key, long count) in sums)
        {
            result[key] = count;
        }

        return result;
    }

    private static Task<JsonNode?> FilesAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        IClientFileSource files = RequireFiles(call);
        string prefix = call.OptionalString("prefix") ?? string.Empty;
        long limit = call.OptionalInt("limit") ?? DefaultFileLimit;
        if (limit is < 1 or > MaxFileLimit)
        {
            throw new CommandException(CommandException.BadArguments, $"client.files: argument 'limit' must be between 1 and {MaxFileLimit}.");
        }

        return Task.Run<JsonNode?>(
            () =>
            {
                IReadOnlyList<string> all = files.List(prefix);
                var list = new JsonArray();
                foreach (string path in all.Take((int)limit))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    list.Add(new JsonObject { ["path"] = path, ["archive"] = files.ArchiveOf(path) });
                }

                return new JsonObject
                {
                    ["prefix"] = prefix,
                    ["total"] = all.Count,
                    ["returned"] = list.Count,
                    ["truncated"] = all.Count > list.Count,
                    ["files"] = list,
                };
            },
            cancellationToken);
    }

    private static Task<JsonNode?> ReadAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        IClientFileSource files = RequireFiles(call);
        string path = call.GetString("path");
        bool overwrite = call.GetBool("overwrite", false);
        var paths = call.Services.GetService(typeof(AtlasPaths)) as AtlasPaths;

        // Refuse a bad destination before reading anything.
        string[] protectedRoots = paths?.ClientRoot is { } root ? [root] : [];
        string destination = ClientOutputPath.Check(call.GetString("out"), overwrite, protectedRoots);

        return Task.Run<JsonNode?>(
            () =>
            {
                byte[]? bytes = files.Read(path);
                if (bytes is null)
                {
                    string? deletedBy = (files as MpqFileSystem)?.DeletedBy(path);
                    throw new CommandException(
                        CommandException.Failed,
                        deletedBy is null
                            ? $"client.read: '{path}' is not in the client files."
                            : $"client.read: '{path}' is deleted by {deletedBy}.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var stream = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                }

                return new JsonObject
                {
                    ["path"] = path,
                    ["archive"] = files.ArchiveOf(path),
                    ["bytes"] = bytes.Length,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    ["out"] = destination,
                };
            },
            cancellationToken);
    }

    private static Task<JsonNode?> MapsAsync(CommandInvocation call, CancellationToken cancellationToken)
    {
        if (call.Services.GetService(typeof(IMapDirectory)) is not IMapDirectory directory)
        {
            throw new CommandException(
                CommandException.Unavailable,
                "client.maps: the client's Map.dbc is not loaded (no client files are open, or the map tables could not be read; see client.status).");
        }

        var maps = new JsonArray();
        foreach (ClientMap map in directory.Maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            maps.Add(new JsonObject
            {
                ["id"] = map.Id,
                ["directory"] = map.Directory,
                ["name"] = map.Name,
                ["instanceType"] = map.InstanceType,
            });
        }

        return Task.FromResult<JsonNode?>(new JsonObject { ["count"] = maps.Count, ["maps"] = maps });
    }

    private static IClientFileSource RequireFiles(CommandInvocation call)
        => call.Services.GetService(typeof(IClientFileSource)) as IClientFileSource
            ?? throw new CommandException(
                CommandException.Unavailable,
                $"{call.Definition.Name}: no client files are open (configure the client directory; see client.status).");
}

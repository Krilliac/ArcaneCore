using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using DotRecast.Detour;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// <c>ref.nav.go</c>: the navigation meshes of transports and other game objects, the <c>go*.mmtile</c> files in the <c>mmaps</c> folder.
/// A <c>goNNNN.mmtile</c> holds one Detour tile for the game object model with display id NNNN, in the model's own coordinates (not world
/// coordinates): vmangos loads it by display id (<c>MMapManager::loadGameObject</c>, MoveMap.cpp:374-433; file name <c>mmaps/go%04i.mmtile</c>, :381-383)
/// with the same prefix and tile format as a terrain tile, read here by <see cref="MmapTileReader"/>. Without <c>displayId</c> the command lists the files
/// found; with it, the command reads and validates that one tile and reports its header, bounds and polygon counts by terrain kind.
/// </summary>
/// <remarks>
/// There is no layer for these tiles: a layer places geometry on the terrain grid, and a model's mesh has a world position only through the
/// transport that carries it (a position that changes every tick), which the files do not hold. Thread safety: any thread; every call reads
/// the file afresh and keeps nothing.
/// </remarks>
public static class GoNavCommands
{
    /// <summary>Name of the command.</summary>
    public const string Name = "ref.nav.go";

    /// <summary>The most a <c>go*.mmtile</c> file may be before it is refused unread (the largest real one is far below this).</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The most file names listed in one answer.</summary>
    public const int MaxListed = 4096;

    /// <summary>The command definition.</summary>
    public static IReadOnlyList<CommandDefinition> Definitions { get; } =
    [
        new CommandDefinition(
            Name,
            "Lists the game-object navmesh tiles (go*.mmtile) or, with displayId, reads one and reports its header, bounds (model coordinates) and polygon counts.",
            [
                new CommandArg("displayId", ArgType.Integer, "Display id of the game object model (the NNNN of goNNNN.mmtile). Omit to list the files."),
            ],
            GoAsync),
    ];

    /// <summary>The file name vmangos reads for a display id.</summary>
    public static string FileName(uint displayId) => string.Create(CultureInfo.InvariantCulture, $"go{displayId:D4}.mmtile");

    /// <summary>The display id in a <c>goNNNN.mmtile</c> file name, or false for any other name.</summary>
    public static bool TryParseFileName(string fileName, out uint displayId)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        displayId = 0;
        const string Prefix = "go";
        const string Suffix = ".mmtile";
        if (!fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            || fileName.Length <= Prefix.Length + Suffix.Length)
        {
            return false;
        }

        return uint.TryParse(fileName.AsSpan(Prefix.Length, fileName.Length - Prefix.Length - Suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out displayId);
    }

    /// <summary>The display ids that have a <c>go*.mmtile</c> in <paramref name="directory"/>, ascending, at most <see cref="MaxListed"/>.</summary>
    public static IReadOnlyList<uint> List(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var ids = new SortedSet<uint>();
        foreach (string path in Directory.EnumerateFiles(directory, "go*.mmtile"))
        {
            if (TryParseFileName(Path.GetFileName(path), out uint id))
            {
                ids.Add(id);
            }
        }

        return [.. ids.Take(MaxListed)];
    }

    /// <summary>Reads and validates the tile of <paramref name="displayId"/> and describes it.</summary>
    /// <exception cref="CommandException">The file is missing, too large or not a readable tile; the message says which.</exception>
    public static JsonObject Describe(string directory, uint displayId)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string path = Path.Combine(directory, FileName(displayId));
        FileInfo info = new(path);
        if (!info.Exists)
        {
            throw new CommandException(CommandException.Unavailable, $"{Name}: there is no {FileName(displayId)} in the mmaps folder.");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new CommandException(CommandException.Failed, $"{Name}: {FileName(displayId)} is {info.Length} bytes, over the {MaxFileBytes} byte limit.");
        }

        ParsedNavTile tile;
        try
        {
            tile = MmapTileReader.Parse(File.ReadAllBytes(path));
        }
        catch (InvalidDataException e)
        {
            throw new CommandException(CommandException.Failed, $"{Name}: {FileName(displayId)} was refused: {e.Message}", e);
        }
        catch (IOException e)
        {
            throw new CommandException(CommandException.Failed, $"{Name}: {FileName(displayId)} could not be read: {e.Message}", e);
        }

        DtMeshHeader h = tile.Data.header;
        var byColor = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int offMesh = 0;
        foreach (DtPoly poly in tile.Data.polys)
        {
            if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
            {
                offMesh++;
                continue;
            }

            string key = NavTerrainNames.ColorKey((ushort)poly.flags);
            byColor[key] = byColor.GetValueOrDefault(key) + 1;
        }

        return new JsonObject
        {
            ["displayId"] = displayId,
            ["file"] = FileName(displayId),
            ["fileBytes"] = tile.FileBytes,
            ["coordinates"] = "model",
            ["prefix"] = new JsonObject
            {
                ["detourVersion"] = tile.Prefix.DetourVersion,
                ["mmapVersion"] = tile.Prefix.MmapVersion,
                ["size"] = tile.Prefix.Size,
                ["usesLiquids"] = tile.Prefix.UsesLiquids,
            },
            ["header"] = new JsonObject
            {
                ["polyCount"] = h.polyCount,
                ["vertCount"] = h.vertCount,
                ["detailMeshCount"] = h.detailMeshCount,
                ["bvNodeCount"] = h.bvNodeCount,
                ["offMeshConCount"] = h.offMeshConCount,
                ["boundsMin"] = Point(NavGeometry.ToWorld(h.bmin)),
                ["boundsMax"] = Point(NavGeometry.ToWorld(h.bmax)),
            },
            ["walkablePolys"] = tile.Data.polys.Length - offMesh,
            ["offMeshPolys"] = offMesh,
            ["colorKeys"] = new JsonObject([.. byColor.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))]),
        };
    }

    private static JsonArray Point(WorldPoint p) => new(p.X, p.Y, p.Z);

    private static Task<JsonNode?> GoAsync(CommandInvocation inv, CancellationToken cancellationToken)
    {
        ReferenceNavMeshService service = inv.Service<ReferenceNavMeshService>();
        if (service.Directory is not { } directory)
        {
            throw new CommandException(CommandException.Unavailable, "No navmesh data: the terrain data root (ARCANECORE_TEST_TERRAIN_DIR or --data) has no 'mmaps' folder.");
        }

        long? asked = inv.OptionalInt("displayId");
        if (asked is < 0 or > uint.MaxValue)
        {
            throw new CommandException(CommandException.BadArguments, $"{Name}: displayId must be between 0 and {uint.MaxValue}.");
        }

        return Task.Run<JsonNode?>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asked is { } id)
                {
                    return Describe(directory, (uint)id);
                }

                IReadOnlyList<uint> ids = List(directory);
                return new JsonObject
                {
                    ["count"] = ids.Count,
                    ["displayIds"] = new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i))]),
                    ["truncated"] = ids.Count >= MaxListed,
                };
            },
            cancellationToken);
    }
}

using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// <c>status</c>, <c>help</c>, <c>layers.list</c>, <c>maps.list</c>, <c>tiles.status</c> and <c>cache.clear</c>: what this
/// host has, what is on disk, and what the tile cache holds.
/// </summary>
internal static class StatusCommands
{
    /// <summary>Largest tile radius <c>tiles.status</c> accepts (33 x 33 tiles).</summary>
    public const int MaxRadius = 16;

    /// <summary>Largest number of status entries one request may return.</summary>
    public const int MaxEntries = 20_000;

    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new CommandDefinition("status", "Version, mode, data paths, problems, modules, layer sources, cache size and UI connections.", [], Status));
        registry.Register(new CommandDefinition(
            "help",
            "Lists the commands, or describes one with its arguments and JSON schema.",
            [new("command", ArgType.String, "Command name to describe.")],
            Help));
        registry.Register(new CommandDefinition("layers.list", "Every layer: group, view, title, defaults, attribute colours, whether it is tiled and has a source here.", [], LayersList));
        registry.Register(new CommandDefinition("maps.list", "Maps with data: per-layer tile counts from directory listings, global content, and names when the client is configured.", [], MapsList));
        registry.Register(new CommandDefinition(
            "tiles.status",
            "What each layer has for tiles: ok, empty, missing, rejected (with the reason) or unknown (not read yet). Never builds a tile.",
            [
                new("map", ArgType.Integer, "Map id.", Required: true),
                new("x", ArgType.Integer, "Tile x; with y selects the tile (-1,-1 is the map's global content). Omit both for every tile with data."),
                new("y", ArgType.Integer, "Tile y."),
                new("layer", ArgType.String, "One layer id; default every tiled layer that has a source."),
                new("radius", ArgType.Integer, $"Tiles around x,y to include, 0 to {MaxRadius}. Default 0."),
            ],
            TilesStatus));
        registry.Register(new CommandDefinition(
            "cache.clear",
            "Drops cached tiles so they are rebuilt on the next request, and tells connected UIs to refetch. Without a layer it also empties the texture and model package caches.",
            [new("layer", ArgType.String, "Only this layer's tiles; default all tiles and every other cache.")],
            CacheClear));
    }

    /// <summary>The <c>status</c> data (also served unwrapped by <c>GET /api/status</c>).</summary>
    public static JsonObject BuildStatus(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problems = new JsonArray([.. context.Problems.Select(p => (JsonNode)p)]);
        if (context.Services.Get<BookmarkStore>()?.LoadProblem is { } bookmarkProblem)
        {
            problems.Add(bookmarkProblem);
        }

        IReadOnlyList<ICacheParticipant> participants = context.CacheParticipants.All;
        var status = new JsonObject
        {
            ["version"] = AtlasVersion.Current,
            ["mode"] = context.Headless ? "headless" : "serve",
            ["paths"] = new JsonObject { ["terrainData"] = context.Paths.TerrainData, ["clientRoot"] = context.Paths.ClientRoot },
            ["problems"] = problems,
            ["modules"] = new JsonArray([.. (context.Services.Get<ModuleList>()?.Names ?? []).Select(n => (JsonNode)n)]),
            ["layers"] = new JsonArray([.. context.Layers.Available.Select(l => (JsonNode)l)]),
            ["cacheBytes"] = context.Cache.Bytes,
            ["caches"] = new JsonArray([.. participants.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["count"] = p.Count, ["bytes"] = p.Bytes })]),
            ["cacheBytesTotal"] = context.Cache.Bytes + participants.Sum(p => p.Bytes),
            ["ui"] = new JsonObject { ["connections"] = context.Services.Get<UiHub>()?.ConnectionCount ?? 0 },
        };
        if (context.Services.Get<Live.LiveFeed>() is { } live)
        {
            status["live"] = live.Status();
        }

        return status;
    }

    /// <summary>The <c>layers.list</c> data.</summary>
    public static JsonArray BuildLayers(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var list = new JsonArray();
        foreach (LayerInfo info in LayerCatalog.All)
        {
            var colors = new JsonObject();
            foreach ((string key, string color) in info.AttributeColors ?? new Dictionary<string, string>())
            {
                colors[key] = color;
            }

            list.Add(new JsonObject
            {
                ["id"] = info.Id,
                ["group"] = ViewState.Name(info.Group),
                ["view"] = ViewState.Name(info.View),
                ["title"] = info.Title,
                ["description"] = info.Description,
                ["defaultColor"] = info.DefaultColor,
                ["defaultVisible"] = info.DefaultVisible,
                ["attributeColors"] = colors,
                ["tiled"] = info.Tiled,
                ["hasSource"] = context.Layers.TryGet(info.Id, out _),
            });
        }

        return list;
    }

    /// <summary>The <c>GET /api/commands</c> entries.</summary>
    public static JsonArray BuildCommandList(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var list = new JsonArray();
        foreach (CommandDefinition definition in registry.All)
        {
            list.Add(new JsonObject
            {
                ["name"] = definition.Name,
                ["summary"] = definition.Summary,
                ["surfaces"] = SurfaceNames(definition.Surfaces),
                ["schema"] = CommandSchema.InputSchema(definition),
            });
        }

        return list;
    }

    public static JsonArray SurfaceNames(CommandSurfaces surfaces)
    {
        var names = new JsonArray();
        foreach ((CommandSurfaces flag, string name) in new[] { (CommandSurfaces.Headless, "headless"), (CommandSurfaces.Instance, "instance"), (CommandSurfaces.Palette, "palette") })
        {
            if (surfaces.HasFlag(flag))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Per-tile status without building anything. With <paramref name="x"/> and <paramref name="y"/> it covers the tiles within
    /// <paramref name="radius"/> of that tile (clipped to the grid); without them, every tile each layer lists as available.
    /// </summary>
    public static JsonObject TileStatus(AtlasContext context, uint map, int? x, int? y, string? layer, int radius)
    {
        if ((x is null) != (y is null))
        {
            throw new CommandException(CommandException.BadArguments, "tiles.status: give both x and y, or neither.");
        }

        if (radius is < 0 or > MaxRadius)
        {
            throw new CommandException(CommandException.BadArguments, $"tiles.status: radius must be within [0, {MaxRadius}].");
        }

        string[] layers;
        if (layer is not null)
        {
            if (!LayerCatalog.TryGet(layer, out LayerInfo info))
            {
                throw new CommandException(CommandException.BadArguments, $"tiles.status: unknown layer '{layer}' (see layers.list).");
            }

            if (!info.Tiled || !context.Layers.TryGet(layer, out _))
            {
                throw new CommandException(CommandException.Unavailable, $"tiles.status: layer '{layer}' has no tile source in this host.");
            }

            layers = [layer];
        }
        else
        {
            layers = [.. context.Layers.Available.Where(l => LayerCatalog.Get(l).Tiled)];
        }

        var tiles = new JsonArray();
        foreach (string id in layers)
        {
            IEnumerable<TileKey> keys;
            if (x is { } tx && y is { } ty)
            {
                var centre = new TileKey(map, tx, ty);
                if (!centre.IsValid)
                {
                    throw new CommandException(CommandException.BadArguments, $"tiles.status: tile {tx},{ty} is outside the 64x64 grid (use -1,-1 for global content).");
                }

                keys = centre.IsGlobal ? [centre] : Neighbourhood(centre, radius);
            }
            else
            {
                context.Layers.TryGet(id, out ILayerSource source);
                keys = source.AvailableTiles(id, map).OrderBy(t => t.X).ThenBy(t => t.Y);
            }

            foreach (TileKey key in keys)
            {
                if (tiles.Count >= MaxEntries)
                {
                    throw new CommandException(CommandException.BadArguments, $"tiles.status: more than {MaxEntries} entries; narrow the request with layer, x, y and radius.");
                }

                tiles.Add(context.Cache.Peek(id, key).ToJson());
            }
        }

        return new JsonObject { ["tiles"] = tiles };
    }

    private static IEnumerable<TileKey> Neighbourhood(TileKey centre, int radius)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                var key = new TileKey(centre.MapId, centre.X + dx, centre.Y + dy);
                if (key.IsInGrid)
                {
                    yield return key;
                }
            }
        }
    }

    private static Task<JsonNode?> Status(CommandInvocation invocation, CancellationToken cancellationToken)
        => Task.FromResult<JsonNode?>(BuildStatus(invocation.Service<AtlasContext>()));

    private static Task<JsonNode?> LayersList(CommandInvocation invocation, CancellationToken cancellationToken)
        => Task.FromResult<JsonNode?>(BuildLayers(invocation.Service<AtlasContext>()));

    private static Task<JsonNode?> Help(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        CommandRegistry registry = invocation.Service<CommandRegistry>();
        if (ArgReader.Text(invocation, "command") is not { } name)
        {
            var summaries = new JsonArray();
            foreach (CommandDefinition d in registry.All)
            {
                summaries.Add(new JsonObject { ["name"] = d.Name, ["summary"] = d.Summary, ["surfaces"] = SurfaceNames(d.Surfaces) });
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["commands"] = summaries });
        }

        if (!registry.TryGet(name, out CommandDefinition definition))
        {
            throw new CommandException(CommandException.UnknownCommand, $"No command named '{name}'.");
        }

        var args = new JsonArray();
        foreach (CommandArg arg in definition.Args)
        {
            var entry = new JsonObject
            {
                ["name"] = arg.Name,
                ["type"] = ViewState.Name(arg.Type),
                ["required"] = arg.Required,
                ["description"] = arg.Description,
            };
            if (arg.Default is not null)
            {
                entry["default"] = arg.Default.DeepClone();
            }

            if (arg.Choices is { Count: > 0 } choices)
            {
                entry["choices"] = new JsonArray([.. choices.Select(c => (JsonNode)c)]);
            }

            args.Add(entry);
        }

        return Task.FromResult<JsonNode?>(new JsonObject
        {
            ["name"] = definition.Name,
            ["summary"] = definition.Summary,
            ["surfaces"] = SurfaceNames(definition.Surfaces),
            ["args"] = args,
            ["schema"] = CommandSchema.InputSchema(definition),
            ["usage"] = CommandSchema.Help(definition),
        });
    }

    private static Task<JsonNode?> TilesStatus(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        AtlasContext context = invocation.Service<AtlasContext>();
        int? x = invocation.OptionalInt("x") is { } tx ? (int)Math.Clamp(tx, -1_000_000, 1_000_000) : null;
        int? y = invocation.OptionalInt("y") is { } ty ? (int)Math.Clamp(ty, -1_000_000, 1_000_000) : null;
        int radius = invocation.OptionalInt("radius") is { } r ? (int)Math.Clamp(r, -1, 1000) : 0;
        return Task.FromResult<JsonNode?>(TileStatus(context, invocation.GetMapId(), x, y, ArgReader.Text(invocation, "layer"), radius));
    }

    private static Task<JsonNode?> CacheClear(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        AtlasContext context = invocation.Service<AtlasContext>();
        string? layer = ArgReader.Text(invocation, "layer");
        if (layer is not null && !LayerCatalog.TryGet(layer, out _))
        {
            throw new CommandException(CommandException.BadArguments, $"cache.clear: unknown layer '{layer}' (see layers.list).");
        }

        int removed = context.Cache.Invalidate(layer);
        if (layer is null)
        {
            context.CacheParticipants.ClearAll();
        }

        // The cache cannot list what it dropped, so the event names the layer (or null for all) and no tile: the UI refetches what it shows.
        context.Events.Publish(EventHub.Topics.TileInvalidated, new JsonObject { ["layer"] = layer, ["tile"] = null });
        return Task.FromResult<JsonNode?>(new JsonObject { ["removed"] = removed });
    }

    private static Task<JsonNode?> MapsList(CommandInvocation invocation, CancellationToken cancellationToken)
        => Task.FromResult<JsonNode?>(MapCatalog.Build(invocation.Service<AtlasContext>(), cancellationToken));
}

/// <summary>The <c>maps.list</c> scan: candidate map ids from the client's Map.dbc and the data folders' file names.</summary>
internal static class MapCatalog
{
    public static JsonArray Build(AtlasContext context, CancellationToken cancellationToken)
    {
        IMapDirectory? names = context.Services.Get<IMapDirectory>();
        SortedSet<uint> candidates = [context.View.Current.MapId];
        if (names is not null)
        {
            foreach (ClientMap map in names.Maps)
            {
                candidates.Add(map.Id);
            }
        }

        foreach (string? folder in new[] { context.Paths.MapsDirectory, context.Paths.VMapsDirectory, context.Paths.MMapsDirectory })
        {
            if (folder is not null)
            {
                AddFromFileNames(folder, candidates);
            }
        }

        string[] layers = [.. context.Layers.Available.Where(l => LayerCatalog.Get(l).Tiled)];
        var list = new JsonArray();
        foreach (uint map in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var counts = new JsonObject();
            var global = new JsonArray();
            foreach (string layer in layers)
            {
                context.Layers.TryGet(layer, out ILayerSource source);
                IReadOnlyCollection<TileKey> tiles = source.AvailableTiles(layer, map);
                int inGrid = tiles.Count(t => t.IsInGrid);
                if (inGrid > 0)
                {
                    counts[layer] = inGrid;
                }

                if (tiles.Any(t => t.IsGlobal))
                {
                    global.Add(layer);
                }
            }

            if (counts.Count == 0 && global.Count == 0)
            {
                continue;
            }

            var entry = new JsonObject { ["map"] = map };
            if (names is not null && names.TryGetMap(map, out ClientMap client))
            {
                entry["name"] = client.Name;
                entry["directory"] = client.Directory;
            }

            entry["layers"] = counts;
            entry["global"] = global;
            list.Add(entry);
        }

        return list;
    }

    /// <summary>
    /// Every data file name begins with the map id as three digits: <c>{map:D3}{x:D2}{y:D2}.map</c>
    /// (ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:215), <c>{map:D3}_{y:D2}_{x:D2}.vmtile</c> and <c>{map:D3}.vmtree</c>
    /// (ref: src/ArcaneCore.Game/Maps/Collision/VMaps/VMapFormat.cs:44 and :37), <c>{map:D3}{x:D2}{y:D2}.mmtile</c> and
    /// <c>{map:D3}.mmap</c> (ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshFormat.cs:44 and :38).
    /// </summary>
    private static void AddFromFileNames(string folder, SortedSet<uint> candidates)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                ReadOnlySpan<char> name = Path.GetFileName(file.AsSpan());
                if (name.Length >= 3 && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1]) && char.IsAsciiDigit(name[2]))
                {
                    candidates.Add(uint.Parse(name[..3], System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CommandException(CommandException.Failed, $"maps.list: '{folder}' could not be listed: {e.Message}", e);
        }
    }
}

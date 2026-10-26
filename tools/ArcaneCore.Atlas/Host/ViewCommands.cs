using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// <c>view.get</c>, <c>view.set</c>, <c>camera.goto</c>, <c>camera.frame</c>, <c>layer.set</c> and the bookmark commands:
/// everything that reads or moves the shared <see cref="ViewStore"/>. Every change goes through
/// <see cref="ViewStore.Patch"/>, so the view event is published by the store's <c>Changed</c> hook, with this call's
/// origin label (<see cref="HostOrigin"/>).
/// </summary>
internal static class ViewCommands
{
    private static readonly string[] RenderModes = [.. Enum.GetValues<RenderMode>().Select(ViewState.Name)];
    private static readonly string[] CameraModes = [.. Enum.GetValues<CameraMode>().Select(ViewState.Name)];

    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new CommandDefinition("view.get", "The complete current view (map, camera, layer styles, compare mode).", [], ViewGet));
        registry.Register(new CommandDefinition(
            "view.set",
            "Changes the view with a JSON merge patch (RFC 7396): give only the fields to change; a layer style merges per field, null removes a field that has a default.",
            [
                new("map", ArgType.Integer, "Map id."),
                new("camera", ArgType.Object, "Camera fields: target [x,y,z], distance, yaw, pitch, fov, mode (orbit|fly|top)."),
                new("layers", ArgType.Object, "Per-layer styles keyed by layer id: visible, color (#RRGGBB), opacity (0..1), mode (wire|fill|both|xray), colorByAttribute."),
                new("compare", ArgType.String, "Which data the viewport shows.", Choices: [.. Enum.GetValues<CompareMode>().Select(ViewState.Name)]),
                new("split", ArgType.Number, "Split divider position, 0.05 to 0.95."),
                new("loadRadius", ArgType.Integer, $"Tiles loaded around the camera, 0 to {ViewState.MaxLoadRadiusTiles}."),
                new("theme", ArgType.String, "UI chrome theme.", Choices: [.. Enum.GetValues<Theme>().Select(ViewState.Name)]),
                new("hud", ArgType.Boolean, "Show the viewport overlays."),
                new("follow", ArgType.String, "Hex guid of the live entity the camera follows; null stops following."),
                new("textureRadius", ArgType.Integer, $"Tiles around the focus tile whose textured layers load, 0 to {ViewState.MaxTextureRadius}. Default {ViewState.DefaultTextureRadius}."),
                new("textureBudgetMb", ArgType.Integer, $"Browser texture memory budget in MiB, {ViewState.MinTextureBudgetMb} to {ViewState.MaxTextureBudgetMb}. Default {ViewState.DefaultTextureBudgetMb}."),
            ],
            ViewSet));
        registry.Register(new CommandDefinition(
            "camera.goto",
            "Moves the camera. Give at=[x,y,z] or x and y (z defaults to the terrain height there when a terrain layer has data); other camera fields keep their values.",
            [
                new("map", ArgType.Integer, "Map id (default: the current map)."),
                new("at", ArgType.Point3, "Target as [x, y, z] (or \"x,y,z\")."),
                new("x", ArgType.Number, "Target x (north)."),
                new("y", ArgType.Number, "Target y (west)."),
                new("z", ArgType.Number, "Target z; default is the terrain height at x,y."),
                new("yaw", ArgType.Number, "Heading in degrees: 0 looks north (+x), 90 west (+y)."),
                new("pitch", ArgType.Number, "Degrees below the horizon, -89 to 89."),
                new("distance", ArgType.Number, "Orbit radius in yards (half-height of the view in top mode)."),
                new("mode", ArgType.String, "Camera mode.", Choices: CameraModes),
            ],
            CameraGoto));
        registry.Register(new CommandDefinition(
            "camera.frame",
            "Frames a tile or a box so it fills the view, or follows a live entity. Give exactly one of tile, bounds or guid.",
            [
                new("tile", ArgType.String, "Tile as \"x,y\" or \"map,x,y\" (server tile indices)."),
                new("bounds", ArgType.Object, "Box as {\"min\":[x,y,z?],\"max\":[x,y,z?]} in world coordinates."),
                new("guid", ArgType.String, "Hex guid of a live entity: the UI follows and centres it."),
                new("map", ArgType.Integer, "Map id for tile or bounds (default: the current map)."),
            ],
            CameraFrame));
        registry.Register(new CommandDefinition(
            "layer.set",
            "Changes one layer's style: visibility, colour, opacity, render mode, colour by attribute.",
            [
                new("id", ArgType.String, "Layer id (see layers.list).", Required: true),
                new("visible", ArgType.Boolean, "Show or hide the layer."),
                new("color", ArgType.String, "Colour as #RRGGBB."),
                new("opacity", ArgType.Number, "0 to 1."),
                new("mode", ArgType.String, "How triangles are drawn.", Choices: RenderModes),
                new("colorByAttribute", ArgType.Boolean, "Colour primitives by their attribute's colour key."),
            ],
            LayerSet));
        registry.Register(new CommandDefinition("bookmarks.list", "Built-in and saved camera bookmarks.", [], BookmarksList));
        registry.Register(new CommandDefinition(
            "bookmark.go",
            "Moves the camera (and map) to a bookmark.",
            [new("name", ArgType.String, "Bookmark name (see bookmarks.list).", Required: true)],
            BookmarkGo));
        registry.Register(new CommandDefinition(
            "bookmark.add",
            "Saves a camera as a bookmark, replacing a saved one of the same name. Without map and camera it saves the shared view's camera (that of whichever client moved last); a UI that flies its own camera passes its map and camera, which are saved without changing the shared view.",
            [
                new("name", ArgType.String, "1 to 64 letters, digits, spaces or . _ - ' characters.", Required: true),
                new("note", ArgType.String, "Free text shown in the bookmark list."),
                new("map", ArgType.Integer, "Map id to save instead of the shared view's."),
                new("camera", ArgType.Object, "Camera fields to save instead of the shared view's, as in view.set: target [x,y,z], distance, yaw, pitch, fov, mode (orbit|fly|top)."),
            ],
            BookmarkAdd));
        registry.Register(new CommandDefinition(
            "bookmark.remove",
            "Deletes a saved bookmark (built-in bookmarks cannot be removed).",
            [new("name", ArgType.String, "Bookmark name.", Required: true)],
            BookmarkRemove));
    }

    private static Task<JsonNode?> ViewGet(CommandInvocation invocation, CancellationToken cancellationToken)
        => Task.FromResult<JsonNode?>(invocation.Service<AtlasContext>().View.Current.ToJson());

    private static Task<JsonNode?> ViewSet(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Args.Count == 0)
        {
            throw new CommandException(CommandException.BadArguments, "view.set: give at least one field to change (see help view.set).");
        }

        return Changed(invocation, invocation.Args);
    }

    private static async Task<JsonNode?> CameraGoto(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        AtlasContext context = invocation.Service<AtlasContext>();
        ViewState current = context.View.Current;
        uint map = ArgReader.Map(invocation, current);
        bool hasAt = invocation.Has("at");
        bool hasXy = invocation.Has("x") || invocation.Has("y") || invocation.Has("z");
        if (hasAt && hasXy)
        {
            throw new CommandException(CommandException.BadArguments, "camera.goto: give either 'at' or x/y/z, not both.");
        }

        var camera = new JsonObject();
        var patch = new JsonObject { ["camera"] = camera };
        var extra = new JsonObject();
        if (hasAt || hasXy)
        {
            double x;
            double y;
            double? z;
            if (hasAt)
            {
                WorldPoint at = invocation.GetPoint("at");
                (x, y, z) = (at.X, at.Y, at.Z);
            }
            else
            {
                if (!invocation.Has("x") || !invocation.Has("y"))
                {
                    throw new CommandException(CommandException.BadArguments, "camera.goto: x and y are both required (z is optional).");
                }

                (x, y, z) = (invocation.GetNumber("x"), invocation.GetNumber("y"), invocation.OptionalNumber("z"));
            }

            if (WorldGrid.TileOf(map, x, y) is not { } tile)
            {
                throw new CommandException(CommandException.BadArguments, $"camera.goto: ({x}, {y}) is outside the 64x64 tile grid of a map.");
            }

            string zSource = "given";
            if (z is null)
            {
                double? surface = await Task.Run(() => SurfaceLookup.TerrainHeight(context, map, x, y, cancellationToken), cancellationToken).ConfigureAwait(false);
                (z, zSource) = surface is { } found ? (found, "terrain") : (current.Camera.Target.Z, "kept");
            }

            camera["target"] = HostJson.Point(new WorldPoint(x, y, z.Value));
            extra["target"] = HostJson.Point(new WorldPoint(x, y, z.Value));
            extra["zSource"] = zSource;
            extra["tile"] = new JsonArray(tile.MapId, tile.X, tile.Y);
        }

        foreach (string name in (string[])["yaw", "pitch", "distance"])
        {
            if (invocation.OptionalNumber(name) is { } value)
            {
                camera[name] = value;
            }
        }

        if (invocation.OptionalString("mode") is { } mode)
        {
            camera["mode"] = mode;
        }

        if (camera.Count == 0 && !invocation.Has("map"))
        {
            throw new CommandException(CommandException.BadArguments, "camera.goto: give a position (at or x,y) or at least one of map, yaw, pitch, distance, mode.");
        }

        if (hasAt || hasXy || invocation.Has("map"))
        {
            patch["map"] = map;
        }

        if (camera.Count == 0)
        {
            patch.Remove("camera");
        }

        return await Changed(invocation, patch, extra).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> CameraFrame(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        AtlasContext context = invocation.Service<AtlasContext>();
        ViewState current = context.View.Current;
        int given = (invocation.Has("tile") ? 1 : 0) + (invocation.Has("bounds") ? 1 : 0) + (invocation.Has("guid") ? 1 : 0);
        if (given != 1)
        {
            throw new CommandException(CommandException.BadArguments, "camera.frame: give exactly one of tile, bounds or guid.");
        }

        if (invocation.Has("guid"))
        {
            string guid = ArgReader.RequiredText(invocation, "guid");
            string normalizedGuid = Live.LiveGuid.TryParse(guid, out ulong entityId) && entityId != 0
                ? Live.LiveGuid.Format(entityId)
                : guid;
            string patchGuid = guid.AsSpan().Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? normalizedGuid : guid;
            var followPatch = new JsonObject { ["follow"] = patchGuid };
            var followExtra = new JsonObject { ["following"] = patchGuid };
            Live.LiveFeed? live = context.Services.Get<Live.LiveFeed>();
            if (live is not null
                && entityId != 0
                && live.Store.TryGetEntity(entityId, out Live.LiveEntity? entity)
                && entity is not null)
            {
                followPatch["camera"] = new JsonObject { ["target"] = HostJson.Point(entity.Position) };
                if (current.MapId != entity.Map)
                {
                    followPatch["map"] = entity.Map;
                }

                followExtra["found"] = true;
                followExtra["map"] = entity.Map;
                followExtra["at"] = HostJson.Point(entity.Position);
            }

            return await Changed(invocation, followPatch, followExtra).ConfigureAwait(false);
        }

        uint map = ArgReader.Map(invocation, current);
        double minX;
        double maxX;
        double minY;
        double maxY;
        double? minZ = null;
        double? maxZ = null;
        if (invocation.Has("tile"))
        {
            double[] numbers = ArgReader.Numbers(invocation.Args["tile"]) ?? throw ArgReader.Bad(invocation, "tile", "\"x,y\" or \"map,x,y\" tile indices");
            TileKey tile;
            if (numbers.Length == 3 && numbers.All(n => Math.Floor(n) == n && n >= 0))
            {
                tile = new TileKey((uint)numbers[0], (int)numbers[1], (int)numbers[2]);
                map = tile.MapId;
            }
            else if (numbers.Length == 2 && numbers.All(n => Math.Floor(n) == n))
            {
                tile = new TileKey(map, (int)numbers[0], (int)numbers[1]);
            }
            else
            {
                throw ArgReader.Bad(invocation, "tile", "\"x,y\" or \"map,x,y\" whole tile indices");
            }

            if (!tile.IsInGrid)
            {
                throw new CommandException(CommandException.BadArguments, $"camera.frame: tile {tile.X},{tile.Y} is outside the 64x64 grid.");
            }

            (minX, maxX, minY, maxY) = WorldGrid.Bounds(tile);
        }
        else
        {
            JsonObject bounds = invocation.OptionalObject("bounds") ?? throw ArgReader.Bad(invocation, "bounds", "an object with min and max");
            WorldPoint min = ArgReader.Point(invocation, "bounds.min", bounds["min"], allowTwo: true, fallbackZ: double.NaN);
            WorldPoint max = ArgReader.Point(invocation, "bounds.max", bounds["max"], allowTwo: true, fallbackZ: double.NaN);
            (minX, maxX) = (Math.Min(min.X, max.X), Math.Max(min.X, max.X));
            (minY, maxY) = (Math.Min(min.Y, max.Y), Math.Max(min.Y, max.Y));
            if (!double.IsNaN(min.Z) && !double.IsNaN(max.Z))
            {
                (minZ, maxZ) = (Math.Min(min.Z, max.Z), Math.Max(min.Z, max.Z));
            }
        }

        double centreX = (minX + maxX) / 2;
        double centreY = (minY + maxY) / 2;
        if (WorldGrid.TileOf(map, centreX, centreY) is null)
        {
            throw new CommandException(CommandException.BadArguments, "camera.frame: the centre of the area is outside the 64x64 tile grid of a map.");
        }

        double centreZ;
        if (minZ is { } lo && maxZ is { } hi)
        {
            centreZ = (lo + hi) / 2;
        }
        else
        {
            double? surface = await Task.Run(() => SurfaceLookup.TerrainHeight(context, map, centreX, centreY, cancellationToken), cancellationToken).ConfigureAwait(false);
            centreZ = surface ?? current.Camera.Target.Z;
        }

        double spanX = maxX - minX;
        double spanY = maxY - minY;
        double spanZ = (maxZ ?? 0) - (minZ ?? 0);
        CameraState camera = current.Camera;
        double radius = Math.Max(1, 0.5 * Math.Sqrt((spanX * spanX) + (spanY * spanY) + (spanZ * spanZ)));
        double distance = camera.Mode == CameraMode.Top
            ? Math.Max(spanX, spanY) / 2 * 1.1
            : radius / Math.Sin(camera.Fov * Math.PI / 360) * 1.05;
        distance = Math.Clamp(distance, CameraState.MinDistance, CameraState.MaxDistance);

        // In fly mode the camera IS its target, so the target moves back along the view direction to see the whole area.
        WorldPoint centre = new(centreX, centreY, centreZ);
        WorldPoint target = centre;
        if (camera.Mode == CameraMode.Fly)
        {
            WorldPoint look = CameraMath.Direction(camera);
            target = new WorldPoint(centre.X - (look.X * distance), centre.Y - (look.Y * distance), centre.Z - (look.Z * distance));
        }

        var patch = new JsonObject
        {
            ["map"] = map,
            ["camera"] = new JsonObject { ["target"] = HostJson.Point(target), ["distance"] = distance },
        };
        var extra = new JsonObject { ["centre"] = HostJson.Point(centre), ["distance"] = distance };
        return await Changed(invocation, patch, extra).ConfigureAwait(false);
    }

    private static Task<JsonNode?> LayerSet(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        string id = ArgReader.RequiredText(invocation, "id");
        if (!LayerCatalog.TryGet(id, out _))
        {
            throw new CommandException(CommandException.BadArguments, $"layer.set: unknown layer '{id}' (see layers.list).");
        }

        var style = new JsonObject();
        if (invocation.OptionalBool("visible") is { } visible)
        {
            style["visible"] = visible;
        }

        if (invocation.OptionalString("color") is { } color)
        {
            style["color"] = color;
        }

        if (invocation.OptionalNumber("opacity") is { } opacity)
        {
            style["opacity"] = opacity;
        }

        if (invocation.OptionalString("mode") is { } mode)
        {
            style["mode"] = mode;
        }

        if (invocation.OptionalBool("colorByAttribute") is { } byAttribute)
        {
            style["colorByAttribute"] = byAttribute;
        }

        if (style.Count == 0)
        {
            throw new CommandException(CommandException.BadArguments, "layer.set: give at least one of visible, color, opacity, mode, colorByAttribute.");
        }

        return Changed(invocation, new JsonObject { ["layers"] = new JsonObject { [id] = style } });
    }

    private static Task<JsonNode?> BookmarksList(CommandInvocation invocation, CancellationToken cancellationToken)
        => Task.FromResult<JsonNode?>(new JsonArray([.. invocation.Service<BookmarkStore>().All().Select(b => (JsonNode)b.ToJson())]));

    private static Task<JsonNode?> BookmarkGo(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        string name = ArgReader.RequiredText(invocation, "name");
        if (!invocation.Service<BookmarkStore>().TryGet(name, out Bookmark bookmark))
        {
            throw new CommandException(CommandException.BadArguments, $"bookmark.go: no bookmark named '{name}' (see bookmarks.list).");
        }

        return Changed(invocation, bookmark.ToPatch(), new JsonObject { ["bookmark"] = bookmark.ToJson() });
    }

    private static Task<JsonNode?> BookmarkAdd(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        string name = ArgReader.RequiredText(invocation, "name").Trim();
        ViewState view = invocation.Service<AtlasContext>().View.Current;
        var given = new JsonObject();
        if (invocation.Has("map"))
        {
            given["map"] = ArgReader.Map(invocation, view);
        }

        if (invocation.OptionalObject("camera") is { } camera)
        {
            given["camera"] = camera.DeepClone();
        }

        // Applied to a copy: the shared view is neither read for these fields nor changed.
        if (given.Count > 0)
        {
            view = view.Apply(given);
        }

        CameraState c = view.Camera;
        var bookmark = new Bookmark(name, view.MapId, c.Target, c.Distance, c.Yaw, c.Pitch, c.Mode, ArgReader.Text(invocation, "note"), null);
        invocation.Service<BookmarkStore>().Save(bookmark);
        return Task.FromResult<JsonNode?>(new JsonObject { ["bookmark"] = bookmark.ToJson() });
    }

    private static Task<JsonNode?> BookmarkRemove(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        string name = ArgReader.RequiredText(invocation, "name").Trim();
        if (!invocation.Service<BookmarkStore>().Remove(name))
        {
            throw new CommandException(CommandException.BadArguments, $"bookmark.remove: no saved bookmark named '{name}'.");
        }

        return Task.FromResult<JsonNode?>(new JsonObject { ["removed"] = name });
    }

    /// <summary>Applies a merge patch with this call's origin label and returns <c>{view, ...extra}</c>.</summary>
    private static Task<JsonNode?> Changed(CommandInvocation invocation, JsonObject patch, JsonObject? extra = null)
    {
        ViewState next = invocation.Service<AtlasContext>().View.Patch(patch, HostOrigin.Label(invocation.Origin));
        var result = new JsonObject { ["view"] = next.ToJson() };
        if (extra is not null)
        {
            foreach ((string key, JsonNode? value) in extra.ToArray())
            {
                extra.Remove(key);
                result[key] = value;
            }
        }

        return Task.FromResult<JsonNode?>(result);
    }
}

/// <summary>Height lookups over the layer cache.</summary>
internal static class SurfaceLookup
{
    private static readonly string[] TerrainLayers = [LayerIds.Terrain, LayerIds.RefTerrain];

    /// <summary>
    /// The terrain surface height at a position from the first terrain layer (server terrain, then the client's) that has
    /// data for the tile, found by casting a ray straight down through the cached chunks; null when no layer covers it
    /// (hole, missing tile, no source). Never builds more than the one tile.
    /// </summary>
    public static double? TerrainHeight(AtlasContext context, uint map, double x, double y, CancellationToken cancellationToken)
    {
        if (WorldGrid.TileOf(map, x, y) is not { } tile)
        {
            return null;
        }

        var origin = new WorldPoint(x, y, 20_000);
        var down = new WorldPoint(0, 0, -1);
        foreach (string layer in TerrainLayers)
        {
            if (!context.Layers.TryGet(layer, out _) || context.Cache.Peek(layer, tile).State is TileState.Missing or TileState.Empty or TileState.NotApplicable or TileState.Rejected)
            {
                continue;
            }

            LayerBuild build;
            try
            {
                build = context.Cache.Get(layer, tile, cancellationToken);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                continue;
            }

            double best = double.PositiveInfinity;
            foreach (GeometryChunk chunk in build.Chunks)
            {
                if (ChunkPicker.Nearest(chunk, origin, down, 40_000, includeLinesAndPoints: false) is { } hit)
                {
                    best = Math.Min(best, hit.Distance);
                }
            }

            if (!double.IsPositiveInfinity(best))
            {
                return origin.Z - best;
            }
        }

        return null;
    }
}

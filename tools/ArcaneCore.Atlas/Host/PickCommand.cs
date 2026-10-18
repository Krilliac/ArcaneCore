using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// <c>query.pick</c>: the nearest primitive a ray meets in the chunks of the chosen layers. The tiles the ray crosses are
/// read through <see cref="LayerCache"/> (built on first use, like any tile request); a primitive belongs to the tile
/// whose chunk lists it, so a model that overhangs a tile edge is found through the tiles that list it.
/// </summary>
internal static class PickCommand
{
    public const double DefaultMaxDistance = 2000;
    public const double LargestMaxDistance = 20_000;

    public static void Register(CommandRegistry registry)
        => registry.Register(new CommandDefinition(
            "query.pick",
            "Picks the nearest triangle (or line/point within 0.25 yd) along a ray through the cached geometry of the visible layers.",
            [
                new("ray", ArgType.Object, "World ray as {\"origin\":[x,y,z],\"direction\":[x,y,z],\"map\"?:id,\"maxDistance\"?:yards}."),
                new("ndc", ArgType.String, "Viewport point in normalized device coordinates \"x,y\" (each -1..1, y up); the ray comes from the current camera."),
                new("aspect", ArgType.Number, "Viewport width/height for ndc (default 16/9)."),
                new("layers", ArgType.StringList, "Layer ids to test; default the visible tiled layers."),
                new("map", ArgType.Integer, "Map id (default: the current map)."),
                new("maxDistance", ArgType.Number, $"Ray length in yards, up to {LargestMaxDistance:0}. Default {DefaultMaxDistance:0}."),
            ],
            Pick));

    private static async Task<JsonNode?> Pick(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        AtlasContext context = invocation.Service<AtlasContext>();
        ViewState view = context.View.Current;
        bool hasRay = invocation.Has("ray");
        if (hasRay == invocation.Has("ndc"))
        {
            throw new CommandException(CommandException.BadArguments, "query.pick: give exactly one of ray or ndc.");
        }

        uint map = ArgReader.Map(invocation, view);
        double max = invocation.OptionalNumber("maxDistance") ?? DefaultMaxDistance;
        WorldPoint origin;
        WorldPoint direction;
        double startOffset = 0;
        if (hasRay)
        {
            JsonObject ray = invocation.OptionalObject("ray")!;
            origin = ArgReader.Point(invocation, "ray.origin", ray["origin"]);
            direction = ArgReader.Point(invocation, "ray.direction", ray["direction"]);
            if (ray["map"] is { } rayMap)
            {
                map = JsonScalars.TryGetInt64(rayMap, out long id) && id is >= 0 and <= uint.MaxValue ? (uint)id : throw ArgReader.Bad(invocation, "ray.map", "a map id");
            }

            if (ray["maxDistance"] is { } rayMax)
            {
                max = JsonScalars.TryGetDouble(rayMax, out double m) ? m : throw ArgReader.Bad(invocation, "ray.maxDistance", "a finite number");
            }
        }
        else
        {
            double[] ndc = ArgReader.Numbers(invocation.Args["ndc"]) is { Length: 2 } pair ? pair : throw ArgReader.Bad(invocation, "ndc", "\"x,y\" with two numbers");
            double aspect = ArgReader.Range(invocation, "aspect", invocation.OptionalNumber("aspect") ?? (16.0 / 9), 0.1, 10);
            ArgReader.Range(invocation, "ndc", ndc[0], -1, 1);
            ArgReader.Range(invocation, "ndc", ndc[1], -1, 1);
            max = ArgReader.Range(invocation, "maxDistance", max, 0.001, LargestMaxDistance);
            PickRay fromCamera = CameraMath.RayThrough(view.Camera, map, ndc[0], ndc[1], aspect, max);
            origin = fromCamera.Origin;
            direction = fromCamera.Direction;

            // A top-down ray starts CameraMath.TopEyeHeight above the target, so maxDistance counts from the target plane.
            startOffset = view.Camera.Mode == CameraMode.Top ? CameraMath.TopEyeHeight : 0;
        }

        ArgReader.Range(invocation, "maxDistance", max, 0.001, LargestMaxDistance);
        max += startOffset;
        double length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y) + (direction.Z * direction.Z));
        if (length < 1e-9)
        {
            throw ArgReader.Bad(invocation, "ray.direction", "a non-zero vector");
        }

        direction = new WorldPoint(direction.X / length, direction.Y / length, direction.Z / length);
        string[] layers = ChooseLayers(invocation, context, view);
        return await Task.Run(() => Cast(context, map, origin, direction, max, layers, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static string[] ChooseLayers(CommandInvocation invocation, AtlasContext context, ViewState view)
    {
        IReadOnlyList<string> requested = invocation.GetStringList("layers");
        if (requested.Count == 0)
        {
            return [.. view.Layers.Where(p => p.Value.Visible && LayerCatalog.Get(p.Key).Tiled && context.Layers.TryGet(p.Key, out _)).Select(p => p.Key)];
        }

        foreach (string id in requested)
        {
            if (!LayerCatalog.TryGet(id, out LayerInfo info))
            {
                throw new CommandException(CommandException.BadArguments, $"query.pick: unknown layer '{id}' (see layers.list).");
            }

            if (!info.Tiled || !context.Layers.TryGet(id, out _))
            {
                throw new CommandException(CommandException.Unavailable, $"query.pick: layer '{id}' has no tile source in this host.");
            }
        }

        return [.. requested.Distinct(StringComparer.Ordinal)];
    }

    private static JsonObject Cast(AtlasContext context, uint map, WorldPoint origin, WorldPoint direction, double max, string[] layers, CancellationToken cancellationToken)
    {
        TileKey[] tiles = [.. TileTraversal.Along(map, origin, direction, max), TileKey.Global(map)];
        int chunks = 0;
        int skipped = 0;
        var problems = new JsonArray();
        PickHit? best = null;
        foreach (TileKey tile in tiles)
        {
            foreach (string layer in layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (context.Cache.Peek(layer, tile).State is TileState.Missing or TileState.Empty or TileState.NotApplicable or TileState.Rejected)
                {
                    skipped++;
                    continue;
                }

                LayerBuild build;
                try
                {
                    build = context.Cache.Get(layer, tile, cancellationToken);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
                {
                    problems.Add(new JsonObject { ["layer"] = layer, ["tile"] = new JsonArray(tile.MapId, tile.X, tile.Y), ["message"] = e.Message });
                    continue;
                }

                foreach (GeometryChunk chunk in build.Chunks)
                {
                    chunks++;
                    if (ChunkPicker.Nearest(chunk, origin, direction, max) is { } hit && (best is null || hit.Distance < best.Distance))
                    {
                        JsonObject details = chunk.Attributes.TryGetValue(hit.Attribute, out JsonObject? described) ? (JsonObject)described.DeepClone() : [];
                        best = new PickHit(layer, tile, hit.Distance, hit.Point, hit.Primitive, hit.Attribute, details);
                    }
                }
            }
        }

        var tested = new JsonObject { ["tiles"] = tiles.Length, ["layers"] = new JsonArray([.. layers.Select(l => (JsonNode)l)]), ["chunks"] = chunks, ["skipped"] = skipped };
        if (problems.Count > 0)
        {
            tested["problems"] = problems;
        }

        if (best is null)
        {
            return new JsonObject { ["hit"] = false, ["tested"] = tested };
        }

        JsonObject result = best.ToJson();
        result["hit"] = true;
        result["tested"] = tested;
        result["overlay"] = new JsonObject
        {
            ["points"] = new JsonArray(new JsonObject
            {
                ["at"] = HostJson.Point(best.Point),
                ["label"] = $"{best.LayerId} {best.Primitive} #{best.Attribute}",
            }),
        };
        return result;
    }
}

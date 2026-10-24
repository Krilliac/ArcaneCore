using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// Headless rendering and export: registers the CPU <see cref="IHeadlessRenderer"/> in the services (the
/// <c>capture</c> command's headless path) and the commands <c>render.png</c>, <c>export.obj</c> and <c>export.json</c>.
/// Geometry is read through the shared <see cref="LayerCache"/>; this module registers no layer sources.
/// </summary>
public sealed class RenderModule : IAtlasModule
{
    /// <summary>Default image width of <c>render.png</c>.</summary>
    public const int DefaultWidth = 1280;

    /// <summary>Default image height of <c>render.png</c>.</summary>
    public const int DefaultHeight = 720;

    /// <inheritdoc />
    public string Name => "render";

    /// <inheritdoc />
    public int Order => ModuleOrder.Layers;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var renderer = new CpuRenderer(context.Layers, context.Cache, context.Services.Get<Textures.TextureCache>(), context.Services.Get<CpuMaterials>());
        context.Services.Add<IHeadlessRenderer>(renderer);
        var handlers = new Handlers(context, renderer, context.Loggers.CreateLogger("ArcaneCore.Atlas.Render"));
        context.Commands.Register(new CommandDefinition(
            "render.png",
            "Render the current view (or the view with a merge patch applied) to a PNG with the CPU renderer; returns the path and render statistics.",
            [
                new CommandArg("width", ArgType.Integer, $"Image width in pixels, {CpuRenderer.MinDimension} to {CpuRenderer.MaxDimension}.", Default: JsonValue.Create(DefaultWidth)),
                new CommandArg("height", ArgType.Integer, $"Image height in pixels, {CpuRenderer.MinDimension} to {CpuRenderer.MaxDimension}; width times height is at most {CpuRenderer.MaxPixels}.", Default: JsonValue.Create(DefaultHeight)),
                new CommandArg("out", ArgType.String, "Path of the PNG to write; must end with .png and lie outside every git checkout. Absolute unless the command runs in your own process.", Required: true),
                new CommandArg("view", ArgType.Object, "JSON merge patch (RFC 7396) applied to a copy of the current view, for example {\"camera\":{\"mode\":\"top\"}}; the shared view is not changed."),
            ],
            handlers.RenderPng));
        context.Commands.Register(new CommandDefinition(
            "export.obj",
            "Export the geometry of tiled layers around a point as Wavefront OBJ in world coordinates (one object per layer and tile, groups per colour key) plus an MTL with the layer colours.",
            ExportArgs("Path of the .obj to write (the .mtl goes next to it); must end with .obj and lie outside every git checkout."),
            handlers.ExportObj));
        context.Commands.Register(new CommandDefinition(
            "export.json",
            "Export the geometry of tiled layers around a point as JSON: per chunk the layer, tile, world-space positions, indices and attributes.",
            ExportArgs("Path of the .json to write; must end with .json and lie outside every git checkout."),
            handlers.ExportJson));
    }

    private static CommandArg[] ExportArgs(string outDescription) =>
    [
        new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
        new CommandArg("x", ArgType.Number, "World X (north) of the centre; give with y. Default: the current view's camera target when it shows this map."),
        new CommandArg("y", ArgType.Number, "World Y (west) of the centre; give with x."),
        new CommandArg("radius", ArgType.Integer, $"Tiles in each direction around the centre tile, 0 to {ViewState.MaxLoadRadiusTiles}.", Default: JsonValue.Create(ExportRequest.DefaultRadius)),
        new CommandArg("layers", ArgType.StringList, "Layer ids to export (tiled layers with a source). Default: the layers the current view shows."),
        new CommandArg("out", ArgType.String, outDescription, Required: true),
    ];

    private sealed class Handlers(AtlasContext context, CpuRenderer renderer, ILogger log)
    {
        public Task<JsonNode?> RenderPng(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            int width = Dimension(invocation, "width", DefaultWidth);
            int height = Dimension(invocation, "height", DefaultHeight);
            try
            {
                CpuRenderer.ValidateSize(width, height);
            }
            catch (ArgumentOutOfRangeException e)
            {
                throw new CommandException(CommandException.BadArguments, $"render.png: {e.Message}");
            }

            string path = OutputPaths.Resolve("render.png", invocation.GetString("out"), ".png", invocation.Headless);
            ViewState view = context.View.Current;
            if (invocation.OptionalObject("view") is { } patch)
            {
                view = view.Apply(patch);
            }

            return Task.Run<JsonNode?>(
                () =>
                {
                    RenderedImage image = renderer.Render(view, width, height, cancellationToken);
                    OutputPaths.WriteAtomically(path, stream => stream.Write(image.Png));
                    log.LogInformation(
                        "render.png wrote {Path}: {Width}x{Height}, {Bytes} bytes",
                        path,
                        width,
                        height,
                        image.Png.Length);
                    return new JsonObject
                    {
                        ["path"] = path,
                        ["width"] = width,
                        ["height"] = height,
                        ["bytes"] = image.Png.Length,
                        ["stats"] = image.Stats.DeepClone(),
                    };
                },
                cancellationToken);
        }

        public Task<JsonNode?> ExportObj(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            ExportRequest request = ExportRequest.Parse(invocation, context.View.Current, context.Layers);
            string objPath = OutputPaths.Resolve("export.obj", invocation.GetString("out"), ".obj", invocation.Headless);
            string mtlPath = Path.ChangeExtension(objPath, ".mtl");
            if (Directory.Exists(mtlPath))
            {
                throw new CommandException(CommandException.BadArguments, $"export.obj: the material file '{mtlPath}' is an existing directory.");
            }

            return Task.Run<JsonNode?>(
                () =>
                {
                    ExportSummary summary = ObjExporter.Write(
                        request,
                        ExportSource.Tiles(request, context.Layers, context.Cache, cancellationToken),
                        objPath,
                        mtlPath,
                        cancellationToken);
                    var result = Finish("export.obj", summary, objPath);
                    result["mtl"] = mtlPath;
                    return result;
                },
                cancellationToken);
        }

        public Task<JsonNode?> ExportJson(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            ExportRequest request = ExportRequest.Parse(invocation, context.View.Current, context.Layers);
            string path = OutputPaths.Resolve("export.json", invocation.GetString("out"), ".json", invocation.Headless);
            return Task.Run<JsonNode?>(
                () => Finish(
                    "export.json",
                    JsonExporter.Write(request, ExportSource.Tiles(request, context.Layers, context.Cache, cancellationToken), path, cancellationToken),
                    path),
                cancellationToken);
        }

        private static int Dimension(CommandInvocation invocation, string name, int fallback)
        {
            long value = invocation.OptionalInt(name) ?? fallback;
            return value is >= int.MinValue and <= int.MaxValue
                ? (int)value
                : throw new CommandException(CommandException.BadArguments, $"render.png: argument '{name}' is out of range.");
        }

        /// <summary>The command result, or the refusal when no tile produced a single primitive (an empty file would hide why).</summary>
        private JsonObject Finish(string command, ExportSummary summary, string path)
        {
            if (!summary.Written)
            {
                throw new CommandException(
                    CommandException.Unavailable,
                    $"{command}: nothing to export, no tile produced geometry ({summary.Describe()}). Check tiles.status for the reasons.");
            }

            var result = new JsonObject
            {
                ["path"] = path,
                ["bytes"] = new FileInfo(path).Length,
            };
            summary.AddTo(result);
            log.LogInformation(
                "{Command} wrote {Path}: {Objects} objects, {Vertices} vertices, {Triangles} triangles, {Lines} lines",
                command,
                path,
                summary.Objects,
                summary.Vertices,
                summary.Triangles,
                summary.Lines);
            return result;
        }
    }
}

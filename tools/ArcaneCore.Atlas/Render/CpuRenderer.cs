using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Materials;
using ArcaneCore.Atlas.Textures;

namespace ArcaneCore.Atlas.Render;

/// <summary>The pixels of one render, kept with the scene it drew (the tests read depth and colour back from it).</summary>
internal sealed class RenderedFrame(Framebuffer buffer, Scene scene, JsonObject stats)
{
    public Framebuffer Buffer { get; } = buffer;

    public Scene Scene { get; } = scene;

    public JsonObject Stats { get; } = stats;
}

/// <summary>
/// The headless renderer behind <c>capture</c> and <c>render.png</c>: a software rasterizer with a z-buffer that draws
/// the same camera (<see cref="CameraMath"/>) and the same layer styles as the UI.
/// <para>
/// Pipeline: gather the visible tiled layers' tiles through the shared <see cref="LayerCache"/> (see <see cref="Scene"/>);
/// pass 1 rasterizes the triangles of every layer that is not <see cref="RenderMode.XRay"/>, writing the biased
/// occluder depth for all of them and painting the opaque <see cref="RenderMode.Fill"/> and <see cref="RenderMode.Both"/>
/// fills; pass 2 blends the translucent fills back to front; pass 3 strokes the lines and points of
/// <see cref="RenderMode.Wire"/> and <see cref="RenderMode.Both"/> layers against the occluder depth and of
/// <see cref="RenderMode.XRay"/> layers without a depth test; then the HUD is painted.
/// </para>
/// <para>
/// Thread safety: an instance holds only its two collaborators and may render concurrently; every render owns its
/// buffers. Output depends only on the view, the size and the cached geometry.
/// </para>
/// </summary>
/// <param name="layers">Which source builds which layer.</param>
/// <param name="cache">The shared tile cache every geometry consumer goes through.</param>
public sealed class CpuRenderer(LayerRegistry layers, LayerCache cache) : IHeadlessRenderer
{
    private readonly LayerRegistry _layers = layers ?? throw new ArgumentNullException(nameof(layers));
    private readonly LayerCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly TextureCache? _textures;
    private readonly CpuMaterials _materials = new();

    /// <summary>
    /// A renderer that also draws textured batches (design section 2.7): chunks with batches are sampled through
    /// <paramref name="textures"/> and <paramref name="materials"/>. Without a texture cache such chunks are drawn flat in the
    /// layer colour, as before.
    /// </summary>
    internal CpuRenderer(LayerRegistry layers, LayerCache cache, TextureCache? textures, CpuMaterials? materials)
        : this(layers, cache)
    {
        _textures = textures;
        _materials = materials ?? _materials;
    }

    /// <summary>Smallest image edge, pixels.</summary>
    public const int MinDimension = 16;

    /// <summary>Largest image edge, pixels.</summary>
    public const int MaxDimension = 8192;

    /// <summary>Largest image area, pixels (3840 x 2160), which bounds the render's memory at about 12 bytes per pixel.</summary>
    public const long MaxPixels = 3840L * 2160;

    /// <summary>Primary directional sun light (unit vector in world space, from upper north-east).</summary>
    internal static Vector3 LightDir { get; } = Vector3.Normalize(new Vector3((float)Shading.Light.X, (float)Shading.Light.Y, (float)Shading.Light.Z));

    /// <summary>Secondary sky/fill light direction (unit vector in world space, from opposite upper south-west).</summary>
    internal static Vector3 FillLightDir { get; } = Vector3.Normalize(new Vector3(-0.45f, 0.35f, 0.82f));

    /// <summary>
    /// Shades a triangle using directional sun lighting (<c>LightDir</c>), secondary sky/fill lighting, subtle world-Z
    /// hypsometric elevation relief, and steep-slope darkening (<c>|n.Z| &lt; 0.5</c>).
    /// </summary>
    internal static Rgb ShadeTriangle(Rgb baseColor, Vector3 v0, Vector3 v1, Vector3 v2, string? layerId = null, bool both = false)
        => ShadeTriangle(
            baseColor,
            new WorldPoint(v0.X, v0.Y, v0.Z),
            new WorldPoint(v1.X, v1.Y, v1.Z),
            new WorldPoint(v2.X, v2.Y, v2.Z),
            layerId,
            both);

    /// <summary>
    /// Shades a world-space triangle using directional sun lighting (<c>LightDir</c>), secondary sky/fill lighting,
    /// subtle world-Z hypsometric elevation relief, and steep-slope darkening (<c>|n.Z| &lt; 0.5</c>).
    /// </summary>
    internal static Rgb ShadeTriangle(Rgb baseColor, WorldPoint v0, WorldPoint v1, WorldPoint v2, string? layerId = null, bool both = false)
    {
        _ = layerId;
        return baseColor.Scaled(TriangleIntensity(v0, v1, v2, both));
    }

    /// <summary>
    /// The lighting factor <see cref="ShadeTriangle(Rgb, WorldPoint, WorldPoint, WorldPoint, string?, bool)"/> applies to a colour; a
    /// textured triangle scales each texel by it, so a flat quad shades its texture exactly as it would shade a flat colour.
    /// </summary>
    internal static double TriangleIntensity(WorldPoint v0, WorldPoint v1, WorldPoint v2, bool both)
    {
        double ux = v1.X - v0.X;
        double uy = v1.Y - v0.Y;
        double uz = v1.Z - v0.Z;
        double vx = v2.X - v0.X;
        double vy = v2.Y - v0.Y;
        double vz = v2.Z - v0.Z;
        double nx = (uy * vz) - (uz * vy);
        double ny = (uz * vx) - (ux * vz);
        double nz = (ux * vy) - (uy * vx);
        double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (!(length > 0))
        {
            nx = 0;
            ny = 0;
            nz = 1;
        }
        else
        {
            nx /= length;
            ny /= length;
            nz /= length;
        }

        // Orient the face normal to the upper hemisphere so terrain/model hillshading is winding-independent.
        if (nz < 0 || (nz == 0 && ((nx * Shading.Light.X) + (ny * Shading.Light.Y)) < 0))
        {
            nx = -nx;
            ny = -ny;
            nz = -nz;
        }

        double sunDot = Math.Max(0.0, (nx * Shading.Light.X) + (ny * Shading.Light.Y) + (nz * Shading.Light.Z));
        double fillDot = Math.Max(0.0, (nx * FillLightDir.X) + (ny * FillLightDir.Y) + (nz * FillLightDir.Z));
        double slopeAmount = Math.Clamp(1.0 - nz, 0.0, 1.0);
        double fillTerm = 0.12 * slopeAmount * fillDot;
        double intensity = Shading.Ambient + (Shading.Diffuse * sunDot) + fillTerm;

        // Emphasize steep slopes (|n.Z| < 0.5) so cliffs and walls stand out in top-down and 3D views.
        double absNz = Math.Abs(nz);
        if (absNz < 0.5)
        {
            double steepness = (0.5 - absNz) / 0.5;
            intensity *= 1.0 - (0.22 * steepness);
        }

        // Subtle world-Z hypsometric elevation relief outside a small sea-level deadband.
        double meanZ = (v0.Z + v1.Z + v2.Z) / 3.0;
        if (double.IsFinite(meanZ))
        {
            double effectiveZ = meanZ > 6.0 ? meanZ - 6.0 : meanZ < -6.0 ? meanZ + 6.0 : 0.0;
            double elevationMod = Math.Tanh(effectiveZ / 220.0) * 0.05;
            intensity += elevationMod;
        }

        intensity = Math.Clamp(intensity, Shading.Ambient, 1.0);
        return intensity * (both ? Shading.BothFillScale : 1.0);
    }

    /// <inheritdoc />
    public RenderedImage Render(ViewState view, int width, int height, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        RenderedFrame frame = Rasterize(view, width, height, cancellationToken);
        byte[] png = PngEncoder.Encode(frame.Buffer.Pixels, width, height);
        frame.Stats["elapsedMs"] = Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 3);
        return new RenderedImage(png, width, height, frame.Stats);
    }

    /// <summary>Checks that an image of this size can be rendered; throws <see cref="ArgumentOutOfRangeException"/> otherwise.</summary>
    public static void ValidateSize(int width, int height)
    {
        if (width is < MinDimension or > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, $"The width must be {MinDimension} to {MaxDimension} pixels.");
        }

        if (height is < MinDimension or > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, $"The height must be {MinDimension} to {MaxDimension} pixels.");
        }

        if ((long)width * height > MaxPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(height), $"{width} x {height} is over the {MaxPixels} pixel limit.");
        }
    }

    internal RenderedFrame Rasterize(ViewState view, int width, int height, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        ValidateSize(width, height);
        long start = Stopwatch.GetTimestamp();
        Scene scene = Scene.Build(view, _layers, _cache, cancellationToken);
        var buffer = new Framebuffer(width, height, Rgb.Background);
        var transform = new ViewTransform(view.Camera, width, height);
        var raster = new Rasterizer(buffer, transform);
        int splitX = Math.Clamp((int)Math.Round(view.SplitPosition * width), 0, width);
        CpuTextureSet? textures = _textures is null ? null : new CpuTextureSet(_textures, cancellationToken);
        new ScenePass(scene, raster, transform, width, splitX, textures, _materials, cancellationToken).Paint();
        if (view.ShowHud)
        {
            raster.SetScissor(0, width);
            Hud.Draw(buffer, raster, view, transform, scene, splitX);
        }

        JsonObject stats = Stats(view, scene, width, height);
        stats["elapsedMs"] = Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 3);
        return new RenderedFrame(buffer, scene, stats);
    }

    private static JsonObject Stats(ViewState view, Scene scene, int width, int height)
    {
        CameraState camera = view.Camera;
        var layerStats = new JsonArray();
        foreach (SceneLayer layer in scene.Layers)
        {
            layerStats.Add(new JsonObject
            {
                ["id"] = layer.Info.Id,
                ["mode"] = ViewState.Name(layer.Mode),
                ["opacity"] = layer.Opacity,
                ["color"] = layer.Style.Color,
                ["colorByAttribute"] = layer.Style.ColorByAttribute,
                ["tiles"] = layer.Tiles.ToJson(),
                ["triangles"] = layer.Triangles,
                ["lines"] = layer.Lines,
                ["points"] = layer.Points,
            });
        }

        var problems = new JsonArray();
        foreach (TileProblem problem in scene.Problems)
        {
            problems.Add(problem.ToJson());
        }

        return new JsonObject
        {
            ["map"] = view.MapId,
            ["width"] = width,
            ["height"] = height,
            ["compare"] = ViewState.Name(view.Compare),
            ["camera"] = new JsonObject
            {
                ["mode"] = ViewState.Name(camera.Mode),
                ["target"] = new JsonArray(camera.Target.X, camera.Target.Y, camera.Target.Z),
                ["distance"] = camera.Distance,
                ["yaw"] = camera.Yaw,
                ["pitch"] = camera.Pitch,
                ["fov"] = camera.Fov,
            },
            ["tiles"] = scene.Tiles.ToJson(),
            ["triangles"] = scene.Triangles,
            ["lines"] = scene.Lines,
            ["points"] = scene.Points,
            ["layers"] = layerStats,
            ["layersWithoutSource"] = Strings(scene.LayersWithoutSource),
            ["hiddenByCompare"] = Strings(scene.HiddenByCompare),
            ["untiledSkipped"] = Strings(scene.UntiledSkipped),
            ["problems"] = problems,
            ["problemsOmitted"] = scene.ProblemsOmitted,
        };
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }

    /// <summary>One translucent triangle waiting for the back-to-front pass.</summary>
    private readonly record struct TranslucentTriangle(float Depth, int Layer, int Chunk, int Triangle, int Batch = -1);

    /// <summary>The three passes over one scene. Single-use, single-threaded.</summary>
    private sealed class ScenePass(
        Scene scene,
        Rasterizer raster,
        ViewTransform transform,
        int width,
        int splitX,
        CpuTextureSet? textures,
        CpuMaterials materials,
        CancellationToken cancellationToken)
    {
        private readonly Dictionary<(int Layer, int Chunk), BatchShader[]> _shaders = [];
        private ViewVertex[] _scratch = [];

        public void Paint()
        {
            List<TranslucentTriangle> translucent = [];
            for (int l = 0; l < scene.Layers.Count; l++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SceneLayer layer = scene.Layers[l];
                if (layer.Mode != RenderMode.XRay)
                {
                    SetScissor(layer);
                    for (int c = 0; c < layer.Chunks.Count; c++)
                    {
                        Surfaces(layer, l, c, translucent);
                    }
                }
            }

            BlendTranslucent(translucent);
            for (int l = 0; l < scene.Layers.Count; l++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SceneLayer layer = scene.Layers[l];
                if (layer.Mode != RenderMode.Fill)
                {
                    SetScissor(layer);
                    foreach (SceneChunk chunk in layer.Chunks)
                    {
                        Strokes(layer, chunk);
                    }
                }
            }
        }

        private void SetScissor(SceneLayer layer)
        {
            if (scene.Compare == CompareMode.Split)
            {
                if (layer.Side == LayerSide.Reference)
                {
                    raster.SetScissor(splitX, width);
                }
                else
                {
                    raster.SetScissor(0, splitX);
                }
            }
            else
            {
                raster.SetScissor(0, width);
            }
        }

        private void Surfaces(SceneLayer layer, int layerIndex, int chunkIndex, List<TranslucentTriangle> translucent)
        {
            SceneChunk sceneChunk = layer.Chunks[chunkIndex];
            GeometryChunk chunk = sceneChunk.Chunk;
            if (chunk.TriangleCount == 0)
            {
                return;
            }

            if (textures is not null && chunk.Batches.Length > 0 && chunk.Uv0 is not null)
            {
                TexturedSurfaces(layer, layerIndex, chunkIndex, textures, translucent);
                return;
            }

            ViewVertex[] vertices = Project(chunk);
            bool paint = layer.Mode is RenderMode.Fill or RenderMode.Both;
            bool opaque = layer.Opacity >= 1.0;
            for (int t = 0; t < chunk.TriangleCount; t++)
            {
                ViewVertex a = vertices[chunk.Triangles[(3 * t) + 0]];
                ViewVertex b = vertices[chunk.Triangles[(3 * t) + 1]];
                ViewVertex c = vertices[chunk.Triangles[(3 * t) + 2]];
                if (paint && opaque)
                {
                    raster.FillTriangle(a, b, c, new TriangleFill(true, true, FillColour(layer, sceneChunk, t), 1.0));
                }
                else
                {
                    raster.FillTriangle(a, b, c, new TriangleFill(true, false, default, 1.0));
                    if (paint)
                    {
                        translucent.Add(new TranslucentTriangle((float)((a.D + b.D + c.D) / 3.0), layerIndex, chunkIndex, t));
                    }
                }
            }
        }

        /// <summary>
        /// A chunk with batches: each batch's material makes the pixel shader of its triangles. Opaque and alpha-key batches of an opaque
        /// layer paint directly (alpha-key texels that fail the test write nothing); every other batch joins the back-to-front pass.
        /// </summary>
        private void TexturedSurfaces(SceneLayer layer, int layerIndex, int chunkIndex, CpuTextureSet textureSet, List<TranslucentTriangle> translucent)
        {
            GeometryChunk chunk = layer.Chunks[chunkIndex].Chunk;
            ViewVertex[] vertices = Project(chunk);
            bool paint = layer.Mode is RenderMode.Fill or RenderMode.Both;
            bool both = layer.Mode == RenderMode.Both;
            bool layerOpaque = layer.Opacity >= 1.0;
            var shaders = new BatchShader[chunk.Batches.Length];
            _shaders[(layerIndex, chunkIndex)] = shaders;
            for (int batchIndex = 0; batchIndex < shaders.Length; batchIndex++)
            {
                GeometryBatch batch = chunk.Batches[batchIndex];
                Material material = chunk.Materials[batch.Material];
                BatchShader shader = materials.For(material.Kind).Prepare(material, textureSet, layer.Colour);
                shaders[batchIndex] = shader;
                int first = (int)batch.FirstTriangle;
                int end = first + (int)batch.TriangleCount;
                for (int t = first; t < end; t++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uint ia = chunk.Triangles[(3 * t) + 0];
                    uint ib = chunk.Triangles[(3 * t) + 1];
                    uint ic = chunk.Triangles[(3 * t) + 2];
                    ViewVertex a = vertices[ia];
                    ViewVertex b = vertices[ib];
                    ViewVertex c = vertices[ic];
                    shader.Intensity = TriangleIntensity(chunk.Vertex(ia), chunk.Vertex(ib), chunk.Vertex(ic), both);
                    if (paint && layerOpaque && shader.WritesDepth)
                    {
                        raster.FillTriangle(a, b, c, new TriangleFill(true, true, default, 1.0), shader);
                        continue;
                    }

                    if (shader.WritesDepth)
                    {
                        raster.FillTriangle(a, b, c, new TriangleFill(true, false, default, 1.0), shader);
                    }

                    if (paint)
                    {
                        translucent.Add(new TranslucentTriangle((float)((a.D + b.D + c.D) / 3.0), layerIndex, chunkIndex, t, batchIndex));
                    }
                }
            }
        }

        private void BlendTranslucent(List<TranslucentTriangle> translucent)
        {
            if (translucent.Count == 0)
            {
                return;
            }

            TranslucentTriangle[] order = [.. translucent];
            Array.Sort(order, static (x, y) =>
            {
                // Farthest first; ties broken by draw order so the result never depends on the sort's stability.
                int byDepth = y.Depth.CompareTo(x.Depth);
                if (byDepth != 0)
                {
                    return byDepth;
                }

                int byLayer = x.Layer.CompareTo(y.Layer);
                if (byLayer != 0)
                {
                    return byLayer;
                }

                int byChunk = x.Chunk.CompareTo(y.Chunk);
                return byChunk != 0 ? byChunk : x.Triangle.CompareTo(y.Triangle);
            });
            foreach (TranslucentTriangle item in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SceneLayer layer = scene.Layers[item.Layer];
                SceneChunk sceneChunk = layer.Chunks[item.Chunk];
                SetScissor(layer);
                GeometryChunk chunk = sceneChunk.Chunk;
                uint ia = chunk.Triangles[(3 * item.Triangle) + 0];
                uint ib = chunk.Triangles[(3 * item.Triangle) + 1];
                uint ic = chunk.Triangles[(3 * item.Triangle) + 2];
                if (item.Batch >= 0)
                {
                    BatchShader shader = _shaders[(item.Layer, item.Chunk)][item.Batch];
                    shader.Intensity = TriangleIntensity(chunk.Vertex(ia), chunk.Vertex(ib), chunk.Vertex(ic), layer.Mode == RenderMode.Both);
                    raster.FillTriangle(
                        ViewVertexOf(chunk, ia),
                        ViewVertexOf(chunk, ib),
                        ViewVertexOf(chunk, ic),
                        new TriangleFill(false, true, default, layer.Opacity),
                        shader);
                    continue;
                }

                raster.FillTriangle(
                    ViewVertexOf(chunk, ia),
                    ViewVertexOf(chunk, ib),
                    ViewVertexOf(chunk, ic),
                    new TriangleFill(false, true, FillColour(layer, sceneChunk, item.Triangle), layer.Opacity));
            }
        }

        private void Strokes(SceneLayer layer, SceneChunk sceneChunk)
        {
            GeometryChunk chunk = sceneChunk.Chunk;
            if (chunk.LineCount == 0 && chunk.PointCount == 0)
            {
                return;
            }

            ViewVertex[] vertices = Project(chunk);
            bool depthTest = layer.Mode != RenderMode.XRay;
            for (int i = 0; i < chunk.LineCount; i++)
            {
                var style = new StrokeStyle(sceneChunk.ColourOf(chunk.LineAttributes[i], layer.Colour), layer.Opacity, Rasterizer.LineHalfWidth, depthTest);
                raster.StrokeLine(vertices[chunk.Lines[2 * i]], vertices[chunk.Lines[(2 * i) + 1]], style);
            }

            for (int i = 0; i < chunk.PointCount; i++)
            {
                var style = new StrokeStyle(sceneChunk.ColourOf(chunk.PointAttributes[i], layer.Colour), layer.Opacity, Rasterizer.PointHalfWidth, depthTest);
                raster.DrawPoint(vertices[chunk.Points[i]], style);
            }
        }

        private static Rgb FillColour(SceneLayer layer, SceneChunk sceneChunk, int triangle)
        {
            GeometryChunk chunk = sceneChunk.Chunk;
            Rgb baseColour = sceneChunk.ColourOf(chunk.TriangleAttributes[triangle], layer.Colour);
            WorldPoint v0 = chunk.Vertex(chunk.Triangles[3 * triangle]);
            WorldPoint v1 = chunk.Vertex(chunk.Triangles[(3 * triangle) + 1]);
            WorldPoint v2 = chunk.Vertex(chunk.Triangles[(3 * triangle) + 2]);
            return ShadeTriangle(baseColour, v0, v1, v2, layer.Info.Id, layer.Mode == RenderMode.Both);
        }

        private ViewVertex[] Project(GeometryChunk chunk)
        {
            int count = chunk.VertexCount;
            if (_scratch.Length < count)
            {
                _scratch = new ViewVertex[Math.Max(count, _scratch.Length * 2)];
            }

            WorldPoint relative = chunk.Origin - transform.Eye;
            float[] p = chunk.Positions;
            for (int i = 0; i < count; i++)
            {
                _scratch[i] = transform.ToView(relative.X + p[3 * i], relative.Y + p[(3 * i) + 1], relative.Z + p[(3 * i) + 2]);
            }

            Vector2[]? uv = chunk.Uv0;
            if (uv is not null)
            {
                for (int i = 0; i < count; i++)
                {
                    _scratch[i] = _scratch[i] with { U = uv[i].X, V = uv[i].Y };
                }
            }

            return _scratch;
        }

        private ViewVertex ViewVertexOf(GeometryChunk chunk, uint index)
        {
            WorldPoint relative = chunk.Origin - transform.Eye;
            int i = (int)index * 3;
            float[] p = chunk.Positions;
            ViewVertex vertex = transform.ToView(relative.X + p[i], relative.Y + p[i + 1], relative.Z + p[i + 2]);
            return chunk.Uv0 is { } uv ? vertex with { U = uv[index].X, V = uv[index].Y } : vertex;
        }
    }
}

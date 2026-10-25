using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Materials;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>A model package failure with a stable code that the commands and the endpoint map to their own error forms.</summary>
public sealed class ModelPackageException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Arguments missing, mistyped or out of range, or a model kind this build does not package.</summary>
    public const string BadArguments = "bad-args";

    /// <summary>The model is not in the client files.</summary>
    public const string NotFound = "not-found";

    /// <summary>The model file is damaged or not a 1.12.1 model.</summary>
    public const string Rejected = "rejected";

    public string Code { get; } = code;
}

/// <summary>One draw batch of a package, for the summaries the commands return.</summary>
public sealed record ModelBatchInfo(int FirstTriangle, int TriangleCount, Material Material, int Geoset, int Priority);

/// <summary>
/// A built model package: the ATG1 v2 bytes plus the facts the commands report. Immutable; the shared <see cref="Wire"/> array must
/// not be modified (callers copy or stream it).
/// </summary>
public sealed record ModelPackage(
    string Key,
    string Path,
    int View,
    byte[] Wire,
    int Vertices,
    int Triangles,
    IReadOnlyList<ModelBatchInfo> Batches,
    IReadOnlyList<string> Problems,
    JsonObject Model);

/// <summary>Builds the package of one kind of model (<c>m2</c> here; lane A3 adds <c>wmo</c>). Implementations are thread-safe.</summary>
public interface IModelPackageSource
{
    /// <summary>The key prefix without the colon, for example <c>m2</c>.</summary>
    string Kind { get; }

    /// <summary>Whether <paramref name="path"/> (an in-archive path with a lower or upper case extension) is a model of this kind.</summary>
    bool Accepts(string path);

    /// <summary>
    /// Builds the package of the model at <paramref name="canonicalPath"/> (already checked to exist, in the archive's own spelling).
    /// </summary>
    /// <exception cref="ModelPackageException">The view does not exist or the file is damaged.</exception>
    ModelPackage Build(IClientFileSource files, string canonicalPath, int view, CancellationToken cancellationToken);
}

/// <summary>
/// Model packages by (key, view), built once and kept as wire bytes. Thread-safe; concurrent requests for one key share one build
/// and a build that throws is not cached. Reported by <c>status</c> as <c>model-packages</c> and emptied by <c>cache.clear</c>.
/// </summary>
public sealed class ModelPackageCache : ICacheParticipant
{
    /// <summary>Default wire budget (design section 3): 256 MB.</summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    /// <summary>Largest view index a request may name.</summary>
    public const int MaxView = 3;

    private readonly IClientFileSource _files;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IModelPackageSource> _sources = new(StringComparer.Ordinal);
    private bool _frozen;
    private readonly CostBoundedLazyCache<(string Key, int View), ModelPackage> _packages;

    public ModelPackageCache(IClientFileSource files, long budgetBytes = DefaultBudgetBytes)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _packages = new CostBoundedLazyCache<(string Key, int View), ModelPackage>(budgetBytes);
        Register(new M2PackageSource());
    }

    public string Name => "model-packages";

    public long Bytes => _packages.Cost;

    public int Count => _packages.Count;

    /// <summary>Adds the source of another model kind (for example WMO).</summary>
    /// <exception cref="ArgumentException">The kind already has a source.</exception>
    /// <exception cref="InvalidOperationException">The registry was frozen (all modules have registered).</exception>
    public void Register(IModelPackageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_frozen)
            {
                throw new InvalidOperationException("The model package sources are frozen; register them while the modules register.");
            }

            if (!_sources.TryAdd(source.Kind, source))
            {
                throw new ArgumentException($"Model kind '{source.Kind}' already has a package source.");
            }
        }
    }

    public void Clear() => _packages.Clear();

    /// <summary>Refuses further <see cref="Register"/> calls; the host calls it once every module has registered.</summary>
    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }

    /// <summary>
    /// The package of a model given as <c>m2:&lt;path&gt;</c> or a bare archive path. Only <c>.m2</c> (and the <c>.mdx</c>/<c>.mdl</c>
    /// spellings of it) are packaged in this build.
    /// </summary>
    /// <exception cref="ModelPackageException">Bad arguments, a missing model or a damaged file.</exception>
    public ModelPackage Get(string keyOrPath, int view, CancellationToken cancellationToken)
    {
        if (view is < 0 or > MaxView)
        {
            throw new ModelPackageException(ModelPackageException.BadArguments, $"view must be 0 to {MaxView}.");
        }

        (IModelPackageSource source, string path) = Resolve(keyOrPath);
        string key = source.Kind + ":" + path.ToLowerInvariant();
        try
        {
            return _packages.GetOrLoad(
                (key, view),
                () =>
                {
                    ModelPackage package = source.Build(_files, CanonicalPath(path), view, cancellationToken);
                    return (package, package.Wire.Length + 1024L);
                },
                cancellationToken: cancellationToken);
        }
        catch (InvalidDataException e)
        {
            throw new ModelPackageException(ModelPackageException.Rejected, e.Message, e);
        }
    }

    /// <summary>The key (<c>m2:&lt;lower path&gt;</c>) and in-archive path a request names, without building anything.</summary>
    /// <exception cref="ModelPackageException">Bad arguments or a model that is not in the client files.</exception>
    public (string Key, string Path) Locate(string keyOrPath)
    {
        (IModelPackageSource source, string path) = Resolve(keyOrPath);
        string canonical = CanonicalPath(path);
        return (source.Kind + ":" + canonical.ToLowerInvariant(), canonical);
    }

    private (IModelPackageSource Source, string Path) Resolve(string keyOrPath)
    {
        ArgumentNullException.ThrowIfNull(keyOrPath);
        string text = keyOrPath.Trim();
        string? kind = null;
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && text[..colon].All(char.IsAsciiLetterOrDigit))
        {
            kind = text[..colon].ToLowerInvariant();
            text = text[(colon + 1)..];
        }

        string path = text.Replace('/', '\\').TrimStart('\\');
        if (path.Length == 0)
        {
            throw new ModelPackageException(ModelPackageException.BadArguments, "A model path is required.");
        }

        // A name that tries to leave the archive, or names a drive, is simply not in the (virtual) client file index.
        if (path.Contains(':', StringComparison.Ordinal) || path.Split('\\').Contains(".."))
        {
            throw new ModelPackageException(ModelPackageException.NotFound, $"{path} is not in the client files.");
        }

        IModelPackageSource? source;
        string supported;
        lock (_gate)
        {
            source = kind is not null
                ? _sources.GetValueOrDefault(kind)
                : _sources.Values.FirstOrDefault(s => s.Accepts(path));
            supported = string.Join(", ", _sources.Keys.Order(StringComparer.Ordinal));
        }

        if (source is null || !source.Accepts(path))
        {
            throw new ModelPackageException(ModelPackageException.BadArguments, $"{path} is not a model this build packages (supported kinds: {supported}).");
        }

        return (source, M2PackageSource.NormalizeExtension(path));
    }

    private string CanonicalPath(string path)
    {
        if (!_files.Exists(path))
        {
            throw new ModelPackageException(ModelPackageException.NotFound, $"{path} is not in the client files ({_files.Description}).");
        }

        string key = ClientPath.Key(path);
        foreach (string listed in _files.List(path))
        {
            if (string.Equals(ClientPath.Key(listed), key, StringComparison.Ordinal))
            {
                return listed;
            }
        }

        return path;
    }
}

/// <summary>
/// The minimal M2 package (design section 2.5): view N of the model as one ATG1 v2 chunk in model space (WoW axes, Z up, origin 0)
/// with normals and the first uv set and one batch per texture unit. Each batch has a <c>generic</c> material from the unit's
/// render flags and texture. Skinning, animation, colour and transparency tracks and multi-texture shaders are not part of it.
/// </summary>
public sealed class M2PackageSource : IModelPackageSource
{
    // M2Material flags (wowdev.wiki M2 page, M2Material table): 0x01 unlit, 0x02 unfogged, 0x04 two-sided, 0x08 depth test disabled,
    // 0x10 depth write disabled.
    private const ushort FlagUnlit = 0x01;
    private const ushort FlagUnfogged = 0x02;
    private const ushort FlagTwoSided = 0x04;
    private const ushort FlagNoDepthTest = 0x08;
    private const ushort FlagNoDepthWrite = 0x10;
    private const uint MaxTextureType = 63;
    private const double AlphaKeyReference = 0.5;
    private const string MissingTexture = "missing";

    public string Kind => "m2";

    public bool Accepts(string path)
        => path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    /// <summary>The path with its <c>.mdx</c>/<c>.mdl</c> extension replaced by <c>.m2</c> (the form the archives list).</summary>
    internal static string NormalizeExtension(string path)
    {
        if (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^4] + ".m2";
        }

        return path;
    }

    public ModelPackage Build(IClientFileSource files, string canonicalPath, int view, CancellationToken cancellationToken)
    {
        byte[] bytes = files.Read(canonicalPath) ?? throw new ModelPackageException(ModelPackageException.NotFound, $"{canonicalPath} is not in the client files.");
        M2PackageModel model;
        try
        {
            model = M2PackageModel.Parse(bytes);
        }
        catch (InvalidDataException e)
        {
            throw new ModelPackageException(ModelPackageException.Rejected, $"{canonicalPath}: {e.Message}", e);
        }

        if (view >= model.Views.Length)
        {
            throw new ModelPackageException(ModelPackageException.BadArguments, $"{canonicalPath} has {model.Views.Length} views; view {view} does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return BuildChunk(files, canonicalPath, model, view);
        }
        catch (ArgumentException e)
        {
            throw new ModelPackageException(ModelPackageException.Rejected, $"{canonicalPath}: {e.Message}", e);
        }
        catch (InvalidDataException e)
        {
            throw new ModelPackageException(ModelPackageException.Rejected, $"{canonicalPath}: {e.Message}", e);
        }
    }

    private static ModelPackage BuildChunk(IClientFileSource files, string path, M2PackageModel model, int viewIndex)
    {
        M2ViewData view = model.Views[viewIndex];
        string key = "m2:" + path.ToLowerInvariant();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Problem(string text)
        {
            if (seen.Add(text))
            {
                problems.Add(text);
            }
        }

        var builder = new GeometryBuilder(LayerIds.RefM2Tex, TileKey.Global(0), new WorldPoint(0, 0, 0));
        for (int i = 0; i < model.Positions.Length; i++)
        {
            Vector3 p = model.Positions[i];
            builder.AddVertex(p.X, p.Y, p.Z, new VertexExtras { Normal = model.Normals[i], Uv0 = model.Uv0[i] });
        }

        var submeshInfo = new JsonArray();
        var submeshRanges = new List<(int First, int Count)>[view.Submeshes.Length];
        if (view.Units.Length == 0)
        {
            Material plain = new("generic", BlendMode.Opaque, MaterialFlags.None, []);
            builder.BeginBatch(plain);
            AddTriangles(builder, model, view, 0, view.Triangles.Length, Problem);
            builder.EndBatch();
        }
        else
        {
            foreach (M2UnitEntry unit in view.Units)
            {
                if (unit.Submesh >= view.Submeshes.Length)
                {
                    Problem("bad-submesh");
                    continue;
                }

                M2SubmeshEntry submesh = view.Submeshes[unit.Submesh];
                Material material = MaterialFor(model, unit, Problem);
                int first = builder.TriangleCount;
                builder.BeginBatch(material, submesh.Id | ((uint)(ushort)(short)unit.Priority << 16));
                AddTriangles(builder, model, view, submesh.StartIndex, submesh.IndexCount, Problem);
                builder.EndBatch();
                if (builder.TriangleCount > first)
                {
                    (submeshRanges[unit.Submesh] ??= []).Add((first, builder.TriangleCount - first));
                }
            }
        }

        // Triangle positions in the chunk's own triangle array (what a geoset mask addresses), not index-space values of the M2: chunk
        // triangles are emitted per texture unit, so a submesh drawn by two units appears twice (ranges lists every copy) and triangles
        // skipped as invalid shift the later ones. firstTriangle/triangleCount are the first copy; a submesh no unit draws has 0/0.
        for (int i = 0; i < view.Submeshes.Length; i++)
        {
            List<(int First, int Count)> ranges = submeshRanges[i] ?? [];
            var rangeInfo = new JsonArray();
            foreach ((int rangeFirst, int rangeCount) in ranges)
            {
                rangeInfo.Add(new JsonArray(rangeFirst, rangeCount));
            }

            submeshInfo.Add(new JsonObject
            {
                ["id"] = view.Submeshes[i].Id,
                ["firstTriangle"] = ranges.Count > 0 ? ranges[0].First : 0,
                ["triangleCount"] = ranges.Count > 0 ? ranges[0].Count : 0,
                ["ranges"] = rangeInfo,
            });
        }

        var textures = new JsonArray();
        foreach (M2TextureEntry texture in model.Textures)
        {
            var entry = new JsonObject { ["type"] = texture.Type, ["flags"] = texture.Flags };
            if (texture.Type == 0 && texture.Name.Length > 0)
            {
                entry["key"] = ClientTextureKey(texture.Name);
            }

            textures.Add(entry);
        }

        (Vector3 min, Vector3 max, double radius) = Bounds(model.Positions);
        builder.Model = new JsonObject
        {
            ["key"] = key,
            ["kind"] = "m2",
            ["version"] = model.Version,
            ["name"] = model.Name,
            ["bounds"] = new JsonArray(
                new JsonArray(min.X, min.Y, min.Z),
                new JsonArray(max.X, max.Y, max.Z)),
            ["radius"] = radius,
            ["views"] = model.Views.Length,
            ["view"] = viewIndex,
            ["textures"] = textures,
            ["submeshes"] = submeshInfo,
        };
        if (builder.TriangleCount == 0)
        {
            throw new InvalidDataException("the model has no triangles in this view.");
        }

        GeometryChunk chunk = builder.Build();
        var batches = new List<ModelBatchInfo>(chunk.Batches.Length);
        foreach (GeometryBatch batch in chunk.Batches)
        {
            batches.Add(new ModelBatchInfo((int)batch.FirstTriangle, (int)batch.TriangleCount, chunk.Materials[batch.Material], batch.Geoset, (short)(batch.Tag >> 16)));
        }

        byte[] wire = GeometryWire.Write(chunk);
        return new ModelPackage(key, path, viewIndex, wire, chunk.VertexCount, chunk.TriangleCount, batches, problems, builder.Model);
    }

    private static void AddTriangles(GeometryBuilder builder, M2PackageModel model, M2ViewData view, int start, int count, Action<string> problem)
    {
        if (start < 0 || count < 0 || start + count > view.Triangles.Length)
        {
            problem("bad-submesh-range");
            return;
        }

        if (count % 3 != 0)
        {
            problem("bad-submesh-range");
            count -= count % 3;
        }

        for (int i = 0; i < count; i += 3)
        {
            ushort a = view.Triangles[start + i];
            ushort b = view.Triangles[start + i + 1];
            ushort c = view.Triangles[start + i + 2];
            if (a >= view.Lookup.Length || b >= view.Lookup.Length || c >= view.Lookup.Length)
            {
                problem("bad-triangle-index");
                continue;
            }

            ushort va = view.Lookup[a];
            ushort vb = view.Lookup[b];
            ushort vc = view.Lookup[c];
            if (va >= model.Positions.Length || vb >= model.Positions.Length || vc >= model.Positions.Length)
            {
                problem("bad-triangle-index");
                continue;
            }

            builder.AddTriangle(va, vb, vc, 0, edges: false);
        }
    }

    private static Material MaterialFor(M2PackageModel model, M2UnitEntry unit, Action<string> problem)
    {
        BlendMode blend = BlendMode.Opaque;
        MaterialFlags flags = MaterialFlags.None;
        if (unit.RenderFlags < model.RenderFlags.Length)
        {
            (ushort renderFlags, ushort blendCode) = model.RenderFlags[unit.RenderFlags];
            if (blendCode <= (ushort)BlendMode.Modulate2x)
            {
                blend = (BlendMode)blendCode;
            }
            else
            {
                problem("bad-blend");
            }

            flags |= (renderFlags & FlagUnlit) != 0 ? MaterialFlags.Unlit : 0;
            flags |= (renderFlags & FlagUnfogged) != 0 ? MaterialFlags.Unfogged : 0;
            flags |= (renderFlags & FlagTwoSided) != 0 ? MaterialFlags.TwoSided : 0;
            flags |= (renderFlags & FlagNoDepthTest) != 0 ? MaterialFlags.NoDepthTest : 0;
            flags |= (renderFlags & FlagNoDepthWrite) != 0 ? MaterialFlags.NoDepthWrite : 0;
        }
        else
        {
            problem("bad-render-flags");
        }

        TextureRef texture = new(MissingTexture);
        if (unit.TextureLookup < model.TextureLookup.Length && model.TextureLookup[unit.TextureLookup] < model.Textures.Length)
        {
            M2TextureEntry entry = model.Textures[model.TextureLookup[unit.TextureLookup]];

            // M2 texture flag bit 0 is wrap in S, bit 1 wrap in T; a clear bit clamps.
            int wrapS = (entry.Flags & 1) != 0 ? 0 : 1;
            int wrapT = (entry.Flags & 2) != 0 ? 0 : 1;
            if (entry.Type != 0)
            {
                if (entry.Type > MaxTextureType)
                {
                    throw new InvalidDataException($"texture type {entry.Type} is not an M2 texture type (0 to {MaxTextureType}).");
                }

                texture = new TextureRef(MissingTexture, 0, wrapS, wrapT, (int)entry.Type);
            }
            else if (entry.Name.Length == 0 || !entry.Name.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            {
                problem("bad-texture-name");
                texture = new TextureRef(MissingTexture, 0, wrapS, wrapT);
            }
            else
            {
                texture = new TextureRef(ClientTextureKey(entry.Name), 0, wrapS, wrapT);
            }
        }
        else
        {
            problem("bad-texture-lookup");
        }

        return new Material("generic", blend, flags, [texture], blend == BlendMode.AlphaKey ? AlphaKeyReference : 0);
    }

    private static string ClientTextureKey(string name) => "client:" + name.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    private static (Vector3 Min, Vector3 Max, double Radius) Bounds(Vector3[] positions)
    {
        if (positions.Length == 0)
        {
            return (Vector3.Zero, Vector3.Zero, 0);
        }

        Vector3 min = positions[0];
        Vector3 max = positions[0];
        double radius = 0;
        foreach (Vector3 p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            radius = Math.Max(radius, p.Length());
        }

        return (min, max, radius);
    }
}

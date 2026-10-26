using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Core;

/// <summary>How the camera moves: orbit around a target, free fly, or straight-down orthographic.</summary>
public enum CameraMode
{
    Orbit,
    Fly,
    Top,
}

/// <summary>How a layer's triangles are drawn.</summary>
public enum RenderMode
{
    /// <summary>Hidden-line wireframe: edges, occluded by the depth of every filled layer.</summary>
    Wire,

    /// <summary>Flat-shaded fill.</summary>
    Fill,

    /// <summary>Fill plus edges.</summary>
    Both,

    /// <summary>Edges drawn through everything (no depth test).</summary>
    XRay,
}

/// <summary>Which data the viewport shows.</summary>
public enum CompareMode
{
    /// <summary>Server layers only.</summary>
    Server,

    /// <summary>Reference layers only.</summary>
    Reference,

    /// <summary>Server on the left of the divider, reference on the right, one camera.</summary>
    Split,

    /// <summary>Diff layers over a dimmed server view.</summary>
    Diff,
}

/// <summary>UI chrome theme (the viewport itself is always dark).</summary>
public enum Theme
{
    Graphite,
    Vellum,
}

/// <summary>
/// Camera. <see cref="Yaw"/> is degrees in [0, 360), measured like a WoW orientation: 0 looks along +X (north),
/// 90 along +Y (west). <see cref="Pitch"/> is degrees below the horizon, in [-89, 89]. <see cref="Distance"/> is the
/// orbit radius in yards (and the half-height of the view in <see cref="CameraMode.Top"/>). Eye, matrices and pick
/// rays come from <see cref="CameraMath"/>.
/// </summary>
public sealed record CameraState(WorldPoint Target, double Distance, double Yaw, double Pitch, double Fov, CameraMode Mode)
{
    public const double MinDistance = 0.5;
    public const double MaxDistance = 40_000;
    public const double MinFov = 15;
    public const double MaxFov = 110;
    public const double MaxPitch = 89;
}

/// <summary>One layer's look. <see cref="ColorByAttribute"/> colours primitives by their attribute's <c>colorKey</c>.</summary>
public sealed record LayerStyle(bool Visible, string Color, double Opacity, RenderMode Mode, bool ColorByAttribute)
{
    public static LayerStyle For(LayerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new LayerStyle(info.DefaultVisible, info.DefaultColor, 1.0, RenderMode.Wire, info.AttributeColors is { Count: > 0 });
    }
}

/// <summary>
/// Everything the viewport shows, as one immutable value. Its JSON form is the basis of the <c>view.get</c> result
/// (which adds <c>version</c> and optional <c>followOrigin</c>) and the <c>view.set</c> argument (a JSON merge patch,
/// RFC 7396), and is what saved views and headless captures replay.
/// </summary>
public sealed record ViewState(
    uint MapId,
    CameraState Camera,
    ImmutableSortedDictionary<string, LayerStyle> Layers,
    CompareMode Compare,
    double SplitPosition,
    int LoadRadiusTiles,
    Theme Theme,
    bool ShowHud,
    string? FollowGuid,
    int TextureRadius = ViewState.DefaultTextureRadius,
    int TextureBudgetMb = ViewState.DefaultTextureBudgetMb)
{
    public const int MaxLoadRadiusTiles = 4;

    /// <summary>Tiles around the focus tile whose textured layers load (0 none, 2 at most); others fall back to the untextured layer.</summary>
    public const int DefaultTextureRadius = 1;

    public const int MaxTextureRadius = 2;

    /// <summary>Browser GPU budget for every texture the viewer holds, in MiB.</summary>
    public const int DefaultTextureBudgetMb = 384;

    public const int MinTextureBudgetMb = 64;

    public const int MaxTextureBudgetMb = 2048;

    /// <summary>
    /// Eastern Kingdoms, inside the Lion's Pride Inn in Goldshire (the real-terrain test anchor,
    /// tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:57), every layer at its catalog default.
    /// </summary>
    public static ViewState Default { get; } = new(
        0,
        new CameraState(new WorldPoint(-9462.66, 16.1915, 57.0459), 140, 45, 45, 55, CameraMode.Orbit),
        LayerCatalog.All.ToImmutableSortedDictionary(l => l.Id, LayerStyle.For, StringComparer.Ordinal),
        CompareMode.Server,
        0.5,
        1,
        Theme.Graphite,
        true,
        null);

    public JsonObject ToJson()
    {
        var layers = new JsonObject();
        foreach ((string id, LayerStyle style) in Layers)
        {
            layers[id] = new JsonObject
            {
                ["visible"] = style.Visible,
                ["color"] = style.Color,
                ["opacity"] = style.Opacity,
                ["mode"] = Name(style.Mode),
                ["colorByAttribute"] = style.ColorByAttribute,
            };
        }

        return new JsonObject
        {
            ["map"] = MapId,
            ["camera"] = new JsonObject
            {
                ["target"] = new JsonArray(Camera.Target.X, Camera.Target.Y, Camera.Target.Z),
                ["distance"] = Camera.Distance,
                ["yaw"] = Camera.Yaw,
                ["pitch"] = Camera.Pitch,
                ["fov"] = Camera.Fov,
                ["mode"] = Name(Camera.Mode),
            },
            ["layers"] = layers,
            ["compare"] = Name(Compare),
            ["split"] = SplitPosition,
            ["loadRadius"] = LoadRadiusTiles,
            ["theme"] = Name(Theme),
            ["hud"] = ShowHud,
            ["follow"] = FollowGuid,
            ["textureRadius"] = TextureRadius,
            ["textureBudgetMb"] = TextureBudgetMb,
        };
    }

    /// <summary>Parses a complete view. Every field is required except <c>follow</c>; layers absent from the JSON keep their defaults.</summary>
    public static ViewState FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return FromJsonCore(json);
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            throw Bad(ex.Message);
        }
    }

    private static ViewState FromJsonCore(JsonObject json)
    {
        JsonObject camera = Obj(json, "camera");
        double[] target = Triple(camera, "target");
        double distance = Num(camera, "distance", CameraState.MinDistance, CameraState.MaxDistance);
        double yaw = Num(camera, "yaw", double.MinValue, double.MaxValue) % 360;
        if (yaw < 0)
        {
            yaw += 360;
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, LayerStyle>(StringComparer.Ordinal);
        foreach (LayerInfo info in LayerCatalog.All)
        {
            builder[info.Id] = LayerStyle.For(info);
        }

        foreach ((string id, JsonNode? node) in Obj(json, "layers"))
        {
            if (!LayerCatalog.TryGet(id, out _))
            {
                throw Bad($"unknown layer '{id}'");
            }

            if (node is not JsonObject layer)
            {
                throw Bad($"layers.{id} must be an object");
            }

            string color = Str(layer, "color");
            if (!IsColor(color))
            {
                throw Bad($"layers.{id}.color must be #RRGGBB");
            }

            builder[id] = new LayerStyle(
                Bool(layer, "visible"),
                color.ToUpperInvariant(),
                Num(layer, "opacity", 0, 1),
                Enum<RenderMode>(layer, "mode"),
                Bool(layer, "colorByAttribute"));
        }

        long map = (long)Num(json, "map", 0, uint.MaxValue);
        double radius = Num(json, "loadRadius", 0, MaxLoadRadiusTiles);
        if (Math.Floor(radius) != radius)
        {
            throw Bad("loadRadius must be a whole number of tiles");
        }

        string? follow = json["follow"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) && IsGuid(s) => s.ToUpperInvariant(),
            _ => throw Bad("follow must be null or a hex guid"),
        };

        return new ViewState(
            (uint)map,
            new CameraState(
                new WorldPoint(target[0], target[1], target[2]),
                distance,
                yaw,
                Num(camera, "pitch", -CameraState.MaxPitch, CameraState.MaxPitch),
                Num(camera, "fov", CameraState.MinFov, CameraState.MaxFov),
                Enum<CameraMode>(camera, "mode")),
            builder.ToImmutable(),
            Enum<CompareMode>(json, "compare"),
            Num(json, "split", 0.05, 0.95),
            (int)radius,
            Enum<Theme>(json, "theme"),
            Bool(json, "hud"),
            follow,
            OptionalWhole(json, "textureRadius", 0, MaxTextureRadius, DefaultTextureRadius),
            OptionalWhole(json, "textureBudgetMb", MinTextureBudgetMb, MaxTextureBudgetMb, DefaultTextureBudgetMb));
    }

    /// <summary>A whole number within [min, max]; the default when the field is absent (older clients do not send it).</summary>
    private static int OptionalWhole(JsonObject json, string name, int min, int max, int fallback)
    {
        if (json[name] is null)
        {
            return fallback;
        }

        double value = Num(json, name, min, max);
        return Math.Floor(value) == value ? (int)value : throw Bad($"{name} must be a whole number");
    }

    /// <summary>Applies a JSON merge patch (RFC 7396) and validates the result; invalid patches throw <see cref="CommandException"/>.</summary>
    public ViewState Apply(JsonObject patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (MergePatch(ToJson(), patch) is not JsonObject merged)
        {
            throw Bad("the patch must leave an object");
        }

        return FromJson(merged);
    }

    /// <summary>RFC 7396 section 2: objects merge recursively, <c>null</c> deletes a member, anything else replaces.</summary>
    public static JsonNode? MergePatch(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        JsonObject result = target is JsonObject targetObject ? (JsonObject)targetObject.DeepClone() : [];
        foreach ((string name, JsonNode? value) in patchObject)
        {
            if (value is null)
            {
                result.Remove(name);
            }
            else
            {
                JsonNode? existing = result.TryGetPropertyValue(name, out JsonNode? current) ? current : null;
                JsonNode? merged = MergePatch(existing, value);
                result.Remove(name);
                result[name] = merged;
            }
        }

        return result;
    }

    public static string Name<T>(T value)
        where T : struct, Enum
        => value.ToString().ToLowerInvariant();

    public static bool IsColor(string text)
        => text.Length == 7 && text[0] == '#' && text.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") is false;

    private static bool IsGuid(string text)
        => text.Length is > 0 and <= 16 && !text.AsSpan().ContainsAnyExcept("0123456789abcdefABCDEF");

    private static CommandException Bad(string message) => new(CommandException.BadArguments, $"view: {message}.");

    private static JsonObject Obj(JsonObject json, string name)
        => json[name] as JsonObject ?? throw Bad($"{name} must be an object");

    private static string Str(JsonObject json, string name)
        => json[name] is JsonValue v && v.TryGetValue(out string? s) ? s : throw Bad($"{name} must be a string");

    private static bool Bool(JsonObject json, string name)
        => json[name] is JsonValue v && v.TryGetValue(out bool b) ? b : throw Bad($"{name} must be true or false");

    private static double Num(JsonObject json, string name, double min, double max)
    {
        if (JsonScalars.TryGetDouble(json[name], out double d))
        {
            return d >= min && d <= max
                ? d
                : throw Bad(string.Create(CultureInfo.InvariantCulture, $"{name} must be within [{min}, {max}]"));
        }

        throw Bad($"{name} must be a finite number");
    }

    private static double[] Triple(JsonObject json, string name)
    {
        if (json[name] is JsonArray { Count: 3 } a)
        {
            var values = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (!JsonScalars.TryGetDouble(a[i], out values[i]))
                {
                    throw Bad($"{name} must be [x, y, z] finite numbers");
                }
            }

            return values;
        }

        throw Bad($"{name} must be [x, y, z]");
    }

    private static T Enum<T>(JsonObject json, string name)
        where T : struct, Enum
    {
        string text = Str(json, name);
        foreach (T value in System.Enum.GetValues<T>())
        {
            if (string.Equals(Name(value), text, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw Bad($"{name} must be one of {string.Join(", ", System.Enum.GetValues<T>().Select(Name))}");
    }
}

/// <summary>
/// The shared, current view of one Atlas instance. Commands and the UI both write through it; every change bumps
/// <see cref="Version"/> and raises <see cref="Changed"/> (the host pushes it to connected UIs).
/// </summary>
public sealed class ViewStore(ViewState initial)
{
    private readonly Lock _gate = new();

    // Held around the state change and its notifications, so versions are published in order: without it, two threads can
    // change the view (v5, v6) and then publish v6 first, and a subscriber that drops versions at or below the latest it saw loses v5.
    // Always taken before _gate, never the other way round.
    private readonly Lock _publishGate = new();
    private ViewState _current = initial ?? throw new ArgumentNullException(nameof(initial));
    private long _version;
    private string? _followOrigin;

    public event Action<ViewState, long, string>? Changed;

    /// <summary>
    /// Raised just before <see cref="Changed"/> with the view as it was before this change, for subscribers that need to
    /// know what the change altered (arguments: previous, next, version, origin, and the origin that owns the follow).
    /// </summary>
    public event Action<ViewState, ViewState, long, string, string?>? Applied;

    /// <summary>
    /// Says whether a <c>ui:&lt;connection&gt;</c> origin is still connected; the host sets it. Null means every origin counts as connected
    /// (no host serves the store). Called under the store's lock, so it must be quick and must not call back into the store.
    /// </summary>
    public Func<string, bool>? OriginConnected { get; set; }

    /// <summary>
    /// The origin of the patch or set that last changed <see cref="ViewState.FollowGuid"/> (a clear counts), or null before any
    /// follow change. Later patches that keep the same follow, such as the live feed moving the camera each frame, do not take it over.
    /// </summary>
    public string? FollowOrigin
    {
        get
        {
            lock (_gate)
            {
                return _followOrigin;
            }
        }
    }

    /// <summary>The view, its version and the follow owner read under one lock, so they describe the same moment (a null owner when nothing is followed).</summary>
    public (ViewState View, long Version, string? FollowOrigin) Snapshot()
    {
        lock (_gate)
        {
            return (_current, _version, _current.FollowGuid is null ? null : _followOrigin);
        }
    }

    public ViewState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>Applies a merge patch atomically; <paramref name="origin"/> names the writer (for example <c>ui</c> or <c>mcp</c>).</summary>
    /// <remarks>
    /// A UI cannot stop a follow that another UI owns: a patch from a <c>ui:</c> origin that only clears <c>follow</c> while a different
    /// UI owns it has that member ignored (the rest of the patch still applies). Anything else, including a UI taking the follow over, goes through.
    /// A patch from a <c>ui:</c> origin that sets a follow after <see cref="OriginConnected"/> says that UI is gone has the member ignored too,
    /// so a command still running when its socket closed cannot leave a follow owned by nobody.
    /// </remarks>
    public ViewState Patch(JsonObject patch, string origin)
    {
        ArgumentNullException.ThrowIfNull(patch);
        lock (_publishGate)
        {
            ViewState previous;
            ViewState next;
            long version;
            string? followOrigin;
            lock (_gate)
            {
                JsonObject effective = patch;
                bool followKey = patch.ContainsKey("follow");
                if (followKey && IgnoresFollowLocked(patch["follow"] is null, origin))
                {
                    effective = (JsonObject)patch.DeepClone();
                    effective.Remove("follow");
                    followKey = false;
                }

                previous = _current;
                next = _current.Apply(effective);
                _current = next;
                version = ++_version;
                followOrigin = NoteFollowLocked(next, origin, followKey);
            }

            Applied?.Invoke(previous, next, version, origin, followOrigin);
            Changed?.Invoke(next, version, origin);
            return next;
        }
    }

    /// <summary>
    /// True when a change of the follow from <paramref name="origin"/> must be dropped: a <c>ui:</c> origin that is no longer connected may
    /// not set a follow, and one UI may not clear a follow another UI owns. The caller holds <c>_gate</c>.
    /// </summary>
    private bool IgnoresFollowLocked(bool clears, string origin)
    {
        if (!clears)
        {
            return origin.StartsWith("ui:", StringComparison.Ordinal) && OriginConnected is { } connected && !connected(origin);
        }

        return _current.FollowGuid is not null && IsPeerUi(_followOrigin, origin);
    }

    private static bool IsPeerUi(string? owner, string origin)
        => owner is not null
            && owner.StartsWith("ui:", StringComparison.Ordinal)
            && origin.StartsWith("ui:", StringComparison.Ordinal)
            && !string.Equals(owner, origin, StringComparison.Ordinal);

    /// <summary>
    /// Stops the follow when <paramref name="owner"/> is the origin that owns it (a UI that disconnected); the check and the change are one
    /// atomic step, so a follow another origin took over in the meantime is left alone. False when nothing was released.
    /// </summary>
    public bool ReleaseFollow(string owner)
    {
        lock (_publishGate)
        {
            ViewState previous;
            ViewState next;
            long version;
            lock (_gate)
            {
                if (_current.FollowGuid is null || !string.Equals(_followOrigin, owner, StringComparison.Ordinal))
                {
                    return false;
                }

                previous = _current;
                next = _current.Apply(new JsonObject { ["follow"] = null });
                _current = next;
                version = ++_version;
                NoteFollowLocked(next, owner, followKey: true);
            }

            Applied?.Invoke(previous, next, version, owner, null);
            Changed?.Invoke(next, version, owner);
            return true;
        }
    }

    /// <summary>Replaces the whole view. The follow rules of <see cref="Patch"/> apply: a replacement that would clear or set the follow against them keeps the current follow.</summary>
    public ViewState Set(ViewState state, string origin)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_publishGate)
        {
            ViewState previous;
            long version;
            string? followOrigin;
            lock (_gate)
            {
                previous = _current;
                bool followChanged = previous.FollowGuid != state.FollowGuid;
                if (followChanged && IgnoresFollowLocked(state.FollowGuid is null, origin))
                {
                    state = state with { FollowGuid = previous.FollowGuid };
                    followChanged = false;
                }

                _current = state;
                version = ++_version;
                followOrigin = NoteFollowLocked(state, origin, followChanged);
            }

            Applied?.Invoke(previous, state, version, origin, followOrigin);
            Changed?.Invoke(state, version, origin);
            return state;
        }
    }

    /// <summary>
    /// Records who owns the follow: the origin of any patch that names <c>follow</c> (even with the guid already followed, so a second
    /// origin can take the follow over), or of a whole-view replacement that changes it. Returns the owner to publish, null when nothing is followed.
    /// </summary>
    private string? NoteFollowLocked(ViewState next, string origin, bool followKey)
    {
        if (followKey)
        {
            _followOrigin = origin;
        }

        return next.FollowGuid is null ? null : _followOrigin;
    }
}

using System.Numerics;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// Builds the <c>overlay</c> object a probe command returns: points, segments, polylines and boxes in world coordinates, drawn by the UI over
/// the layers (design section 14). Every coordinate must be finite; a primitive with a non-finite coordinate is refused so a bad probe can never
/// produce JSON the UI cannot draw.
/// <para>Thread affinity: single thread (a command builds its own overlay).</para>
/// </summary>
public sealed class ServerOverlay
{
    private readonly JsonArray _points = [];
    private readonly JsonArray _segments = [];
    private readonly JsonArray _polylines = [];
    private readonly JsonArray _boxes = [];

    /// <summary>Primitives added so far.</summary>
    public int Count => _points.Count + _segments.Count + _polylines.Count + _boxes.Count;

    /// <summary>A labelled point. <paramref name="colorKey"/> names an attribute colour of the layer palette.</summary>
    public ServerOverlay Point(Vector3 at, string label, string? color = null, string? colorKey = null)
    {
        var o = new JsonObject { ["at"] = Triple(at), ["label"] = label };
        AddOptional(o, "color", color);
        AddOptional(o, "colorKey", colorKey);
        _points.Add(o);
        return this;
    }

    /// <summary>A straight segment.</summary>
    public ServerOverlay Segment(Vector3 from, Vector3 to, string? label = null, string? color = null, bool dashed = false)
    {
        var o = new JsonObject { ["from"] = Triple(from), ["to"] = Triple(to) };
        AddOptional(o, "label", label);
        AddOptional(o, "color", color);
        if (dashed)
        {
            o["dashed"] = true;
        }

        _segments.Add(o);
        return this;
    }

    /// <summary>A connected line through <paramref name="points"/> (two or more).</summary>
    public ServerOverlay Polyline(IEnumerable<Vector3> points, string? label = null, string? color = null, bool dashed = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        var pts = new JsonArray();
        foreach (Vector3 p in points)
        {
            pts.Add(Triple(p));
        }

        if (pts.Count < 2)
        {
            throw new ArgumentException("A polyline needs at least two points.", nameof(points));
        }

        var o = new JsonObject { ["pts"] = pts };
        AddOptional(o, "label", label);
        AddOptional(o, "color", color);
        if (dashed)
        {
            o["dashed"] = true;
        }

        _polylines.Add(o);
        return this;
    }

    /// <summary>An axis-aligned box.</summary>
    public ServerOverlay Box(Vector3 min, Vector3 max, string? label = null, string? color = null)
    {
        var o = new JsonObject { ["min"] = Triple(min), ["max"] = Triple(max) };
        AddOptional(o, "label", label);
        AddOptional(o, "color", color);
        _boxes.Add(o);
        return this;
    }

    /// <summary>The overlay object (empty arrays are left out).</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (_points.Count > 0)
        {
            o["points"] = _points.DeepClone();
        }

        if (_segments.Count > 0)
        {
            o["segments"] = _segments.DeepClone();
        }

        if (_polylines.Count > 0)
        {
            o["polylines"] = _polylines.DeepClone();
        }

        if (_boxes.Count > 0)
        {
            o["boxes"] = _boxes.DeepClone();
        }

        return o;
    }

    /// <summary>A JSON array <c>[x, y, z]</c>, refusing non-finite components.</summary>
    public static JsonArray Triple(Vector3 v)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
        {
            throw new ArgumentException($"Overlay coordinate {v} is not finite.", nameof(v));
        }

        return new JsonArray((double)v.X, (double)v.Y, (double)v.Z);
    }

    private static void AddOptional(JsonObject o, string name, string? value)
    {
        if (value is not null)
        {
            o[name] = value;
        }
    }
}

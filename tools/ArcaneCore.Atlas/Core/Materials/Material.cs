using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Core.Materials;

/// <summary>How a batch is blended with what is already drawn. The numbers are wire values (M2 and WMO share them).</summary>
public enum BlendMode
{
    Opaque = 0,
    AlphaKey = 1,
    AlphaBlend = 2,
    Additive = 3,
    AdditiveAlpha = 4,
    Modulate = 5,
    Modulate2x = 6,
}

/// <summary>Per-material switches. The bit values are wire values.</summary>
[Flags]
public enum MaterialFlags : uint
{
    None = 0,
    Unlit = 0x1,
    Unfogged = 0x2,
    TwoSided = 0x4,
    NoDepthWrite = 0x8,
    NoDepthTest = 0x10,
    ClampS = 0x20,
    ClampT = 0x40,
    UseColor0 = 0x80,

    /// <summary>Reserved: the browser does not billboard yet.</summary>
    Billboard = 0x100,

    /// <summary>Environment mapped (approximated by the browser).</summary>
    EnvMap = 0x200,
}

/// <summary>
/// One texture a material samples: a texture key (<c>client:</c>, <c>gen:</c> or <c>missing</c>), the sampler slot, the wrap
/// mode per axis (0 repeat, 1 clamp) and, for M2 replaceable textures, the M2 texture type that a display resolves later.
/// </summary>
public readonly record struct TextureRef(string Key, int Slot = 0, int WrapS = 0, int WrapT = 0, int? Replace = null);

/// <summary>One draw range of a chunk: a contiguous run of triangles that share a material.</summary>
/// <param name="FirstTriangle">Index of the first triangle of the run.</param>
/// <param name="TriangleCount">Number of triangles (at least 1 in a valid chunk).</param>
/// <param name="Material">Index into the chunk's material table.</param>
/// <param name="Tag">Low 16 bits geoset or submesh id, high 16 bits batch priority (a signed 16-bit value, so a negative M2 priority reads back negative).</param>
public readonly record struct GeometryBatch(uint FirstTriangle, uint TriangleCount, uint Material, uint Tag)
{
    public int Geoset => (int)(Tag & 0xFFFF);

    public int Priority => (short)(Tag >> 16);
}

/// <summary>
/// A material of ATG1 version 2 (design section 2.3). Immutable. <see cref="ToJson"/> and <see cref="FromJson"/> are the wire form;
/// <see cref="FromJson"/> refuses anything the wire does not allow with an <see cref="InvalidDataException"/>.
/// </summary>
public sealed record Material(
    string Kind,
    BlendMode Blend,
    MaterialFlags Flags,
    IReadOnlyList<TextureRef> Textures,
    double AlphaRef = 0,
    double[]? Color = null,
    JsonObject? Extra = null)
{
    /// <summary>The material kinds the browser and the CPU renderer know.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["generic", "terrain", "wmo", "m2", "skinned", "character"];

    /// <summary>A canonical string for the material, equal for materials that serialize equally (used to share table entries).</summary>
    public string Identity => ToJson().ToJsonString();

    public JsonObject ToJson()
    {
        var textures = new JsonArray();
        foreach (TextureRef t in Textures)
        {
            var entry = new JsonObject
            {
                ["key"] = t.Key,
                ["slot"] = t.Slot,
                ["wrap"] = new JsonArray(t.WrapS, t.WrapT),
            };
            if (t.Replace is int replace)
            {
                entry["replace"] = replace;
            }

            textures.Add(entry);
        }

        var json = new JsonObject
        {
            ["kind"] = Kind,
            ["blend"] = (int)Blend,
            ["flags"] = (long)(uint)Flags,
            ["textures"] = textures,
            ["alphaRef"] = AlphaRef,
        };
        if (Color is not null)
        {
            json["color"] = new JsonArray([.. Color.Select(c => (JsonNode)JsonValue.Create(c)!)]);
        }

        if (Extra is not null)
        {
            json["extra"] = Extra.DeepClone();
        }

        return json;
    }

    public static Material FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            string kind = json["kind"] is JsonValue kv && kv.TryGetValue(out string? k) && k is not null && Kinds.Contains(k, StringComparer.Ordinal)
                ? k
                : throw new InvalidDataException("Material kind is missing or unknown.");
            double blend = Number(json["blend"], "blend");
            if (blend != Math.Floor(blend) || blend < 0 || blend > (int)BlendMode.Modulate2x)
            {
                throw new InvalidDataException("Material blend must be an integer from 0 to 6.");
            }

            double flags = Number(json["flags"], "flags");
            if (flags != Math.Floor(flags) || flags < 0 || flags > uint.MaxValue)
            {
                throw new InvalidDataException("Material flags must be an unsigned integer.");
            }

            double alphaRef = json["alphaRef"] is null ? 0 : Number(json["alphaRef"], "alphaRef");
            if (alphaRef < 0 || alphaRef > 1)
            {
                throw new InvalidDataException("Material alphaRef must be from 0 to 1.");
            }

            if (json["textures"] is not JsonArray array)
            {
                throw new InvalidDataException("Material textures must be an array.");
            }

            var textures = new List<TextureRef>(array.Count);
            foreach (JsonNode? node in array)
            {
                textures.Add(ReadTexture(node));
            }

            double[]? color = null;
            if (json["color"] is not null)
            {
                if (json["color"] is not JsonArray c || c.Count != 4)
                {
                    throw new InvalidDataException("Material color must be four numbers.");
                }

                color = [.. c.Select(n => Number(n, "color"))];
            }

            JsonObject? extra = null;
            if (json["extra"] is not null)
            {
                extra = json["extra"] as JsonObject ?? throw new InvalidDataException("Material extra must be an object.");
                extra = extra.DeepClone().AsObject();
            }

            return new Material(kind, (BlendMode)(int)blend, (MaterialFlags)(uint)flags, textures, alphaRef, color, extra);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or JsonException)
        {
            throw new InvalidDataException("Material is malformed.", ex);
        }
    }

    private static TextureRef ReadTexture(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            throw new InvalidDataException("A material texture must be an object.");
        }

        string key = o["key"] is JsonValue kv && kv.TryGetValue(out string? s) && !string.IsNullOrEmpty(s)
            ? s
            : throw new InvalidDataException("A material texture needs a key.");
        double slot = o["slot"] is null ? 0 : Number(o["slot"], "slot");
        if (slot != Math.Floor(slot) || slot < 0 || slot > 15)
        {
            throw new InvalidDataException("A material texture slot must be an integer from 0 to 15.");
        }

        int wrapS = 0;
        int wrapT = 0;
        if (o["wrap"] is not null)
        {
            if (o["wrap"] is not JsonArray wrap || wrap.Count != 2)
            {
                throw new InvalidDataException("A material texture wrap must be two numbers.");
            }

            wrapS = Wrap(wrap[0]);
            wrapT = Wrap(wrap[1]);
        }

        int? replace = null;
        if (o["replace"] is not null)
        {
            double r = Number(o["replace"], "replace");
            if (r != Math.Floor(r) || r < 0 || r > 63)
            {
                throw new InvalidDataException("A material texture replace type must be an integer from 0 to 63.");
            }

            replace = (int)r;
        }

        return new TextureRef(key, (int)slot, wrapS, wrapT, replace);
    }

    private static int Wrap(JsonNode? node)
    {
        double v = Number(node, "wrap");
        return v is 0 or 1 ? (int)v : throw new InvalidDataException("A material wrap mode must be 0 (repeat) or 1 (clamp).");
    }

    /// <summary>Reads any JSON number node (parsed or built in memory) as a finite double.</summary>
    private static double Number(JsonNode? node, string what)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
            || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d))
        {
            throw new InvalidDataException($"Material {what} must be a number.");
        }

        return d;
    }
}

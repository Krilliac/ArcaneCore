using System.Globalization;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Render;

/// <summary>An opaque 8-bit RGB colour.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>The chart (viewport) colour, <c>#101A21</c> (design doc section 10, tokens).</summary>
    public static Rgb Background { get; } = new(0x10, 0x1A, 0x21);

    /// <summary>Parses <c>#RRGGBB</c> (the form <see cref="ViewState"/> stores).</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (text is null || !ViewState.IsColor(text))
        {
            return false;
        }

        color = new Rgb(Hex(text, 1), Hex(text, 3), Hex(text, 5));
        return true;
    }

    /// <summary>Each channel multiplied by <paramref name="factor"/> (at least 0) and rounded to the nearest value, clamped to 255.</summary>
    public Rgb Scaled(double factor) => new(Channel(R * factor), Channel(G * factor), Channel(B * factor));

    /// <summary>Linear mix toward <paramref name="other"/>: <c>this * (1 - t) + other * t</c>, rounded per channel.</summary>
    public Rgb Mixed(Rgb other, double t)
        => new(Channel((R * (1 - t)) + (other.R * t)), Channel((G * (1 - t)) + (other.G * t)), Channel((B * (1 - t)) + (other.B * t)));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>Round half up and clamp to a byte. Inputs are never negative or NaN here.</summary>
    internal static byte Channel(double value)
        => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + 0.5);

    private static byte Hex(string text, int start)
        => byte.Parse(text.AsSpan(start, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}

/// <summary>
/// The fixed light and the fixed shading factors of the headless renderer. Fills are flat-shaded per triangle:
/// layer colour times <c>Ambient + Diffuse * |n . L|</c> with a light direction fixed in WORLD space (so a render does
/// not change colour when the camera orbits), double-sided because the data's winding is not trustworthy.
/// </summary>
internal static class Shading
{
    /// <summary>Share of the colour a face gets whatever its orientation.</summary>
    public const double Ambient = 0.35;

    /// <summary>Share added by a face turned square to the light.</summary>
    public const double Diffuse = 0.65;

    /// <summary>
    /// In <see cref="RenderMode.Both"/> the fill is drawn at this fraction of its shaded colour so the edges, drawn in
    /// the full layer colour, stay legible over it.
    /// </summary>
    public const double BothFillScale = 0.55;

    /// <summary>Opacity factor applied to server layers in <see cref="CompareMode.Diff"/> (design: "over a dimmed server view").</summary>
    public const double DiffServerDim = 0.35;

    /// <summary>Unit light direction (towards the light) in world space: from above, slightly north and east.</summary>
    public static (double X, double Y, double Z) Light { get; } = NormalizedLight();

    /// <summary>Lambert intensity of a face with unit normal (<paramref name="nx"/>, <paramref name="ny"/>, <paramref name="nz"/>).</summary>
    public static double Intensity(double nx, double ny, double nz)
        => Ambient + (Diffuse * Math.Abs((nx * Light.X) + (ny * Light.Y) + (nz * Light.Z)));

    /// <summary>The colour a fill of <paramref name="layer"/> takes on a face with the given unit normal.</summary>
    public static Rgb Fill(Rgb layer, double nx, double ny, double nz, bool both)
        => layer.Scaled(Intensity(nx, ny, nz) * (both ? BothFillScale : 1.0));

    private static (double, double, double) NormalizedLight()
    {
        const double x = 0.32;
        const double y = -0.24;
        const double z = 0.915;
        double length = Math.Sqrt((x * x) + (y * y) + (z * z));
        return (x / length, y / length, z / length);
    }
}

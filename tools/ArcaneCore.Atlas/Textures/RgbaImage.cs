namespace ArcaneCore.Atlas.Textures;

/// <summary>A straight-alpha RGBA8 colour.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A);

/// <summary>What happens to a texture coordinate outside 0..1.</summary>
public enum TextureWrap
{
    Repeat,
    Clamp,
}

/// <summary>How a texture is sampled between texels.</summary>
public enum TextureFilter
{
    Nearest,
    Bilinear,
}

/// <summary>
/// A decoded image: <see cref="Width"/> x <see cref="Height"/> straight-alpha RGBA8 texels, row-major, top row first. The pixel
/// array is owned by the image and never changes after construction, so an image may be read from several threads.
/// <see cref="Sample"/> uses the texel-centre convention of GL and the browser (texel i spans (i / size, (i + 1) / size)), so a
/// CPU render and the WebGL render of the same material agree.
/// </summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if ((long)width * height * 4 != pixels.Length)
        {
            throw new ArgumentException($"A {width}x{height} image needs {(long)width * height * 4} bytes, not {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>R, G, B, A per texel. Callers must not modify it.</summary>
    public byte[] Pixels { get; }

    public Rgba Get(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Texel(x, y);
    }

    /// <summary>The colour at (<paramref name="u"/>, <paramref name="v"/>), v = 0 at the top row. Non-finite coordinates sample the first texel.</summary>
    public Rgba Sample(double u, double v, TextureWrap wrapS, TextureWrap wrapT, TextureFilter filter)
    {
        if (!double.IsFinite(u) || !double.IsFinite(v))
        {
            return Texel(0, 0);
        }

        if (filter == TextureFilter.Nearest)
        {
            return Texel(WrapIndex(Math.Floor(u * Width), Width, wrapS), WrapIndex(Math.Floor(v * Height), Height, wrapT));
        }

        double x = (u * Width) - 0.5;
        double y = (v * Height) - 0.5;
        double x0f = Math.Floor(x);
        double y0f = Math.Floor(y);
        double fx = x - x0f;
        double fy = y - y0f;
        int x0 = WrapIndex(x0f, Width, wrapS);
        int x1 = WrapIndex(x0f + 1, Width, wrapS);
        int y0 = WrapIndex(y0f, Height, wrapT);
        int y1 = WrapIndex(y0f + 1, Height, wrapT);
        Rgba a = Texel(x0, y0);
        Rgba b = Texel(x1, y0);
        Rgba c = Texel(x0, y1);
        Rgba d = Texel(x1, y1);
        return new Rgba(
            Mix(a.R, b.R, c.R, d.R, fx, fy),
            Mix(a.G, b.G, c.G, d.G, fx, fy),
            Mix(a.B, b.B, c.B, d.B, fx, fy),
            Mix(a.A, b.A, c.A, d.A, fx, fy));
    }

    private Rgba Texel(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return new Rgba(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    private static int WrapIndex(double index, int size, TextureWrap wrap)
    {
        if (wrap == TextureWrap.Clamp)
        {
            return (int)Math.Clamp(index, 0, size - 1);
        }

        double m = index % size;
        if (m < 0)
        {
            m += size;
        }

        return (int)m;
    }

    private static byte Mix(byte a, byte b, byte c, byte d, double fx, double fy)
    {
        double top = (a * (1 - fx)) + (b * fx);
        double bottom = (c * (1 - fx)) + (d * fx);
        double value = (top * (1 - fy)) + (bottom * fy);
        return (byte)Math.Clamp((int)Math.Floor(value + 0.5), 0, 255);
    }
}

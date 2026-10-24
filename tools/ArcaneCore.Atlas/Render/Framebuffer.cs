namespace ArcaneCore.Atlas.Render;

/// <summary>
/// The render target: RGBA8 pixels (top row first, alpha always 255) plus two depth planes. Depth values are view
/// depths in yards (the distance along the camera's forward axis), not clip-space values, so the hidden-line bias can be
/// stated in yards. Owned by one render on one thread; nothing here is thread-safe.
/// </summary>
internal sealed class Framebuffer
{
    public Framebuffer(int width, int height, Rgb background)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
        FillDepth = new float[width * height];
        OccluderDepth = new float[width * height];
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = background.R;
            Pixels[i + 1] = background.G;
            Pixels[i + 2] = background.B;
            Pixels[i + 3] = 255;
        }

        Array.Fill(FillDepth, float.PositiveInfinity);
        Array.Fill(OccluderDepth, float.PositiveInfinity);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>RGBA, 4 bytes per pixel, row-major, top row first.</summary>
    public byte[] Pixels { get; }

    /// <summary>View depth of the nearest opaque fill at each pixel (positive infinity where none).</summary>
    public float[] FillDepth { get; }

    /// <summary>
    /// View depth of the nearest surface of any occluding layer at each pixel, pushed away from the camera by the
    /// hidden-line bias (see <see cref="Rasterizer"/>); lines pass the depth test when they are at least this near.
    /// </summary>
    public float[] OccluderDepth { get; }

    public Rgb Get(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return new Rgb(Pixels[i], Pixels[i + 1], Pixels[i + 2]);
    }

    /// <summary>Writes <paramref name="color"/> over the pixel with the given opacity in (0, 1]; opacity 1 replaces it exactly.</summary>
    public void Blend(int x, int y, Rgb color, double alpha) => BlendAt(((y * Width) + x) * 4, color, alpha);

    /// <summary>
    /// Combines a shaded pixel with the frame by a material blend mode at a byte offset: alpha blend (also alpha key, after its test),
    /// additive (the source times <paramref name="alpha"/> added), or modulate (the destination times the source, doubled for
    /// <see cref="Core.Materials.BlendMode.Modulate2x"/>, mixed in by <paramref name="alpha"/>).
    /// </summary>
    public void Combine(int offset, Rgb color, double alpha, Core.Materials.BlendMode mode)
    {
        switch (mode)
        {
            case Core.Materials.BlendMode.Additive:
            case Core.Materials.BlendMode.AdditiveAlpha:
                Pixels[offset] = Rgb.Channel(Pixels[offset] + (color.R * alpha));
                Pixels[offset + 1] = Rgb.Channel(Pixels[offset + 1] + (color.G * alpha));
                Pixels[offset + 2] = Rgb.Channel(Pixels[offset + 2] + (color.B * alpha));
                break;
            case Core.Materials.BlendMode.Modulate:
            case Core.Materials.BlendMode.Modulate2x:
                double gain = mode == Core.Materials.BlendMode.Modulate2x ? 2.0 / 255.0 : 1.0 / 255.0;
                double keep = 1.0 - alpha;
                Pixels[offset] = Rgb.Channel((Pixels[offset] * color.R * gain * alpha) + (Pixels[offset] * keep));
                Pixels[offset + 1] = Rgb.Channel((Pixels[offset + 1] * color.G * gain * alpha) + (Pixels[offset + 1] * keep));
                Pixels[offset + 2] = Rgb.Channel((Pixels[offset + 2] * color.B * gain * alpha) + (Pixels[offset + 2] * keep));
                break;
            default:
                BlendAt(offset, color, alpha);
                break;
        }
    }

    /// <summary>Blends at a byte offset into <see cref="Pixels"/> (pixel index times four).</summary>
    public void BlendAt(int offset, Rgb color, double alpha)
    {
        if (alpha >= 1.0)
        {
            Pixels[offset] = color.R;
            Pixels[offset + 1] = color.G;
            Pixels[offset + 2] = color.B;
            return;
        }

        if (alpha <= 0.0)
        {
            return;
        }

        double keep = 1.0 - alpha;
        Pixels[offset] = Rgb.Channel((color.R * alpha) + (Pixels[offset] * keep));
        Pixels[offset + 1] = Rgb.Channel((color.G * alpha) + (Pixels[offset + 1] * keep));
        Pixels[offset + 2] = Rgb.Channel((color.B * alpha) + (Pixels[offset + 2] * keep));
    }

    /// <summary>Blends a rectangle, half-open [x0, x1) x [y0, y1), clipped to the frame.</summary>
    public void FillRect(int x0, int y0, int x1, int y1, Rgb color, double alpha)
    {
        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        x1 = Math.Min(x1, Width);
        y1 = Math.Min(y1, Height);
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                Blend(x, y, color, alpha);
            }
        }
    }
}

using ArcaneCore.Atlas.Core.Materials;
using ArcaneCore.Atlas.Textures;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// The pixel stage of one draw batch: given the interpolated texture coordinate it returns the colour and opacity of the pixel, or
/// discards it. One instance serves every triangle of a batch; the renderer sets <see cref="Intensity"/> before each triangle.
/// Single-threaded (owned by one render).
/// </summary>
internal abstract class BatchShader(BlendMode blend)
{
    /// <summary>How the shaded pixel combines with what is already in the frame.</summary>
    public BlendMode Blend { get; } = blend;

    /// <summary>True when the batch writes colour and depth directly (opaque and alpha key); other blends go through the back-to-front pass.</summary>
    public bool WritesDepth => Blend is BlendMode.Opaque or BlendMode.AlphaKey;

    /// <summary>The triangle's lighting factor (see <c>CpuRenderer.TriangleIntensity</c>); unlit materials ignore it.</summary>
    public double Intensity { get; set; } = 1.0;

    /// <summary>Shades the pixel at (<paramref name="u"/>, <paramref name="v"/>); false discards it.</summary>
    public abstract bool Shade(double u, double v, out Rgb colour, out double alpha);
}

/// <summary>Turns a <see cref="Material"/> of one kind into the <see cref="BatchShader"/> of a batch.</summary>
internal interface ICpuMaterial
{
    /// <param name="material">The batch's material.</param>
    /// <param name="textures">The render's texture set.</param>
    /// <param name="flat">The colour a batch with no texture takes (the layer's colour).</param>
    BatchShader Prepare(Material material, CpuTextureSet textures, Rgb flat);
}

/// <summary>
/// The material kinds the CPU renderer draws. Only <c>generic</c> is built in (a texture at slot 0 sampled at uv0, lit or unlit, with the
/// blend mode of the material); later lanes register their kinds, and a kind nobody registered is drawn as <c>generic</c>.
/// </summary>
internal sealed class CpuMaterials
{
    public const string Generic = "generic";

    private readonly Dictionary<string, ICpuMaterial> _materials = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public CpuMaterials()
    {
        _materials[Generic] = new GenericCpuMaterial();
    }

    /// <exception cref="ArgumentException">The kind already has an implementation.</exception>
    public void Register(string kind, ICpuMaterial material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(material);
        lock (_gate)
        {
            if (!_materials.TryAdd(kind, material))
            {
                throw new ArgumentException($"Material kind '{kind}' is already registered.", nameof(kind));
            }
        }
    }

    public bool TryGet(string kind, out ICpuMaterial material)
    {
        lock (_gate)
        {
            return _materials.TryGetValue(kind, out material!);
        }
    }

    /// <exception cref="KeyNotFoundException">No such kind.</exception>
    public ICpuMaterial Get(string kind)
        => TryGet(kind, out ICpuMaterial material) ? material : throw new KeyNotFoundException($"No CPU material of kind '{kind}'.");

    /// <summary>The implementation for <paramref name="kind"/>, or <c>generic</c> for a kind nobody registered.</summary>
    public ICpuMaterial For(string kind) => TryGet(kind, out ICpuMaterial material) ? material : Get(Generic);
}

/// <summary>
/// The decoded images one render uses, fetched through the <see cref="TextureCache"/> once per key. A key that cannot be served (a
/// missing file, a damaged BLP, an unknown scheme) draws the <c>missing</c> placeholder instead of a hole, so the failure stays
/// visible. Single-threaded (owned by one render).
/// </summary>
internal sealed class CpuTextureSet(TextureCache cache, CancellationToken cancellationToken)
{
    private readonly Dictionary<string, RgbaImage> _images = new(StringComparer.Ordinal);

    public RgbaImage Get(string key)
    {
        if (_images.TryGetValue(key, out RgbaImage? image))
        {
            return image;
        }

        image = Load(key) ?? Load("missing") ?? throw new InvalidOperationException("The missing-texture placeholder could not be built.");
        _images[key] = image;
        return image;
    }

    private RgbaImage? Load(string key)
    {
        try
        {
            AtxTexture texture = cache.Get(key, TextureDelivery.Rgba, 0, cancellationToken).Texture;
            return texture.Format == AtxFormat.Rgba8 && texture.Layers == 1
                ? new RgbaImage(texture.Width, texture.Height, texture.Levels[0])
                : null;
        }
        catch (TextureException)
        {
            return null;
        }
    }
}

/// <summary>The built-in kind: slot 0's texture at uv0, nearest filtering, opaque / alpha key / blended per the material.</summary>
internal sealed class GenericCpuMaterial : ICpuMaterial
{
    public BatchShader Prepare(Material material, CpuTextureSet textures, Rgb flat)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(textures);
        TextureRef? slot0 = null;
        foreach (TextureRef texture in material.Textures)
        {
            if (texture.Slot == 0)
            {
                slot0 = texture;
                break;
            }
        }

        bool unlit = material.Flags.HasFlag(MaterialFlags.Unlit);
        if (slot0 is not TextureRef reference)
        {
            return new GenericShader(material.Blend, null, TextureWrap.Repeat, TextureWrap.Repeat, flat, unlit, material.AlphaRef);
        }

        TextureWrap wrapS = reference.WrapS == 1 || material.Flags.HasFlag(MaterialFlags.ClampS) ? TextureWrap.Clamp : TextureWrap.Repeat;
        TextureWrap wrapT = reference.WrapT == 1 || material.Flags.HasFlag(MaterialFlags.ClampT) ? TextureWrap.Clamp : TextureWrap.Repeat;
        return new GenericShader(material.Blend, textures.Get(reference.Key), wrapS, wrapT, flat, unlit, material.AlphaRef);
    }

    private sealed class GenericShader(BlendMode blend, RgbaImage? image, TextureWrap wrapS, TextureWrap wrapT, Rgb flat, bool unlit, double alphaRef)
        : BatchShader(blend)
    {
        public override bool Shade(double u, double v, out Rgb colour, out double alpha)
        {
            Rgb texel = flat;
            double a = 1.0;
            if (image is not null)
            {
                Rgba sample = image.Sample(u, v, wrapS, wrapT, TextureFilter.Nearest);
                texel = new Rgb(sample.R, sample.G, sample.B);
                a = sample.A / 255.0;
            }

            alpha = 1.0;
            colour = unlit ? texel : texel.Scaled(Intensity);
            switch (Blend)
            {
                case BlendMode.Opaque:
                    return true;
                case BlendMode.AlphaKey:
                    return a >= alphaRef;
                case BlendMode.AlphaBlend:
                case BlendMode.AdditiveAlpha:
                    alpha = a;
                    return a > 0;
                default:
                    // Additive, modulate and modulate-2x ignore the texture's alpha channel.
                    return true;
            }
        }
    }
}

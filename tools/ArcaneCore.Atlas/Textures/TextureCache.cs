using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Textures;

/// <summary>A texture request that cannot be served, with a stable code the HTTP endpoint and the commands map to a status.</summary>
public sealed class TextureException(string code, string message) : Exception(message)
{
    /// <summary>An argument (<c>fmt</c>, <c>maxSize</c>, <c>mip</c>) is malformed or out of range.</summary>
    public const string BadArguments = "bad-args";

    /// <summary>The texture key is malformed.</summary>
    public const string BadKey = "bad-key";

    /// <summary>The key is well formed but names nothing the client has.</summary>
    public const string NotFound = "not-found";

    /// <summary>The file exists but is not a texture this decoder can read.</summary>
    public const string Rejected = "rejected";

    /// <summary>A <c>gen:</c> key names a scheme no module registered.</summary>
    public const string UnknownScheme = "unknown-texture-scheme";

    public string Code { get; } = code;
}

/// <summary>What the caller wants back: decoded RGBA8, or the file's own DXT blocks when it has them.</summary>
public enum TextureDelivery
{
    Rgba,
    Dxt,
}

/// <summary>An encoded texture: the ATX1 bytes and the same texture parsed (the command twin reads metadata from it).</summary>
public sealed record TextureReply(byte[] Wire, AtxTexture Texture);

/// <summary>Counters of a <see cref="TextureCache"/>. Hits and misses count wire-entry lookups; decodes count BLP decodes.</summary>
public readonly record struct TextureCacheStats(long Hits, long Misses, long Decodes, long Encodes);

/// <summary>
/// Turns a texture key (design section 2.4) into ATX1 bytes. <c>client:&lt;path&gt;</c> reads a BLP from the
/// <see cref="IClientFileSource"/> (only <c>.blp</c> names that the source lists are served), <c>gen:&lt;scheme&gt;/&lt;arguments&gt;</c> asks a
/// registered <see cref="IGeneratedTextureSource"/>, and <c>missing</c> is the magenta and black placeholder. Each BLP is decoded once
/// (the decoded cache, bounded by <c>decodedBudgetBytes</c>) and each (key, delivery, maxSize) is encoded once (the wire cache,
/// bounded by <c>wireBudgetBytes</c>). Failures are <see cref="TextureException"/> and are not cached.
/// <para>
/// Thread affinity: any thread; concurrent requests for one key share one decode and one encode. State: two
/// <see cref="CostBoundedLazyCache{TKey, TValue}"/> instances and four counters. Allocation: one decode allocates the whole mip
/// chain (RGBA8); wire entries hold the ATX1 bytes plus a parsed copy.
/// </para>
/// </summary>
public sealed class TextureCache : ICacheParticipant
{
    /// <summary>Default budget for decoded images (256 MiB).</summary>
    public const long DefaultDecodedBudgetBytes = 256L * 1024 * 1024;

    /// <summary>Default budget for encoded ATX1 entries (128 MiB).</summary>
    public const long DefaultWireBudgetBytes = 128L * 1024 * 1024;

    /// <summary>Longest key accepted.</summary>
    public const int MaxKeyLength = 1024;

    private const string ClientPrefix = "client:";
    private const string GeneratedPrefix = "gen:";
    private const string MissingKey = "missing";

    private readonly IClientFileSource _files;
    private readonly GeneratedTextureRegistry _generated;
    private readonly CostBoundedLazyCache<string, Decoded> _decoded;
    private readonly CostBoundedLazyCache<string, TextureReply> _wire;
    private long _decodes;
    private long _encodes;

    public TextureCache(IClientFileSource files, GeneratedTextureRegistry generated, long decodedBudgetBytes = DefaultDecodedBudgetBytes, long wireBudgetBytes = DefaultWireBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentOutOfRangeException.ThrowIfLessThan(decodedBudgetBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(wireBudgetBytes, 1);
        _files = files;
        _generated = generated;
        DecodedBudgetBytes = decodedBudgetBytes;
        WireBudgetBytes = wireBudgetBytes;
        _decoded = new CostBoundedLazyCache<string, Decoded>(decodedBudgetBytes);
        _wire = new CostBoundedLazyCache<string, TextureReply>(wireBudgetBytes);
    }

    public string Name => "textures";

    public long DecodedBudgetBytes { get; }

    public long WireBudgetBytes { get; }

    /// <summary>Encoded entries held.</summary>
    public int Count => _wire.Count;

    /// <summary>Bytes held by the decoded images and the encoded entries.</summary>
    public long Bytes => _decoded.Cost + _wire.Cost;

    public TextureCacheStats Stats => new(_wire.Hits, _wire.Loads, Interlocked.Read(ref _decodes), Interlocked.Read(ref _encodes));

    public void Clear()
    {
        _wire.Clear();
        _decoded.Clear();
    }

    /// <summary>The texture for <paramref name="key"/> in the requested delivery, with levels above <paramref name="maxSize"/> dropped.</summary>
    /// <param name="maxSize">0 for the full size, or a power of two from 1 to 4096: the largest width or height to deliver.</param>
    /// <exception cref="TextureException">The key or an argument is bad, the texture does not exist, or the file is not a readable BLP.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public TextureReply Get(string key, TextureDelivery delivery, int maxSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxSize < 0 || maxSize > BlpFile.MaxDimension || (maxSize != 0 && (maxSize & (maxSize - 1)) != 0))
        {
            throw new TextureException(TextureException.BadArguments, $"maxSize must be 0 or a power of two up to {BlpFile.MaxDimension}, not {maxSize}.");
        }

        ParsedKey parsed = ParseKey(key);
        string wireKey = $"{parsed.Canonical}|{(int)delivery}|{maxSize}";
        return _wire.GetOrLoad(
            wireKey,
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                TextureReply reply = parsed.Kind switch
                {
                    KeyKind.Missing => Finish(Placeholder()),
                    KeyKind.Generated => Finish(Generate(parsed, maxSize, cancellationToken)),
                    _ => BuildClient(parsed, delivery, maxSize, cancellationToken),
                };
                Interlocked.Increment(ref _encodes);
                // An RGBA client texture's levels are the arrays the decoded cache holds, but once that entry is evicted this one still pins them,
                // so the wire entry is charged its levels as well: the figure over-counts while both caches hold them and never under-counts.
                return (reply, reply.Wire.Length + reply.Texture.Levels.Sum(l => (long)l.Length));
            },
            cancellationToken: cancellationToken);
    }

    private static TextureReply Finish(AtxTexture texture) => new(TextureWire.Write(texture), texture);

    private TextureReply BuildClient(ParsedKey parsed, TextureDelivery delivery, int maxSize, CancellationToken cancellationToken)
    {
        Decoded decoded = _decoded.GetOrLoad(
            parsed.Canonical,
            () =>
            {
                Decoded loaded = Decode(parsed, cancellationToken);
                return (loaded, loaded.Cost);
            },
            cancellationToken: cancellationToken);
        BlpImage image = decoded.Image;
        BlpFile header = image.Header;
        int skip = 0;
        if (maxSize > 0)
        {
            while (skip < image.Levels.Count - 1 && (image.Levels[skip].Width > maxSize || image.Levels[skip].Height > maxSize))
            {
                skip++;
            }
        }

        AtxFlags flags = AtxFlags.SrgbHint | (image.HasAlpha ? AtxFlags.HasAlpha : 0) | (image.Alpha1Bit ? AtxFlags.Alpha1Bit : 0);
        JsonObject meta = decoded.Meta();
        AtxTexture texture;
        RgbaImage first = image.Levels[skip];
        if (delivery == TextureDelivery.Dxt && header.IsDxt && skip < header.ValidLevelCount && first.Width % 4 == 0 && first.Height % 4 == 0)
        {
            // The file's own blocks go through untouched: the browser uploads them compressed.
            var levels = new List<byte[]>(header.ValidLevelCount - skip);
            for (int i = skip; i < header.ValidLevelCount; i++)
            {
                levels.Add(header.LevelData(i));
            }

            AtxFormat format = header.AlphaType switch
            {
                0 => AtxFormat.Dxt1,
                1 => AtxFormat.Dxt3,
                _ => AtxFormat.Dxt5,
            };
            texture = new AtxTexture(format, first.Width, first.Height, 1, flags, levels, meta);
        }
        else
        {
            var levels = new List<byte[]>(image.Levels.Count - skip);
            for (int i = skip; i < image.Levels.Count; i++)
            {
                levels.Add(image.Levels[i].Pixels);
            }

            texture = new AtxTexture(AtxFormat.Rgba8, first.Width, first.Height, 1, flags, levels, meta);
        }

        return Finish(texture);
    }

    private Decoded Decode(ParsedKey parsed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_files.Exists(parsed.Path))
        {
            throw new TextureException(TextureException.NotFound, $"The client has no file '{parsed.Path}'.");
        }

        byte[]? bytes;
        try
        {
            bytes = _files.Read(parsed.Path);
        }
        catch (InvalidDataException e)
        {
            throw new TextureException(TextureException.Rejected, $"'{parsed.Path}' could not be read from its archive: {e.Message}");
        }

        if (bytes is null)
        {
            throw new TextureException(TextureException.NotFound, $"The client has no file '{parsed.Path}'.");
        }

        BlpImage image;
        try
        {
            image = BlpDecoder.Decode(bytes);
        }
        catch (InvalidDataException e)
        {
            throw new TextureException(TextureException.Rejected, $"'{parsed.Path}' is not a readable BLP: {e.Message}");
        }

        Interlocked.Increment(ref _decodes);
        string listed = _files.List(parsed.Path).FirstOrDefault(p => string.Equals(ClientPath.Key(p), ClientPath.Key(parsed.Path), StringComparison.Ordinal)) ?? parsed.Path;
        RgbaImage smallest = image.Levels[image.Header.ValidLevelCount - 1];
        double[] sum = new double[4];
        byte[] pixels = smallest.Pixels;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            sum[0] += pixels[i];
            sum[1] += pixels[i + 1];
            sum[2] += pixels[i + 2];
            sum[3] += pixels[i + 3];
        }

        double count = pixels.Length / 4.0;
        return new Decoded(
            image,
            parsed.Canonical,
            listed,
            _files.ArchiveOf(parsed.Path),
            [Math.Round(sum[0] / count, 2), Math.Round(sum[1] / count, 2), Math.Round(sum[2] / count, 2), Math.Round(sum[3] / count, 2)],
            bytes.Length + image.PixelBytes);
    }

    private AtxTexture Generate(ParsedKey parsed, int maxSize, CancellationToken cancellationToken)
    {
        if (!_generated.TryGet(parsed.Scheme, out IGeneratedTextureSource? source))
        {
            throw new TextureException(TextureException.UnknownScheme, $"No texture scheme '{parsed.Scheme}' is registered.");
        }

        AtxTexture made;
        try
        {
            made = source.Generate(parsed.Arguments, cancellationToken);
        }
        catch (InvalidDataException e)
        {
            throw new TextureException(TextureException.Rejected, $"Scheme '{parsed.Scheme}' could not build '{parsed.Arguments}': {e.Message}");
        }

        var meta = new JsonObject { ["key"] = parsed.Canonical };
        foreach ((string name, JsonNode? value) in made.Meta)
        {
            if (name != "key")
            {
                meta[name] = value?.DeepClone();
            }
        }

        IReadOnlyList<byte[]> levels = made.Levels;
        int width = made.Width;
        int height = made.Height;
        if (maxSize > 0 && made.Format == AtxFormat.Rgba8)
        {
            int skip = 0;
            while (skip < levels.Count - 1 && (Math.Max(1, width >> skip) > maxSize || Math.Max(1, height >> skip) > maxSize))
            {
                skip++;
            }

            levels = [.. levels.Skip(skip)];
            width = Math.Max(1, width >> skip);
            height = Math.Max(1, height >> skip);
        }

        if (meta["mean"] is null && made.Format == AtxFormat.Rgba8)
        {
            meta["mean"] = Mean(levels[^1], made.Layers);
        }

        return new AtxTexture(made.Format, width, height, made.Layers, made.Flags | AtxFlags.Generated, levels, meta);
    }

    private static AtxTexture Placeholder()
    {
        const int Size = 8;
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool magenta = ((x ^ y) & 1) == 0;
                int at = ((y * Size) + x) * 4;
                pixels[at] = magenta ? (byte)255 : (byte)0;
                pixels[at + 2] = magenta ? (byte)255 : (byte)0;
                pixels[at + 3] = 255;
            }
        }

        var levels = new List<byte[]> { pixels };
        RgbaImage level = new(Size, Size, pixels);
        while (level.Width > 1 || level.Height > 1)
        {
            level = BlpDecoder.BoxDown(level);
            levels.Add(level.Pixels);
        }

        return new AtxTexture(
            AtxFormat.Rgba8,
            Size,
            Size,
            1,
            AtxFlags.Generated,
            levels,
            new JsonObject { ["key"] = MissingKey, ["mean"] = new JsonArray(127.5, 0, 127.5, 255) });
    }

    private static JsonArray Mean(byte[] pixels, int layers)
    {
        double[] sum = new double[4];
        int perLayer = pixels.Length / Math.Max(1, layers);
        for (int i = 0; i < perLayer; i += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                sum[c] += pixels[i + c];
            }
        }

        double count = Math.Max(1, perLayer / 4.0);
        return new JsonArray(Math.Round(sum[0] / count, 2), Math.Round(sum[1] / count, 2), Math.Round(sum[2] / count, 2), Math.Round(sum[3] / count, 2));
    }

    private static ParsedKey ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength || key.Any(char.IsControl))
        {
            throw new TextureException(TextureException.BadKey, "A texture key is 'client:<path>.blp', 'gen:<scheme>/<arguments>' or 'missing'.");
        }

        if (key == MissingKey)
        {
            return new ParsedKey(KeyKind.Missing, MissingKey, string.Empty, string.Empty, string.Empty);
        }

        if (key.StartsWith(ClientPrefix, StringComparison.Ordinal))
        {
            string path = key[ClientPrefix.Length..].Replace('/', '\\').TrimStart('\\');
            if (path.Length == 0)
            {
                throw new TextureException(TextureException.BadKey, "A client texture key needs a path after 'client:'.");
            }

            // Anything that could leave the client's own namespace is simply a name the client does not have.
            if (path.Contains(':') || path.Split('\\').Any(s => s is ".." or "."))
            {
                throw new TextureException(TextureException.NotFound, "The client has no such file.");
            }

            if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            {
                throw new TextureException(TextureException.BadKey, "Only .blp client files are textures.");
            }

            return new ParsedKey(KeyKind.Client, ClientPrefix + path.ToLowerInvariant(), path, string.Empty, string.Empty);
        }

        if (key.StartsWith(GeneratedPrefix, StringComparison.Ordinal))
        {
            string rest = key[GeneratedPrefix.Length..];
            int slash = rest.IndexOf('/');
            string scheme = slash < 0 ? rest : rest[..slash];
            string arguments = slash < 0 ? string.Empty : rest[(slash + 1)..];
            if (!GeneratedTextureRegistry.IsValidScheme(scheme))
            {
                throw new TextureException(TextureException.BadKey, "A generated texture key is 'gen:<scheme>/<arguments>' with a lower-case scheme.");
            }

            return new ParsedKey(KeyKind.Generated, key, string.Empty, scheme, arguments);
        }

        throw new TextureException(TextureException.BadKey, "A texture key is 'client:<path>.blp', 'gen:<scheme>/<arguments>' or 'missing'.");
    }

    private enum KeyKind
    {
        Client,
        Generated,
        Missing,
    }

    private sealed record ParsedKey(KeyKind Kind, string Canonical, string Path, string Scheme, string Arguments);

    private sealed record Decoded(BlpImage Image, string Canonical, string Path, string? Archive, double[] MeanValues, long Cost)
    {
        public JsonObject Meta()
        {
            BlpFile header = Image.Header;
            var meta = new JsonObject { ["key"] = Canonical, ["path"] = Path };
            if (Archive is not null)
            {
                meta["archive"] = Archive;
            }

            meta["source"] = new JsonObject
            {
                ["compression"] = header.Compression,
                ["alphaDepth"] = header.AlphaDepth,
                ["alphaType"] = header.AlphaType,
            };
            meta["mean"] = new JsonArray(MeanValues[0], MeanValues[1], MeanValues[2], MeanValues[3]);
            return meta;
        }
    }
}

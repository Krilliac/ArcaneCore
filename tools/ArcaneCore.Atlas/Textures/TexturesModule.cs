using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Render;

namespace ArcaneCore.Atlas.Textures;

/// <summary>
/// Textures for every other module: the <see cref="TextureCache"/> and <see cref="GeneratedTextureRegistry"/> services, the
/// <c>GET /api/bin/texture</c> endpoint, the cache in <c>status</c> and <c>cache.clear</c>, and the commands <c>texture.get</c> and
/// <c>texture.stats</c>. Registers after the client (so it finds the file source) and before the layers (so a layer module can add
/// generated schemes while it registers).
/// </summary>
public sealed class TexturesModule : IAtlasModule
{
    public string Name => "textures";

    public int Order => ModuleOrder.Foundation + 50;

    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IClientFileSource? files = context.Services.Get<IClientFileSource>();
        var generated = new GeneratedTextureRegistry();
        var cache = new TextureCache(files ?? new EmptyClientFiles(), generated);
        context.Services.Add(generated);
        context.Services.Add(cache);
        context.CacheParticipants.Add(cache);
        context.BinaryEndpoints.Add(new TextureEndpoint(cache));
        context.OnComposed(generated.Freeze);
        var handlers = new Handlers(cache, generated, files is not null);
        context.Commands.Register(new CommandDefinition(
            "texture.get",
            "Decodes one client texture (a BLP) or a generated one and reports its metadata: size, format, levels, alpha, source encoding, mean colour. Never returns pixels; with out it writes one mip level as a PNG.",
            [
                new CommandArg("key", ArgType.String, "Texture key: client:<path>.blp, gen:<scheme>/<arguments> or missing. Give key or path."),
                new CommandArg("path", ArgType.String, "In-archive path of a .blp, for example Creature\\Wolf\\WolfSkinTimber.blp (the same as key=client:<path>)."),
                new CommandArg("fmt", ArgType.String, "Delivery: rgba (default) or dxt (the file's own blocks when it has them).", Choices: ["rgba", "dxt"]),
                new CommandArg("maxSize", ArgType.Integer, "0 (default) or a power of two up to 4096: drop mip levels larger than this."),
                new CommandArg("mip", ArgType.Integer, "Mip level written to out, 0 (default) is the largest."),
                new CommandArg("out", ArgType.String, "Path of a PNG to write; must end with .png and lie outside every git checkout. Absolute unless the command runs in your own process."),
            ],
            handlers.Get));
        context.Commands.Register(new CommandDefinition(
            "texture.stats",
            "Texture cache entries, bytes, hits, misses, decodes and encodes, its budgets, and the generated texture schemes.",
            [],
            handlers.Stats));
    }

    private sealed class Handlers(TextureCache cache, GeneratedTextureRegistry generated, bool hasClient)
    {
        public Task<JsonNode?> Get(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            string? key = invocation.OptionalString("key");
            string? path = invocation.OptionalString("path");
            if ((key is null) == (path is null))
            {
                throw new CommandException(CommandException.BadArguments, "texture.get: give exactly one of 'key' or 'path'.");
            }

            key ??= "client:" + path;
            TextureDelivery delivery = invocation.OptionalString("fmt") == "dxt" ? TextureDelivery.Dxt : TextureDelivery.Rgba;
            long maxSizeArg = invocation.OptionalInt("maxSize") ?? 0;
            long? mip = invocation.OptionalInt("mip");
            if (maxSizeArg is < 0 or > BlpFile.MaxDimension)
            {
                throw new CommandException(CommandException.BadArguments, $"texture.get: maxSize must be 0 or a power of two up to {BlpFile.MaxDimension}.");
            }

            if (mip is < 0 or > 15)
            {
                throw new CommandException(CommandException.BadArguments, "texture.get: mip must be 0 to 15.");
            }

            string? outArg = invocation.OptionalString("out");
            string? outPath = outArg is null ? null : OutputPaths.Resolve("texture.get", outArg, ".png", invocation.Headless);
            if (!hasClient && key.StartsWith("client:", StringComparison.Ordinal))
            {
                throw new CommandException(CommandException.Unavailable, "texture.get needs the client files; set ARCANECORE_CLIENT_DIR (see client.status).");
            }

            int maxSize = (int)maxSizeArg;
            return Task.Run<JsonNode?>(
                () =>
                {
                    try
                    {
                        TextureReply reply = cache.Get(key, delivery, maxSize, cancellationToken);
                        JsonObject data = Describe(reply);
                        if (mip is not null || outPath is not null)
                        {
                            // Pixels for a PNG (or a mip check) always come from the decoded delivery, whatever the caller asked to describe.
                            TextureReply rgba = delivery == TextureDelivery.Rgba ? reply : cache.Get(key, TextureDelivery.Rgba, maxSize, cancellationToken);
                            int level = (int)(mip ?? 0);
                            if (level >= rgba.Texture.Levels.Count)
                            {
                                throw new CommandException(CommandException.BadArguments, $"texture.get: mip {level} does not exist; this texture has {rgba.Texture.Levels.Count} levels (0 to {rgba.Texture.Levels.Count - 1}).");
                            }

                            if (outPath is not null && rgba.Texture.Format != AtxFormat.Rgba8)
                            {
                                throw new CommandException(CommandException.Unavailable, $"texture.get: cannot write a PNG of a {rgba.Texture.Format} texture.");
                            }

                            if (outPath is not null)
                            {
                                AtxTexture texture = rgba.Texture;
                                int width = Math.Max(1, texture.Width >> level);
                                int height = Math.Max(1, texture.Height >> level);
                                byte[] png = PngEncoder.Encode(texture.Levels[level].AsSpan(0, width * height * 4), width, height);
                                OutputPaths.WriteAtomically(outPath, stream => stream.Write(png));
                                data["written"] = new JsonObject { ["path"] = outPath, ["bytes"] = png.Length, ["mip"] = level, ["width"] = width, ["height"] = height };
                            }
                        }

                        return data;
                    }
                    catch (TextureException e)
                    {
                        throw e.Code is TextureException.BadArguments or TextureException.BadKey
                            ? new CommandException(CommandException.BadArguments, $"texture.get: {e.Message}")
                            : new CommandException(CommandException.Failed, $"texture.get: {e.Code}: {e.Message}");
                    }
                },
                cancellationToken);
        }

        public Task<JsonNode?> Stats(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            TextureCacheStats stats = cache.Stats;
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["count"] = cache.Count,
                ["bytes"] = cache.Bytes,
                ["hits"] = stats.Hits,
                ["misses"] = stats.Misses,
                ["decodes"] = stats.Decodes,
                ["encodes"] = stats.Encodes,
                ["decodedBudgetBytes"] = cache.DecodedBudgetBytes,
                ["wireBudgetBytes"] = cache.WireBudgetBytes,
                ["schemes"] = new JsonArray([.. generated.Schemes.Select(s => (JsonNode)s)]),
            });
        }

        private static JsonObject Describe(TextureReply reply)
        {
            AtxTexture texture = reply.Texture;
            var data = new JsonObject();
            foreach (string name in new[] { "key", "path", "archive" })
            {
                if (texture.Meta[name] is { } value)
                {
                    data[name] = value.DeepClone();
                }
            }

            data["format"] = texture.Format.ToString().ToLowerInvariant();
            data["width"] = texture.Width;
            data["height"] = texture.Height;
            data["layers"] = texture.Layers;
            data["levels"] = texture.Levels.Count;
            data["hasAlpha"] = texture.Flags.HasFlag(AtxFlags.HasAlpha);
            data["alpha1Bit"] = texture.Flags.HasFlag(AtxFlags.Alpha1Bit);
            data["generated"] = texture.Flags.HasFlag(AtxFlags.Generated);
            foreach (string name in new[] { "source", "mean" })
            {
                if (texture.Meta[name] is { } value)
                {
                    data[name] = value.DeepClone();
                }
            }

            data["wireBytes"] = reply.Wire.Length;
            return data;
        }
    }

    /// <summary>A file source with no files, for a host that has no client configured.</summary>
    private sealed class EmptyClientFiles : IClientFileSource
    {
        public string Description => "no client";

        public bool Exists(string path) => false;

        public byte[]? Read(string path) => null;

        public IReadOnlyList<string> List(string prefix) => [];

        public string? ArchiveOf(string path) => null;
    }
}

/// <summary><c>GET /api/bin/texture?key=&amp;fmt=&amp;maxSize=</c>: the ATX1 bytes of a texture key.</summary>
internal sealed class TextureEndpoint(TextureCache cache) : IBinaryEndpoint
{
    public string Name => "texture";

    public async Task<BinaryReply> HandleAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (!query.TryGetValue("key", out string? key) || string.IsNullOrEmpty(key))
        {
            return BinaryReply.Error(400, TextureException.BadArguments, "The 'key' query parameter is required.");
        }

        TextureDelivery delivery;
        switch (query.GetValueOrDefault("fmt"))
        {
            case null or "" or "rgba":
                delivery = TextureDelivery.Rgba;
                break;
            case "dxt":
                delivery = TextureDelivery.Dxt;
                break;
            default:
                return BinaryReply.Error(400, TextureException.BadArguments, "'fmt' must be rgba or dxt.");
        }

        int maxSize = 0;
        if (query.TryGetValue("maxSize", out string? sizeText)
            && !int.TryParse(sizeText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxSize))
        {
            return BinaryReply.Error(400, TextureException.BadArguments, "'maxSize' must be a whole number.");
        }

        try
        {
            TextureReply reply = await Task.Run(() => cache.Get(key, delivery, maxSize, cancellationToken), cancellationToken).ConfigureAwait(false);
            return BinaryReply.Ok(reply.Wire, "ATX1");
        }
        catch (TextureException e)
        {
            return BinaryReply.Error(StatusFor(e.Code), e.Code, e.Message);
        }
    }

    private static int StatusFor(string code) => code switch
    {
        TextureException.BadArguments or TextureException.BadKey => 400,
        TextureException.NotFound or TextureException.UnknownScheme => 404,
        TextureException.Rejected => 422,
        _ => 500,
    };
}

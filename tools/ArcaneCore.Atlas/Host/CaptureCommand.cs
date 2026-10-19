using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// <c>capture</c>: a PNG of the view. <c>source=ui</c> asks the connected UI to render off-screen over its WebSocket
/// (<see cref="UiHub.RequestCaptureAsync"/>, 15 s timeout); <c>headless</c> renders with the registered
/// <see cref="IHeadlessRenderer"/>; <c>auto</c> (default) uses the UI when one is connected and the renderer otherwise.
/// </summary>
internal static class CaptureCommand
{
    public const int DefaultWidth = 1600;
    public const int DefaultHeight = 900;
    public const int MinSide = 16;
    public const int MaxSide = 8192;
    public const long MaxPixels = 8192L * 4096;

    // ref: W3C "Portable Network Graphics (PNG) Specification (Third Edition)", 5.2 PNG signature: 89 50 4E 47 0D 0A 1A 0A.
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new CommandDefinition(
            "capture",
            "Renders the view to a PNG file and returns its path. A connected UI renders it (source ui); otherwise the CPU renderer does (source headless).",
            [
                new("width", ArgType.Integer, $"Image width in pixels, {MinSide} to {MaxSide}.", Default: DefaultWidth),
                new("height", ArgType.Integer, $"Image height in pixels, {MinSide} to {MaxSide}.", Default: DefaultHeight),
                new("out", ArgType.String, "A .png file path, or a directory for a generated name. Default: the captures folder under the Atlas state directory."),
                new("source", ArgType.String, "auto uses the UI when one is connected, else the headless renderer.", Default: "auto", Choices: ["auto", "ui", "headless"]),
                new("view", ArgType.Object, "A view merge patch applied to a copy of the current view (the shared view is not changed)."),
            ],
            Capture));
    }

    /// <summary>Whether <paramref name="data"/> starts with the PNG signature.</summary>
    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(PngSignature);

    /// <summary>
    /// Width and height from the IHDR chunk, which must come first: 4-byte length, "IHDR", then width and height as
    /// big-endian 32-bit integers (ref: W3C PNG Third Edition, 5.3 chunk layout and 11.2.2 IHDR image header).
    /// Null when the data is not a PNG or its first chunk is not a 13-byte IHDR with non-zero dimensions.
    /// </summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> data)
    {
        if (!IsPng(data) || data.Length < 24 || BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        return width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? ((int)width, (int)height) : null;
    }

    private static async Task<JsonNode?> Capture(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        AtlasContext context = invocation.Service<AtlasContext>();
        int width = Side(invocation, "width", DefaultWidth);
        int height = Side(invocation, "height", DefaultHeight);
        if ((long)width * height > MaxPixels)
        {
            throw new CommandException(CommandException.BadArguments, $"capture: {width}x{height} is over the {MaxPixels} pixel limit.");
        }

        string source = invocation.OptionalString("source") ?? "auto";
        JsonObject? patch = invocation.OptionalObject("view");
        ViewState view = patch is null ? context.View.Current : context.View.Current.Apply(patch);
        UiHub? hub = context.Services.Get<UiHub>();
        bool uiConnected = !invocation.Headless && hub is { ConnectionCount: > 0 };
        string chosen = source == "auto" ? (uiConnected ? "ui" : "headless") : source;

        byte[] png;
        int outWidth;
        int outHeight;
        JsonObject? stats = null;
        if (chosen == "ui")
        {
            if (invocation.Headless)
            {
                throw new CommandException(CommandException.Unavailable, "capture: source 'ui' needs a running 'arcane-atlas serve' instance with a UI open.");
            }

            if (hub is null || hub.ConnectionCount == 0)
            {
                throw new CommandException(CommandException.Unavailable, "capture: no UI is connected; open one with 'arcane-atlas serve --open' or use source=headless.");
            }

            png = await hub.RequestCaptureAsync(width, height, patch is null ? null : view.ToJson(), cancellationToken).ConfigureAwait(false);
            (outWidth, outHeight) = PngSize(png) ?? throw new CommandException(CommandException.Failed, "capture: the UI answered with data that is not a PNG.");
        }
        else
        {
            IHeadlessRenderer renderer = context.Services.Get<IHeadlessRenderer>()
                ?? throw new CommandException(CommandException.Unavailable, source == "auto"
                    ? "capture: no UI is connected and no headless renderer is registered in this build."
                    : "capture: no headless renderer is registered in this build.");
            RenderedImage image = await Task.Run(() => renderer.Render(view, width, height, cancellationToken), cancellationToken).ConfigureAwait(false);
            png = image.Png;
            (outWidth, outHeight) = PngSize(png) ?? throw new CommandException(CommandException.Failed, "capture: the headless renderer returned data that is not a PNG.");
            stats = image.Stats;
        }

        string path = ResolveOutput(invocation.OptionalString("out"), invocation.Service<AtlasState>());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, png, cancellationToken).ConfigureAwait(false);
        var result = new JsonObject { ["path"] = path, ["width"] = outWidth, ["height"] = outHeight, ["source"] = chosen, ["bytes"] = png.Length };
        if (stats is not null)
        {
            result["stats"] = stats.DeepClone();
        }

        return result;
    }

    private static int Side(CommandInvocation invocation, string name, int fallback)
    {
        long value = invocation.OptionalInt(name) ?? fallback;
        return value is >= MinSide and <= MaxSide
            ? (int)value
            : throw new CommandException(CommandException.BadArguments, $"capture: argument '{name}' must be within [{MinSide}, {MaxSide}].");
    }

    private static string ResolveOutput(string? requested, AtlasState state)
    {
        string name = $"atlas-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}.png";
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Path.Combine(state.CapturesDirectory, name);
        }

        if (requested.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(requested);
        }

        if (requested.EndsWith('/') || requested.EndsWith('\\') || !Path.HasExtension(requested))
        {
            return Path.Combine(Path.GetFullPath(requested), name);
        }

        throw new CommandException(CommandException.BadArguments, "capture: 'out' must be a .png file path or a directory.");
    }
}

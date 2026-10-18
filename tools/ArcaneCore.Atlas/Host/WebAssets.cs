using System.Collections.Concurrent;
using System.Reflection;

namespace ArcaneCore.Atlas.Host;

/// <summary>One static file of the UI.</summary>
public sealed record WebAsset(byte[] Content, string ContentType);

/// <summary>Where the UI's static files come from (embedded in the assembly in production; a fake in tests).</summary>
public interface IWebAssets
{
    /// <summary>The file at a relative, forward-slash path such as <c>index.html</c> or <c>js/main.js</c>.</summary>
    bool TryGet(string path, out WebAsset asset);
}

/// <summary>
/// Serves the resources the project embeds from <c>Web/**</c> under the logical names <c>web/&lt;path&gt;</c> (see the
/// EmbeddedResource item in the project file). MSBuild writes the path with the platform's separator, so both separators
/// are accepted when indexing.
/// </summary>
public sealed class EmbeddedWebAssets : IWebAssets
{
    private const string Prefix = "web/";

    private readonly Assembly _assembly;
    private readonly Dictionary<string, string> _resourceNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WebAsset> _loaded = new(StringComparer.Ordinal);

    public EmbeddedWebAssets(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _assembly = assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                _resourceNames[name[Prefix.Length..].Replace('\\', '/')] = name;
            }
        }
    }

    /// <summary>The paths of every embedded file, ordered.</summary>
    public IReadOnlyList<string> Paths => [.. _resourceNames.Keys.Order(StringComparer.Ordinal)];

    public bool TryGet(string path, out WebAsset asset)
    {
        if (_loaded.TryGetValue(path, out asset!))
        {
            return true;
        }

        if (!_resourceNames.TryGetValue(path, out string? name))
        {
            return false;
        }

        using Stream stream = _assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded resource '{name}' is listed but cannot be opened.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        asset = _loaded.GetOrAdd(path, new WebAsset(buffer.ToArray(), ContentTypes.For(path)));
        return true;
    }
}

/// <summary>Content types of the files the UI ships (anything else is served as an opaque download).</summary>
internal static class ContentTypes
{
    public static string For(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".txt" => "text/plain; charset=utf-8",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream",
        };
}

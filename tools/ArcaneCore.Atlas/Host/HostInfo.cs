using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host;

/// <summary>The build identity of this executable (reported by <c>status</c>, the discovery file and the WebSocket hello).</summary>
public static class AtlasVersion
{
    /// <summary>The assembly's informational version (the source revision is appended after a '+' when the build recorded it).</summary>
    public static string Current { get; } = typeof(AtlasVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}

/// <summary>The modules a context was composed from, registered by <see cref="HostComposer"/> for <c>status</c>.</summary>
public sealed record ModuleList(IReadOnlyList<string> Names);

/// <summary>Composes a context from discovered or explicit modules and records the module names.</summary>
public static class HostComposer
{
    /// <summary>
    /// Runs <see cref="AtlasModules"/> composition over <paramref name="modules"/> (default: every module of the Atlas
    /// assembly) and registers the <see cref="ModuleList"/> that <c>status</c> reports.
    /// </summary>
    public static AtlasContext Compose(AtlasPaths paths, ILoggerFactory loggers, bool headless, IReadOnlyList<IAtlasModule>? modules = null)
    {
        IReadOnlyList<IAtlasModule> list = modules ?? AtlasModules.Discover(typeof(AtlasModules).Assembly);
        AtlasContext context = AtlasModules.Compose(paths, loggers, headless, list);
        context.Services.Add(new ModuleList([.. list.OrderBy(m => m.Order).ThenBy(m => m.Name, StringComparer.Ordinal).Select(m => m.Name)]));
        return context;
    }
}

/// <summary>Where a command's JSON origin label comes from (the HTTP surface, a CLI verb, or a UI WebSocket connection).</summary>
public static class HostOrigin
{
    private static readonly AsyncLocal<string?> Override = new();

    /// <summary>The label set by the innermost <see cref="With"/> scope on this async flow, or null.</summary>
    public static string? Current => Override.Value;

    /// <summary>The label for a command call: the scope override (<c>ui:&lt;connection&gt;</c>) else the lower-case origin kind.</summary>
    public static string Label(CommandOrigin origin) => Override.Value ?? origin.ToString().ToLowerInvariant();

    /// <summary>Labels every command started inside the returned scope; dispose to restore the previous label.</summary>
    public static IDisposable With(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        string? previous = Override.Value;
        Override.Value = label;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}

/// <summary>Small JSON helpers shared by the host.</summary>
internal static class HostJson
{
    /// <summary>
    /// The reader settings every request surface shares (HTTP bodies, WebSocket frames, ctl arguments, script and MCP
    /// lines, state files): depth-capped and strict about duplicate property names, so a duplicate is a parse error at the
    /// boundary (400, usage error, script line problem, JSON-RPC -32700) instead of an exception when the object is read.
    /// </summary>
    public static readonly JsonDocumentOptions Strict = new() { MaxDepth = 64, AllowDuplicateProperties = false };

    public static JsonArray Point(WorldPoint p) => new(p.X, p.Y, p.Z);

    public static JsonObject Failure(string command, string code, string message)
        => new() { ["command"] = command, ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

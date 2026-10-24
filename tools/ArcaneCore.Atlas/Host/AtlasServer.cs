using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host;

/// <summary>Settings of one <see cref="AtlasServer"/>.</summary>
public sealed record AtlasServerOptions
{
    /// <summary>TCP port on 127.0.0.1; 0 picks a free one.</summary>
    public int Port { get; init; }

    /// <summary>The UI's static files; default the files embedded in this assembly.</summary>
    public IWebAssets? Assets { get; init; }

    /// <summary>How long a client has to send its <c>auth</c> message after the WebSocket opens (5 s).</summary>
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long <c>capture</c> waits for the UI (15 s).</summary>
    public TimeSpan CaptureTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Whether to publish the discovery file <c>ctl</c> finds this instance through.</summary>
    public bool WriteDiscovery { get; init; } = true;
}

/// <summary>A host lifetime that never listens for Ctrl+C or SIGTERM: the caller owns the process lifetime.</summary>
internal sealed class LoopbackLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The loopback web server: REST routes, the tile endpoint and the UI WebSocket of doc section 13, in front of one
/// <see cref="AtlasContext"/>. It listens on 127.0.0.1 only, requires a per-run bearer token on every <c>/api</c> route
/// (the WebSocket authenticates with its first message), and rejects any <c>Host</c> that is not this loopback endpoint
/// so a DNS-rebinding page cannot reach it.
/// <para>
/// Threading: requests run on Kestrel's pool threads and call into the registry, cache and layer sources, which are
/// thread-safe by contract. Each WebSocket has a receive loop, an event pump and one task per running command; all sends
/// on a socket are serialized by <see cref="UiConnection"/>.
/// </para>
/// </summary>
public sealed partial class AtlasServer : IAsyncDisposable
{
    private const long MaxCommandBodyBytes = 4L * 1024 * 1024;
    private const long MaxCaptureBodyBytes = 64L * 1024 * 1024;

    private readonly AtlasContext _context;
    private readonly AtlasServerOptions _options;
    private readonly UiHub _hub;
    private readonly AtlasState _state;
    private readonly IWebAssets _assets;
    private readonly ILogger _log;
    private readonly byte[] _tokenBytes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly WebApplication _app;
    private IDisposable? _events;
    private DiscoveryRecord? _discovery;
    private int _port;
    private int _disposed;

    private AtlasServer(AtlasContext context, AtlasServerOptions options)
    {
        _context = context;
        _options = options;
        _hub = context.Services.Get<UiHub>() ?? throw new InvalidOperationException("The host module is not registered: no UiHub.");
        _state = context.Services.Get<AtlasState>() ?? throw new InvalidOperationException("The host module is not registered: no AtlasState.");
        _assets = options.Assets ?? new EmbeddedWebAssets(typeof(AtlasServer).Assembly);
        _log = context.Loggers.CreateLogger("ArcaneCore.Atlas.Host");
        Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _tokenBytes = Encoding.UTF8.GetBytes(Token);
        _hub.CaptureTimeout = options.CaptureTimeout;
        _port = options.Port;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(context.Loggers);
        builder.Services.AddSingleton<IHostLifetime, LoopbackLifetime>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxCaptureBodyBytes;
            kestrel.Listen(IPAddress.Loopback, options.Port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        _app.Run(HandleAsync);
    }

    /// <summary>The bearer token of this run (base64url of 32 random bytes).</summary>
    public string Token { get; }

    /// <summary>The bound port (valid after <see cref="StartAsync"/>).</summary>
    public int Port => _port;

    /// <summary>The loopback address clients connect to (scheme <see cref="Uri.UriSchemeHttp"/>, host 127.0.0.1, the bound port, path /).</summary>
    public Uri BaseAddress => new($"{Uri.UriSchemeHttp}://127.0.0.1:{_port}/");

    /// <summary>The address a browser opens: the base address with the token in the fragment, which never reaches a request line or log.</summary>
    public Uri BrowserAddress => new($"{BaseAddress}#t={Token}");

    /// <summary>The discovery file this instance published, or null.</summary>
    public string? DiscoveryFile => _discovery is null ? null : Discovery.FileFor(_state, _discovery.Pid);

    public UiHub Hub => _hub;

    /// <summary>Starts listening, forwards the context's events to connected UIs and publishes the discovery file.</summary>
    /// <exception cref="IOException">The port is in use or the listener could not start.</exception>
    public static async Task<AtlasServer> StartAsync(AtlasContext context, AtlasServerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        options ??= new AtlasServerOptions();
        if (options.Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Port, "The port must be within [0, 65535].");
        }

        var server = new AtlasServer(context, options);
        try
        {
            await server._app.StartAsync(cancellationToken).ConfigureAwait(false);
            IServerAddressesFeature addresses = server._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The web server reports no listening address.");
            server._port = new Uri(addresses.Addresses.First()).Port;
            server._events = context.Events.Subscribe(server._hub.Broadcast);
            if (options.WriteDiscovery)
            {
                server._discovery = new DiscoveryRecord(Environment.ProcessId, server._port, server.Token, DateTimeOffset.UtcNow, AtlasVersion.Current, context.Paths.TerrainData, context.Paths.ClientRoot);
                Discovery.Write(server._state, server._discovery);
            }
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        server._log.LogInformation("Atlas listening on {Address} (pid {Pid})", server.BaseAddress, Environment.ProcessId);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _events?.Dispose();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_discovery is not null)
        {
            Discovery.Delete(_state, _discovery);
        }

        using (var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try
            {
                await _app.StopAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("The web server did not stop within 5 s; closing it anyway.");
            }
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task HandleAsync(HttpContext http)
    {
        try
        {
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!HostAllowed(http.Request))
            {
                await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "bad-host", "The Host header must be this loopback endpoint.").ConfigureAwait(false);
                return;
            }

            if (http.Request.Path.StartsWithSegments("/api", out PathString rest))
            {
                await ApiAsync(http, rest.Value ?? string.Empty).ConfigureAwait(false);
            }
            else
            {
                await StaticAsync(http).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested || _shutdown.IsCancellationRequested)
        {
            // The client went away or the server is stopping; there is nobody to answer.
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
        {
            _log.LogWarning("Bad request: {Message}", e.Message);
            if (!http.Response.HasStarted)
            {
                await WriteErrorAsync(http, e.StatusCode, "bad-request", e.Message).ConfigureAwait(false);
            }
        }
        catch (CommandException e)
        {
            if (!http.Response.HasStarted)
            {
                await WriteErrorAsync(http, StatusFor(e.Code), e.Code, e.Message).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Request {Method} {Path} failed", http.Request.Method, http.Request.Path);
            if (!http.Response.HasStarted)
            {
                await WriteErrorAsync(http, StatusCodes.Status500InternalServerError, "failed", $"{e.GetType().Name}: {e.Message}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>The Host header must be <c>127.0.0.1:&lt;port&gt;</c> or <c>localhost:&lt;port&gt;</c> (a rebound DNS name is anything else).</summary>
    private bool HostAllowed(HttpRequest request)
    {
        if (!request.Host.HasValue)
        {
            return false;
        }

        string host = request.Host.Value;
        return string.Equals(host, $"127.0.0.1:{_port}", StringComparison.Ordinal)
            || string.Equals(host, $"localhost:{_port}", StringComparison.OrdinalIgnoreCase);
    }

    private bool Authorized(HttpRequest request)
    {
        string? header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TokenMatches(header[scheme.Length..].Trim());
    }

    private bool TokenMatches(string candidate) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _tokenBytes);

    private static int StatusFor(string code) => code switch
    {
        CommandException.BadArguments => StatusCodes.Status400BadRequest,
        CommandException.UnknownCommand => StatusCodes.Status404NotFound,
        CommandException.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private async Task ApiAsync(HttpContext http, string rest)
    {
        string[] segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            await WriteErrorAsync(http, StatusCodes.Status404NotFound, "not-found", "No such API route.").ConfigureAwait(false);
            return;
        }

        if (segments[0] == "ws")
        {
            await WebSocketAsync(http).ConfigureAwait(false);
            return;
        }

        if (!Authorized(http.Request))
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            await WriteErrorAsync(http, StatusCodes.Status401Unauthorized, "unauthorized", "A valid 'Authorization: Bearer <token>' header is required.").ConfigureAwait(false);
            return;
        }

        http.Response.Headers.CacheControl = "no-store";
        CancellationToken ct = http.RequestAborted;
        switch (segments[0])
        {
            case "status" when segments.Length == 1:
                if (await RequireMethodAsync(http, HttpMethods.Get).ConfigureAwait(false))
                {
                    await WriteJsonAsync(http, StatusCodes.Status200OK, StatusCommands.BuildStatus(_context)).ConfigureAwait(false);
                }

                break;
            case "commands" when segments.Length == 1:
                if (await RequireMethodAsync(http, HttpMethods.Get).ConfigureAwait(false))
                {
                    await WriteJsonAsync(http, StatusCodes.Status200OK, StatusCommands.BuildCommandList(_context.Commands)).ConfigureAwait(false);
                }

                break;
            case "command" when segments.Length == 1:
                if (await RequireMethodAsync(http, HttpMethods.Post).ConfigureAwait(false))
                {
                    await CommandAsync(http, ct).ConfigureAwait(false);
                }

                break;
            case "tiles" when segments.Length == 1:
                if (await RequireMethodAsync(http, HttpMethods.Get).ConfigureAwait(false))
                {
                    await TilesAsync(http).ConfigureAwait(false);
                }

                break;
            case "tile" when segments.Length == 5:
                if (await RequireMethodAsync(http, HttpMethods.Get).ConfigureAwait(false))
                {
                    await TileAsync(http, segments[1], segments[2], segments[3], segments[4], ct).ConfigureAwait(false);
                }

                break;
            case "bin" when segments.Length == 2:
                if (await RequireMethodAsync(http, HttpMethods.Get).ConfigureAwait(false))
                {
                    await BinaryAsync(http, segments[1], ct).ConfigureAwait(false);
                }

                break;
            case "capture" when segments.Length == 2:
                if (await RequireMethodAsync(http, HttpMethods.Post).ConfigureAwait(false))
                {
                    await CaptureAnswerAsync(http, segments[1], ct).ConfigureAwait(false);
                }

                break;
            default:
                await WriteErrorAsync(http, StatusCodes.Status404NotFound, "not-found", "No such API route.").ConfigureAwait(false);
                break;
        }
    }

    private static async Task<bool> RequireMethodAsync(HttpContext http, string method)
    {
        if (string.Equals(http.Request.Method, method, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        http.Response.Headers.Allow = method;
        await WriteErrorAsync(http, StatusCodes.Status405MethodNotAllowed, "method-not-allowed", $"Use {method}.").ConfigureAwait(false);
        return false;
    }

    private async Task CommandAsync(HttpContext http, CancellationToken ct)
    {
        byte[]? body = await ReadBodyAsync(http, MaxCommandBodyBytes, ct).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        JsonObject request;
        try
        {
            request = JsonNode.Parse(body, nodeOptions: null, HostJson.Strict) as JsonObject
                ?? throw new JsonException("The body must be a JSON object.");
        }
        catch (JsonException e)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "bad-request", $"The body is not valid JSON: {e.Message}").ConfigureAwait(false);
            return;
        }

        if (request["name"] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name) || string.IsNullOrWhiteSpace(name))
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "bad-request", "The body needs a string 'name'.").ConfigureAwait(false);
            return;
        }

        JsonObject? args = null;
        if (request["args"] is { } argsNode)
        {
            if (argsNode is not JsonObject argsObject)
            {
                await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "bad-request", "'args' must be a JSON object.").ConfigureAwait(false);
                return;
            }

            args = (JsonObject)argsObject.DeepClone();
        }

        // The origin label is advisory (it only names the caller in the command log); the bearer token is what authorizes the call.
        CommandOrigin origin = request["origin"] is JsonValue originValue && originValue.TryGetValue(out string? label)
            ? label switch
            {
                "cli" => CommandOrigin.Cli,
                "script" => CommandOrigin.Script,
                "mcp" => CommandOrigin.Mcp,
                _ => CommandOrigin.Http,
            }
            : CommandOrigin.Http;
        CommandResult result = await _context.Commands.InvokeAsync(name, args, _context.Services, origin, headless: false, ct).ConfigureAwait(false);
        await WriteJsonAsync(http, StatusCodes.Status200OK, result.ToJson()).ConfigureAwait(false);
    }

    private async Task TilesAsync(HttpContext http)
    {
        IQueryCollection query = http.Request.Query;
        if (!TryUInt(query["map"], out uint map))
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, CommandException.BadArguments, "'map' is required and must be a map id.").ConfigureAwait(false);
            return;
        }

        if (!TryOptionalInt(query, "x", out int? x) || !TryOptionalInt(query, "y", out int? y) || !TryOptionalInt(query, "radius", out int? radius))
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, CommandException.BadArguments, "'x', 'y' and 'radius' must be whole numbers.").ConfigureAwait(false);
            return;
        }

        string? layer = query["layer"].ToString();
        JsonObject result = StatusCommands.TileStatus(_context, map, x, y, string.IsNullOrEmpty(layer) ? null : layer, radius ?? 0);
        await WriteJsonAsync(http, StatusCodes.Status200OK, result).ConfigureAwait(false);
    }

    /// <summary>A query parameter that may be absent; false only when it is present and not a whole number.</summary>
    private static bool TryOptionalInt(IQueryCollection query, string name, out int? value)
    {
        value = null;
        if (!query.ContainsKey(name))
        {
            return true;
        }

        if (!TryInt(query[name], out int parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private async Task TileAsync(HttpContext http, string layer, string mapText, string xText, string yText, CancellationToken ct)
    {
        if (!_context.Layers.TryGet(layer, out _))
        {
            await WriteErrorAsync(http, StatusCodes.Status404NotFound, "unknown-layer", $"No source builds layer '{layer}'.").ConfigureAwait(false);
            return;
        }

        if (!uint.TryParse(mapText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint map)
            || !int.TryParse(xText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(yText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int y))
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, CommandException.BadArguments, "The tile route is /api/tile/<layer>/<map>/<x>/<y>.").ConfigureAwait(false);
            return;
        }

        var tile = new TileKey(map, x, y);
        if (!tile.IsValid)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, CommandException.BadArguments, "A tile is x,y within 0..63 or -1,-1 for the map's global content.").ConfigureAwait(false);
            return;
        }

        TileState state;
        string? detail;
        IReadOnlyList<byte[]> parts;
        try
        {
            LayerBuild build = await Task.Run(() => _context.Cache.Get(layer, tile, ct), ct).ConfigureAwait(false);
            state = build.Status.State;
            detail = build.Status.Detail;
            parts = build.Chunks.Count == 0 ? [] : await Task.Run(() => _context.Cache.GetWire(layer, tile, ct), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // A source that throws for bad data instead of reporting Rejected: say so rather than serve an empty tile as empty space.
            _log.LogWarning("Layer {Layer} tile {Tile} failed: {Message}", layer, tile, e.Message);
            state = TileState.Rejected;
            detail = $"{e.GetType().Name}: {e.Message}";
            parts = [];
        }

        long total = 4;
        foreach (byte[] part in parts)
        {
            total += 4L + part.Length;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = total;
        http.Response.Headers["X-Atlas-Tile-State"] = state.ToString();
        http.Response.Headers["X-Atlas-Tile-Detail"] = Uri.EscapeDataString(detail ?? string.Empty);
        byte[] word = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)parts.Count);
        await http.Response.Body.WriteAsync(word, ct).ConfigureAwait(false);
        foreach (byte[] part in parts)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)part.Length);
            await http.Response.Body.WriteAsync(word, ct).ConfigureAwait(false);
            await http.Response.Body.WriteAsync(part, ct).ConfigureAwait(false);
        }
    }

    /// <summary><c>GET /api/bin/{name}?...</c>: runs the registered binary endpoint and serves its body as octet-stream.</summary>
    private async Task BinaryAsync(HttpContext http, string name, CancellationToken ct)
    {
        if (!_context.BinaryEndpoints.TryGet(name, out IBinaryEndpoint? endpoint))
        {
            await WriteErrorAsync(http, StatusCodes.Status404NotFound, "not-found", $"No binary endpoint named '{name}'.").ConfigureAwait(false);
            return;
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, Microsoft.Extensions.Primitives.StringValues values) in http.Request.Query)
        {
            query[key] = values.Count > 0 ? values[0] ?? string.Empty : string.Empty;
        }

        BinaryReply reply = await endpoint.HandleAsync(query, ct).ConfigureAwait(false);
        if (!reply.IsOk)
        {
            await WriteErrorAsync(http, reply.Status, reply.ErrorCode, reply.ErrorMessage).ConfigureAwait(false);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = reply.Body.Length;
        http.Response.Headers["X-Atlas-Format"] = reply.Format;
        await http.Response.Body.WriteAsync(reply.Body, ct).ConfigureAwait(false);
    }

    private async Task CaptureAnswerAsync(HttpContext http, string id, CancellationToken ct)
    {
        byte[]? body = await ReadBodyAsync(http, MaxCaptureBodyBytes, ct).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        if (!CaptureCommand.IsPng(body))
        {
            _hub.Fail(id, "the UI posted data that is not a PNG");
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "bad-request", "The body must be a PNG image.").ConfigureAwait(false);
            return;
        }

        if (!_hub.Complete(id, body))
        {
            await WriteErrorAsync(http, StatusCodes.Status404NotFound, "not-found", "No capture request with that id is waiting (it may have timed out).").ConfigureAwait(false);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task StaticAsync(HttpContext http)
    {
        bool head = HttpMethods.IsHead(http.Request.Method);
        if (!head && !HttpMethods.IsGet(http.Request.Method))
        {
            http.Response.Headers.Allow = "GET, HEAD";
            await WriteErrorAsync(http, StatusCodes.Status405MethodNotAllowed, "method-not-allowed", "Use GET.").ConfigureAwait(false);
            return;
        }

        string path = http.Request.Path.Value ?? "/";
        string? relative = path switch
        {
            "/" or "/index.html" => "index.html",
            _ when path.StartsWith("/app/", StringComparison.Ordinal) => path["/app/".Length..],
            _ => null,
        };
        if (relative is null || !IsSafeRelativePath(relative))
        {
            await WriteErrorAsync(http, StatusCodes.Status404NotFound, "not-found", "No such file.").ConfigureAwait(false);
            return;
        }

        http.Response.Headers.CacheControl = "no-cache";
        if (!_assets.TryGet(relative, out WebAsset asset))
        {
            if (relative == "index.html")
            {
                http.Response.Headers["X-Atlas-UI"] = "missing";
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                http.Response.ContentType = "text/html; charset=utf-8";
                byte[] page = Encoding.UTF8.GetBytes(UiMissingPage);
                http.Response.ContentLength = page.Length;
                if (!head)
                {
                    await http.Response.Body.WriteAsync(page, http.RequestAborted).ConfigureAwait(false);
                }

                return;
            }

            await WriteErrorAsync(http, StatusCodes.Status404NotFound, "not-found", "No such file.").ConfigureAwait(false);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = asset.ContentType;
        http.Response.ContentLength = asset.Content.Length;
        if (!head)
        {
            await http.Response.Body.WriteAsync(asset.Content, http.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>A relative path of plain segments: no empty, '.' or '..' segment, no backslash, colon or control character.</summary>
    private static bool IsSafeRelativePath(string relative)
    {
        if (relative.Length == 0)
        {
            return false;
        }

        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return !relative.Any(c => c is '\\' or ':' || char.IsControl(c));
    }

    private const string UiMissingPage = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Atlas: UI not built</title>
        <style>body{font:15px/1.5 system-ui,sans-serif;margin:3rem auto;max-width:42rem;color:#1b262c}code{background:#e8ecee;padding:.1rem .3rem;border-radius:3px}</style></head>
        <body><h1>Atlas is running, but this build has no UI</h1>
        <p>The web interface (<code>Web/index.html</code>) is not embedded in this <code>arcane-atlas</code> build, so there is nothing to show here.</p>
        <p>The command API is available: use <code>arcane-atlas ctl status</code> or <code>arcane-atlas ctl help</code> from a terminal.</p>
        </body></html>
        """;

    private static async Task<byte[]?> ReadBodyAsync(HttpContext http, long limit, CancellationToken ct)
    {
        if (http.Request.ContentLength is { } declared && declared > limit)
        {
            // The declared body is never read, so the connection cannot be reused for another request.
            http.Response.Headers.Connection = "close";
            await WriteErrorAsync(http, StatusCodes.Status413PayloadTooLarge, "too-large", $"The body is over {limit} bytes.").ConfigureAwait(false);
            return null;
        }

        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    await WriteErrorAsync(http, StatusCodes.Status413PayloadTooLarge, "too-large", $"The body is over {limit} bytes.").ConfigureAwait(false);
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    private static Task WriteJsonAsync(HttpContext http, int status, JsonNode body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.ContentLength = bytes.Length;
        return http.Response.Body.WriteAsync(bytes, http.RequestAborted).AsTask();
    }

    private static Task WriteErrorAsync(HttpContext http, int status, string code, string message)
        => WriteJsonAsync(http, status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private static bool TryUInt(string? text, out uint value)
        => uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static bool TryInt(string? text, out int value)
        => int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
}

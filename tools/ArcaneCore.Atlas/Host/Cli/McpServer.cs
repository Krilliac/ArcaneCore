using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Host.Cli;

/// <summary>
/// A Model Context Protocol server over stdio that exposes every Atlas command as a tool.
/// <para>
/// Revisions implemented (fetched from modelcontextprotocol.io on 2026-10-18):
/// the current revision <b>2026-07-28</b> ("modern": no handshake, every request carries
/// <c>_meta["io.modelcontextprotocol/protocolVersion"]</c> and <c>.../clientCapabilities</c>, plus the mandatory
/// <c>server/discover</c>; specification/2026-07-28/basic/versioning, basic/transports/stdio, server/discover, server/tools,
/// server/utilities/caching, basic/patterns/cancellation) and the handshake revisions <b>2025-11-25, 2025-06-18, 2025-03-26 and
/// 2024-11-05</b> ("legacy": <c>initialize</c> then <c>notifications/initialized</c>; specification/2025-11-25/basic/lifecycle and
/// server/tools, whose tools/list and tools/call shapes the older revisions share). The two eras are served side by side as the
/// 2026-07-28 versioning page allows for a dual-era server: a request carrying modern <c>_meta</c> is served statelessly by the
/// modern rules, <c>initialize</c> switches the process to legacy rules for requests without <c>_meta</c>.
/// </para>
/// <para>
/// Transport (stdio page): one JSON-RPC message per line in each direction with no embedded newlines; nothing but MCP messages
/// on the output; diagnostics on the log writer (standard error); the server exits when input ends, after answering the requests
/// already running. <c>notifications/cancelled</c> stops a running request and suppresses its response.
/// </para>
/// <para>
/// Threading: <c>tools/list</c> and <c>tools/call</c> run concurrently on the thread pool (a long capture must not block a cancel
/// notification); <c>initialize</c>, <c>ping</c> and <c>server/discover</c> are answered in arrival order on the read loop so
/// pipelined input behaves deterministically. All output goes through one gate, so lines never interleave.
/// </para>
/// </summary>
public sealed class McpServer
{
    /// <summary>The 2026-07-28 revision (per-request metadata).</summary>
    public const string ModernVersion = "2026-07-28";

    /// <summary>JSON-RPC 2.0 "Parse error" (invalid JSON).</summary>
    public const int ParseError = -32700;

    /// <summary>JSON-RPC 2.0 "Invalid Request".</summary>
    public const int InvalidRequest = -32600;

    /// <summary>JSON-RPC 2.0 "Method not found".</summary>
    public const int MethodNotFound = -32601;

    /// <summary>JSON-RPC 2.0 "Invalid params" (also: missing required <c>_meta</c> fields, unknown tool, bad cursor).</summary>
    public const int InvalidParams = -32602;

    /// <summary>JSON-RPC 2.0 "Internal error".</summary>
    public const int InternalError = -32603;

    /// <summary>MCP 2026-07-28 <c>UnsupportedProtocolVersionError</c>.</summary>
    public const int UnsupportedProtocolVersion = -32022;

    private const string VersionKey = "io.modelcontextprotocol/protocolVersion";
    private const string CapabilitiesKey = "io.modelcontextprotocol/clientCapabilities";
    private const string ServerInfoKey = "io.modelcontextprotocol/serverInfo";
    private const string ServerName = "arcane-atlas";
    private const long MaxImageBytes = 24L * 1024 * 1024;

    private const string Instructions =
        "Atlas inspects World of Warcraft 1.12.1 map, collision and navmesh data. Every command is a tool: camera_goto and bookmark_go "
        + "move the view, layer_set chooses layers, capture returns the current view as an image, query_pick and tiles_status inspect data.";

    private readonly ICommandClient _client;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly TimeSpan _drainTimeout;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private volatile bool _legacySession;
    private volatile Dictionary<string, CommandInfo>? _tools;

    /// <param name="client">Where tool calls go (headless context or a running instance).</param>
    /// <param name="input">Requests, one per line.</param>
    /// <param name="output">Responses, one per line; written only with MCP messages.</param>
    /// <param name="log">Diagnostics (standard error).</param>
    /// <param name="drainTimeout">How long to wait for running requests after input ends (default 30 s).</param>
    public McpServer(ICommandClient client, TextReader input, TextWriter output, TextWriter log, TimeSpan? drainTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>The handshake revisions served after <c>initialize</c>, newest first.</summary>
    public static IReadOnlyList<string> LegacyVersions { get; } = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>The tool name of a command: dots become underscores (command names contain no underscore, so this is reversible).</summary>
    public static string ToolName(string command) => command.Replace('.', '_');

    /// <summary>Reads requests until input ends or <paramref name="cancellationToken"/> is cancelled, answering each one.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pending = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await _input.ReadLineAsync(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.AsSpan().IsWhiteSpace())
                {
                    continue;
                }

                await DispatchAsync(line, pending, cancellationToken).ConfigureAwait(false);
                pending.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested: in-flight requests share the token and stop on their own.
        }

        // Input ended (or shutdown): requests already running still get their answers, within a bound.
        TimeSpan grace = cancellationToken.IsCancellationRequested ? TimeSpan.FromSeconds(2) : _drainTimeout;
        await Task.WhenAll(pending).WaitAsync(grace).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (pending.Exists(t => !t.IsCompleted))
        {
            foreach (CancellationTokenSource source in _running.Values)
            {
                TryCancel(source);
            }

            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task DispatchAsync(string line, List<Task> pending, CancellationToken cancellationToken)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line, nodeOptions: null, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            pending.Add(WriteQuietlyAsync(Error(null, ParseError, $"Parse error: {e.Message}"), cancellationToken));
            return;
        }

        switch (node)
        {
            case JsonObject message when IsNotification(message):
                HandleNotification(message);
                break;
            case JsonObject message when message["method"] is null:
                // A response or error from the client: this server never sends requests, so there is nothing it answers.
                break;
            case JsonObject message:
                string? method = message["method"] is JsonValue m && m.TryGetValue(out string? text) ? text : null;
                if (method is "initialize" or "ping" or "server/discover")
                {
                    await RespondAsync(message, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    pending.Add(Task.Run(() => RespondAsync(message, cancellationToken), CancellationToken.None));
                }

                break;
            case JsonArray batch:
                pending.Add(Task.Run(() => RespondBatchAsync(batch, cancellationToken), CancellationToken.None));
                break;
            default:
                pending.Add(WriteQuietlyAsync(Error(null, InvalidRequest, "A message is a JSON object (or a batch array of objects)."), cancellationToken));
                break;
        }
    }

    private static bool IsNotification(JsonObject message) => message["method"] is JsonValue && !message.ContainsKey("id");

    private void HandleNotification(JsonObject message)
    {
        string? method = message["method"] is JsonValue m && m.TryGetValue(out string? text) ? text : null;
        if (method == "notifications/cancelled" && message["params"] is JsonObject parameters && parameters["requestId"] is { } requestId
            && _running.TryGetValue(requestId.ToJsonString(), out CancellationTokenSource? source))
        {
            TryCancel(source);
        }
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request finished between the lookup and the cancel.
        }
    }

    private async Task RespondBatchAsync(JsonArray batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            await WriteQuietlyAsync(Error(null, InvalidRequest, "An empty batch is not a valid request."), cancellationToken).ConfigureAwait(false);
            return;
        }

        Task<JsonObject?>[] work = [.. batch.Select(item => ProcessItemAsync(item, cancellationToken))];
        JsonObject?[] results = await Task.WhenAll(work).ConfigureAwait(false);
        var responses = new JsonArray();
        foreach (JsonObject? result in results)
        {
            if (result is not null)
            {
                responses.Add(result);
            }
        }

        if (responses.Count > 0)
        {
            await WriteQuietlyAsync(responses, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject?> ProcessItemAsync(JsonNode? item, CancellationToken cancellationToken)
    {
        if (item is not JsonObject message)
        {
            return Error(null, InvalidRequest, "A batch holds JSON objects.");
        }

        if (IsNotification(message))
        {
            HandleNotification(message);
            return null;
        }

        return message["method"] is null ? null : await ProcessAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task RespondAsync(JsonObject message, CancellationToken cancellationToken)
    {
        JsonObject? response = await ProcessAsync(message, cancellationToken).ConfigureAwait(false);
        if (response is not null)
        {
            await WriteQuietlyAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Answers one request; null when the request was cancelled (a cancelled request gets no response).</summary>
    private async Task<JsonObject?> ProcessAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (message["id"] is not JsonValue id || id.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return Error(null, InvalidRequest, "A request needs a string or number 'id'.");
        }

        if (message["jsonrpc"] is not JsonValue version || !version.TryGetValue(out string? versionText) || versionText != "2.0")
        {
            return Error(id, InvalidRequest, "'jsonrpc' must be \"2.0\".");
        }

        if (message["method"] is not JsonValue methodValue || !methodValue.TryGetValue(out string? method) || method.Length == 0)
        {
            return Error(id, InvalidRequest, "A request needs a string 'method'.");
        }

        string key = id.ToJsonString();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_running.TryAdd(key, source))
        {
            return Error(id, InvalidRequest, "A request with this id is already running.");
        }

        try
        {
            JsonObject? parameters = message["params"] switch
            {
                null => null,
                JsonObject o => o,
                _ => throw new RpcException(InvalidParams, "'params' must be an object."),
            };
            JsonObject result = await RouteAsync(method, parameters, source.Token).ConfigureAwait(false);
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
        }
        catch (RpcException e)
        {
            return Error(id, e.Code, e.Message, e.Detail);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            await _log.WriteLineAsync($"arcane-atlas mcp: {method} failed: {e.GetType().Name}: {e.Message}").ConfigureAwait(false);
            return Error(id, InternalError, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private async Task<JsonObject> RouteAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return Initialize(parameters);
            case "ping":
                // ping is a 2025-11-25 request that 2026-07-28 removed; answering it is harmless for either era.
                return [];
            case "server/discover":
                return Discover(parameters);
            case "tools/list":
                return await ListToolsAsync(parameters, ResolveEra(parameters), cancellationToken).ConfigureAwait(false);
            case "tools/call":
                return await CallToolAsync(parameters, ResolveEra(parameters), cancellationToken).ConfigureAwait(false);
            default:
                throw new RpcException(MethodNotFound, $"Method not found: {method}");
        }
    }

    /// <summary>Legacy handshake (2025-11-25 lifecycle): echo a supported client version, else offer the newest legacy one.</summary>
    private JsonObject Initialize(JsonObject? parameters)
    {
        if (parameters?["protocolVersion"] is not JsonValue requestedValue || !requestedValue.TryGetValue(out string? requested) || requested.Length == 0)
        {
            throw new RpcException(InvalidParams, "initialize needs a string 'protocolVersion'.");
        }

        string negotiated = LegacyVersions.Contains(requested, StringComparer.Ordinal) ? requested : LegacyVersions[0];
        _legacySession = true;
        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = ServerInfo(),
            ["instructions"] = Instructions,
        };
    }

    /// <summary>Modern <c>server/discover</c> (2026-07-28 server/discover): versions, capabilities, identity, with the caching hints the caching page requires.</summary>
    private static JsonObject Discover(JsonObject? parameters)
    {
        if (parameters?["_meta"] is JsonObject meta && meta.ContainsKey(VersionKey))
        {
            CheckSupported(meta[VersionKey]);
        }

        return Complete(new JsonObject
        {
            ["supportedVersions"] = new JsonArray([.. new[] { ModernVersion }.Concat(LegacyVersions).Select(v => (JsonNode)v)]),
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["instructions"] = Instructions,
            ["ttlMs"] = 0,
            ["cacheScope"] = "private",
        });
    }

    /// <summary>Which rules apply to a request: modern when it carries the per-request metadata, legacy after <c>initialize</c>.</summary>
    private Era ResolveEra(JsonObject? parameters)
    {
        if (parameters?["_meta"] is JsonObject meta && meta.ContainsKey(VersionKey))
        {
            if (CheckSupported(meta[VersionKey]) != ModernVersion)
            {
                throw new RpcException(InvalidParams, $"Protocol version '{meta[VersionKey]}' is a handshake revision: call initialize first and omit the per-request _meta fields.");
            }

            if (meta[CapabilitiesKey] is not JsonObject)
            {
                throw new RpcException(InvalidParams, $"params._meta['{CapabilitiesKey}'] is required on every request.");
            }

            return Era.Modern;
        }

        return _legacySession
            ? Era.Legacy
            : throw new RpcException(InvalidParams, $"params._meta['{VersionKey}'] and params._meta['{CapabilitiesKey}'] are required (protocol {ModernVersion}); handshake clients must call initialize first.");
    }

    private static string CheckSupported(JsonNode? requested)
    {
        string? version = requested is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        if (version is null)
        {
            throw new RpcException(InvalidParams, $"params._meta['{VersionKey}'] must be a string.");
        }

        if (version != ModernVersion && !LegacyVersions.Contains(version, StringComparer.Ordinal))
        {
            throw new RpcException(
                UnsupportedProtocolVersion,
                "Unsupported protocol version",
                new JsonObject
                {
                    ["supported"] = new JsonArray([.. new[] { ModernVersion }.Concat(LegacyVersions).Select(v => (JsonNode)v)]),
                    ["requested"] = version,
                });
        }

        return version;
    }

    private async Task<JsonObject> ListToolsAsync(JsonObject? parameters, Era era, CancellationToken cancellationToken)
    {
        if (parameters?["cursor"] is not null)
        {
            // Pagination page: invalid cursors are -32602. This server returns every tool in one page, so it never issues a cursor.
            throw new RpcException(InvalidParams, "Invalid cursor: tools/list returns every tool in a single page.");
        }

        Dictionary<string, CommandInfo> map = await RefreshToolsAsync(cancellationToken).ConfigureAwait(false);
        var tools = new JsonArray();
        foreach (CommandInfo info in map.Values.DistinctBy(i => i.Name).OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            tools.Add(new JsonObject
            {
                ["name"] = ToolName(info.Name),
                ["description"] = info.Summary,
                ["inputSchema"] = info.Schema.DeepClone(),
            });
        }

        var result = new JsonObject { ["tools"] = tools };
        if (era == Era.Modern)
        {
            result["ttlMs"] = 0;
            result["cacheScope"] = "private";
            Complete(result);
        }

        return result;
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, Era era, CancellationToken cancellationToken)
    {
        if (parameters?["name"] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name) || name.Length == 0)
        {
            throw new RpcException(InvalidParams, "tools/call needs a string 'name'.");
        }

        JsonObject arguments = parameters["arguments"] switch
        {
            null => [],
            JsonObject o => (JsonObject)o.DeepClone(),
            _ => throw new RpcException(InvalidParams, "'arguments' must be an object."),
        };

        CommandInfo? info = await FindToolAsync(name, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            // An unknown tool is a protocol error (tools page, "Error Handling"); a failing command is a tool result with isError.
            throw new RpcException(InvalidParams, $"Unknown tool: {name}");
        }

        JsonObject outcome = await _client.InvokeAsync(info.Name, arguments, cancellationToken).ConfigureAwait(false);
        bool ok = outcome["ok"] is JsonValue okValue && okValue.TryGetValue(out bool flag) && flag;
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = outcome.ToJsonString() } };
        if (ok && info.Name == "capture" && await ImageBlockAsync(outcome, cancellationToken).ConfigureAwait(false) is { } image)
        {
            content.Add(image);
        }

        var result = new JsonObject { ["content"] = content, ["isError"] = !ok };
        if (era == Era.Modern)
        {
            Complete(result);
        }

        return result;
    }

    /// <summary>The PNG a successful <c>capture</c> wrote, as MCP image content (tools page, "Image Content"), so a model can look at it.</summary>
    private async Task<JsonObject?> ImageBlockAsync(JsonObject outcome, CancellationToken cancellationToken)
    {
        if (outcome["data"] is not JsonObject data || data["path"] is not JsonValue pathValue || !pathValue.TryGetValue(out string? path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxImageBytes)
            {
                await _log.WriteLineAsync($"arcane-atlas mcp: capture file '{path}' is missing or over {MaxImageBytes} bytes; returning text only.").ConfigureAwait(false);
                return null;
            }

            byte[] png = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!CaptureCommand.IsPng(png))
            {
                return null;
            }

            return new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await _log.WriteLineAsync($"arcane-atlas mcp: cannot read capture file '{path}': {e.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private async Task<Dictionary<string, CommandInfo>> RefreshToolsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CommandInfo> commands = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, CommandInfo>(StringComparer.Ordinal);
        foreach (CommandInfo info in commands)
        {
            map[ToolName(info.Name)] = info;
            map[info.Name] = info;
        }

        _tools = map;
        return map;
    }

    private async Task<CommandInfo?> FindToolAsync(string name, CancellationToken cancellationToken)
    {
        Dictionary<string, CommandInfo> map = _tools ?? await RefreshToolsAsync(cancellationToken).ConfigureAwait(false);
        if (map.TryGetValue(name, out CommandInfo? info))
        {
            return info;
        }

        // The instance may have gained commands since the cached list (it restarted): look once more.
        map = await RefreshToolsAsync(cancellationToken).ConfigureAwait(false);
        return map.GetValueOrDefault(name);
    }

    /// <summary>Marks a result as a modern "complete" result and names this server in its <c>_meta</c> (basic page, per-response fields).</summary>
    private static JsonObject Complete(JsonObject result)
    {
        result["resultType"] = "complete";
        result["_meta"] = new JsonObject { [ServerInfoKey] = ServerInfo() };
        return result;
    }

    private static JsonObject ServerInfo() => new() { ["name"] = ServerName, ["version"] = AtlasVersion.Current };

    private static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
        {
            error["data"] = data.DeepClone();
        }

        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error };
    }

    /// <summary>Writes one message as one line; a closed output is logged, never thrown (the process is going away).</summary>
    private async Task WriteQuietlyAsync(JsonNode message, CancellationToken cancellationToken)
    {
        string json = message.ToJsonString();
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _output.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            await _log.WriteLineAsync($"arcane-atlas mcp: could not write a response: {e.Message}").ConfigureAwait(false);
        }
    }

    private enum Era
    {
        Legacy,
        Modern,
    }

    private sealed class RpcException(int code, string message, JsonNode? data = null) : Exception(message)
    {
        public int Code { get; } = code;

        public JsonNode? Detail { get; } = data;
    }
}

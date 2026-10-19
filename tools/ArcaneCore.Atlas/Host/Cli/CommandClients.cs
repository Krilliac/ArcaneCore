using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host.Cli;

/// <summary>A command as the MCP tool list and <c>ctl help</c> describe it.</summary>
/// <param name="Name">Dotted command name.</param>
/// <param name="Summary">One-line description.</param>
/// <param name="Schema">JSON Schema of the arguments (<see cref="CommandSchema.InputSchema"/>).</param>
public sealed record CommandInfo(string Name, string Summary, JsonObject Schema);

/// <summary>
/// Where a verb sends its commands: an in-process headless context or a running <c>serve</c> instance. Results are always the
/// <see cref="CommandResult.ToJson"/> shape, including transport failures (code <c>unavailable</c>), so every verb reports
/// success and failure the same way. Implementations are safe for concurrent calls.
/// </summary>
public interface ICommandClient
{
    /// <summary>Short text for diagnostics, such as <c>headless</c> or <c>instance pid 4242 on port 51234</c>.</summary>
    string Description { get; }

    /// <summary>Runs one command. Only cancellation throws.</summary>
    Task<JsonObject> InvokeAsync(string name, JsonObject? args, CancellationToken cancellationToken);

    /// <summary>The commands this target offers.</summary>
    /// <exception cref="CommandException">The target cannot be reached.</exception>
    Task<IReadOnlyList<CommandInfo>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Runs commands in-process against a context composed with <c>headless: true</c> (no UI, no listener).</summary>
public sealed class LocalCommandClient : ICommandClient
{
    private readonly AtlasContext _context;
    private readonly CommandOrigin _origin;

    public LocalCommandClient(AtlasContext context, CommandOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _origin = origin;
    }

    public string Description => "headless";

    public async Task<JsonObject> InvokeAsync(string name, JsonObject? args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        CommandResult result = await _context.Commands
            .InvokeAsync(name, args, _context.Services, _origin, headless: true, cancellationToken)
            .ConfigureAwait(false);
        return result.ToJson();
    }

    /// <summary>The commands that work without a running UI.</summary>
    public Task<IReadOnlyList<CommandInfo>> ListAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CommandInfo>>(
        [
            .. _context.Commands.All
                .Where(c => c.Surfaces.HasFlag(CommandSurfaces.Headless))
                .Select(c => new CommandInfo(c.Name, c.Summary, CommandSchema.InputSchema(c))),
        ]);
}

/// <summary>
/// Sends commands to a running instance over its loopback HTTP API with the bearer token from the discovery file. The
/// <c>origin</c> it reports (<c>cli</c>, <c>script</c>, <c>mcp</c>) is advisory: it only labels the instance's command log.
/// </summary>
public sealed class RemoteCommandClient : ICommandClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _origin;
    private readonly DiscoveryRecord _record;

    public RemoteCommandClient(DiscoveryRecord record, CommandOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(record);
        _record = record;
        _origin = origin.ToString().ToLowerInvariant();

        // A loopback call must never go through a configured proxy, and a command may run for as long as a capture takes.
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"{Uri.UriSchemeHttp}://127.0.0.1:{record.Port}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", record.Token);
    }

    public string Description => $"instance pid {_record.Pid} on port {_record.Port}";

    public async Task<JsonObject> InvokeAsync(string name, JsonObject? args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var body = new JsonObject { ["name"] = name, ["args"] = (args ?? []).DeepClone(), ["origin"] = _origin };
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _http.PostAsync("api/command", content, cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonObject? json = TryParse(text);
            if (response.StatusCode == HttpStatusCode.OK && json is not null && json["ok"] is not null)
            {
                return json;
            }

            return HostJson.Failure(name, ErrorCode(json) ?? CommandException.Failed, ErrorMessage(json) ?? $"The instance answered HTTP {(int)response.StatusCode}.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return HostJson.Failure(name, CommandException.Unavailable, $"Cannot reach the Atlas {Description}: {e.Message}");
        }
    }

    public async Task<IReadOnlyList<CommandInfo>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("api/commands", cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new CommandException(CommandException.Unavailable, $"The Atlas {Description} answered HTTP {(int)response.StatusCode} to the command list: {ErrorMessage(TryParse(text)) ?? text}");
            }

            JsonArray list = JsonNode.Parse(text) as JsonArray ?? throw new CommandException(CommandException.Failed, "The command list is not a JSON array.");
            var commands = new List<CommandInfo>(list.Count);
            foreach (JsonNode? item in list)
            {
                if (item is JsonObject entry && entry["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? name))
                {
                    string summary = entry["summary"] is JsonValue summaryValue && summaryValue.TryGetValue(out string? s) ? s : string.Empty;
                    JsonObject schema = entry["schema"] is JsonObject schemaObject ? (JsonObject)schemaObject.DeepClone() : new JsonObject { ["type"] = "object" };
                    commands.Add(new CommandInfo(name, summary, schema));
                }
            }

            return commands;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new CommandException(CommandException.Unavailable, $"Cannot reach the Atlas {Description}: {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new CommandException(CommandException.Failed, $"The command list is not valid JSON: {e.Message}", e);
        }
    }

    public void Dispose() => _http.Dispose();

    private static JsonObject? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ErrorCode(JsonObject? json)
        => json?["error"] is JsonObject error && error["code"] is JsonValue code && code.TryGetValue(out string? text) ? text : null;

    private static string? ErrorMessage(JsonObject? json)
        => json?["error"] is JsonObject error && error["message"] is JsonValue message && message.TryGetValue(out string? text) ? text : null;
}

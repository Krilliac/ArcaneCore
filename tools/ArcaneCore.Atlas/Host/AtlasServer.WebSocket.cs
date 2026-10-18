using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// The UI WebSocket (doc section 13). Messages are JSON text frames:
/// client to server <c>auth</c>, <c>command</c>, <c>captureFailed</c>; server to client <c>hello</c>, <c>event</c>,
/// <c>result</c>, <c>capture</c>, <c>error</c>.
/// </summary>
public sealed partial class AtlasServer
{
    /// <summary>Close code sent when the first message is not a valid <c>auth</c> (or does not arrive within the auth timeout).</summary>
    public const int CloseUnauthorized = 4401;

    private const int MaxMessageBytes = 1024 * 1024;

    private async Task WebSocketAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "bad-request", "A WebSocket upgrade is required.").ConfigureAwait(false);
            return;
        }

        // Browsers always send Origin on a WebSocket handshake; a page from any other origin must not be able to open this socket.
        string origin = http.Request.Headers["Origin"].ToString();
        if (origin.Length > 0 && !OriginAllowed(origin))
        {
            await WriteErrorAsync(http, StatusCodes.Status403Forbidden, "forbidden", "The WebSocket origin is not this loopback endpoint.").ConfigureAwait(false);
            return;
        }

        using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await RunSessionAsync(socket, http.RequestAborted).ConfigureAwait(false);
    }

    private bool OriginAllowed(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && uri.Port == _port
            && (uri.Host == "127.0.0.1" || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private async Task RunSessionAsync(WebSocket socket, CancellationToken aborted)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(aborted, _shutdown.Token);
        CancellationToken ct = lifetime.Token;
        UiConnection? connection = null;
        Task pump = Task.CompletedTask;
        var commands = new List<Task>();
        try
        {
            if (!await AuthenticateAsync(socket, ct).ConfigureAwait(false))
            {
                return;
            }

            connection = _hub.Add(socket, Guid.NewGuid().ToString("N")[..12]);

            // The connection is registered before the view is read, so no change can fall between the hello and the first event;
            // the version is read first so a view newer than the reported version only produces a repeat the UI ignores.
            long viewVersion = _context.View.Version;
            ViewState view = _context.View.Current;
            var hello = new JsonObject
            {
                ["op"] = "hello",
                ["connection"] = connection.Id,
                ["version"] = AtlasVersion.Current,
                ["view"] = view.ToJson(),
                ["viewVersion"] = viewVersion,
            };
            await connection.SendAsync(hello.ToJsonString(), ct).ConfigureAwait(false);
            pump = PumpEventsAsync(connection, ct);

            while (!ct.IsCancellationRequested)
            {
                string? text = await ReceiveTextAsync(socket, ct).ConfigureAwait(false);
                if (text is null)
                {
                    break;
                }

                HandleMessage(connection, text, commands, ct);
                commands.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing: the client left or the server is stopping.
        }
        catch (WebSocketException e)
        {
            _log.LogDebug("UI socket ended: {Message}", e.Message);
        }
        catch (InvalidDataException e)
        {
            _log.LogWarning("UI socket closed: {Message}", e.Message);
            await TryCloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, e.Message).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (connection is not null)
            {
                _hub.Remove(connection);
            }

            await Task.WhenAll(commands.Append(pump)).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
        }
    }

    /// <summary>Waits up to the auth timeout for <c>{"op":"auth","token":"..."}</c>; closes with 4401 otherwise.</summary>
    private async Task<bool> AuthenticateAsync(WebSocket socket, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.AuthTimeout);
        string? first;
        try
        {
            first = await ReceiveTextAsync(socket, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TryCloseAsync(socket, (WebSocketCloseStatus)CloseUnauthorized, "no auth message within the timeout").ConfigureAwait(false);
            return false;
        }
        catch (InvalidDataException)
        {
            await TryCloseAsync(socket, (WebSocketCloseStatus)CloseUnauthorized, "the first message must be auth").ConfigureAwait(false);
            return false;
        }

        if (first is not null && TryParseObject(first) is { } message
            && message["op"] is JsonValue op && op.TryGetValue(out string? opName) && opName == "auth"
            && message["token"] is JsonValue token && token.TryGetValue(out string? candidate) && TokenMatches(candidate))
        {
            return true;
        }

        if (first is not null)
        {
            await TryCloseAsync(socket, (WebSocketCloseStatus)CloseUnauthorized, "unauthorized").ConfigureAwait(false);
        }

        return false;
    }

    private void HandleMessage(UiConnection connection, string text, List<Task> commands, CancellationToken ct)
    {
        if (TryParseObject(text) is not { } message || message["op"] is not JsonValue opValue || !opValue.TryGetValue(out string? op))
        {
            _ = SendErrorAsync(connection, "bad-request", "Messages are JSON objects with a string 'op'.", ct);
            return;
        }

        switch (op)
        {
            case "command":
                if (message["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name)
                    && (message["args"] is null || message["args"] is JsonObject))
                {
                    JsonNode? id = message["id"]?.DeepClone();
                    JsonObject? args = (JsonObject?)message["args"]?.DeepClone();
                    commands.Add(Task.Run(() => RunCommandAsync(connection, id, name, args, ct), CancellationToken.None));
                }
                else
                {
                    _ = SendErrorAsync(connection, "bad-request", "A command message needs a string 'name' and an object 'args'.", ct);
                }

                break;
            case "captureFailed":
                if (message["id"] is JsonValue captureId && captureId.TryGetValue(out string? failedId))
                {
                    string reason = message["message"] is JsonValue m && m.TryGetValue(out string? text2) ? text2 : "no reason given";
                    _hub.Fail(failedId, reason);
                }

                break;
            case "auth":
                break;
            default:
                _ = SendErrorAsync(connection, "bad-request", $"Unknown op '{op}'.", ct);
                break;
        }
    }

    private async Task RunCommandAsync(UiConnection connection, JsonNode? id, string name, JsonObject? args, CancellationToken ct)
    {
        using IDisposable scope = HostOrigin.With($"ui:{connection.Id}");
        CommandResult result;
        try
        {
            result = await _context.Commands.InvokeAsync(name, args, _context.Services, CommandOrigin.Ui, headless: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            // A handler bug must not take the connection (or the server) down: report it as the command's failure.
            _log.LogWarning(e, "Command {Command} from UI {Connection} threw", name, connection.Id);
            result = new CommandResult(name, false, null, CommandException.Failed, $"{e.GetType().Name}: {e.Message}", 0);
        }

        try
        {
            string json = new JsonObject { ["op"] = "result", ["id"] = id, ["result"] = result.ToJson() }.ToJsonString();
            await connection.SendAsync(json, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _log.LogDebug("Result of {Command} not delivered: {Message}", name, e.Message);
        }
    }

    private static async Task SendErrorAsync(UiConnection connection, string code, string message, CancellationToken ct)
    {
        try
        {
            string json = new JsonObject { ["op"] = "error", ["code"] = code, ["message"] = message }.ToJsonString();
            await connection.SendAsync(json, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The socket is going away; nothing to report to.
        }
    }

    private static async Task PumpEventsAsync(UiConnection connection, CancellationToken ct)
    {
        try
        {
            await foreach (string json in connection.PendingEvents.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await connection.SendAsync(json, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection ended; the receive loop does the cleanup.
        }
    }

    /// <summary>One complete text message, or null when the peer sent a close frame.</summary>
    /// <exception cref="InvalidDataException">The peer sent a binary frame or a message over the size limit.</exception>
    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (received.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("Only text frames are accepted.");
                }

                message.Write(buffer, 0, received.Count);
                if (message.Length > MaxMessageBytes)
                {
                    throw new InvalidDataException($"Messages are limited to {MaxMessageBytes} bytes.");
                }

                if (received.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static JsonObject? TryParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text, nodeOptions: null, new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseAsync(status, description, grace.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The peer is already gone.
        }
    }
}

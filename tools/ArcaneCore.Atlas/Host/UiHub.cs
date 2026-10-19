using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// One authenticated UI WebSocket. Control messages (hello, results, capture requests) are sent directly; event pushes go
/// through a bounded queue that drops the oldest entry when the browser reads slowly, because every view event carries
/// the complete view and live frames are superseded by the next frame.
/// </summary>
public sealed class UiConnection
{
    /// <summary>Events queued per connection before the oldest is dropped.</summary>
    public const int EventQueueCapacity = 512;

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Channel<string> _events = Channel.CreateBounded<string>(new BoundedChannelOptions(EventQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    internal UiConnection(WebSocket socket, string id, long sequence)
    {
        _socket = socket;
        Id = id;
        Sequence = sequence;
    }

    /// <summary>The id the hello message reported (also the origin label <c>ui:&lt;id&gt;</c>).</summary>
    public string Id { get; }

    /// <summary>Connection order; capture requests go to the lowest.</summary>
    internal long Sequence { get; }

    internal ChannelReader<string> PendingEvents => _events.Reader;

    internal void EnqueueEvent(string json) => _events.Writer.TryWrite(json);

    internal void CompleteEvents() => _events.Writer.TryComplete();

    /// <summary>Sends one text frame; only one frame is in flight at a time.</summary>
    internal async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new WebSocketException(WebSocketError.InvalidState, "The UI connection is closed.");
            }

            // The token only bounds the wait for the gate: cancelling a frame half-way through would abort the WebSocket (no close
            // handshake possible afterwards), so a started frame is always finished.
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Sends the server's close frame after any frame in flight; the receive loop then reads the peer's reply.</summary>
    internal async Task CloseOutputAsync(WebSocketCloseStatus status, string description)
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _sendGate.WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await AtlasServer.CloseOutputQuietlyAsync(_socket, status, description).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }
}

/// <summary>
/// The connected UIs: event fan-out and the capture round trip (the host asks a UI to render off-screen and the UI
/// answers with a POST of the PNG or a <c>captureFailed</c> message).
/// </summary>
public sealed class UiHub
{
    private readonly ConcurrentDictionary<string, UiConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCapture> _pending = new(StringComparer.Ordinal);
    private long _sequence;

    private sealed record PendingCapture(TaskCompletionSource<byte[]> Source, string ConnectionId);

    /// <summary>How long a capture waits for the UI's answer (15 s).</summary>
    public TimeSpan CaptureTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public int ConnectionCount => _connections.Count;

    internal UiConnection Add(WebSocket socket, string id)
    {
        var connection = new UiConnection(socket, id, Interlocked.Increment(ref _sequence));
        _connections[id] = connection;
        return connection;
    }

    internal void Remove(UiConnection connection)
    {
        _connections.TryRemove(connection.Id, out _);
        connection.CompleteEvents();
        foreach ((string id, PendingCapture pending) in _pending)
        {
            if (pending.ConnectionId == connection.Id && _pending.TryRemove(id, out _))
            {
                pending.Source.TrySetException(new CommandException(CommandException.Unavailable, "The UI disconnected before it answered the capture request."));
            }
        }
    }

    /// <summary>Pushes <c>{"op":"event","topic":...,"data":...}</c> to every connection (non-blocking).</summary>
    public void Broadcast(string topic, JsonNode? data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (_connections.IsEmpty)
        {
            return;
        }

        string json = new JsonObject { ["op"] = "event", ["topic"] = topic, ["data"] = data?.DeepClone() }.ToJsonString();
        foreach (UiConnection connection in _connections.Values)
        {
            connection.EnqueueEvent(json);
        }
    }

    /// <summary>
    /// Asks the oldest connected UI to render <paramref name="width"/> x <paramref name="height"/> off-screen and returns the
    /// PNG it posts. <paramref name="view"/>, when given, is the complete view to render instead of the UI's own.
    /// </summary>
    public async Task<byte[]> RequestCaptureAsync(int width, int height, JsonObject? view, CancellationToken cancellationToken)
    {
        UiConnection target = _connections.Values.MinBy(c => c.Sequence)
            ?? throw new CommandException(CommandException.Unavailable, "No UI is connected; open one with 'arcane-atlas serve --open'.");
        string id = Guid.NewGuid().ToString("D");
        var source = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingCapture(source, target.Id);
        try
        {
            var request = new JsonObject { ["op"] = "capture", ["id"] = id, ["width"] = width, ["height"] = height };
            if (view is not null)
            {
                request["view"] = view.DeepClone();
            }

            try
            {
                await target.SendAsync(request.ToJsonString(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                throw new CommandException(CommandException.Unavailable, $"The UI connection closed before the capture request was sent: {e.Message}", e);
            }

            return await source.Task.WaitAsync(CaptureTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CommandException(CommandException.Failed, $"The UI did not answer the capture request within {CaptureTimeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Delivers the PNG a UI posted for a capture request; false when the request is unknown (late or never sent).</summary>
    internal bool Complete(string id, byte[] png)
        => _pending.TryGetValue(id, out PendingCapture? pending) && pending.Source.TrySetResult(png);

    /// <summary>Fails a capture request with the UI's own message; false when the request is unknown.</summary>
    internal bool Fail(string id, string message)
        => _pending.TryGetValue(id, out PendingCapture? pending)
            && pending.Source.TrySetException(new CommandException(CommandException.Failed, $"The UI could not render the capture: {message}"));
}

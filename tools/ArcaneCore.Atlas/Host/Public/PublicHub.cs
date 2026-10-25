using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// The public WebSockets (design 0.3, 0.8, 0.11). Each socket belongs to one session: it authenticates with the session's CSRF token in an <c>auth</c>
/// frame, chooses one public map (<c>view</c>) and then receives that map's guest projection with its session's opaque ids at <c>Guest:FeedHz</c>.
/// It may run allowlisted commands (<c>command</c>). Frames that would exceed the address's byte budget are dropped, not queued; a socket that cannot
/// keep up has at most two frames waiting (the oldest is dropped). Messages in are at most 4 KiB, 10 per second and JSON depth 8. The hub never touches
/// the world: it reads the <see cref="PublicFrameStore"/> and asks the <see cref="IPublicWorld"/> for map demand.
/// <para>
/// Threading: one receive loop and one send loop per socket; <see cref="Tick"/> runs on the hub's timer and only renders and enqueues, never
/// awaiting a socket. Sends on one socket are serialized by its lock.
/// </para>
/// </summary>
internal sealed class PublicHub
{
    /// <summary>The longest message a client may send.</summary>
    public const int MaxMessageBytes = 4096;

    /// <summary>The JSON depth limit of client messages.</summary>
    public const int MaxJsonDepth = 8;

    /// <summary>How long a new socket has to send its <c>auth</c> frame.</summary>
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StatusRepeat = TimeSpan.FromSeconds(5);

    /// <summary>How long a socket we started closing may wait for the peer's close frame before it is aborted (so it releases its connection slot).</summary>
    internal TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(3);

    private sealed class SocketState(PublicSession session, WebSocket socket, string key)
    {
        public readonly PublicSession Session = session;
        public readonly WebSocket Socket = socket;
        public readonly string Key = key;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly Channel<byte[]> Frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public volatile bool Ready;
        public uint? Map;
        public IDisposable? Demand;
        public long LastSeq = -1;
        public long LastSent = long.MinValue;
        public long LastStatusAt = long.MinValue;
        public string LastStatus = string.Empty;
    }

    private readonly PublicRuntime _runtime;
    private readonly PublicCommandRunner _runner;
    private readonly ConcurrentDictionary<string, SocketState> _sockets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _perIp = new(StringComparer.Ordinal);
    private int _count;
    private long _nextSocket;

    public PublicHub(PublicRuntime runtime)
    {
        _runtime = runtime;
        _runner = new PublicCommandRunner(runtime);
    }

    /// <summary>Open sockets.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Reserves a socket slot for an address; false when the global or per-address cap is reached. Pair with <see cref="Release"/> (done by <see cref="RunSocketAsync"/>).</summary>
    public bool TryReserve(string ipKey)
    {
        if (Interlocked.Increment(ref _count) > _runtime.Options.MaxConnections)
        {
            Interlocked.Decrement(ref _count);
            return false;
        }

        int perIp = _perIp.AddOrUpdate(ipKey, 1, (_, n) => n + 1);
        if (perIp > _runtime.Options.MaxConnectionsPerIp)
        {
            Release(ipKey);
            return false;
        }

        return true;
    }

    /// <summary>Gives back a slot taken with <see cref="TryReserve"/> when the socket was never run (the upgrade failed).</summary>
    public void ReleaseReservation(string ipKey) => Release(ipKey);

    private void Release(string ipKey)
    {
        Interlocked.Decrement(ref _count);
        if (_perIp.AddOrUpdate(ipKey, 0, (_, n) => n - 1) <= 0)
        {
            _perIp.TryRemove(ipKey, out _);
        }
    }

    /// <summary>Serves one accepted socket until it closes. The slot reserved with <see cref="TryReserve"/> is released here.</summary>
    public async Task RunSocketAsync(PublicSession session, WebSocket socket, CancellationToken stopping)
    {
        string key = $"ws{Interlocked.Increment(ref _nextSocket)}";
        var state = new SocketState(session, socket, key);
        _sockets[key] = state;
        _runtime.Stats.SocketOpened();
        using IDisposable registration = session.RegisterSocket(reason => _ = CloseAsync(state, WebSocketCloseStatus.PolicyViolation, reason));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Task? sender = null;
        try
        {
            if (!await AuthenticateAsync(state, cts.Token).ConfigureAwait(false))
            {
                return;
            }

            await SendTextAsync(state, Hello(session), cts.Token).ConfigureAwait(false);
            await SetMapAsync(state, _runtime.AllowedMaps.Count > 0 ? _runtime.AllowedMaps[0] : null, cts.Token).ConfigureAwait(false);
            state.Ready = true;
            sender = SendLoopAsync(state, cts.Token);
            await ReceiveLoopAsync(state, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
            // The peer went away or the host is stopping.
        }
        finally
        {
            state.Ready = false;
            state.Frames.Writer.TryComplete();
            await cts.CancelAsync().ConfigureAwait(false);
            if (sender is not null)
            {
                try
                {
                    await sender.ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
                {
                    // Already closed.
                }
            }

            state.Demand?.Dispose();
            _sockets.TryRemove(key, out _);
            _runtime.Limits.ForgetSocket(key);
            _runtime.Stats.SocketClosed();
            Release(session.IpKey);
            state.SendLock.Dispose();
        }
    }

    /// <summary>
    /// One pass over the open sockets: renders each ready socket's map projection when it has a newer frame than the socket last got, charges the
    /// address's byte budget (dropping the frame when it is over) and queues the bytes. Called by the host's timer; tests call it directly.
    /// </summary>
    public void Tick()
    {
        long now = _runtime.Time.GetTimestamp();
        long interval = (long)(_runtime.Time.TimestampFrequency / _runtime.Options.Guest.FeedHz);
        foreach (SocketState state in _sockets.Values)
        {
            if (!state.Ready || state.Map is not { } map)
            {
                continue;
            }

            PublicMapStatus status = _runtime.Frames.Status(map);
            string statusText = StatusJson(status);
            if (statusText != state.LastStatus || now - state.LastStatusAt >= StatusRepeat.TotalSeconds * _runtime.Time.TimestampFrequency)
            {
                state.LastStatus = statusText;
                state.LastStatusAt = now;
                state.Frames.Writer.TryWrite(Encoding.UTF8.GetBytes(statusText));
            }

            if (status.For(state.Session) is not { } projection || projection.Seq == state.LastSeq && projection.Map == map && state.LastSeq >= 0)
            {
                continue;
            }

            if (state.LastSent != long.MinValue && now - state.LastSent < interval)
            {
                continue;
            }

            var buffer = new ArrayBufferWriter<byte>(4096);
            GuestFrameProjector.WriteFrame(buffer, projection, state.Session.OpaqueKey);
            state.LastSent = now;
            state.LastSeq = projection.Seq;
            if (!_runtime.Limits.AllowBytes(state.Session.IpKey, buffer.WrittenCount))
            {
                _runtime.Stats.Dropped();
                continue;
            }

            state.Frames.Writer.TryWrite(buffer.WrittenSpan.ToArray());
        }
    }

    /// <summary>Runs <see cref="Tick"/> on a timer until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>Closes every socket (shutdown).</summary>
    public async Task CloseAllAsync(string reason)
    {
        foreach (SocketState state in _sockets.Values)
        {
            await CloseAsync(state, WebSocketCloseStatus.EndpointUnavailable, reason).ConfigureAwait(false);
        }
    }

    // ---- per socket ---------------------------------------------------------------------------------------------------------------

    private static string StatusJson(PublicMapStatus status)
        => new JsonObject
        {
            ["op"] = "event",
            ["topic"] = "live.status",
            ["data"] = new JsonObject
            {
                ["connected"] = status.Connected && status.Projection is not null,
                ["detail"] = status.Busy ? "busy" : status.Connected ? (status.Reduced ? "reduced" : string.Empty) : "waiting",
                ["map"] = status.Map,
            },
        }.ToJsonString();

    private string Hello(PublicSession session)
    {
        var maps = new JsonArray();
        foreach (PublicMapOption map in _runtime.Options.Guest.Maps)
        {
            maps.Add(new JsonObject { ["id"] = map.Id, ["name"] = map.Name });
        }

        return new JsonObject
        {
            ["op"] = "hello",
            ["connection"] = session.Ref,
            ["version"] = AtlasVersion.Current,
            ["tier"] = session.Tier == PublicTier.Account ? "account" : "guest",
            ["account"] = session.Account,
            ["maps"] = maps,
            ["feedHz"] = _runtime.Options.Guest.FeedHz,
        }.ToJsonString();
    }

    private async Task<bool> AuthenticateAsync(SocketState state, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AuthTimeout);
        byte[]? message;
        try
        {
            message = await ReceiveMessageAsync(state, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(state, WebSocketCloseStatus.PolicyViolation, "auth-timeout").ConfigureAwait(false);
            return false;
        }

        if (message is null || !TryParse(message, out JsonObject? root) || root["op"]?.GetValue<string>() != "auth"
            || !state.Session.CsrfMatches(root["csrf"] is JsonValue v && v.TryGetValue(out string? csrf) ? csrf : null))
        {
            await CloseAsync(state, (WebSocketCloseStatus)4401, "unauthorized").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task ReceiveLoopAsync(SocketState state, CancellationToken cancellationToken)
    {
        while (state.Socket.State == WebSocketState.Open)
        {
            byte[]? message = await ReceiveMessageAsync(state, cancellationToken).ConfigureAwait(false);
            if (message is null || state.Socket.State != WebSocketState.Open)
            {
                // A socket we have already started closing (kick, expiry, shutdown) processes nothing more the peer sends.
                return;
            }

            if (!_runtime.Limits.AllowSocketMessage(state.Key))
            {
                await SendTextAsync(state, new JsonObject { ["op"] = "error", ["code"] = PublicCommandRunner.RateLimited, ["message"] = PublicCommandRunner.Message(PublicCommandRunner.RateLimited) }.ToJsonString(), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!TryParse(message, out JsonObject? root))
            {
                await SendTextAsync(state, new JsonObject { ["op"] = "error", ["code"] = "bad-message", ["message"] = "That message was not understood." }.ToJsonString(), cancellationToken).ConfigureAwait(false);
                continue;
            }

            switch (root["op"] is JsonValue opValue && opValue.TryGetValue(out string? op) ? op : null)
            {
                case "ping":
                    await SendTextAsync(state, "{\"op\":\"pong\"}", cancellationToken).ConfigureAwait(false);
                    break;
                case "view":
                    await ViewAsync(state, root, cancellationToken).ConfigureAwait(false);
                    break;
                case "command":
                    await CommandAsync(state, root, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await SendTextAsync(state, new JsonObject { ["op"] = "error", ["code"] = "bad-message", ["message"] = "That message was not understood." }.ToJsonString(), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task ViewAsync(SocketState state, JsonObject message, CancellationToken cancellationToken)
    {
        if (message["map"] is JsonValue v && v.TryGetValue(out long id) && id is >= 0 and <= uint.MaxValue && _runtime.AllowedMaps.Contains((uint)id))
        {
            await SetMapAsync(state, (uint)id, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendTextAsync(state, new JsonObject { ["op"] = "error", ["code"] = "bad-args", ["message"] = PublicCommandRunner.Message("bad-args") }.ToJsonString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SetMapAsync(SocketState state, uint? map, CancellationToken cancellationToken)
    {
        if (state.Map == map)
        {
            return;
        }

        IDisposable? previous = state.Demand;
        state.Demand = null;
        state.Map = map;
        state.LastSeq = -1;
        state.LastSent = long.MinValue;
        state.LastStatus = string.Empty;
        previous?.Dispose();
        if (map is { } wanted)
        {
            state.Demand = await _runtime.World.DemandAsync(wanted, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CommandAsync(SocketState state, JsonObject message, CancellationToken cancellationToken)
    {
        long id = message["id"] is JsonValue idValue && idValue.TryGetValue(out long parsedId) ? parsedId : 0;
        string? name = message["name"] is JsonValue n && n.TryGetValue(out string? text) ? text : null;
        JsonObject args = message["args"] as JsonObject ?? [];
        JsonObject result = name is null
            ? PublicCommandRunner.Failure("?", "bad-args", System.Diagnostics.Stopwatch.GetTimestamp())
            : await _runner.RunAsync(state.Session, name, args, cancellationToken).ConfigureAwait(false);
        await SendTextAsync(state, new JsonObject { ["op"] = "result", ["id"] = id, ["result"] = result }.ToJsonString(), cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParse(byte[] message, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonObject? root)
    {
        root = null;
        try
        {
            root = JsonNode.Parse(message, nodeOptions: null, new JsonDocumentOptions { MaxDepth = MaxJsonDepth }) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }

        return root is not null;
    }

    /// <summary>Reads one text message of at most <see cref="MaxMessageBytes"/>; null when the peer closed. A longer message closes the socket (1009).</summary>
    private async Task<byte[]?> ReceiveMessageAsync(SocketState state, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxMessageBytes + 1);
        try
        {
            int length = 0;
            while (true)
            {
                ValueWebSocketReceiveResult result = await state.Socket.ReceiveAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(state, WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
                    return null;
                }

                length += result.Count;
                if (result.MessageType != WebSocketMessageType.Text || length > MaxMessageBytes)
                {
                    await CloseAsync(state, WebSocketCloseStatus.MessageTooBig, "message").ConfigureAwait(false);
                    return null;
                }

                if (result.EndOfMessage)
                {
                    return buffer.AsSpan(0, length).ToArray();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendLoopAsync(SocketState state, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] bytes in state.Frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendBytesAsync(state, bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
        {
            // The peer is gone; the receive loop ends the socket.
        }
    }

    private Task SendTextAsync(SocketState state, string text, CancellationToken cancellationToken)
        => SendBytesAsync(state, Encoding.UTF8.GetBytes(text), cancellationToken);

    private async Task SendBytesAsync(SocketState state, byte[] bytes, CancellationToken cancellationToken)
    {
        if (state.Socket.State != WebSocketState.Open)
        {
            return;
        }

        await state.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Socket.State == WebSocketState.Open)
            {
                await state.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                state.Session.AddBytes(bytes.Length);
                _runtime.Stats.Sent(state.Session.IpKey, bytes.Length);
            }
        }
        finally
        {
            state.SendLock.Release();
        }
    }

    private async Task CloseAsync(SocketState state, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (state.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                bool peerAlreadyClosed = state.Socket.State == WebSocketState.CloseReceived;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await state.Socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
                if (!peerAlreadyClosed)
                {
                    // We closed first: the socket stays in CloseSent until the peer answers. A peer that never does must not keep its slot.
                    _ = AbortAfterGraceAsync(state.Socket);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException or IOException or InvalidOperationException)
        {
            state.Socket.Abort();
        }
    }

    private async Task AbortAfterGraceAsync(WebSocket socket)
    {
        try
        {
            await Task.Delay(CloseGrace).ConfigureAwait(false);
            if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
            {
                socket.Abort();
            }
        }
        catch (ObjectDisposedException)
        {
            // Already disposed with the connection.
        }
    }
}

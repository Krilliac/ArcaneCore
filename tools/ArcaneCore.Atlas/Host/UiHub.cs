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
    private string _name;
    private UiPresence? _presence;
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
        DefaultName = "session " + id[..4];
        _name = DefaultName;
    }

    /// <summary>The id the hello message reported (also the origin label <c>ui:&lt;id&gt;</c>).</summary>
    public string Id { get; }

    /// <summary>Connection order; capture requests go to the lowest.</summary>
    internal long Sequence { get; }

    /// <summary>The name a connection has until its UI picks one: <c>session</c> and the first four characters of its id.</summary>
    public string DefaultName { get; }

    /// <summary>The session's display name (default <c>session &lt;first 4 id chars&gt;</c>); set from the receive loop, read from any thread.</summary>
    public string Name
    {
        get => Volatile.Read(ref _name);
        internal set => Volatile.Write(ref _name, value);
    }

    /// <summary>The latest published presence, or null until the UI sends its first valid <c>presence</c> message.</summary>
    public UiPresence? Presence
    {
        get => Volatile.Read(ref _presence);
        internal set => Volatile.Write(ref _presence, value);
    }

    /// <summary><see cref="TimeProvider.GetTimestamp"/> of the last accepted presence message, null before the first; only the receive loop touches it.</summary>
    internal long? LastPresenceStamp { get; set; }

    /// <summary><see cref="TimeProvider.GetTimestamp"/> of the last invalid presence message seen (the bucket's last update), null before the first; only the receive loop touches it.</summary>
    internal long? LastPresenceErrorStamp { get; set; }

    /// <summary>Answers left in the invalid-presence bucket (refilled by elapsed time); only the receive loop touches it.</summary>
    internal double PresenceErrorTokens { get; set; } = AtlasServer.PresenceErrorBurst;

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
/// answers with a POST of the PNG or a <c>captureFailed</c> message). Presence goes through the same bounded per-connection
/// queues, which drop the oldest entry when a browser reads slowly. A <c>ui.presence.left</c> is not superseded by any later
/// event, so a browser that drops one keeps a ghost marker until its next hello.
/// </summary>
public sealed class UiHub
{
    private readonly ConcurrentDictionary<string, UiConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCapture> _pending = new(StringComparer.Ordinal);
    private readonly Lock _agentGate = new();
    private readonly Dictionary<string, AgentSession> _agents = new(StringComparer.Ordinal);
    private long _sequence;
    private TimeProvider _time = TimeProvider.System;
    private TimeSpan _minInterval = TimeSpan.FromMilliseconds(50);
    private TimeSpan _agentTimeout = TimeSpan.FromMinutes(10);
    private int _maxAgents = 16;
    private volatile bool _attached;

    private sealed record PendingCapture(TaskCompletionSource<byte[]> Source, string ConnectionId);

    /// <summary>Topic of the event carrying one session's new presence.</summary>
    public const string PresenceTopic = "ui.presence";

    /// <summary>Topic of the event announcing that a session with published presence disconnected.</summary>
    public const string PresenceLeftTopic = "ui.presence.left";

    /// <summary>How long a capture waits for the UI's answer (15 s).</summary>
    public TimeSpan CaptureTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public int ConnectionCount => _connections.Count;

    /// <summary>Whether a server serves this hub: agent sessions exist only then (a headless run has none).</summary>
    internal bool Attached => _attached;

    /// <summary>Takes the server's session settings (rate limit, agent timeout and cap, clock) and opens the hub to agent sessions.</summary>
    internal void Attach(AtlasServerOptions options)
    {
        lock (_agentGate)
        {
            _time = options.Time;
            _minInterval = options.PresenceMinInterval;
            _agentTimeout = options.AgentSessionTimeout;
            _maxAgents = options.MaxAgentSessions;
            _agents.Clear();
            _attached = true;
        }
    }

    /// <summary>Closes the hub to agent sessions and forgets them (the server is stopping; their UIs are going too).</summary>
    internal void Detach()
    {
        lock (_agentGate)
        {
            _attached = false;
            _agents.Clear();
        }
    }

    /// <summary>
    /// Creates an agent session. <paramref name="build"/> receives the new id and default name and returns its first presence
    /// (it throws <see cref="CommandException"/> for bad input, which creates nothing). Announces the presence like a UI's.
    /// Every announcement is queued while the session lock is held, so what the UIs are told is in the order the state changed.
    /// </summary>
    /// <exception cref="CommandException">The hub is not attached to a server, or the cap of agent sessions is reached.</exception>
    internal UiPresence JoinAgent(Func<string, string, UiPresence> build)
    {
        lock (_agentGate)
        {
            RequireAttachedLocked("session.join");
            SweepLocked();
            if (_agents.Count >= _maxAgents)
            {
                throw new CommandException(CommandException.Failed, $"session.join: {_maxAgents} agent sessions are already joined; leave one with session.leave, or wait for one to expire.");
            }

            string id;
            do
            {
                id = Guid.NewGuid().ToString("N")[..12];
            }
            while (_agents.ContainsKey(id) || _connections.ContainsKey(id));

            string defaultName = "session " + id[..4];
            UiPresence presence = build(id, defaultName);
            _agents[id] = new AgentSession(Interlocked.Increment(ref _sequence), defaultName, presence, _time.GetTimestamp());
            BroadcastPresence(presence);
            return presence;
        }
    }

    /// <summary>
    /// Moves an agent session. <paramref name="build"/> receives the current presence and the default name and returns the new one
    /// (it throws <see cref="CommandException"/> for bad input). Refreshes the session's expiry.
    /// </summary>
    /// <exception cref="CommandException">The hub is not attached, the id is unknown or expired, or the previous move was less than the presence interval ago.</exception>
    internal UiPresence MoveAgent(string id, Func<UiPresence, string, UiPresence> build)
    {
        lock (_agentGate)
        {
            RequireAttachedLocked("session.move");
            SweepLocked();
            AgentSession session = FindAgentLocked(id, "session.move");
            UiPresence presence = build(session.Presence, session.DefaultName);
            long now = _time.GetTimestamp();
            if (_minInterval > TimeSpan.Zero && session.LastMoveStamp is { } last && _time.GetElapsedTime(last, now) < _minInterval)
            {
                TimeSpan wait = _minInterval - _time.GetElapsedTime(last, now);
                throw new CommandException(CommandException.Unavailable, $"session.move: moves must be at least {_minInterval.TotalMilliseconds:0} ms apart; retry in {Math.Ceiling(wait.TotalMilliseconds):0} ms.");
            }

            session.Presence = presence;
            session.LastMoveStamp = now;
            session.LastActiveStamp = now;
            BroadcastPresence(presence);
            return presence;
        }
    }

    /// <summary>Removes an agent session and announces it; <see cref="CommandException"/> when the id is not a live agent session.</summary>
    internal void LeaveAgent(string id)
    {
        lock (_agentGate)
        {
            RequireAttachedLocked("session.leave");
            SweepLocked();
            FindAgentLocked(id, "session.leave");
            _agents.Remove(id);
            AnnounceLeft(id);
        }
    }

    /// <summary>Drops agent sessions that have not joined or moved within the timeout and announces each as gone.</summary>
    internal void SweepExpired()
    {
        lock (_agentGate)
        {
            SweepLocked();
        }
    }

    private void RequireAttachedLocked(string command)
    {
        if (!_attached)
        {
            throw new CommandException(CommandException.Unavailable, $"{command} needs a running 'arcane-atlas serve' instance (this one is stopping or not serving).");
        }
    }

    private AgentSession FindAgentLocked(string id, string command)
    {
        if (_agents.TryGetValue(id, out AgentSession? session))
        {
            return session;
        }

        throw new CommandException(
            CommandException.BadArguments,
            _connections.ContainsKey(id)
                ? $"{command}: '{id}' is a UI session, not an agent session."
                : $"{command}: no agent session '{id}' (it never joined, left, or expired after {_agentTimeout.TotalMinutes:0.#} minutes without a join or move; join again).");
    }

    /// <summary>Removes the expired agent sessions and queues a <c>ui.presence.left</c> for each. The caller holds <c>_agentGate</c>.</summary>
    private void SweepLocked()
    {
        if (_agents.Count == 0)
        {
            return;
        }

        long now = _time.GetTimestamp();
        List<string>? expired = null;
        foreach ((string id, AgentSession session) in _agents)
        {
            if (_time.GetElapsedTime(session.LastActiveStamp, now) >= _agentTimeout)
            {
                (expired ??= []).Add(id);
            }
        }

        foreach (string id in expired ?? [])
        {
            _agents.Remove(id);
            AnnounceLeft(id);
        }
    }

    private void AnnounceLeft(string id) => Broadcast(PresenceLeftTopic, new JsonObject { ["id"] = id });

    private sealed class AgentSession(long sequence, string defaultName, UiPresence presence, long activeStamp)
    {
        public long Sequence { get; } = sequence;

        public string DefaultName { get; } = defaultName;

        public UiPresence Presence { get; set; } = presence;

        public long? LastMoveStamp { get; set; }

        public long LastActiveStamp { get; set; } = activeStamp;
    }

    internal UiConnection Add(WebSocket socket, string id)
    {
        var connection = new UiConnection(socket, id, Interlocked.Increment(ref _sequence));
        _connections[id] = connection;
        return connection;
    }

    internal void Remove(UiConnection connection)
    {
        bool removed = _connections.TryRemove(connection.Id, out _);
        connection.CompleteEvents();
        if (removed && connection.Presence is not null)
        {
            Broadcast(PresenceLeftTopic, new JsonObject { ["id"] = connection.Id });
        }

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
    /// Pushes a <c>ui.presence</c> event to every connection except the sender. Goes straight to the per-connection queues
    /// (not through the event hub), so presence never reaches insight or recording.
    /// </summary>
    internal void BroadcastPresence(UiPresence presence)
    {
        string json = new JsonObject { ["op"] = "event", ["topic"] = PresenceTopic, ["data"] = presence.ToJson() }.ToJsonString();
        foreach (UiConnection connection in _connections.Values)
        {
            if (!string.Equals(connection.Id, presence.Id, StringComparison.Ordinal))
            {
                connection.EnqueueEvent(json);
            }
        }
    }

    /// <summary>
    /// The published presence of every session except <paramref name="exceptId"/> (null for all), oldest first: UI connections that
    /// have sent a presence message and agent sessions that have not expired.
    /// </summary>
    public IReadOnlyList<UiPresence> PresenceSnapshot(string? exceptId = null)
    {
        SweepExpired();
        var found = new List<(long Sequence, UiPresence Presence)>();
        foreach (UiConnection connection in _connections.Values)
        {
            if (connection.Presence is { } presence)
            {
                found.Add((connection.Sequence, presence));
            }
        }

        lock (_agentGate)
        {
            foreach (AgentSession session in _agents.Values)
            {
                found.Add((session.Sequence, session.Presence));
            }
        }

        return [.. found
            .Where(f => !string.Equals(f.Presence.Id, exceptId, StringComparison.Ordinal))
            .OrderBy(f => f.Sequence)
            .Select(f => f.Presence)];
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

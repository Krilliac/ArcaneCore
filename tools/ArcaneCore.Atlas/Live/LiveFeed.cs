using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Live;

/// <summary>Which entities <see cref="LiveFeed.ListEntities"/> returns.</summary>
/// <param name="Kind">Only this kind.</param>
/// <param name="Map">Only this map.</param>
/// <param name="Instance">Only this instance.</param>
/// <param name="Near">Only entities within <paramref name="Radius"/> of this point (straight-line distance); results are then nearest first.</param>
/// <param name="Radius">Radius in yards for <paramref name="Near"/>.</param>
/// <param name="Name">Only entities whose name contains this text (case-insensitive).</param>
/// <param name="Guid">Only this guid.</param>
/// <param name="Limit">At most this many entities are returned.</param>
/// <param name="Trails">Include each returned entity's recent positions.</param>
public sealed record LiveEntityFilter(
    LiveKind? Kind,
    uint? Map,
    uint? Instance,
    WorldPoint? Near,
    double Radius,
    string? Name,
    ulong? Guid,
    int Limit,
    bool Trails);

/// <summary>
/// Atlas's live client: one connection at a time to a world daemon's map feed (design section 9), the entity store it fills, the pushes
/// to connected UIs and the camera follow. Registered as a service by <see cref="LiveModule"/>; the <c>live.*</c> commands are thin wrappers.
/// <para>
/// Threads: commands call in from any thread; frames are applied on the connection's reader loop. Connect, disconnect and subscribe are
/// serialised by one gate; queries are not (they only read the current connection). The store is only written by the connection that is
/// current, under a lock that disconnect also takes, and a frame is pushed to the hub under that same lock, so a stale connection can
/// never put entities into a newer store and no frame is pushed after the disconnect that retired it. Event-hub and view subscribers run
/// on the reader loop and must not block (their contract).
/// </para>
/// <para>
/// One subscription: the feed serves a connection one map/instance at a time (the protocol's <c>unsubscribe</c> names nothing, and a new
/// <c>subscribe</c> replaces the old one), so subscribing again replaces the remembered request and drops the entities of the map that was
/// watched before; frames of any other map/instance that are still in flight are ignored and counted.
/// </para>
/// </summary>
public sealed class LiveFeed : IDisposable
{
    /// <summary>Logger category. Refusals and bad input log at Warning or below: errors are published to GitHub by the server's QA tap.</summary>
    public const string LogCategory = "ArcaneCore.Atlas.Live";

    private const string ViewOrigin = "live";

    private readonly LiveOptions _options;
    private readonly EventHub _events;
    private readonly ViewStore _view;
    private readonly ILogger _log;
    private readonly Sink _sink;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private readonly Lock _apply = new();
    private readonly CancellationTokenSource _lifetime = new();
    private LiveSubscription? _subscription;
    private FeedSession? _session;
    private LiveStats _stats = new();
    private string _detail = "not connected";
    private long _sessionCounter;
    private long _currentId;
    private long _connections;
    private ulong _followId;
    private WorldPoint? _followPosition;
    private uint _followMap;
    private bool _disposed;

    /// <summary>Creates a client; nothing is connected until <see cref="ConnectAsync"/>.</summary>
    /// <param name="options">Pipe naming, discovery and timeouts.</param>
    /// <param name="events">Where <see cref="EventHub.Topics.LiveFrame"/> and <see cref="EventHub.Topics.LiveStatus"/> are published.</param>
    /// <param name="view">The shared view the follow camera moves.</param>
    /// <param name="log">Logger for the client.</param>
    public LiveFeed(LiveOptions options, EventHub events, ViewStore view, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _sink = new Sink(this);
    }

    /// <summary>The entities the feed last reported.</summary>
    public LiveStore Store { get; } = new();

    /// <summary>The map the shared view currently shows.</summary>
    internal uint ViewMap => _view.Current.MapId;

    /// <summary>True while a connection is open (after hello, until the server or this client closes it).</summary>
    public bool IsConnected
    {
        get
        {
            lock (_state)
            {
                return _session is { IsOpen: true };
            }
        }
    }

    /// <summary>Test hook invoked after hello succeeds and before the session swap lock is taken.</summary>
    internal Action<FeedSession>? BeforeSwap { get; set; }

    /// <summary>
    /// Connects to the feed of process <paramref name="pid"/>, or to the only feed found by discovery when it is null, replacing any
    /// current connection. Subscriptions from the previous connection are asked for again. Returns <see cref="Status"/>.
    /// </summary>
    /// <exception cref="CommandException">Unavailable (no feed, wrong owner, closed), BadArguments (several feeds) or Failed (hello mismatch).</exception>
    public async Task<JsonObject> ConnectAsync(int? pid, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            int target = pid ?? FindSingleFeed();
            string pipeName = _options.PipeNameFor(target);
            if (_options.Discovery.PipeExists(pipeName) == false)
            {
                throw new CommandException(
                    CommandException.Unavailable,
                    $"no map feed pipe '{pipeName}' exists: process {target.ToString(CultureInfo.InvariantCulture)} is not running, or its map feed is not enabled.");
            }

            // The current connection stays up until the new pipe has answered hello: a failed live.connect must not cost a working one.
            // Until the swap below the new connection's frames are ignored (its id is not the current one); the server sends none before subscribe.
            long id = Interlocked.Increment(ref _sessionCounter);
            FeedSession session;
            try
            {
                session = await FeedSession.OpenAsync(id, pipeName, target, _options, _log, _sink, linked.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                lock (_state)
                {
                    if (_session is null)
                    {
                        _detail = e switch
                        {
                            CommandException => e.Message,
                            OperationCanceledException => "the connection attempt was cancelled",
                            _ => $"the connection attempt failed ({e.GetType().Name})",
                        };
                    }
                }

                throw;
            }

            BeforeSwap?.Invoke(session);

            FeedSession? previous = null;
            bool disposedMeanwhile;
            bool closedMeanwhile = false;
            lock (_state)
            {
                disposedMeanwhile = _disposed;
                if (!disposedMeanwhile)
                {
                    previous = _session;
                    _session = session;
                    _stats = session.Stats;
                    _connections++;
                    _detail = $"connected to process {(session.Hello?.Pid ?? target).ToString(CultureInfo.InvariantCulture)} on pipe '{session.PipeName}'";

                    // A close that landed before the swap found another session installed and was ignored by OnClosed; one after the swap is
                    // handled there. Checking under the same lock as the swap leaves no gap between the two.
                    if (!session.IsOpen)
                    {
                        closedMeanwhile = true;
                        _session = null;
                        _detail = "the server closed the connection right after hello.";
                    }
                }
            }

            if (disposedMeanwhile)
            {
                // Dispose ran between the handshake and here; it never saw this session, so closing it is ours to do.
                session.Dispose();
                throw new ObjectDisposedException(nameof(LiveFeed));
            }

            if (closedMeanwhile)
            {
                lock (_apply)
                {
                    _currentId = 0;
                    Store.Clear();
                }

                previous?.Dispose();
                session.Dispose();
                _log.LogInformation("map feed connection ended: {Reason}", "the server closed the connection right after hello.");
                PublishStatus(connected: false, session, "the server closed the connection right after hello.");
                throw new CommandException(CommandException.Unavailable, "the server closed the connection right after hello.");
            }

            lock (_apply)
            {
                _currentId = id;
                Store.Clear();
            }

            if (previous is not null)
            {
                previous.Dispose();
                PublishStatus(connected: false, previous, "reconnecting");
            }

            PublishStatus(connected: true, session, Detail());
            await ResubscribeAsync(session, linked.Token).ConfigureAwait(false);
            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the connection (telling the server to stop first) and forgets the subscriptions. Safe when not connected.</summary>
    public async Task<JsonObject> DisconnectAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            JsonObject closed = Status();
            closed["wasConnected"] = false;
            return closed;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            FeedSession? session = Detach();
            lock (_state)
            {
                _subscription = null;
            }

            if (session is not null)
            {
                Retire(session.Id);
                using var courtesy = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                courtesy.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    await session.SendAsync(new JsonObject { ["op"] = LiveProtocol.OpUnsubscribe }, courtesy.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is CommandException or OperationCanceledException)
                {
                    // Best effort: the connection is closed next either way.
                }

                session.Dispose();
                SetDetail("disconnected");
                PublishStatus(connected: false, session, "disconnected");
            }

            JsonObject status = Status();
            status["wasConnected"] = session is not null;
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks the server for frames of one map/instance, replacing the connection's previous subscription, and remembers the request for
    /// reconnects. The entities of the map that was watched before are dropped once the request is sent. The server sends no
    /// acknowledgement: frames follow, and a refusal shows up in <see cref="Status"/> as <c>lastServerError</c>.
    /// </summary>
    public async Task<JsonObject> SubscribeAsync(LiveSubscription subscription, CancellationToken cancellationToken)
    {
        // ref: D:\ArcaneCore-lanes\_wf\ATLAS-2026-10-18.md:215,223 (subscribe names one map/instance; unsubscribe names none)
        // ref: claude/atlas-l1@5891e4773:src/ArcaneCore.World/Ops/MapFeed/WorldMapFeed.cs:299,314 (a subscribe replaces the connection's previous one)
        ArgumentNullException.ThrowIfNull(subscription);
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            FeedSession session = RequireSession();

            // The request is remembered before it is sent: the first frame of the new map can arrive before SendAsync returns, and frames
            // of any map other than the subscribed one are ignored.
            LiveSubscription? previous;
            lock (_state)
            {
                previous = _subscription;
                _subscription = subscription;
            }

            Store.ResetSequence(subscription.Map, subscription.Instance);
            try
            {
                await session.SendAsync(subscription.ToMessage(), linked.Token).ConfigureAwait(false);
            }
            catch
            {
                lock (_state)
                {
                    _subscription = previous;
                }

                throw;
            }

            lock (_apply)
            {
                Store.Retain(subscription.Map, subscription.Instance);
            }

            return new JsonObject { ["subscribed"] = subscription.ToJson(), ["subscriptions"] = SubscriptionsJson() };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs a query on the server (on its world thread, against loaded grids only) and returns its result fields. Fails with
    /// <c>failed</c> when the server does not answer within <see cref="LiveOptions.QueryTimeout"/> or answers <c>ok:false</c>.
    /// </summary>
    public async Task<JsonObject> QueryAsync(LiveQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        FeedSession session = RequireSession();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        JsonObject reply = await session.QueryAsync(query.ToMessage(), linked.Token).ConfigureAwait(false);
        bool ok = reply["ok"] is JsonValue v && v.TryGetValue(out bool b) && b;
        if (!ok)
        {
            string why = reply["message"] is JsonValue m && m.TryGetValue(out string? text)
                ? text
                : reply["error"]?.ToJsonString() ?? "it gave no reason";
            string code = reply["code"] is JsonValue c && c.TryGetValue(out string? codeText) ? $"{codeText}: " : string.Empty;
            throw new CommandException(CommandException.Failed, $"the server could not answer the {query.Kind} query: {code}{why}");
        }

        var data = new JsonObject();
        foreach ((string key, JsonNode? value) in reply)
        {
            if (key is not ("op" or "id" or "ok"))
            {
                data[key] = value?.DeepClone();
            }
        }

        if (!data.ContainsKey("kind"))
        {
            data["kind"] = query.Kind;
        }

        if (!data.ContainsKey("map"))
        {
            data["map"] = query.Map;
        }

        if (!data.ContainsKey("instance"))
        {
            data["instance"] = query.Instance;
        }

        return data;
    }

    /// <summary>
    /// Sends a generic id-correlated query (<c>{"op":"query","kind":...}</c>) and returns the result fields (without op, id and ok).
    /// A world that answers <c>bad-args</c> with "unknown query kind" predates the kind: that is an <c>unavailable</c> error, remembered for
    /// this connection so the kind is not asked again (a new connection asks afresh, the world may have been upgraded). Any other refusal
    /// is a <c>failed</c> error carrying the world's message.
    /// </summary>
    public async Task<JsonObject> RequestAsync(JsonObject query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        FeedSession session = RequireSession();
        string kind = query["kind"] is JsonValue kv && kv.TryGetValue(out string? kindText) ? kindText : "query";
        if (session.UnsupportedKinds.ContainsKey(kind))
        {
            throw UnsupportedKind(kind);
        }

        var message = (JsonObject)query.DeepClone();
        message["op"] = LiveProtocol.OpQuery;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        JsonObject reply = await session.QueryAsync(message, linked.Token).ConfigureAwait(false);
        if (!(reply["ok"] is JsonValue okValue && okValue.TryGetValue(out bool ok) && ok))
        {
            string code = reply["code"] is JsonValue c && c.TryGetValue(out string? codeText) ? codeText : "refused";
            string why = reply["message"] is JsonValue m && m.TryGetValue(out string? text) ? text : "it gave no reason";
            if (code == "bad-args" && why.Contains("unknown query kind", StringComparison.Ordinal))
            {
                session.UnsupportedKinds[kind] = 0;
                throw UnsupportedKind(kind);
            }

            throw new CommandException(CommandException.Failed, $"the world daemon could not answer the {kind} query: {why}");
        }

        var data = new JsonObject();
        foreach ((string key, JsonNode? value) in reply)
        {
            if (key is not ("op" or "id" or "ok"))
            {
                data[key] = value?.DeepClone();
            }
        }

        return data;
    }

    /// <summary>
    /// Sends one extension line (<c>caps</c> or <c>action</c>; any other op is an <see cref="ArgumentException"/>, because subscribe and
    /// unsubscribe get no reply, the tools must go through <see cref="ToolAsync"/>'s checks, and a <c>hello</c> could change the connection's role)
    /// as it is (the transport adds the <c>id</c>) and returns the daemon's whole answer object, <c>ok:false</c> refusals included: the caller
    /// decides what a refusal means. Unlike <see cref="RequestAsync"/> it neither forces <c>op:query</c> nor strips the answer. Unavailable when
    /// not connected or when the connection closes while waiting; a timeout (<paramref name="timeout"/>, else <see cref="LiveOptions.QueryTimeout"/>)
    /// is a <c>failed</c> error; an id-carrying <c>op:"error"</c> reply is also a <c>failed</c> <see cref="CommandException"/>; cancellation (the
    /// caller's, or this feed's disposal) throws <see cref="OperationCanceledException"/>. Any thread.
    /// </summary>
    public async Task<JsonObject> ExchangeAsync(JsonObject message, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? op = message["op"] is JsonValue opValue && opValue.TryGetValue(out string? opText) ? opText : null;
        if (op is not ("caps" or "action"))
        {
            throw new ArgumentException($"ExchangeAsync sends only the caps and action ops, not '{op}'.", nameof(message));
        }

        FeedSession session = RequireSession();
        var line = (JsonObject)message.DeepClone();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await session.QueryAsync(line, linked.Token, timeout).ConfigureAwait(false);
    }

    private static CommandException UnsupportedKind(string kind)
        => new(CommandException.Unavailable, $"the connected world daemon does not support the {kind} query (it predates live insight); update and restart it.");

    /// <summary>The connected feed's revision (hello <c>rev</c>, 1 when absent); 1 when not connected.</summary>
    public int FeedRevision => _session?.FeedRevision ?? 1;

    /// <summary>
    /// Sends one packet-tools request (docs/areas/map-feed.md, "Packet tools") and returns its result fields. Unavailable when not connected
    /// or when the daemon's hello did not offer the tools; a refusal by the daemon fails with its code in the message (BadArguments for a
    /// malformed request, Unavailable when the tools are off, Failed otherwise).
    /// </summary>
    public async Task<JsonObject> ToolAsync(JsonObject message, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        FeedSession session = RequireSession();
        if (session.Hello is not { Tools: true })
        {
            throw new CommandException(
                CommandException.Unavailable,
                "the connected world daemon does not offer the packet tools: they need World:Control:PacketTools:Enabled=true and the Development environment (or World:Control:PacketTools:AllowOutsideDevelopment=true), then a restart.");
        }

        string op = message["op"] is JsonValue opValue && opValue.TryGetValue(out string? opText) ? opText : "tool";
        if (op == LiveProtocol.OpTap)
        {
            session.Tools.Reset();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        JsonObject reply = await session.QueryAsync(message, linked.Token, timeout).ConfigureAwait(false);
        if (!(reply["ok"] is JsonValue okValue && okValue.TryGetValue(out bool ok) && ok))
        {
            string code = reply["code"] is JsonValue c && c.TryGetValue(out string? codeText) ? codeText : "refused";
            string why = reply["message"] is JsonValue m && m.TryGetValue(out string? text) ? text : "it gave no reason";
            string kind = code switch
            {
                "bad-args" or "unknown-opcode" => CommandException.BadArguments,
                "tools-off" => CommandException.Unavailable,
                _ => CommandException.Failed,
            };
            throw new CommandException(kind, $"the world daemon refused the {op}: {code}: {why}");
        }

        var data = new JsonObject();
        foreach ((string key, JsonNode? value) in reply)
        {
            if (key is not ("op" or "id" or "ok"))
            {
                data[key] = value?.DeepClone();
            }
        }

        return data;
    }

    /// <summary>The tap packets received so far (up to <paramref name="max"/>, removed), the tap's end once reached, and late console lines.</summary>
    public JsonObject ReadToolTraffic(int max)
    {
        FeedSession session = RequireSession();
        JsonObject read = session.Tools.Drain(max);
        read["connected"] = session.IsOpen;
        return read;
    }

    /// <summary>Whether tap packets or the tap's end are waiting (false when not connected).</summary>
    internal bool HasToolTraffic
    {
        get
        {
            lock (_state)
            {
                return _session is { } session && session.Tools.HasNews;
            }
        }
    }

    /// <summary>
    /// The map and instance a query without them targets: the subscribed map and instance, else the view's map and instance 0. A map named
    /// without an instance gets the subscription's instance when the subscription is for that map, else instance 0.
    /// </summary>
    public (uint Map, uint Instance) QueryTarget(uint? map, uint? instance)
    {
        lock (_state)
        {
            if (map is { } m)
            {
                return (m, instance ?? (_subscription is { } same && same.Map == m ? same.Instance : 0));
            }

            if (_subscription is { } subscribed)
            {
                return (subscribed.Map, instance ?? subscribed.Instance);
            }
        }

        return (_view.Current.MapId, instance ?? 0);
    }

    /// <summary>The stored entities that pass <paramref name="filter"/>, as JSON.</summary>
    public JsonObject ListEntities(LiveEntityFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        string? name = filter.Name;
        IReadOnlyList<StoredEntity> found = Store.Snapshot(
            e =>
            (filter.Kind is null || e.Kind == filter.Kind)
            && (filter.Map is null || e.Map == filter.Map)
            && (filter.Instance is null || e.Instance == filter.Instance)
            && (filter.Guid is null || e.Id == filter.Guid)
            && (name is null || (e.Name is not null && e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            && (filter.Near is not { } near || Distance(e.Position, near) <= filter.Radius),
            filter.Trails);

        IEnumerable<StoredEntity> ordered = filter.Near is { } centre
            ? found.OrderBy(s => Distance(s.Entity.Position, centre)).ThenBy(s => s.Entity.Id)
            : found;
        var entities = new JsonArray();
        foreach (StoredEntity stored in ordered.Take(filter.Limit))
        {
            JsonObject json = stored.Entity.ToJson(stored.Trail);
            if (filter.Near is { } from)
            {
                json["distance"] = Math.Round(Distance(stored.Entity.Position, from), 3);
            }

            entities.Add(json);
        }

        return new JsonObject
        {
            ["connected"] = IsConnected,
            ["matched"] = found.Count,
            ["returned"] = entities.Count,
            ["truncated"] = found.Count > entities.Count,
            ["entities"] = entities,
        };
    }

    /// <summary>
    /// Sets (or, with null, clears) the view's follow and, when the entity is known, moves the camera to it in the same patch. While a
    /// follow is set, every frame that moves the entity moves the camera target again (and switches the view to the entity's map).
    /// </summary>
    public JsonObject Follow(ulong? guid)
    {
        if (guid is null)
        {
            _view.Patch(new JsonObject { ["follow"] = null }, ViewOrigin);
            lock (_state)
            {
                _followId = 0;
                _followPosition = null;
            }

            return new JsonObject { ["following"] = null, ["found"] = false };
        }

        ulong id = guid.Value;
        bool found = Store.TryGetEntity(id, out LiveEntity? entity);
        var patch = new JsonObject { ["follow"] = LiveGuid.Format(id) };
        if (found)
        {
            AddCameraMove(patch, entity!);
        }

        _view.Patch(patch, ViewOrigin);
        lock (_state)
        {
            _followId = id;
            _followPosition = found ? entity!.Position : null;
            _followMap = found ? entity!.Map : 0;
        }

        var result = new JsonObject { ["following"] = LiveGuid.Format(id), ["found"] = found };
        if (found)
        {
            result["map"] = entity!.Map;
            result["at"] = LiveJson.Point(entity.Position);
        }
        else
        {
            result["note"] = "no such entity in the live store (yet); the camera follows it as soon as a frame carries it.";
        }

        return result;
    }

    /// <summary>The connection, subscriptions, per-map frame state, entity counts and problem counters, as JSON.</summary>
    public JsonObject Status()
    {
        FeedSession? session;
        LiveStats stats;
        string detail;
        long connections;
        LiveSubscription? subscription;
        lock (_state)
        {
            session = _session;
            stats = _stats;
            detail = _detail;
            connections = _connections;
            subscription = _subscription;
        }

        IReadOnlyList<MapSummary> summaries = Store.Summaries();
        var frames = new JsonArray();
        long missed = 0;
        int[] byKind = new int[LiveKinds.Names.Count];
        foreach (MapSummary s in summaries)
        {
            missed += s.MissedFrames;
            var perKind = new JsonObject();
            for (int i = 0; i < byKind.Length; i++)
            {
                byKind[i] += s.ByKind[i];
                perKind[LiveKinds.Names[i]] = s.ByKind[i];
            }

            frames.Add(new JsonObject
            {
                ["map"] = s.Map,
                ["instance"] = s.Instance,
                ["seq"] = s.Seq,
                ["clockMs"] = s.ClockMs,
                ["ageMs"] = Math.Round(s.AgeMs, 1),
                ["entities"] = s.Entities,
                ["byKind"] = perKind,
                ["grids"] = s.Grids,
                ["counters"] = s.Counters,
            });
        }

        var totals = new JsonObject { ["total"] = byKind.Sum() };
        for (int i = 0; i < byKind.Length; i++)
        {
            totals[LiveKinds.Names[i]] = byKind[i];
        }

        bool connected = session is { IsOpen: true };
        IReadOnlyList<ServerMap> serverMaps = connected && session!.Hello is { } hello ? hello.Maps : [];
        string? following = _view.Current.FollowGuid;
        return new JsonObject
        {
            ["connected"] = connected,
            ["detail"] = detail,
            ["pid"] = connected ? session!.Hello?.Pid : null,
            ["sha"] = connected ? session!.Hello?.Sha : null,
            ["pipe"] = connected ? session!.PipeName : null,
            ["protocol"] = LiveProtocol.Version,
            ["connections"] = connections,
            ["serverMaps"] = new JsonArray([.. serverMaps.Select(m => (JsonNode?)m.ToJson())]),
            ["subscriptions"] = SubscriptionsJson(subscription),
            ["frames"] = frames,
            ["entities"] = totals,
            ["stats"] = stats.ToJson(missed, Store.DroppedForCapacity),
            ["following"] = following,
        };
    }

    /// <summary>Closes the connection and cancels anything waiting on it. Idempotent.</summary>
    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        FeedSession? session = Detach();
        lock (_apply)
        {
            _currentId = 0;
            Store.Clear();
        }

        session?.Dispose();
    }

    private static double Distance(WorldPoint a, WorldPoint b)
    {
        WorldPoint d = a - b;
        return Math.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
    }

    private int FindSingleFeed()
    {
        IReadOnlyList<int> pids = _options.Discovery.FindFeeds();
        return pids.Count switch
        {
            0 => throw new CommandException(
                CommandException.Unavailable,
                "no map feed found: no pipe named " + LiveProtocol.PipePrefix + "<pid> exists. The world daemon must be running with its map feed enabled in its configuration; pass pid to connect to a specific process."),
            1 => pids[0],
            _ => throw new CommandException(
                CommandException.BadArguments,
                $"several map feeds are running (process ids {string.Join(", ", pids)}); pass pid to choose one."),
        };
    }

    private FeedSession RequireSession()
    {
        lock (_state)
        {
            return _session is { IsOpen: true } session
                ? session
                : throw new CommandException(CommandException.Unavailable, $"not connected to a map feed ({_detail}); run live.connect first.");
        }
    }

    private FeedSession? Detach()
    {
        lock (_state)
        {
            FeedSession? session = _session;
            _session = null;
            return session;
        }
    }

    /// <summary>
    /// Stops connection <paramref name="sessionId"/> from writing into the store and empties the store, but only while it is the connection
    /// that may write: a late close notice of an old connection must not silence the one that replaced it.
    /// </summary>
    private void Retire(long sessionId)
    {
        lock (_apply)
        {
            if (_currentId == sessionId)
            {
                _currentId = 0;
                Store.Clear();
            }
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (_state)
            {
                return _disposed;
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private string Detail()
    {
        lock (_state)
        {
            return _detail;
        }
    }

    private void SetDetail(string detail)
    {
        lock (_state)
        {
            _detail = detail;
        }
    }

    /// <summary>The subscriptions as an array for <c>live.status</c> (a connection carries at most one, so it holds zero or one entry).</summary>
    private JsonArray SubscriptionsJson()
    {
        lock (_state)
        {
            return SubscriptionsJson(_subscription);
        }
    }

    private static JsonArray SubscriptionsJson(LiveSubscription? subscription)
        => subscription is null ? new JsonArray() : new JsonArray(subscription.ToJson());

    private async Task ResubscribeAsync(FeedSession session, CancellationToken cancellationToken)
    {
        LiveSubscription? remembered;
        lock (_state)
        {
            remembered = _subscription;
        }

        if (remembered is null)
        {
            return;
        }

        Store.ResetSequence(remembered.Map, remembered.Instance);
        try
        {
            await session.SendAsync(remembered.ToMessage(), cancellationToken).ConfigureAwait(false);
        }
        catch (CommandException e)
        {
            _log.LogWarning("could not ask the new connection for map {Map} again: {Reason}", remembered.Map, e.Message);
        }
    }

    private void OnFrame(FeedSession session, ParsedFrame parsed)
    {
        // The hub push stays inside the lock that disconnect takes to retire this connection: no frame is announced after the
        // "disconnected" status, and a stale connection cannot announce one at all.
        lock (_apply)
        {
            if (session.Id != _currentId)
            {
                return;
            }

            LiveSubscription? wanted = Volatile.Read(ref _subscription);
            if (wanted is not null && (wanted.Map != parsed.Frame.Map || wanted.Instance != parsed.Frame.Instance))
            {
                session.Stats.IgnoredFrame();
                return;
            }

            Store.Apply(parsed.Frame);
            Publish(EventHub.Topics.LiveFrame, parsed.Json);
        }

        FollowStep();
    }

    private void OnClosed(FeedSession session, string reason)
    {
        // The store is emptied before the connection is reported gone, so nobody sees "disconnected" next to a stale entity list.
        Retire(session.Id);
        lock (_state)
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }

            _session = null;
            _detail = reason;
        }

        _log.LogInformation("map feed connection ended: {Reason}", reason);
        PublishStatus(connected: false, session, reason);
    }

    private void FollowStep()
    {
        string? text = _view.Current.FollowGuid;
        if (text is null || !LiveGuid.TryParse(text, out ulong id) || id == 0 || !Store.TryGetEntity(id, out LiveEntity? entity))
        {
            return;
        }

        lock (_state)
        {
            if (_followId == id && _followPosition == entity.Position && _followMap == entity.Map)
            {
                return;
            }

            _followId = id;
            _followPosition = entity.Position;
            _followMap = entity.Map;
        }

        var patch = new JsonObject();
        AddCameraMove(patch, entity);

        // Narrow the check-then-act window: a follow cleared or switched while this frame was being applied must not get one more camera move.
        if (_view.Current.FollowGuid is not { } stillFollowing || !LiveGuid.TryParse(stillFollowing, out ulong stillId) || stillId != id)
        {
            return;
        }

        try
        {
            _view.Patch(patch, ViewOrigin);
        }
        catch (Exception e) when (e is CommandException or InvalidOperationException)
        {
            _log.LogWarning("follow could not move the camera: {Reason}", e.Message);
        }
    }

    private void AddCameraMove(JsonObject patch, LiveEntity entity)
    {
        patch["camera"] = new JsonObject { ["target"] = LiveJson.Point(entity.Position) };
        if (_view.Current.MapId != entity.Map)
        {
            patch["map"] = entity.Map;
        }
    }

    private void PublishStatus(bool connected, FeedSession session, string detail)
        => Publish(EventHub.Topics.LiveStatus, new JsonObject
        {
            ["connected"] = connected,
            ["pid"] = session.Hello?.Pid,
            ["sha"] = session.Hello?.Sha,
            ["detail"] = detail,
        });

    private void Publish(string topic, JsonNode? payload)
    {
        try
        {
            _events.Publish(topic, payload);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Event subscribers are foreign code (a closed WebSocket, a UI bug); one that throws must not end the connection.
            _log.LogWarning("an event subscriber failed on '{Topic}': {Reason}", topic, e.Message);
        }
    }

    private sealed class Sink(LiveFeed owner) : IFeedSink
    {
        public void Frame(FeedSession session, ParsedFrame frame) => owner.OnFrame(session, frame);

        public void Closed(FeedSession session, string reason) => owner.OnClosed(session, reason);
    }
}

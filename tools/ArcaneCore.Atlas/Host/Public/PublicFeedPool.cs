using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>Receives what one pooled feed connection reads (frames) and when it ends. Called on the connection's reader loop; must not block.</summary>
internal interface IPublicFeedSink
{
    void Frame(IPublicFeedConnection connection, ParsedFrame frame);

    void Closed(IPublicFeedConnection connection, string reason);
}

/// <summary>One connection of the public host to the world's map feed. Opened by an <see cref="IPublicFeedConnector"/>; owned by the <see cref="PublicFeedPool"/>.</summary>
internal interface IPublicFeedConnection : IDisposable
{
    /// <summary>Identifies the connection among those the connector opened.</summary>
    long Id { get; }

    /// <summary>The world's hello reply.</summary>
    HelloInfo Hello { get; }

    /// <summary>True until the connection ends.</summary>
    bool IsOpen { get; }

    /// <summary>How many error lines the world has sent on this connection (the pool watches for strikes).</summary>
    long ServerErrors { get; }

    /// <summary>Sends a message with a fresh <c>id</c> and waits for its <c>result</c> line (any <c>ok</c>); an <c>error</c> line or a timeout throws <see cref="CommandException"/>.</summary>
    Task<JsonObject> RequestAsync(JsonObject message, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Sends a message that has no result line (<c>subscribe</c>, <c>unsubscribe</c>).</summary>
    Task SendAsync(JsonObject message, CancellationToken cancellationToken);
}

/// <summary>Opens connections to the world's feed: connects, completes the hello exchange and sends nothing else.</summary>
internal interface IPublicFeedConnector
{
    Task<IPublicFeedConnection> OpenAsync(IPublicFeedSink sink, CancellationToken cancellationToken);
}

/// <summary>The world's answer to a web login.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Ticket">The opaque ticket on success.</param>
/// <param name="Account">The account name on success.</param>
/// <param name="Security">The account's security level on success (display only; the world decides what it may do).</param>
internal sealed record WorldLoginResult(WorldLoginKind Kind, string? Ticket = null, string? Account = null, int Security = 0);

internal enum WorldLoginKind
{
    /// <summary>Logged in.</summary>
    Ok,

    /// <summary>The single <c>login-failed</c> answer (every cause alike).</summary>
    Failed,

    /// <summary>Web login is off in the world.</summary>
    Off,

    /// <summary>No feed connection, a timeout or a protocol error.</summary>
    Unavailable,
}

/// <summary>What the public host needs from the world: map frames by demand, web login and the guest-derived requests (design 0.4, 0.11).</summary>
internal interface IPublicWorld
{
    /// <summary>Whether at least one web-role feed connection is open.</summary>
    bool Connected { get; }

    /// <summary>Asks the world to log an account in (the typed values go into one feed line, never into a log).</summary>
    Task<WorldLoginResult> LoginAsync(string user, string password, string? code, string clientIp, int floor, CancellationToken cancellationToken);

    /// <summary>Ends a web ticket in the world (logout, kick, expiry); best effort.</summary>
    Task EndTicketAsync(string ticket, bool kick, CancellationToken cancellationToken);

    /// <summary>The account's own online characters (<c>characters</c>), or null when the world cannot answer.</summary>
    Task<JsonObject?> CharactersAsync(string ticket, CancellationToken cancellationToken);

    /// <summary>The basic public information of one entity (<c>info.public</c>), or null when the world cannot answer.</summary>
    Task<JsonObject?> InfoPublicAsync(ulong guid, bool showPlayers, bool showNames, CancellationToken cancellationToken);

    /// <summary>Registers that someone is viewing <paramref name="map"/>; the map is subscribed while demand exists and for a grace period after. Null when the pool is at capacity ("busy").</summary>
    ValueTask<IDisposable?> DemandAsync(uint map, CancellationToken cancellationToken);
}

/// <summary>A failure to start the pooled feed (the listener does not start).</summary>
internal sealed class PublicFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The pool of the public host's feed connections (design 0.10 and 0.11). Every connection is opened by the connector, sends the <c>role web</c> op
/// first and is used only after the world acknowledges it; a hello with <c>tools:true</c> stops the start. One pooled connection serves one map
/// (a feed connection holds one subscription): it is subscribed when the first visitor views the map, released 60 s after the last one leaves and at
/// most one subscription change per map every 10 s. Frames go to the <see cref="PublicFrameStore"/>; after each frame the pool asks the world for
/// <c>links</c> (the world throttles it) so the projector can hide entities linked to real players. All guest-derived requests are typed values
/// serialized by Atlas and refused when the line would exceed 4096 bytes; a connection that collects two world errors within 60 s stops carrying
/// guest-derived requests for a minute. Thread-safety: all state changes happen under one semaphore; frame callbacks only read it.
/// </summary>
internal sealed class PublicFeedPool : IPublicWorld, IPublicFeedSink, IAsyncDisposable
{
    /// <summary>How long a map stays subscribed after its last viewer leaves.</summary>
    public static readonly TimeSpan ReleaseGrace = TimeSpan.FromSeconds(60);

    /// <summary>The fewest time between two subscription changes of one map.</summary>
    public static readonly TimeSpan MinChangeInterval = TimeSpan.FromSeconds(10);

    /// <summary>The longest line the pool sends (the world's limit without the packet tools).</summary>
    public const int MaxLineBytes = 4096;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StrikeWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private sealed class Slot(IPublicFeedConnection connection)
    {
        public readonly IPublicFeedConnection Connection = connection;
        public uint? Map;
        public long LastChange = long.MinValue;
        public long LinksAskedSeq = -1;
        public int LinksInFlight;
        public long SeenServerErrors;
        public readonly List<long> Strikes = [];
        public long QuarantinedUntil;
    }

    private sealed class Demand(PublicFeedPool pool, uint map) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                pool.Release(map);
            }
        }
    }

    /// <summary>Raised when a connection reports that the packet tools are on (design 0.10): the host must stop serving. Called on the thread that opened the connection.</summary>
    public event Action<string>? Fatal;

    private readonly IPublicFeedConnector _connector;
    private readonly PublicOptions _options;
    private readonly int _capacity;
    private readonly PublicFrameStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Slot> _slots = [];
    private readonly Lock _demandLock = new();
    private readonly Dictionary<uint, int> _demand = [];
    private readonly Dictionary<uint, long> _releasedAt = [];
    private readonly Dictionary<uint, long> _lastChange = [];
    private long _nextAttempt;
    private int _disposed;

    public PublicFeedPool(IPublicFeedConnector connector, PublicOptions options, int capacity, PublicFrameStore store, TimeProvider time, ILogger log)
    {
        _connector = connector;
        _options = options;
        _capacity = Math.Max(1, capacity);
        _store = store;
        _time = time;
        _log = log;
    }

    /// <inheritdoc />
    public bool Connected
    {
        get
        {
            lock (_slots)
            {
                return _slots.Any(s => s.Connection.IsOpen);
            }
        }
    }

    /// <summary>The number of open pooled connections (tests, status).</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_slots)
            {
                return _slots.Count(s => s.Connection.IsOpen);
            }
        }
    }

    /// <summary>The maps with a subscribed connection.</summary>
    public IReadOnlyList<uint> SubscribedMaps
    {
        get
        {
            lock (_slots)
            {
                return [.. _slots.Where(s => s.Map is not null).Select(s => s.Map!.Value).Order()];
            }
        }
    }

    /// <summary>
    /// Opens the first connection, which proves the world is reachable and does not run the packet tools: a hello with <c>tools:true</c> is a
    /// <see cref="PublicFeedException"/> (the listener refuses to start, design 0.10). The connection stays in the pool for the first map.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Slot slot = await OpenSlotAsync(cancellationToken).ConfigureAwait(false);
            _log.LogInformation("Atlas public feed: connected (world pid {Pid}), role web acknowledged", slot.Connection.Hello.Pid);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IDisposable?> DemandAsync(uint map, CancellationToken cancellationToken)
    {
        lock (_demandLock)
        {
            _demand[map] = _demand.GetValueOrDefault(map) + 1;
            _releasedAt.Remove(map);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSubscribedLockedAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return new Demand(this, map);
    }

    private void Release(uint map)
    {
        lock (_demandLock)
        {
            int left = _demand.GetValueOrDefault(map) - 1;
            if (left <= 0)
            {
                _demand.Remove(map);
                _releasedAt[map] = _time.GetTimestamp();
            }
            else
            {
                _demand[map] = left;
            }
        }
    }

    /// <summary>One maintenance pass: unsubscribes maps whose grace ended, subscribes maps that could not be subscribed earlier, reconnects after a lost connection.</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long now = _time.GetTimestamp();
            foreach (Slot slot in _slots.ToList())
            {
                if (!slot.Connection.IsOpen)
                {
                    RemoveSlotLocked(slot);
                    continue;
                }

                if (slot.Map is { } map && ReleasedFor(map, now, out long releasedAt) && Since(releasedAt, now) >= ReleaseGrace && Since(slot.LastChange, now) >= MinChangeInterval)
                {
                    await TrySendAsync(slot, new JsonObject { ["op"] = LiveProtocol.OpUnsubscribe }, cancellationToken).ConfigureAwait(false);
                    slot.Map = null;
                    slot.LastChange = now;
                    lock (_demandLock)
                    {
                        _releasedAt.Remove(map);
                    }

                    _store.SetConnected(map, false);
                    _log.LogInformation("Atlas public feed: map {Map} unsubscribed (no viewers for {Seconds} s)", map, (int)ReleaseGrace.TotalSeconds);
                }
            }

            uint[] wanted;
            lock (_demandLock)
            {
                wanted = [.. _demand.Keys];
            }

            foreach (uint map in wanted)
            {
                if (!_slots.Any(s => s.Map == map && s.Connection.IsOpen))
                {
                    await EnsureSubscribedLockedAsync(map, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ReleasedFor(uint map, long now, out long releasedAt)
    {
        lock (_demandLock)
        {
            releasedAt = 0;
            return !_demand.ContainsKey(map) && _releasedAt.TryGetValue(map, out releasedAt);
        }
    }

    // ---- world requests ----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<WorldLoginResult> LoginAsync(string user, string password, string? code, string clientIp, int floor, CancellationToken cancellationToken)
    {
        Slot? slot = PickSlot(guestDerived: false);
        if (slot is null)
        {
            return new WorldLoginResult(WorldLoginKind.Unavailable);
        }

        var message = new JsonObject { ["op"] = "login", ["user"] = user, ["password"] = password, ["ip"] = clientIp, ["floor"] = floor };
        if (!string.IsNullOrEmpty(code))
        {
            message["code"] = code;
        }

        try
        {
            JsonObject result = await slot.Connection.RequestAsync(message, LoginTimeout, cancellationToken).ConfigureAwait(false);
            if (result["ok"]?.GetValue<bool>() == true && result["ticket"]?.GetValue<string>() is { Length: > 0 } ticket)
            {
                return new WorldLoginResult(
                    WorldLoginKind.Ok,
                    ticket,
                    result["account"]?.GetValue<string>(),
                    result["security"] is JsonValue s && s.TryGetValue(out int level) ? level : 0);
            }

            return result["code"]?.GetValue<string>() switch
            {
                "login-failed" => new WorldLoginResult(WorldLoginKind.Failed),
                "web-login-off" => new WorldLoginResult(WorldLoginKind.Off),
                _ => new WorldLoginResult(WorldLoginKind.Unavailable),
            };
        }
        catch (Exception e) when (e is CommandException or InvalidOperationException or FormatException)
        {
            _log.LogWarning("Atlas public feed: login request failed: {Message}", e.Message);
            return new WorldLoginResult(WorldLoginKind.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task EndTicketAsync(string ticket, bool kick, CancellationToken cancellationToken)
    {
        Slot? slot = PickSlot(guestDerived: false);
        if (slot is null)
        {
            return;
        }

        var message = new JsonObject { ["op"] = "logout", ["ticket"] = ticket };
        if (kick)
        {
            message["reason"] = "kick";
        }

        try
        {
            await slot.Connection.RequestAsync(message, RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CommandException or InvalidOperationException)
        {
            _log.LogWarning("Atlas public feed: logout request failed: {Message}", e.Message);
        }
    }

    /// <inheritdoc />
    public Task<JsonObject?> CharactersAsync(string ticket, CancellationToken cancellationToken)
        => GuestDerivedAsync(new JsonObject { ["op"] = "characters", ["ticket"] = ticket }, cancellationToken);

    /// <inheritdoc />
    public Task<JsonObject?> InfoPublicAsync(ulong guid, bool showPlayers, bool showNames, CancellationToken cancellationToken)
        => GuestDerivedAsync(
            new JsonObject
            {
                ["op"] = "info.public",
                ["guid"] = LiveGuid.Format(guid),
                ["players"] = showPlayers ? "shown" : "hidden",
                ["names"] = showPlayers && showNames,
            },
            cancellationToken);

    private async Task<JsonObject?> GuestDerivedAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(message.ToJsonString()) > MaxLineBytes)
        {
            return null;
        }

        Slot? slot = PickSlot(guestDerived: true);
        if (slot is null)
        {
            return null;
        }

        try
        {
            JsonObject result = await slot.Connection.RequestAsync(message, RequestTimeout, cancellationToken).ConfigureAwait(false);
            WatchStrikes(slot);
            return result["ok"]?.GetValue<bool>() == true ? result : null;
        }
        catch (Exception e) when (e is CommandException or InvalidOperationException)
        {
            WatchStrikes(slot);
            _log.LogDebug("Atlas public feed: {Op} failed: {Message}", message["op"], e.Message);
            return null;
        }
    }

    // ---- frames and ends ------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void Frame(IPublicFeedConnection connection, ParsedFrame frame)
    {
        Slot? slot = SlotOf(connection);
        if (slot?.Map is not { } map || frame.Frame.Map != map || frame.Frame.Instance != 0)
        {
            return;
        }

        _store.Ingest(frame.Frame);
        long seq = frame.Frame.Seq;
        if (Interlocked.CompareExchange(ref slot.LinksInFlight, 1, 0) == 0)
        {
            slot.LinksAskedSeq = seq;
            _ = AskLinksAsync(slot, map, seq);
        }
    }

    /// <inheritdoc />
    public void Closed(IPublicFeedConnection connection, string reason)
    {
        Slot? slot = SlotOf(connection);
        if (slot is null)
        {
            return;
        }

        _log.LogWarning("Atlas public feed: a pooled connection ended: {Reason}", reason);
        if (slot.Map is { } map)
        {
            _store.SetConnected(map, false);
        }
    }

    private async Task AskLinksAsync(Slot slot, uint map, long seq)
    {
        try
        {
            JsonObject result = await slot.Connection.RequestAsync(new JsonObject { ["op"] = "links", ["map"] = map, ["instance"] = 0 }, RequestTimeout, CancellationToken.None).ConfigureAwait(false);
            if (result["ok"]?.GetValue<bool>() == true)
            {
                _store.SetLinks(map, new LinksSnapshot(true, seq, Guids(result["linked"]), Entries(result["summonEntries"]), Entries(result["ownedObjectEntries"])));
            }
            else
            {
                _store.SetLinks(map, LinksSnapshot.Unsupported);
            }
        }
        catch (Exception e) when (e is CommandException or InvalidOperationException or FormatException)
        {
            _store.SetLinks(map, LinksSnapshot.Unsupported);
            _log.LogDebug("Atlas public feed: links for map {Map} failed: {Message}", map, e.Message);
        }
        finally
        {
            Interlocked.Exchange(ref slot.LinksInFlight, 0);
        }
    }

    private static HashSet<ulong> Guids(JsonNode? node)
    {
        var set = new HashSet<ulong>();
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonValue v && v.TryGetValue(out string? text) && LiveGuid.TryParse(text, out ulong guid))
                {
                    set.Add(guid);
                }
            }
        }

        return set;
    }

    private static HashSet<uint> Entries(JsonNode? node)
    {
        var set = new HashSet<uint>();
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonValue v && v.TryGetValue(out long value) && value is >= 0 and <= uint.MaxValue)
                {
                    set.Add((uint)value);
                }
            }
        }

        return set;
    }

    // ---- internals ---------------------------------------------------------------------------------------------------------------

    private Slot? SlotOf(IPublicFeedConnection connection)
    {
        lock (_slots)
        {
            return _slots.FirstOrDefault(s => ReferenceEquals(s.Connection, connection));
        }
    }

    private Slot? PickSlot(bool guestDerived)
    {
        long now = _time.GetTimestamp();
        lock (_slots)
        {
            return _slots.Where(s => s.Connection.IsOpen && (!guestDerived || s.QuarantinedUntil <= now))
                .OrderBy(s => s.Map is null ? 0 : 1)
                .FirstOrDefault();
        }
    }

    /// <summary>Two world errors within a minute on one connection: stop carrying guest-derived requests on it for a minute (design 0.11).</summary>
    private void WatchStrikes(Slot slot)
    {
        long errors = slot.Connection.ServerErrors;
        long now = _time.GetTimestamp();
        lock (slot.Strikes)
        {
            for (long i = slot.SeenServerErrors; i < errors; i++)
            {
                slot.Strikes.Add(now);
            }

            slot.SeenServerErrors = errors;
            slot.Strikes.RemoveAll(t => Since(t, now) > StrikeWindow);
            if (slot.Strikes.Count >= 2 && slot.QuarantinedUntil <= now)
            {
                slot.QuarantinedUntil = now + (long)(StrikeWindow.TotalSeconds * _time.TimestampFrequency);
                _log.LogError("Atlas public feed: connection {Id} collected {Count} world errors within a minute; guest-derived requests are paused on it for a minute", slot.Connection.Id, slot.Strikes.Count);
            }
        }
    }

    private async Task EnsureSubscribedLockedAsync(uint map, CancellationToken cancellationToken)
    {
        long now = _time.GetTimestamp();
        if (_slots.Any(s => s.Map == map && s.Connection.IsOpen))
        {
            _store.SetBusy(map, false);
            return;
        }

        if (_lastChange.TryGetValue(map, out long last) && Since(last, now) < MinChangeInterval)
        {
            return; // the maintenance pass subscribes it once the interval has passed
        }

        Slot? slot = _slots.FirstOrDefault(s => s.Map is null && s.Connection.IsOpen);
        if (slot is null && _slots.Count < _capacity && now >= _nextAttempt)
        {
            try
            {
                slot = await OpenSlotAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is CommandException or PublicFeedException)
            {
                _nextAttempt = now + (long)(ReconnectDelay.TotalSeconds * _time.TimestampFrequency);
                _log.LogWarning("Atlas public feed: could not open a connection for map {Map}: {Message}", map, e.Message);
                return;
            }
        }

        if (slot is null)
        {
            _store.SetBusy(map, _slots.Count >= _capacity);
            return;
        }

        var subscription = new LiveSubscription(map, 0, _options.Guest.FeedHz, [.. LiveKinds.Names], Paths: false);
        if (await TrySendAsync(slot, subscription.ToMessage(), cancellationToken).ConfigureAwait(false))
        {
            slot.Map = map;
            slot.LastChange = now;
            _lastChange[map] = now;
            _store.SetBusy(map, false);
            _log.LogInformation("Atlas public feed: map {Map} subscribed at {Hz} Hz", map, _options.Guest.FeedHz.ToString(CultureInfo.InvariantCulture));
        }
    }

    private async Task<bool> TrySendAsync(Slot slot, JsonObject message, CancellationToken cancellationToken)
    {
        try
        {
            await slot.Connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CommandException e)
        {
            _log.LogWarning("Atlas public feed: a send failed: {Message}", e.Message);
            return false;
        }
    }

    /// <summary>Opens a connection and binds its role. Under <c>_gate</c>. Nothing but the hello and the <c>role</c> op is sent before the world acknowledges the role.</summary>
    private async Task<Slot> OpenSlotAsync(CancellationToken cancellationToken)
    {
        IPublicFeedConnection connection;
        try
        {
            connection = await _connector.OpenAsync(this, cancellationToken).ConfigureAwait(false);
        }
        catch (CommandException e)
        {
            throw new PublicFeedException($"the world's map feed is not reachable: {e.Message}", e);
        }

        try
        {
            if (connection.Hello.Tools)
            {
                const string reason = "the world's hello reports tools:true (the packet tools are on): the public listener refuses to use a feed that offers them. Turn World:Control:PacketTools off, or use the separate web pipe.";
                Fatal?.Invoke(reason);
                throw new PublicFeedException(reason);
            }

            JsonObject ack = await connection.RequestAsync(new JsonObject { ["op"] = "role", ["role"] = "web" }, RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (ack["ok"]?.GetValue<bool>() != true || ack["role"]?.GetValue<string>() != "web")
            {
                throw new PublicFeedException($"the world did not bind the connection as web (answer code {ack["code"]?.GetValue<string>() ?? "none"}).");
            }
        }
        catch (CommandException e)
        {
            connection.Dispose();
            throw new PublicFeedException($"the role handshake failed: {e.Message}", e);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        var slot = new Slot(connection);
        lock (_slots)
        {
            _slots.Add(slot);
        }

        return slot;
    }

    private void RemoveSlotLocked(Slot slot)
    {
        lock (_slots)
        {
            _slots.Remove(slot);
        }

        if (slot.Map is { } map)
        {
            _store.SetConnected(map, false);
        }

        slot.Connection.Dispose();
    }

    private TimeSpan Since(long then, long now) => then == long.MinValue ? TimeSpan.MaxValue : TimeSpan.FromSeconds(Math.Max(0, now - then) / (double)_time.TimestampFrequency);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_slots)
            {
                foreach (Slot slot in _slots)
                {
                    slot.Connection.Dispose();
                }

                _slots.Clear();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

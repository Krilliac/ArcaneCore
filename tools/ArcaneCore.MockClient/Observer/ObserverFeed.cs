using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Game;
using ArcaneCore.MockClient.Scenarios;

namespace ArcaneCore.MockClient.Observer;

/// <summary>One thing the mock client sees: its guid, the wire kind Atlas draws it as, where it is and (for a creature or object) its template entry.</summary>
/// <param name="Guid">The object guid.</param>
/// <param name="Kind">The map-feed kind: <c>player</c>, <c>creature</c> or <c>gameobject</c>.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
/// <param name="Entry">Template entry (0 for a player).</param>
internal sealed record ObserverEntity(ulong Guid, string Kind, float X, float Y, float Z, uint Entry);

/// <summary>What the mock client sees on its map at one moment.</summary>
/// <param name="Map">The map id it is on.</param>
/// <param name="Instance">The instance id (the client never learns one; 0).</param>
/// <param name="Entities">Itself and every object whose position it knows.</param>
internal sealed record ObserverView(uint Map, uint Instance, IReadOnlyList<ObserverEntity> Entities);

/// <summary>
/// The observer channel of the mock client: what the client itself saw in the world, in the world daemon's own map-feed protocol 1
/// (docs/areas/map-feed.md), so Atlas can connect to it exactly as to a daemon (<c>live.connect</c> with the mock client's process id)
/// and draw the client's view next to the server's. Pure parts first: <see cref="KindOf"/>, <see cref="ViewOf"/>, <see cref="Hello"/> and
/// <see cref="Frame"/> build the wire messages from plain values.
/// </summary>
internal static class ObserverFrames
{
    /// <summary>Pipe name prefix of every map feed (the daemon's own prefix); the process id follows.</summary>
    public const string PipePrefix = "arcanecore-mapfeed-";

    /// <summary>The feed protocol number.</summary>
    public const int Protocol = 1;

    /// <summary>The feed revision announced in <c>hello</c> (1: no combat counters, no insight queries).</summary>
    public const int Revision = 1;

    /// <summary>The pipe name of the observer channel of the process <paramref name="pid"/>.</summary>
    public static string PipeName(int pid) => PipePrefix + pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>The map-feed kind of a guid, or null for an item, corpse, dynamic object or anything else Atlas does not draw.</summary>
    public static string? KindOf(ulong guid) => new ObjectGuid(guid).High switch
    {
        HighGuid.Player => "player",
        HighGuid.Unit or HighGuid.Pet => "creature",
        HighGuid.GameObject or HighGuid.Transport => "gameobject",
        _ => null,
    };

    /// <summary>
    /// The view from what the connection observed. The client itself comes first as a <c>player</c> at <paramref name="selfAt"/>; every other
    /// object with a known position and a drawable kind follows, in guid order. Objects without a position are left out: they would have to be invented.
    /// </summary>
    public static ObserverView ViewOf(
        uint map, ulong self, MockPosition selfAt, IEnumerable<(ulong Guid, MockPosition? Position)> objects)
    {
        ArgumentNullException.ThrowIfNull(selfAt);
        ArgumentNullException.ThrowIfNull(objects);
        var entities = new List<ObserverEntity> { new(self, "player", selfAt.X, selfAt.Y, selfAt.Z, 0) };
        foreach ((ulong guid, MockPosition? position) in objects.OrderBy(o => o.Guid))
        {
            if (guid == self || position is null || KindOf(guid) is not { } kind)
            {
                continue;
            }

            entities.Add(new ObserverEntity(guid, kind, position.X, position.Y, position.Z, new ObjectGuid(guid).Entry));
        }

        return new ObserverView(map, 0, entities);
    }

    /// <summary>The answer to <c>hello</c>.</summary>
    public static JsonObject Hello(int pid, ObserverView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new JsonObject
        {
            ["op"] = "hello",
            ["protocol"] = Protocol,
            ["rev"] = Revision,
            ["sha"] = "mock-client",
            ["pid"] = pid,
            ["maps"] = new JsonArray(new JsonObject
            {
                ["map"] = view.Map,
                ["instance"] = view.Instance,
                ["players"] = view.Entities.Count(e => e.Kind == "player"),
                ["creatures"] = view.Entities.Count(e => e.Kind == "creature"),
            }),
        };
    }

    /// <summary>A frame of <paramref name="view"/> restricted to <paramref name="kinds"/> (null = every kind).</summary>
    public static JsonObject Frame(long seq, long clockMs, ObserverView view, IReadOnlySet<string>? kinds)
    {
        ArgumentNullException.ThrowIfNull(view);
        var entities = new JsonArray();
        foreach (ObserverEntity entity in view.Entities)
        {
            if (kinds is not null && !kinds.Contains(entity.Kind))
            {
                continue;
            }

            var json = new JsonObject
            {
                ["g"] = entity.Guid.ToString("X16", CultureInfo.InvariantCulture),
                ["k"] = entity.Kind,
                ["x"] = entity.X,
                ["y"] = entity.Y,
                ["z"] = entity.Z,
                ["o"] = 0,
            };
            if (entity.Entry != 0)
            {
                json["e"] = entity.Entry;
            }

            entities.Add(json);
        }

        return new JsonObject
        {
            ["op"] = "frame",
            ["seq"] = seq,
            ["clockMs"] = clockMs,
            ["map"] = view.Map,
            ["instance"] = view.Instance,
            ["grids"] = new JsonArray(),
            ["counters"] = new JsonObject
            {
                ["players"] = view.Entities.Count(e => e.Kind == "player"),
                ["bots"] = 0,
                ["creatures"] = view.Entities.Count(e => e.Kind == "creature"),
                ["gameobjects"] = view.Entities.Count(e => e.Kind == "gameobject"),
                ["emitted"] = entities.Count,
                ["truncated"] = 0,
                ["dropped"] = 0,
            },
            ["entities"] = entities,
        };
    }

    /// <summary>A refused line.</summary>
    public static JsonObject Error(string code, string message)
        => new() { ["op"] = "error", ["code"] = code, ["message"] = message };

    /// <summary>A refused query (the client has no collision data, so every query is refused).</summary>
    public static JsonObject Refused(long id, string code, string message)
        => new() { ["op"] = "result", ["id"] = id, ["ok"] = false, ["code"] = code, ["message"] = message };
}

/// <summary>
/// Serves <see cref="ObserverFrames"/> over a named pipe to one Atlas at a time, in map-feed protocol 1: <c>hello</c> is answered, <c>subscribe</c>
/// starts frames of the current <see cref="ObserverView"/> at the asked rate (0.1 to <see cref="MaxHz"/>), <c>unsubscribe</c> stops them, a
/// <c>query</c> is refused <c>not-loaded</c> (the mock client holds no terrain, model or navmesh data), anything else is <c>unknown-op</c>.
/// </summary>
/// <remarks>
/// Ownership: the server owns the pipe instance and every task it starts; <see cref="DisposeAsync"/> cancels and awaits them. The view callback runs
/// on a thread-pool thread, so it must read only state that is safe to read from there (the connection's observed-object tables are written by
/// the one reader loop; a snapshot taken between frames can be a frame old, which is the nature of an observer). The pipe is current-user-only
/// (<see cref="PipeOptions.CurrentUserOnly"/>) and is never offered beyond this machine. Five refused lines in a row or a line over
/// <see cref="MaxLineBytes"/> close the connection, as the daemon's own feed does.
/// </remarks>
internal sealed class ObserverFeedServer : IAsyncDisposable
{
    /// <summary>The longest line accepted from a client.</summary>
    public const int MaxLineBytes = 4096;

    /// <summary>The fastest frame rate a subscription gets.</summary>
    public const double MaxHz = 10;

    private const double DefaultHz = 4;
    private const int MaxRefusedInARow = 5;

    private readonly string _pipeName;
    private readonly int _pid;
    private readonly Func<ObserverView> _view;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private int _disposed;

    private ObserverFeedServer(string pipeName, int pid, Func<ObserverView> view)
    {
        _pipeName = pipeName;
        _pid = pid;
        _view = view;
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Starts serving on <paramref name="pipeName"/>, reporting <paramref name="pid"/> in <c>hello</c>.</summary>
    public static ObserverFeedServer Start(string pipeName, int pid, Func<ObserverView> view)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentNullException.ThrowIfNull(view);
        return new ObserverFeedServer(pipeName, pid, view);
    }

    /// <summary>Stops serving, closes the client and waits for the tasks to end.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the stop
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        CancellationToken stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = new(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                await ServeAsync(pipe, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // the client went away; the next one gets a fresh instance
            }
            finally
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var writeGate = new SemaphoreSlim(1, 1);
        CancellationTokenSource? frames = null;
        Task? frameTask = null;
        try
        {
            async Task SendAsync(JsonObject message)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
                await writeGate.WaitAsync(stop).ConfigureAwait(false);
                try
                {
                    await pipe.WriteAsync(bytes, stop).ConfigureAwait(false);
                }
                finally
                {
                    writeGate.Release();
                }
            }

            var reader = new LineReader(pipe, MaxLineBytes);
            bool hello = false;
            int refused = 0;
            while (true)
            {
                string? line = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (line == LineReader.TooLong)
                {
                    await SendAsync(ObserverFrames.Error("line-too-long", "a line is longer than " + MaxLineBytes.ToString(CultureInfo.InvariantCulture) + " bytes")).ConfigureAwait(false);
                    return;
                }

                (JsonObject? request, string? op) = Parse(line);
                JsonObject? reply = null;
                bool ok = true;
                if (request is null)
                {
                    reply = ObserverFrames.Error("bad-json", "the line is not a JSON object");
                    ok = false;
                }
                else if (op == "hello")
                {
                    if (request["protocol"] is JsonValue p && p.TryGetValue(out int protocol) && protocol == ObserverFrames.Protocol)
                    {
                        hello = true;
                        reply = ObserverFrames.Hello(_pid, _view());
                    }
                    else
                    {
                        reply = ObserverFrames.Error("protocol", "this feed speaks protocol " + ObserverFrames.Protocol.ToString(CultureInfo.InvariantCulture));
                        ok = false;
                    }
                }
                else if (!hello)
                {
                    reply = ObserverFrames.Error("hello-required", "send hello first");
                    ok = false;
                }
                else if (op == "subscribe")
                {
                    ObserverView current = _view();
                    if (request["map"] is JsonValue m && m.TryGetValue(out long map) && map == current.Map)
                    {
                        double hz = request["hz"] is JsonValue h && h.TryGetValue(out double asked) && double.IsFinite(asked) ? asked : DefaultHz;
                        IReadOnlySet<string>? kinds = KindsOf(request["kinds"]);
                        if (frames is not null)
                        {
                            await frames.CancelAsync().ConfigureAwait(false);
                            await frameTask!.ConfigureAwait(false);
                            frames.Dispose();
                        }

                        frames = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        frameTask = FramesAsync(SendAsync, Math.Clamp(hz, 0.1, MaxHz), kinds, frames.Token);
                    }
                    else
                    {
                        reply = ObserverFrames.Error("map-not-loaded", "the mock client is on map " + current.Map.ToString(CultureInfo.InvariantCulture));
                        ok = false;
                    }
                }
                else if (op == "unsubscribe")
                {
                    if (frames is not null)
                    {
                        await frames.CancelAsync().ConfigureAwait(false);
                        await frameTask!.ConfigureAwait(false);
                        frames.Dispose();
                        frames = null;
                        frameTask = null;
                    }
                }
                else if (op == "query")
                {
                    long id = request["id"] is JsonValue q && q.TryGetValue(out long queryId) ? queryId : 0;
                    reply = ObserverFrames.Refused(id, "not-loaded", "the mock client holds no terrain, model or navmesh data to answer queries with");
                }
                else
                {
                    reply = ObserverFrames.Error("unknown-op", "the observer channel serves hello, subscribe, unsubscribe and query");
                    ok = false;
                }

                refused = ok ? 0 : refused + 1;
                if (reply is not null)
                {
                    await SendAsync(reply).ConfigureAwait(false);
                }

                if (refused >= MaxRefusedInARow)
                {
                    await SendAsync(ObserverFrames.Error("too-many-errors", "five refused lines in a row")).ConfigureAwait(false);
                    return;
                }
            }
        }
        finally
        {
            if (frames is not null)
            {
                await frames.CancelAsync().ConfigureAwait(false);
                if (frameTask is not null)
                {
                    await frameTask.ConfigureAwait(false);
                }

                frames.Dispose();
            }
        }
    }

    private async Task FramesAsync(Func<JsonObject, Task> send, double hz, IReadOnlySet<string>? kinds, CancellationToken cancellationToken)
    {
        TimeSpan period = TimeSpan.FromSeconds(1 / hz);
        long seq = 0;
        try
        {
            using var timer = new PeriodicTimer(period);
            do
            {
                long clockMs = (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
                await send(ObserverFrames.Frame(++seq, clockMs, _view(), kinds)).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // unsubscribed, or the client is gone: the reader loop ends the connection
        }
    }

    private static (JsonObject? Request, string? Op) Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) is JsonObject request)
            {
                return (request, request["op"] is JsonValue v && v.TryGetValue(out string? op) ? op : null);
            }
        }
        catch (JsonException)
        {
            // not JSON
        }

        return (null, null);
    }

    private static HashSet<string>? KindsOf(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? item in array)
        {
            if (item is JsonValue v && v.TryGetValue(out string? kind) && kind is not null)
            {
                kinds.Add(kind);
            }
        }

        return kinds;
    }

    /// <summary>Reads newline-terminated UTF-8 lines of at most a fixed length from a stream.</summary>
    private sealed class LineReader(Stream stream, int maxBytes)
    {
        /// <summary>The value <see cref="ReadLineAsync"/> returns for a line over the limit.</summary>
        public const string TooLong = "\0too-long";

        private readonly byte[] _buffer = new byte[maxBytes + 1];
        private int _length;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                int newline = Array.IndexOf(_buffer, (byte)'\n', 0, _length);
                if (newline >= 0)
                {
                    int end = newline > 0 && _buffer[newline - 1] == (byte)'\r' ? newline - 1 : newline;
                    string line = Encoding.UTF8.GetString(_buffer, 0, end);
                    Buffer.BlockCopy(_buffer, newline + 1, _buffer, 0, _length - newline - 1);
                    _length -= newline + 1;
                    return line;
                }

                if (_length > maxBytes)
                {
                    return TooLong;
                }

                int read = await stream.ReadAsync(_buffer.AsMemory(_length, _buffer.Length - _length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                _length += read;
            }
        }
    }
}

/// <summary>The latest <see cref="ObserverView"/>, published by the thread that reads frames and read by the observer server's threads.</summary>
internal sealed class ObserverSnapshot(ObserverView initial)
{
    private ObserverView _view = initial;

    /// <summary>The most recently published view.</summary>
    public ObserverView Current => Volatile.Read(ref _view);

    /// <summary>Replaces the view; the view is immutable, so readers never see it half written.</summary>
    public void Publish(ObserverView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        Volatile.Write(ref _view, view);
    }
}

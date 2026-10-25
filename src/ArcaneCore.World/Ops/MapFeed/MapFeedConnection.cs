using ArcaneCore.Kernel.Diagnostics;
using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// One connected tool (docs/areas/map-feed.md): a reader that parses its lines and answers them, a writer task that serializes frames
/// and puts every byte on the stream, and the small amount of state the world thread needs to hand it frames. Works over any duplex
/// <see cref="Stream"/> (the named pipe in production, an in-memory pair in tests).
/// <para>
/// Thread affinity: <see cref="RunAsync"/> and its two loops run on the thread pool, never on the world thread. The world thread only calls
/// <see cref="TryReserveFrame"/>, <see cref="TryEnqueueFrame"/>, <see cref="ReleaseFrameReservation"/>, <see cref="NoteDropped"/> and
/// <see cref="NoteMapMissing"/>, which never block: a connection that cannot take another frame loses it (counted), the world waits for
/// nobody. Backpressure: at most <see cref="MaxInFlightFrames"/> frames are reserved, queued or being written at once; the outbound queue
/// is bounded, and the reader awaits it (so a client that stops reading replies stops being read); a write that makes no progress for
/// <see cref="WorldMapFeed.WriteStallTimeout"/> ends the connection.
/// </para>
/// </summary>
internal sealed partial class MapFeedConnection
{
    /// <summary>Frames one connection may hold (reserved by the capture until the writer has serialized them).</summary>
    internal const int MaxInFlightFrames = 2;

    private const int OutboundCapacity = 64;
    private const int InitialBuffer = 16 * 1024;
    private const int ShrinkBuffer = 4 * 1024 * 1024;

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    private readonly WorldMapFeed _feed;
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime;
    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(OutboundCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });

    private ArrayBufferWriter<byte> _buffer = new(InitialBuffer);
    private int _inFlight;
    private long _dropped;
    private long _framesSent;
    private long _sequence;
    private int _strikes;
    private bool _greeted;
    private volatile bool _subscribed;

    public MapFeedConnection(WorldMapFeed feed, Stream stream, string client, ILogger logger, CancellationToken stopping, FeedRole role = FeedRole.Unbound)
    {
        _feed = feed;
        _stream = stream;
        Client = client;
        _logger = logger;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Role = role;
        _roleFixedByPipe = role != FeedRole.Unbound;
    }

    /// <summary>Who is connected, for the log ("client pid 1234 atlas").</summary>
    public string Client { get; }

    /// <summary>Frames this connection lost to backpressure so far.</summary>
    public long DroppedFrames => Volatile.Read(ref _dropped);

    /// <summary>Frames written to the stream so far.</summary>
    public long FramesSent => Volatile.Read(ref _framesSent);

    /// <summary>Whether the client has a subscription.</summary>
    public bool IsSubscribed => _subscribed;

    private enum OutboundKind : byte
    {
        Frame,
        Line,
        MapMissing,
    }

    private enum Outcome : byte
    {
        Served,
        Refused,
        Close,
    }

    /// <summary>Reserve room for one more frame (world thread; never blocks). False: the connection is busy, skip it.</summary>
    public bool TryReserveFrame()
    {
        if (Interlocked.Increment(ref _inFlight) > MaxInFlightFrames)
        {
            Interlocked.Decrement(ref _inFlight);
            return false;
        }

        return true;
    }

    /// <summary>Give back a reservation whose frame was not queued.</summary>
    public void ReleaseFrameReservation() => Interlocked.Decrement(ref _inFlight);

    /// <summary>
    /// Queue a captured frame for this connection (world thread; never blocks). On true the writer owns the frame holder and the
    /// reservation; on false the caller keeps both and must release them.
    /// </summary>
    public bool TryEnqueueFrame(FeedFrame frame) => _outbound.Writer.TryWrite(new Outbound(OutboundKind.Frame, frame, null, 0, 0));

    /// <summary>Count a frame this connection will not get.</summary>
    public void NoteDropped() => Interlocked.Increment(ref _dropped);

    /// <summary>Tell the client, once, that the map it watches is not running (world thread; dropped when the queue is full).</summary>
    public void NoteMapMissing(uint mapId, uint instanceId)
        => _outbound.Writer.TryWrite(new Outbound(OutboundKind.MapMissing, null, null, mapId, instanceId));

    /// <summary>Serve the connection until the client leaves, misbehaves, goes quiet or the daemon stops.</summary>
    public async Task RunAsync()
    {
        _logger.LogInformation("map feed: client connected ({Client})", Client);
        Task writer = Task.Run(WriteLoopAsync);
        try
        {
            await ReadLoopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The daemon is stopping or the writer gave up on the client.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or ChannelClosedException)
        {
            _logger.LogDebug(ex, "map feed: the connection of {Client} ended with an error", Client);
        }
        finally
        {
            _feed.Unsubscribe(this);
            _subscribed = false;
            _outbound.Writer.TryComplete();
            if (_feed.Extension is { } extension)
            {
                await extension.ConnectionEndedAsync(this).ConfigureAwait(false);
            }

            await EndToolsAsync().ConfigureAwait(false);
            try
            {
                await writer.WaitAsync(CloseWait, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A client that does not read what it was sent: cancel the pending write.
                await _lifetime.CancelAsync().ConfigureAwait(false);
                await writer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            Drain();
            _lifetime.Dispose();
            _logger.LogInformation(
                "map feed: client disconnected ({Client}); {Sent} frames sent, {Dropped} dropped", Client, FramesSent, DroppedFrames);
        }
    }

    private async Task ReadLoopAsync()
    {
        // With the packet tools on, a line may carry the hex payload of the largest client packet.
        int maxLine = _feed.Tools is null || Role == FeedRole.Web ? MapFeedProtocol.MaxLineBytes : MapFeedProtocol.MaxToolLineBytes;
        var reader = new MapFeedLineReader(_stream, maxLine);
        while (true)
        {
            if (Role == FeedRole.Web && maxLine != MapFeedProtocol.MaxLineBytes)
            {
                // A connection on the shared pipe starts unbound with the tools' limit; once it binds the web role it gets the web limit.
                maxLine = MapFeedProtocol.MaxLineBytes;
                reader.MaxBytes = maxLine;
            }

            LineRead read;
            using (CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                if (!_subscribed)
                {
                    idle.CancelAfter(_feed.IdleTimeout);
                }

                try
                {
                    read = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    _logger.LogInformation("map feed: closing an idle connection ({Client})", Client);
                    await QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.IdleTimeout, "no request for a while and no subscription; closing the connection")).ConfigureAwait(false);
                    return;
                }
            }

            if (read.Status == LineStatus.EndOfInput)
            {
                return;
            }

            if (read.Status == LineStatus.TooLong)
            {
                _logger.LogWarning(
                    "map feed: a line longer than {Max} bytes closed the connection ({Client}); nothing was run", maxLine, Client);
                await QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.LineTooLong, $"a line may hold at most {maxLine} bytes; closing the connection")).ConfigureAwait(false);
                return;
            }

            if (read.Line.IsEmpty)
            {
                continue;
            }

            Outcome outcome = await HandleLineAsync(read.Line).ConfigureAwait(false);
            if (outcome == Outcome.Close)
            {
                return;
            }

            if (outcome == Outcome.Refused && ++_strikes >= MapFeedProtocol.MaxStrikes)
            {
                _logger.LogWarning("map feed: {Strikes} refused lines in a row closed the connection ({Client})", _strikes, Client);
                await QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.TooManyErrors, "too many refused lines in a row; closing the connection")).ConfigureAwait(false);
                return;
            }

            if (outcome == Outcome.Served)
            {
                _strikes = 0;
            }
        }
    }

    private async Task<Outcome> HandleLineAsync(ReadOnlyMemory<byte> line)
    {
        if (!MapFeedProtocol.TryParse(line, out FeedRequest request, out FeedError error))
        {
            return await RefuseAsync(request, error).ConfigureAwait(false);
        }

        if (!_greeted && request.Op != FeedOp.Hello)
        {
            return await RefuseAsync(request, new FeedError(MapFeedProtocol.HelloRequired, "send {\"op\":\"hello\",\"protocol\":1} first")).ConfigureAwait(false);
        }

        if (MapFeedRoles.RefuseCoreOp(Role, request.Op) is { } mismatch)
        {
            // An answer, not a strike: the role is the connection's, and the client is told which op it may not use.
            string kind = MapFeedRoles.KindOf(request.Op);
            _logger.LogWarning("map feed: {Kind} refused on a web connection ({Client})", kind, Client);
            await QueueAsync(PacketToolsJson.Refused(request.Id, kind, mismatch.Code, mismatch.Message)).ConfigureAwait(false);
            return Outcome.Served;
        }

        switch (request.Op)
        {
            case FeedOp.Hello:
                return await HelloAsync(request).ConfigureAwait(false);
            case FeedOp.Extension:
                return await HandleExtensionAsync(request).ConfigureAwait(false);
            case FeedOp.Subscribe:
                _feed.Subscribe(this, request);
                _subscribed = true;
                return Outcome.Served;
            case FeedOp.Unsubscribe:
                _feed.Unsubscribe(this);
                _subscribed = false;
                return Outcome.Served;
            case FeedOp.Console or FeedOp.Inject or FeedOp.Send or FeedOp.Tap or FeedOp.Untap:
                return await HandleToolAsync(request).ConfigureAwait(false);
            default:
                await QueryAsync(request).ConfigureAwait(false);
                return Outcome.Served;
        }
    }

    private async Task<Outcome> HelloAsync(FeedRequest request)
    {
        if (request.Protocol != MapFeedProtocol.Version)
        {
            _logger.LogWarning(
                "map feed: client {Client} speaks protocol {Protocol}, this daemon speaks {Version}; closing the connection", Client, request.Protocol, MapFeedProtocol.Version);
            await QueueAsync(MapFeedJson.ErrorLine(
                MapFeedProtocol.ProtocolMismatch, $"this daemon speaks protocol {MapFeedProtocol.Version}, not {request.Protocol}")).ConfigureAwait(false);
            return Outcome.Close;
        }

        _greeted = true;
        (MapSummary[]? maps, FeedError? failure) = await InvokeWorldAsync(() => MapFeedQueries.Summarize(_feed.World)).ConfigureAwait(false);
        if (maps is null)
        {
            await QueueAsync(MapFeedJson.ErrorLine(failure!.Value.Code, failure.Value.Message)).ConfigureAwait(false);
            return Outcome.Served;
        }

        await QueueAsync(MapFeedJson.HelloLine(WorldMapFeed.BuildSha, Environment.ProcessId, maps, tools: _feed.Tools is not null && Role != FeedRole.Web)).ConfigureAwait(false);
        return Outcome.Served;
    }

    private async Task QueryAsync(FeedRequest request)
    {
        if (request.Kind is QueryKind.Inspect or QueryKind.Anomalies)
        {
            await InsightQueryAsync(request).ConfigureAwait(false);
            return;
        }

        (QueryAnswer? answer, FeedError? failure) = await InvokeWorldAsync(() => MapFeedQueries.Run(_feed.World, request)).ConfigureAwait(false);
        answer ??= QueryAnswer.Refused(request, failure!.Value.Code, failure.Value.Message);
        if (answer.Ok)
        {
            _logger.LogDebug("map feed: {Kind} query {Id} answered ({Client})", request.Kind, request.Id, Client);
        }
        else if (answer.Code == MapFeedProtocol.NotLoaded)
        {
            _logger.LogDebug("map feed: {Kind} query {Id} refused ({Client}): {Message}", request.Kind, request.Id, Client, answer.Message);
        }
        else
        {
            _logger.LogWarning("map feed: {Kind} query {Id} refused ({Client}): {Code} {Message}", request.Kind, request.Id, Client, answer.Code, answer.Message);
        }

        await QueueAsync(MapFeedJson.ResultLine(answer)).ConfigureAwait(false);
    }

    /// <summary>
    /// The live-insight queries (docs/areas/atlas-insight.md): run on the world thread like every query, written by
    /// <see cref="MapFeedInsightJson"/> (never by <see cref="MapFeedJson.WriteResult"/>, which would call an unknown kind an under-map answer).
    /// </summary>
    private async Task InsightQueryAsync(FeedRequest request)
    {
        (InsightAnswer? answer, FeedError? failure) = await InvokeWorldAsync(
            () => request.Kind == QueryKind.Inspect ? MapFeedInspect.Run(_feed, request) : MapFeedAnomalies.Run(_feed, request)).ConfigureAwait(false);
        answer ??= InsightAnswer.Refused(request, failure!.Value.Code, failure.Value.Message);
        if (answer.Ok)
        {
            _logger.LogDebug("map feed: {Kind} query {Id} answered ({Client})", request.Kind, request.Id, Client);
        }
        else
        {
            _logger.LogDebug("map feed: {Kind} query {Id} refused ({Client}): {Code} {Message}", request.Kind, request.Id, Client, answer.Code, answer.Message);
        }

        await QueueAsync(MapFeedInsightJson.ResultLine(answer)).ConfigureAwait(false);
    }

    private async Task<Outcome> RefuseAsync(FeedRequest request, FeedError error)
    {
        _logger.LogWarning("map feed: refused a line from {Client}: {Code}: {Message}", Client, error.Code, error.Message);
        byte[] line = !request.HasId ? MapFeedJson.ErrorLine(error.Code, error.Message)
            : request.Op == FeedOp.Query ? MapFeedJson.ResultLine(QueryAnswer.Refused(request.Id, error.Code, error.Message))
            : PacketToolsJson.Refused(request.Id, KindOf(request.Op), error.Code, error.Message);
        await QueueAsync(line).ConfigureAwait(false);
        return Outcome.Refused;
    }

    /// <summary>
    /// Run <paramref name="work"/> on the world thread and wait for it, at most <see cref="WorldMapFeed.QueryTimeout"/>. The value, or a
    /// refusal that says why there is none. A connection that is going away rethrows the cancellation instead.
    /// </summary>
    internal async Task<(T? Value, FeedError? Failure)> InvokeWorldAsync<T>(Func<T> work)
        where T : class
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(_feed.QueryTimeout);
        try
        {
            return (await _feed.World.InvokeAsync(work, timeout.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            if (_lifetime.IsCancellationRequested)
            {
                throw;
            }

            return timeout.IsCancellationRequested
                ? (null, new FeedError(MapFeedProtocol.Timeout, $"the world did not run the request within {(int)_feed.QueryTimeout.TotalSeconds} s"))
                : (null, new FeedError(MapFeedProtocol.WorldStopping, "the world is stopping"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("ops.map-feed", ex);
            _logger.LogWarning(ex, "map feed: a request failed inside the server ({Client})", Client);
            return (null, new FeedError(MapFeedProtocol.QueryFailed, "the request failed inside the server; see the server log"));
        }
    }

    /// <summary>Queue a control line; waits while the queue is full (the reader stops reading meanwhile).</summary>
    internal ValueTask QueueAsync(byte[] line)
        => _outbound.Writer.WriteAsync(new Outbound(OutboundKind.Line, null, line, 0, 0), _lifetime.Token);

    private async Task WriteLoopAsync()
    {
        var json = new Utf8JsonWriter(_buffer, WriterOptions);
        try
        {
            await foreach (Outbound item in _outbound.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (!Serialize(item, json))
                {
                    continue;
                }

                await WriteAsync(_buffer.WrittenMemory).ConfigureAwait(false);
                if (item.Kind == OutboundKind.Frame)
                {
                    Interlocked.Increment(ref _framesSent);
                }

                if (_buffer.Capacity > ShrinkBuffer)
                {
                    _buffer = new ArrayBufferWriter<byte>(InitialBuffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection is ending (daemon stop, a stalled write, or the reader gave up).
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "map feed: could not write to {Client}", Client);
        }
        finally
        {
            await json.DisposeAsync().ConfigureAwait(false);

            // A reader that waits on a client the writer can no longer reach would wait forever.
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Put the item's line in the buffer; false when there is nothing to send. A frame's holder and reservation end here.</summary>
    private bool Serialize(in Outbound item, Utf8JsonWriter json)
    {
        _buffer.ResetWrittenCount();
        switch (item.Kind)
        {
            case OutboundKind.Line:
                _buffer.Write(item.Line!);
                return true;
            case OutboundKind.MapMissing:
                _buffer.Write(MapFeedJson.ErrorLine(
                    MapFeedProtocol.MapNotLoaded,
                    $"map {item.MapId} instance {item.InstanceId} is not running; frames resume when it is"));
                return true;
            default:
                FeedFrame frame = item.Frame!;
                try
                {
                    json.Reset(_buffer);
                    MapFeedJson.WriteFrame(json, frame, ++_sequence, Volatile.Read(ref _dropped));
                    json.Flush();
                    _buffer.GetSpan(1)[0] = (byte)'\n';
                    _buffer.Advance(1);
                    return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
                {
                    _logger.LogWarning(ex, "map feed: a frame for {Client} could not be serialized and was skipped", Client);
                    NoteDropped();
                    return false;
                }
                finally
                {
                    frame.Release();
                    Interlocked.Decrement(ref _inFlight);
                }
        }
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> bytes)
    {
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        stall.CancelAfter(_feed.WriteStallTimeout);
        try
        {
            await _stream.WriteAsync(bytes, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            _logger.LogWarning("map feed: {Client} did not read for {Seconds} s; closing the connection", Client, (int)_feed.WriteStallTimeout.TotalSeconds);
            throw;
        }
    }

    /// <summary>Release what is still queued once nothing will write it.</summary>
    private void Drain()
    {
        _outbound.Writer.TryComplete();
        while (_outbound.Reader.TryRead(out Outbound item))
        {
            if (item.Kind == OutboundKind.Frame)
            {
                item.Frame!.Release();
                Interlocked.Decrement(ref _inFlight);
                NoteDropped();
            }
        }
    }

    private readonly record struct Outbound(OutboundKind Kind, FeedFrame? Frame, byte[]? Line, uint MapId, uint InstanceId);
}

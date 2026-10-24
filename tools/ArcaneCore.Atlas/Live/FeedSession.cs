using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Live;

/// <summary>Receives what a <see cref="FeedSession"/> reads. Called on the session's reader loop; implementations must not block for long.</summary>
internal interface IFeedSink
{
    /// <summary>A frame passed validation.</summary>
    void Frame(FeedSession session, ParsedFrame frame);

    /// <summary>The session ended (the server closed it, it broke, or it was disposed). Called exactly once.</summary>
    void Closed(FeedSession session, string reason);
}

/// <summary>
/// One connection to a map feed: the pipe, the handshake, the NDJSON reader loop and the queries waiting for their results.
/// <para>
/// Ownership: created by <see cref="OpenAsync"/>, owned by <see cref="LiveFeed"/>, which disposes it. Threads: the reader loop runs on the
/// thread pool and is the only reader of the pipe; any thread may send (writes are serialised) or query. Every failure is a
/// <see cref="CommandException"/> with a precise message; bad input from the server is counted in <see cref="Stats"/> and never thrown.
/// </para>
/// </summary>
internal sealed class FeedSession : IDisposable
{
    private const int MaxPendingQueries = 64;

    private readonly NamedPipeClientStream _pipe;
    private readonly NdjsonReader _reader;
    private readonly LiveOptions _options;
    private readonly ILogger _log;
    private readonly IFeedSink _sink;
    private readonly int? _expectedPid;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly TaskCompletionSource<HelloInfo> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextQueryId;
    private int _queriesWaiting;
    private int _disposed;
    private int _ended;
    private string? _abortReason;
    private volatile bool _helloDone;

    private FeedSession(long id, string pipeName, NamedPipeClientStream pipe, int? expectedPid, LiveOptions options, ILogger log, IFeedSink sink)
    {
        Id = id;
        PipeName = pipeName;
        _pipe = pipe;
        _expectedPid = expectedPid;
        _options = options;
        _log = log;
        _sink = sink;
        _reader = new NdjsonReader(pipe, options.MaxLineBytes);
    }

    /// <summary>Identifies this connection among the ones <see cref="LiveFeed"/> has opened (later connections have larger ids).</summary>
    public long Id { get; }

    /// <summary>The pipe this session is connected to.</summary>
    public string PipeName { get; }

    /// <summary>The server's hello reply; set once <see cref="OpenAsync"/> has returned.</summary>
    public HelloInfo? Hello { get; private set; }

    /// <summary>The map-feed revision the server's hello announced (<c>rev</c>; 2 adds the combat counters and insight queries); 1 when absent or malformed.</summary>
    public int FeedRevision { get; private set; } = 1;

    /// <summary>Query kinds this connection's world answered "unknown query kind" to; they are not asked again on this connection.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> UnsupportedKinds { get; } = new(StringComparer.Ordinal);

    /// <summary>What this connection has received and refused.</summary>
    public LiveStats Stats { get; } = new();

    /// <summary>The packet tools' streamed lines (tap packets, tap ends, late console lines) waiting to be read.</summary>
    public PacketToolTraffic Tools { get; } = new();

    /// <summary>True until the session is disposed or the pipe ends.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _ended) == 0;

    /// <summary>
    /// Connects, verifies the pipe's owner, starts the reader loop and completes the hello exchange. On any failure the pipe is closed and a
    /// <see cref="CommandException"/> says why.
    /// </summary>
    public static async Task<FeedSession> OpenAsync(
        long id,
        string pipeName,
        int? expectedPid,
        LiveOptions options,
        ILogger log,
        IFeedSink sink,
        CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = CreatePipe(pipeName);
        FeedSession? session = null;
        try
        {
            await ConnectPipeAsync(pipe, pipeName, options, cancellationToken).ConfigureAwait(false);
            session = new FeedSession(id, pipeName, pipe, expectedPid, options, log, sink);
            _ = session.RunAsync();
            await session.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is null)
            {
                pipe.Dispose();
            }
            else
            {
                session.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Writes one message as a line. Serialised with other writers. If the pipe stops accepting data for the write timeout, or the write is
    /// cancelled part way, the connection is closed: a line cut in half would be glued to the next one and the server would read garbage.
    /// </summary>
    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        linked.CancelAfter(_options.WriteTimeout);
        bool writing = false;
        try
        {
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                writing = true;
                await _pipe.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (OperationCanceledException) when (writing)
        {
            // Cancelled after the first byte may have gone out. The reader loop reports the close, with this reason. When the session is
            // already closing (_stop) there is nothing to add.
            if (!_stop.IsCancellationRequested)
            {
                Abort(cancellationToken.IsCancellationRequested
                    ? "a write to the map feed was cancelled part way, so the connection was closed"
                    : string.Create(CultureInfo.InvariantCulture, $"the map feed stopped accepting data for {_options.WriteTimeout.TotalSeconds:0.#} s, so the connection was closed"));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new CommandException(CommandException.Unavailable, "the map feed connection closed or stopped accepting data.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CommandException(CommandException.Unavailable, "the map feed connection closed or stopped accepting data.");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new CommandException(CommandException.Unavailable, $"the map feed connection is gone: {e.Message}", e);
        }
    }

    /// <summary>
    /// Sends <paramref name="message"/> with a fresh <c>id</c> and waits for the matching result line. Times out after
    /// <paramref name="timeout"/>, else <see cref="LiveOptions.QueryTimeout"/>; a result that arrives later is counted and dropped.
    /// </summary>
    public async Task<JsonObject> QueryAsync(JsonObject message, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        TimeSpan wait = timeout ?? _options.QueryTimeout;
        ArgumentNullException.ThrowIfNull(message);
        if (Interlocked.Increment(ref _queriesWaiting) > MaxPendingQueries)
        {
            Interlocked.Decrement(ref _queriesWaiting);
            throw new CommandException(CommandException.Failed, $"{MaxPendingQueries} queries are already waiting for the server; wait for some to finish.");
        }

        long id = Interlocked.Increment(ref _nextQueryId);
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            message["id"] = id;
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new CommandException(
                CommandException.Failed,
                string.Create(CultureInfo.InvariantCulture, $"query {id} timed out after {wait.TotalSeconds:0.#} s: the server did not answer (its world thread may be busy, or it does not support this query)."),
                e);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            Interlocked.Decrement(ref _queriesWaiting);
        }
    }

    /// <summary>Closes the pipe, stops the reader loop and fails every waiting query. Safe to call more than once and from any thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The stop source has no timers or linked registrations of its own, so it needs no disposal; disposing it would make later
        // sends throw ObjectDisposedException from Token instead of reporting a closed connection.
        _stop.Cancel();
        FailWaiters("the map feed connection was closed");
        _pipe.Dispose();
    }

    /// <summary>Closes the connection because of <paramref name="reason"/>, which the close notice then carries instead of the generic "disconnected".</summary>
    private void Abort(string reason)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Interlocked.CompareExchange(ref _abortReason, reason, null);
        }

        Dispose();
    }

    private static NamedPipeClientStream CreatePipe(string pipeName)
        => OperatingSystem.IsWindows()
            // "." is this machine only. Identification lets the server learn who connected but never act as that user.
            ? new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification)
            // Elsewhere the runtime's own owner check (uid of the socket) is the equivalent.
            : new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task ConnectPipeAsync(NamedPipeClientStream pipe, string pipeName, LiveOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await pipe.ConnectAsync((int)options.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            // A pipe with every instance in use (the daemon's client limit) also ends here: Windows reports it as busy, not as refused.
            throw new CommandException(
                CommandException.Unavailable,
                string.Create(CultureInfo.InvariantCulture, $"nothing answered on pipe '{pipeName}' within {options.ConnectTimeout.TotalSeconds:0.#} s: the world daemon is not running, its map feed is off, all of its client slots are in use, or it runs as another user."),
                e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new CommandException(
                CommandException.Unavailable,
                $"access to pipe '{pipeName}' was denied: it belongs to another user, or its access list does not admit this account.",
                e);
        }
        catch (IOException e)
        {
            throw new CommandException(CommandException.Unavailable, $"could not connect to pipe '{pipeName}': {e.Message}", e);
        }

        if (OperatingSystem.IsWindows())
        {
            VerifyOwner(pipe, pipeName);
        }
    }

    /// <summary>
    /// Refuses a pipe that another user created. The server stamps its pipe with its own account as owner
    /// (src/ArcaneCore.World/Ops/Control/ControlPipeSecurity.cs: <c>security.SetOwner(user)</c>). The runtime's built-in
    /// <c>PipeOptions.CurrentUserOnly</c> compares that owner with the token's default owner, which for an elevated administrator is the
    /// Administrators group and so would refuse the same user; this accepts the user SID or the token owner.
    /// </summary>
    // ref: src/ArcaneCore.World/Ops/Control/ControlPipeSecurity.cs:45 (security.SetOwner(user): the daemon's pipe is owned by the daemon's user)
    [SupportedOSPlatform("windows")]
    private static void VerifyOwner(NamedPipeClientStream pipe, string pipeName)
    {
        SecurityIdentifier? owner;
        try
        {
            owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        {
            throw new CommandException(CommandException.Unavailable, $"could not read the owner of pipe '{pipeName}', so it is not trusted: {e.Message}", e);
        }

        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        if (!IsTrustedOwner(owner, me.User, me.Owner))
        {
            throw new CommandException(
                CommandException.Unavailable,
                $"pipe '{pipeName}' is owned by {owner?.Value ?? "an unknown account"}, not by the current user ({me.User?.Value}); refusing to talk to another user's process.");
        }
    }

    /// <summary>
    /// Whether a pipe owned by <paramref name="owner"/> is one the current user created: the owner is the user's SID or the token's default owner.
    /// Caveat: for an elevated administrator the token's default owner is BUILTIN\Administrators, so any pipe owned by that group is trusted,
    /// including one created by a different administrator account. That matches how Windows itself assigns ownership to elevated processes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsTrustedOwner(SecurityIdentifier? owner, SecurityIdentifier? user, SecurityIdentifier? tokenOwner)
        => owner is not null && (owner == user || owner == tokenOwner);

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        await SendAsync(new JsonObject { ["op"] = LiveProtocol.OpHello, ["protocol"] = LiveProtocol.Version }, cancellationToken).ConfigureAwait(false);
        try
        {
            Hello = await _hello.Task.WaitAsync(_options.HelloTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new CommandException(
                CommandException.Failed,
                string.Create(CultureInfo.InvariantCulture, $"the server did not answer hello within {_options.HelloTimeout.TotalSeconds:0.#} s: pipe '{PipeName}' is not a map feed, or the world daemon is stuck."),
                e);
        }
    }

    private async Task RunAsync()
    {
        string reason;
        try
        {
            reason = await ReadLoopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            reason = "disconnected";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            reason = Volatile.Read(ref _disposed) != 0 ? "disconnected" : $"the connection broke: {e.Message}";
        }
        catch (Exception e)
        {
            // The loop is a background task: an unexpected failure must end this connection with a reason, never escape unobserved.
            _log.LogWarning(e, "map feed reader failed");
            reason = $"the reader failed: {e.GetType().Name}: {e.Message}";
        }

        if (Volatile.Read(ref _abortReason) is { } aborted)
        {
            reason = aborted;
        }

        Volatile.Write(ref _ended, 1);
        FailWaiters(reason);
        _sink.Closed(this, reason);
    }

    private async Task<string> ReadLoopAsync()
    {
        CancellationToken stop = _stop.Token;
        while (true)
        {
            NdjsonItem item = await _reader.ReadAsync(stop).ConfigureAwait(false);
            Stats.BytesRead(_reader.BytesRead);
            switch (item.Kind)
            {
                case NdjsonKind.End:
                    // A server that refuses and then hangs up (protocol, idle, too many errors) says why in its last error line.
                    return Stats.LastServerError is { } lastError
                        ? $"the server closed the connection (its last error was {lastError})"
                        : "the server closed the connection";
                case NdjsonKind.Truncated:
                    Stats.Truncated(item.Length);
                    return "the server closed the connection in the middle of a message";
                case NdjsonKind.Oversize:
                    Stats.Oversize(item.Length, _options.MaxLineBytes);
                    Warn(string.Create(CultureInfo.InvariantCulture, $"a line of {item.Length} bytes exceeded the {_options.MaxLineBytes} byte limit and was skipped"));
                    break;
                default:
                    try
                    {
                        Dispatch(item.Line.Span);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Subscribers of the event hub and view store are foreign code; one that throws must not end the connection.
                        Bad($"a handler failed: {e.GetType().Name}: {e.Message}");
                    }

                    break;
            }
        }
    }

    private void Dispatch(ReadOnlySpan<byte> line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line, documentOptions: FrameParser.DocumentOptions);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            Bad($"not valid JSON: {e.Message}");
            return;
        }

        if (node is not JsonObject root)
        {
            Bad("a line is not a JSON object");
            return;
        }

        if (!FrameParser.TryString(root["op"], out string? op))
        {
            Bad("a message has no op");
            return;
        }

        switch (op)
        {
            case LiveProtocol.OpHello:
                HandleHello(root);
                break;
            case LiveProtocol.OpFrame:
                HandleFrame(root);
                break;
            case LiveProtocol.OpResult:
                HandleResult(root);
                break;
            case LiveProtocol.OpError:
                HandleError(root);
                break;
            case LiveProtocol.OpPacket or LiveProtocol.OpTapEnd or LiveProtocol.OpConsoleLine:
                if (!_helloDone)
                {
                    Bad($"a {op} arrived before the hello exchange finished");
                    break;
                }

                Tools.Accept(op, root);
                break;
            default:
                Stats.UnknownOp(op);
                Warn($"unknown op '{op}' ignored");
                break;
        }
    }

    private void HandleHello(JsonObject root)
    {
        if (_helloDone)
        {
            Bad("a second hello arrived");
            return;
        }

        if (!FrameParser.TryParseHello(root, out HelloInfo? hello, out string? problem))
        {
            _hello.TrySetException(new CommandException(CommandException.Failed, $"the server's hello is malformed: {problem}."));
            return;
        }

        if (hello.Protocol != LiveProtocol.Version)
        {
            _hello.TrySetException(new CommandException(
                CommandException.Failed,
                string.Create(CultureInfo.InvariantCulture, $"protocol mismatch: the server speaks map-feed protocol {hello.Protocol}, this Atlas speaks {LiveProtocol.Version}; update the older side.")));
            return;
        }

        if (_expectedPid is { } expected)
        {
            // ref: D:\ArcaneCore-lanes\_wf\ATLAS-2026-10-18.md:214 (the hello reply carries "pid"), so a reply without it cannot be checked.
            if (hello.Pid is not { } actual)
            {
                _hello.TrySetException(new CommandException(
                    CommandException.Failed,
                    string.Create(CultureInfo.InvariantCulture, $"pipe '{PipeName}' answered hello without its process id, so it cannot be confirmed to belong to process {expected}; refusing to use it.")));
                return;
            }

            if (expected != actual)
            {
                _hello.TrySetException(new CommandException(
                    CommandException.Failed,
                    string.Create(CultureInfo.InvariantCulture, $"pipe '{PipeName}' is served by process {actual}, not by process {expected}; refusing to use it.")));
                return;
            }
        }

        if (hello.MapsRejected > 0)
        {
            Stats.ItemsRejected(hello.MapsRejected, hello.FirstMapProblem);
            Warn(string.Create(CultureInfo.InvariantCulture, $"the hello dropped {hello.MapsRejected} malformed map entr{(hello.MapsRejected == 1 ? "y" : "ies")}; first: {hello.FirstMapProblem}"));
        }

        // rev is read here rather than in FrameParser so the shared hello record stays untouched.
        FeedRevision = root["rev"] is JsonValue revNode && revNode.TryGetValue(out int rev) && rev is >= 1 and <= 1000 ? rev : 1;
        _helloDone = true;
        _hello.TrySetResult(hello);
    }

    private void HandleFrame(JsonObject root)
    {
        if (!_helloDone)
        {
            Bad("a frame arrived before the hello exchange finished");
            return;
        }

        if (!FrameParser.TryParseFrame(root, Stopwatch.GetTimestamp(), out ParsedFrame? parsed, out string? problem))
        {
            Bad($"frame refused: {problem}");
            return;
        }

        Stats.FrameAccepted(parsed.Rejected, parsed.FirstProblem);
        if (parsed.Rejected > 0)
        {
            Warn(string.Create(CultureInfo.InvariantCulture, $"a frame for map {parsed.Frame.Map} dropped {parsed.Rejected} item(s); first: {parsed.FirstProblem}"));
        }

        _sink.Frame(this, parsed);
    }

    private void HandleResult(JsonObject root)
    {
        if (!FrameParser.TryInteger(root["id"], 0, long.MaxValue, out long id))
        {
            Bad("a result has no id");
            return;
        }

        if (_pending.TryRemove(id, out TaskCompletionSource<JsonObject>? waiter))
        {
            waiter.TrySetResult(root);
        }
        else
        {
            Stats.LateResult();
        }
    }

    private void HandleError(JsonObject root)
    {
        string code = FrameParser.TryString(root["code"], out string? c) ? c : "error";
        string message = FrameParser.TryString(root["message"], out string? m) ? m : "(no message)";
        string text = $"{code}: {message}";
        Stats.ServerError(text);
        if (FrameParser.TryInteger(root["id"], 0, long.MaxValue, out long id) && _pending.TryRemove(id, out TaskCompletionSource<JsonObject>? waiter))
        {
            waiter.TrySetException(new CommandException(CommandException.Failed, $"the server refused the query: {text}"));
        }
        else if (!_helloDone)
        {
            _hello.TrySetException(new CommandException(CommandException.Failed, $"the server answered hello with an error: {text}"));
        }
        else
        {
            Warn($"the server reported an error: {text}");
        }
    }

    private void FailWaiters(string reason)
    {
        var hello = new CommandException(CommandException.Unavailable, $"the server closed the connection before answering hello ({reason}).");
        if (_hello.TrySetException(hello))
        {
            // Nobody may be awaiting hello (the close can come before the handshake waits); reading Exception marks it observed.
            _ = _hello.Task.Exception;
        }

        foreach (long id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<JsonObject>? waiter))
            {
                waiter.TrySetException(new CommandException(CommandException.Unavailable, $"the map feed connection closed before the query was answered ({reason})."));
            }
        }
    }

    private void Bad(string problem)
    {
        Stats.BadMessage(problem);
        Warn(problem);
    }

    /// <summary>Warning for the first few occurrences and every thousandth after, so a flooding peer cannot flood the log (refusals are Warning, never Error).</summary>
    private void Warn(string detail)
    {
        long seen = Stats.BadMessages + Stats.BadItems + Stats.OversizeLines + Stats.UnknownOps + Stats.ServerErrors;
        if (seen <= 5 || seen % 1000 == 0)
        {
            _log.LogWarning("map feed: {Detail}", detail);
        }
        else
        {
            _log.LogDebug("map feed: {Detail}", detail);
        }
    }
}

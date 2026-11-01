using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Control;

/// <summary>The gateway link to the worker ended (or was never up) while a control request was waiting for its reply. The verb may or may not have run on the worker.</summary>
public sealed class ControlLinkLostException(string message) : IOException(message);

/// <summary>A control request got no reply within its verb's timeout. The verb may still be running on the worker; the journal tells what it did.</summary>
public sealed class ControlTimeoutException(ControlVerb verb, Guid operationId, TimeSpan timeout)
    : TimeoutException($"control verb {verb} of operation {operationId} got no reply within {timeout.TotalSeconds:0.###} s")
{
    public ControlVerb Verb { get; } = verb;

    public Guid OperationId { get; } = operationId;
}

/// <summary>The worker answered a control request with anything but <see cref="ControlStatus.Ok"/>: it refused the verb, the verb failed, or the worker's opt-in key is off.</summary>
public sealed class ControlRefusedException(ControlReply reply)
    : InvalidOperationException($"control verb {reply.Verb} of operation {reply.OperationId} was answered {reply.Status}: {reply.Reason}")
{
    public ControlReply Reply { get; } = reply;
}

/// <summary>
/// How long the gateway waits for the reply to each control verb (design _wf/handoff-control-DESIGN.md section 2.4; configurable under <c>Gateway:Control:*</c> by the
/// hosting that builds the channel). The defaults are the design's table; tests shorten them.
/// </summary>
public sealed record LinkControlTimeouts
{
    /// <summary>Describe, Warm, StopAdmissions, Resume, ReleaseWriterLock, Retire, TargetDescribe, Reserve, Install, Discard, ResumeSource, RetireSource.</summary>
    public TimeSpan Default { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Freeze and Suspend: the world thread plus a durable flush.</summary>
    public TimeSpan Freeze { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Flush { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan Activate { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan Promote { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The timeout of <paramref name="verb"/>.</summary>
    public TimeSpan For(ControlVerb verb) => verb switch
    {
        ControlVerb.Freeze or ControlVerb.Suspend => Freeze,
        ControlVerb.Flush => Flush,
        ControlVerb.Activate => Activate,
        ControlVerb.Promote => Promote,
        _ => Default,
    };
}

/// <summary>
/// The gateway end of the unified control channel to one worker (design _wf/handoff-control-DESIGN.md section 2.4): sends <see cref="ControlRequest"/> frames on an
/// admitted <see cref="GatewaySessionLinkClient"/> and correlates the <see cref="ControlReply"/> frames by request id.
/// <list type="bullet">
/// <item><description>A request that gets no reply within its verb's timeout throws <see cref="ControlTimeoutException"/>.</description></item>
/// <item><description>When the link ends, every pending request fails at once with <see cref="ControlLinkLostException"/>, and so does every later one.</description></item>
/// <item><description>The channel never resends: the caller (the handoff or transfer coordinator) owns the retry rule (a mutating verb is not repeated before the point of no return).</description></item>
/// </list>
/// Owns the client's <see cref="GatewaySessionLinkClient.ControlReplyReceived"/> handler (one channel per client); it does not own the client, which the caller disposes.
/// Thread-safe: any number of requests may be pending; the reply handler runs on the link's receive loop and never blocks.
/// </summary>
public sealed class LinkControlChannel
{
    private readonly GatewaySessionLinkClient _client;
    private readonly LinkControlTimeouts _timeouts;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, TaskCompletionSource<ControlReply>> _pending = [];
    private long _nextRequestId;
    private bool _lost;

    public LinkControlChannel(GatewaySessionLinkClient client, LinkControlTimeouts? timeouts = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _timeouts = timeouts ?? new LinkControlTimeouts();
        _logger = logger ?? NullLogger.Instance;
        client.ControlReplyReceived = OnReply;
        _ = client.Ended.ContinueWith(
            static (_, state) => ((LinkControlChannel)state!).FailAll(), this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The worker endpoint this channel talks to (for messages).</summary>
    public string Worker => _client.WorkerEndPoint.ToString();

    /// <summary>True while the link is up.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return !_lost && _client.IsConnected;
            }
        }
    }

    /// <summary>
    /// Sends one request and waits for its reply, whatever its status. Throws <see cref="ControlLinkLostException"/> when the link is down or ends while waiting,
    /// <see cref="ControlTimeoutException"/> after the verb's timeout, <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires, and
    /// <see cref="InvalidDataException"/> for a reply that names another operation or verb than the request.
    /// </summary>
    public async Task<ControlReply> SendAsync(
        ControlKind kind, Guid operationId, ControlVerb verb, ControlArguments? arguments = null, CancellationToken cancellationToken = default)
    {
        TimeSpan timeout = _timeouts.For(verb);
        ulong requestId = (ulong)Interlocked.Increment(ref _nextRequestId);
        var waiting = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_lost)
            {
                throw new ControlLinkLostException($"the control link to worker {Worker} is down");
            }

            _pending[requestId] = waiting;
        }

        try
        {
            if (!_client.SendControlRequest(new ControlRequest(requestId, kind, operationId, verb, arguments ?? ControlNoArguments.Instance)))
            {
                throw new ControlLinkLostException($"the control link to worker {Worker} is down");
            }

            ControlReply reply;
            try
            {
                reply = await waiting.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new ControlTimeoutException(verb, operationId, timeout);
            }

            if (reply.Kind != kind || reply.OperationId != operationId || reply.Verb != verb)
            {
                throw new InvalidDataException($"the control reply {reply.RequestId} answers {reply.Verb} of operation {reply.OperationId}, not {verb} of operation {operationId}");
            }

            return reply;
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(requestId);
            }
        }
    }

    /// <summary><see cref="SendAsync"/>, and any status but <see cref="ControlStatus.Ok"/> throws <see cref="ControlRefusedException"/>.</summary>
    public async Task<ControlReply> CallAsync(
        ControlKind kind, Guid operationId, ControlVerb verb, ControlArguments? arguments = null, CancellationToken cancellationToken = default)
    {
        ControlReply reply = await SendAsync(kind, operationId, verb, arguments, cancellationToken).ConfigureAwait(false);
        return reply.Status == ControlStatus.Ok ? reply : throw new ControlRefusedException(reply);
    }

    private void OnReply(ControlReply reply)
    {
        TaskCompletionSource<ControlReply>? waiting;
        lock (_gate)
        {
            _pending.TryGetValue(reply.RequestId, out waiting);
        }

        if (waiting is null)
        {
            // Late (the caller timed out or was cancelled) or unknown: nothing waits for it.
            _logger.LogDebug("Control reply {RequestId} ({Verb}) from {Worker} has no waiting request", reply.RequestId, reply.Verb, Worker);
            return;
        }

        waiting.TrySetResult(reply);
    }

    private void FailAll()
    {
        TaskCompletionSource<ControlReply>[] waiting;
        lock (_gate)
        {
            _lost = true;
            waiting = [.. _pending.Values];
        }

        foreach (TaskCompletionSource<ControlReply> request in waiting)
        {
            request.TrySetException(new ControlLinkLostException($"the control link to worker {Worker} ended"));
        }
    }
}

// Ported from Krilliac/Sonder-Inference src/sessions/session.cpp @1c4c152 (Apache-2.0) (lifecycle only)
// Ported from Krilliac/Sonder-Inference src/engine/request_runtime.hpp @1c4c152 (Apache-2.0) (request summary only)
//
// Generation session lifecycle for one bot conversation:
// - state machine Idle -> Running -> Idle, and Closed (terminal);
// - at most one request in flight per session (a second Begin is refused, not queued);
// - per-request cancellation through a CancellationToken linked to the caller's token, so either
//   the caller, Session.Cancel or Session.Close stops it;
// - request telemetry: started/completed/cancelled/failed counters, queue time, TTFT, token count,
//   cancel latency, preemptions and reused prompt tokens.
//
// The source's backend call, sampler and telemetry bus are out of scope: the in-process engine
// drives tokens and reports them through InferenceRequest. Per-token methods only bump counters
// and read the monotonic clock; nothing allocates per token.

using System.Diagnostics;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Sessions;

public enum InferenceSessionState : byte
{
    Idle,
    Running,
    Closed,
}

public enum RequestOutcome : byte
{
    None,
    Completed,
    Cancelled,
    Failed,
}

public enum BeginRequestError : byte
{
    None,
    SessionClosed,
    RequestInFlight,
}

/// <summary>Per-request telemetry handed back when a request ends.</summary>
public readonly record struct RequestSummary(
    ulong RequestId,
    RequestOutcome Outcome,
    double QueueMs,
    double TtftMs,
    double TotalMs,
    uint GeneratedTokens,
    uint Preemptions,
    ulong ReusedPromptTokens,
    double CancelLatencyMs);

/// <summary>
/// Request counters shared across sessions (thread-safe, lock-free). One instance per engine is
/// the expected use; it is also the request id source so ids stay unique across sessions.
/// </summary>
public sealed class InferenceRequestCounters
{
    private long _nextRequestId;
    private long _started;
    private long _completed;
    private long _cancelled;
    private long _failed;
    private long _rejectedBusy;
    private long _rejectedClosed;
    private long _generatedTokens;
    private long _reusedPromptTokens;
    private long _preemptions;

    public long Started => Interlocked.Read(ref _started);

    public long Completed => Interlocked.Read(ref _completed);

    public long Cancelled => Interlocked.Read(ref _cancelled);

    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Begin refused because the session already had a request in flight.</summary>
    public long RejectedBusy => Interlocked.Read(ref _rejectedBusy);

    /// <summary>Begin refused because the session was closed.</summary>
    public long RejectedClosed => Interlocked.Read(ref _rejectedClosed);

    public long GeneratedTokens => Interlocked.Read(ref _generatedTokens);

    public long ReusedPromptTokens => Interlocked.Read(ref _reusedPromptTokens);

    public long Preemptions => Interlocked.Read(ref _preemptions);

    /// <summary>Requests started and not yet ended.</summary>
    public long InFlight => Started - Completed - Cancelled - Failed;

    internal ulong NextRequestId() => (ulong)Interlocked.Increment(ref _nextRequestId);

    internal void OnStarted() => Interlocked.Increment(ref _started);

    internal void OnRejected(BeginRequestError why)
    {
        if (why == BeginRequestError.RequestInFlight)
        {
            Interlocked.Increment(ref _rejectedBusy);
        }
        else
        {
            Interlocked.Increment(ref _rejectedClosed);
        }
    }

    internal void OnEnded(in RequestSummary summary)
    {
        switch (summary.Outcome)
        {
            case RequestOutcome.Completed: Interlocked.Increment(ref _completed); break;
            case RequestOutcome.Cancelled: Interlocked.Increment(ref _cancelled); break;
            default: Interlocked.Increment(ref _failed); break;
        }

        Interlocked.Add(ref _generatedTokens, summary.GeneratedTokens);
        Interlocked.Add(ref _reusedPromptTokens, (long)summary.ReusedPromptTokens);
        Interlocked.Add(ref _preemptions, summary.Preemptions);
    }
}

/// <summary>One bot conversation's generation session.</summary>
public sealed class InferenceSession : IDisposable
{
    private readonly Lock _lock = new();
    private readonly InferenceRequestCounters _counters;
    private InferenceSessionState _state = InferenceSessionState.Idle;
    private InferenceRequest? _active;
    private RequestOutcome _lastOutcome = RequestOutcome.None;
    private ulong _requestsStarted;

    public InferenceSession(string sessionId, WorkloadClass workload, InferenceRequestCounters? counters = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        SessionId = sessionId;
        Workload = workload;
        _counters = counters ?? new InferenceRequestCounters();
    }

    public string SessionId { get; }

    public WorkloadClass Workload { get; }

    public InferenceRequestCounters Counters => _counters;

    public InferenceSessionState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public RequestOutcome LastOutcome
    {
        get
        {
            lock (_lock)
            {
                return _lastOutcome;
            }
        }
    }

    public ulong RequestsStarted
    {
        get
        {
            lock (_lock)
            {
                return _requestsStarted;
            }
        }
    }

    /// <summary>
    /// Start a request. Refused (no request created) when the session is closed or already has a
    /// request in flight. The returned request's <see cref="InferenceRequest.Token"/> trips when the
    /// caller's token, <see cref="Cancel"/> or <see cref="Close"/> fires.
    /// </summary>
    public BeginRequestError TryBegin(CancellationToken callerToken, out InferenceRequest? request)
    {
        request = null;
        BeginRequestError error;
        lock (_lock)
        {
            if (_state == InferenceSessionState.Closed)
            {
                error = BeginRequestError.SessionClosed;
            }
            else if (_state == InferenceSessionState.Running)
            {
                error = BeginRequestError.RequestInFlight;
            }
            else
            {
                CancellationTokenSource cts = callerToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(callerToken)
                    : new CancellationTokenSource();
                request = new InferenceRequest(this, _counters.NextRequestId(), cts);
                _active = request;
                _state = InferenceSessionState.Running;
                _requestsStarted++;
                error = BeginRequestError.None;
            }
        }

        if (error == BeginRequestError.None)
        {
            _counters.OnStarted();
        }
        else
        {
            _counters.OnRejected(error);
        }

        return error;
    }

    /// <summary>Cancel the in-flight request, if any. The session stays usable.</summary>
    public void Cancel()
    {
        InferenceRequest? active;
        lock (_lock)
        {
            active = _active;
        }

        active?.RequestCancel();
    }

    /// <summary>Close the session: cancels the in-flight request and refuses new ones. Idempotent.</summary>
    public void Close()
    {
        InferenceRequest? active;
        lock (_lock)
        {
            if (_state == InferenceSessionState.Closed)
            {
                return;
            }

            active = _active;
            _state = InferenceSessionState.Closed;
        }

        active?.RequestCancel();
    }

    public void Dispose() => Close();

    internal void OnRequestEnded(InferenceRequest request, in RequestSummary summary)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_active, request))
            {
                _active = null;
            }

            _lastOutcome = summary.Outcome;
            if (_state == InferenceSessionState.Running)
            {
                _state = InferenceSessionState.Idle;
            }
        }

        _counters.OnEnded(summary);
    }
}

/// <summary>
/// Handle for the single in-flight request of an <see cref="InferenceSession"/>. The engine reports
/// progress on it and ends it exactly once with <see cref="Complete"/>; disposing an unfinished
/// request ends it as cancelled (if cancellation was requested) or failed.
/// </summary>
public sealed class InferenceRequest : IDisposable
{
    private readonly InferenceSession _session;
    private readonly CancellationTokenSource _cts;
    private readonly long _startTs;
    private long _admittedTs;
    private long _firstTokenTs;
    private long _cancelRequestedTs;
    private uint _generated;
    private uint _preemptions;
    private ulong _reusedPromptTokens;
    private int _ended;

    internal InferenceRequest(InferenceSession session, ulong requestId, CancellationTokenSource cts)
    {
        _session = session;
        _cts = cts;
        RequestId = requestId;
        _startTs = Stopwatch.GetTimestamp();
    }

    /// <summary>Unique id; use it as the scheduler request id and the KV cache sequence id.</summary>
    public ulong RequestId { get; }

    public InferenceSession Session => _session;

    public CancellationToken Token => _cts.Token;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    public bool IsEnded => Volatile.Read(ref _ended) != 0;

    public uint GeneratedTokens => _generated;

    /// <summary>The scheduler admitted the request (end of its queue wait).</summary>
    public void MarkAdmitted(ulong reusedPromptTokens = 0)
    {
        if (_admittedTs == 0)
        {
            _admittedTs = Stopwatch.GetTimestamp();
        }

        _reusedPromptTokens = reusedPromptTokens;
    }

    /// <summary>One token was produced. The first call fixes the TTFT.</summary>
    public void MarkToken()
    {
        if (_firstTokenTs == 0)
        {
            _firstTokenTs = Stopwatch.GetTimestamp();
        }

        _generated++;
    }

    /// <summary>The scheduler preempted the request (it will be requeued or fail).</summary>
    public void MarkPreempted() => _preemptions++;

    internal void RequestCancel()
    {
        Interlocked.CompareExchange(ref _cancelRequestedTs, Stopwatch.GetTimestamp(), 0);
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ended concurrently; nothing left to cancel.
        }
    }

    /// <summary>
    /// End the request and return its telemetry. Only the first call has effect; later calls
    /// return null. A request whose token was cancelled reports <see cref="RequestOutcome.Cancelled"/>
    /// unless it completed.
    /// </summary>
    public RequestSummary? Complete(RequestOutcome outcome)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return null;
        }

        if (outcome == RequestOutcome.None)
        {
            outcome = _cts.IsCancellationRequested ? RequestOutcome.Cancelled : RequestOutcome.Failed;
        }

        long now = Stopwatch.GetTimestamp();
        long cancelTs = Interlocked.Read(ref _cancelRequestedTs);
        var summary = new RequestSummary(
            RequestId,
            outcome,
            _admittedTs == 0 ? 0.0 : Stopwatch.GetElapsedTime(_startTs, _admittedTs).TotalMilliseconds,
            _firstTokenTs == 0 ? -1.0 : Stopwatch.GetElapsedTime(_startTs, _firstTokenTs).TotalMilliseconds,
            Stopwatch.GetElapsedTime(_startTs, now).TotalMilliseconds,
            _generated,
            _preemptions,
            _reusedPromptTokens,
            outcome == RequestOutcome.Cancelled && cancelTs != 0 && now >= cancelTs
                ? Stopwatch.GetElapsedTime(cancelTs, now).TotalMilliseconds
                : 0.0);
        _session.OnRequestEnded(this, summary);
        _cts.Dispose();
        return summary;
    }

    public void Dispose() => Complete(RequestOutcome.None);
}

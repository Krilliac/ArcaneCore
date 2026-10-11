using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>
/// An in-process <see cref="EventListener"/> on the runtime's <c>Microsoft-Windows-DotNETRuntime</c> event source with the GC keyword
/// (0x1) at Verbose, feeding every <c>GCAllocationTick</c> event (about one per 100 KB allocated) into an <see cref="AllocationAggregator"/>.
/// <para>
/// Threading: <see cref="OnEventWritten"/> runs on a runtime thread; it reads the payload, takes the aggregator's short lock and returns.
/// <see cref="Dispose"/> disables the event source first and then disposes the base listener, so no callback reaches the aggregator after it
/// returns (an in-flight one is ignored through <c>_disposed</c>). The base constructor calls <see cref="OnEventSourceCreated"/> before this
/// class's constructor body runs, which is why the source is parked in a field and enabled once construction has finished.
/// </para>
/// </summary>
internal sealed class AllocationTickListener : EventListener
{
    internal const string RuntimeSourceName = "Microsoft-Windows-DotNETRuntime";
    internal const EventKeywords GcKeyword = (EventKeywords)0x1;
    internal const int AllocationTickEventId = 10;

    // Field initialisers run before the base constructor, so _gate exists when OnEventSourceCreated is first called.
    private readonly object _gate = new();
    private readonly AllocationAggregator _sink;
    private EventSource? _runtimeSource;
    private bool _constructed;
    private volatile bool _disposed;

    internal AllocationTickListener(AllocationAggregator sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        EventSource? source;
        lock (_gate)
        {
            _constructed = true;
            source = _runtimeSource;
        }

        if (source is not null)
        {
            EnableEvents(source, EventLevel.Verbose, GcKeyword);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (!string.Equals(eventSource.Name, RuntimeSourceName, StringComparison.Ordinal))
        {
            return;
        }

        bool enableNow;
        lock (_gate)
        {
            _runtimeSource = eventSource;
            enableNow = _constructed && !_disposed;
        }

        if (enableNow)
        {
            EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_disposed || !IsAllocationTick(eventData))
        {
            return;
        }

        Record(_sink, eventData.PayloadNames, eventData.Payload);
    }

    internal static bool IsAllocationTick(EventWrittenEventArgs eventData)
    {
        string? name = eventData.EventName;
        return name is null
            ? eventData.EventId == AllocationTickEventId
            : name.StartsWith("GCAllocationTick", StringComparison.Ordinal);
    }

    /// <summary>
    /// Feed one GCAllocationTick payload to <paramref name="sink"/>. Fields are found by name (<c>AllocationAmount64</c>, else the 32-bit
    /// <c>AllocationAmount</c>; <c>AllocationKind</c>; <c>TypeName</c>), so an event version that moves or drops one is still read or counted as
    /// unreadable, never mis-attributed. A missing type name files the bytes under the unknown type.
    /// </summary>
    internal static void Record(AllocationAggregator sink, ReadOnlyCollection<string>? names, ReadOnlyCollection<object?>? payload)
    {
        if (names is null || payload is null)
        {
            sink.AddMalformed();
            return;
        }

        long amount = ToLong(Find(names, payload, "AllocationAmount64"));
        if (amount < 0)
        {
            amount = ToLong(Find(names, payload, "AllocationAmount"));
        }

        long kind = ToLong(Find(names, payload, "AllocationKind"));
        if (amount < 0 || kind < 0)
        {
            sink.AddMalformed();
            return;
        }

        sink.Add(Find(names, payload, "TypeName") as string, (int)Math.Min(kind, int.MaxValue), amount);
    }

    private static object? Find(ReadOnlyCollection<string> names, ReadOnlyCollection<object?> payload, string name)
    {
        int count = Math.Min(names.Count, payload.Count);
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return payload[i];
            }
        }

        return null;
    }

    private static long ToLong(object? value) => value switch
    {
        ulong u => u > long.MaxValue ? long.MaxValue : (long)u,
        long l => l,
        uint u => u,
        int i => i,
        ushort u => u,
        short s => s,
        byte b => b,
        _ => -1,
    };

    public override void Dispose()
    {
        EventSource? source;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            source = _runtimeSource;
            _runtimeSource = null;
        }

        if (source is not null)
        {
            DisableEvents(source);
        }

        base.Dispose();
    }
}

/// <summary>An allocation sample as of one instant: the aggregated rows, how long it ran, and whether it is still running.</summary>
public sealed record AllocationReportData(AllocationSnapshot Snapshot, TimeSpan Elapsed, bool Running, TimeSpan Planned);

/// <summary>
/// The sampling allocation profiler behind <c>.debug alloc start|report|stop</c> (docs/ops/diagnostics.md). At most one session runs at a time;
/// it stops itself after the requested seconds and keeps its result until the next session replaces it.
/// <para>
/// Ownership: the profiler owns the running session's listener and timer and disposes both when the session ends or the profiler is disposed.
/// Threading: every member may be called from any thread (the commands call from the world thread, the auto-stop from a timer thread); state
/// changes are under one lock that no runtime event callback ever takes.
/// </para>
/// </summary>
public sealed class AllocationProfiler : IDisposable
{
    public const int DefaultSeconds = 30;
    public const int MinSeconds = 1;
    public const int MaxSeconds = 600;
    public const int DefaultTop = 15;
    public const int MaxTop = 100;

    private sealed class Session(Guid id, AllocationAggregator aggregator, IDisposable listener, TimeSpan planned)
    {
        public Guid Id { get; } = id;
        public AllocationAggregator Aggregator { get; } = aggregator;
        public IDisposable Listener { get; } = listener;
        public TimeSpan Planned { get; } = planned;
        public long StartTimestamp { get; } = Stopwatch.GetTimestamp();
        public Timer? Timer { get; set; }
    }

    private readonly object _gate = new();
    private readonly Func<AllocationAggregator, IDisposable> _listenerFactory;
    private readonly ILogger? _logger;
    private readonly int _maxTypes;
    private Session? _active;
    private AllocationReportData? _last;
    private bool _disposed;

    /// <param name="listenerFactory">Creates the event source subscription for an aggregator; null uses the runtime listener. Tests substitute a fake.</param>
    /// <param name="logger">Receives the start and stop lines; may be null.</param>
    /// <param name="maxTypes">The aggregator's row cap.</param>
    public AllocationProfiler(Func<AllocationAggregator, IDisposable>? listenerFactory = null, ILogger? logger = null, int maxTypes = AllocationAggregator.DefaultMaxTypes)
    {
        _listenerFactory = listenerFactory ?? (aggregator => new AllocationTickListener(aggregator));
        _logger = logger;
        _maxTypes = maxTypes;
    }

    /// <summary>True while a session is sampling.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    /// <summary>Start a session of <paramref name="seconds"/>. Refuses (false, with the reason) when one is already running.</summary>
    public bool TryStart(int seconds, out string message)
    {
        if (seconds is < MinSeconds or > MaxSeconds)
        {
            message = string.Create(CultureInfo.InvariantCulture, $"seconds must be {MinSeconds}-{MaxSeconds}.");
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                message = "The allocation profiler is shutting down.";
                return false;
            }

            if (_active is { } running)
            {
                message = string.Create(CultureInfo.InvariantCulture,
                    $"A sample is already running ({Stopwatch.GetElapsedTime(running.StartTimestamp).TotalSeconds:F0} of {running.Planned.TotalSeconds:F0} s). Use .debug alloc stop first.");
                return false;
            }

            var aggregator = new AllocationAggregator(_maxTypes);
            IDisposable listener;
            try
            {
                listener = _listenerFactory(aggregator);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.LogError(ex, "allocation sampling could not subscribe to the runtime event source");
                message = "The runtime event source could not be enabled: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            var session = new Session(Guid.NewGuid(), aggregator, listener, TimeSpan.FromSeconds(seconds));
            Guid id = session.Id;
            session.Timer = new Timer(_ => StopIfCurrent(id), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
            _active = session;
            _last = null;
            _logger?.LogInformation("allocation sampling started for {Seconds} s", seconds);
            message = string.Create(CultureInfo.InvariantCulture,
                $"Allocation sampling started for {seconds} s (GCAllocationTick, about one event per 100 KB). Use .debug alloc report [top] while it runs or after it stops, .debug alloc stop to end it early.");
            return true;
        }
    }

    /// <summary>Stop the running session. False (nothing to stop) when none runs.</summary>
    public bool Stop()
    {
        lock (_gate)
        {
            if (_active is null)
            {
                return false;
            }

            Finish(_active);
            return true;
        }
    }

    /// <summary>The running session's current totals, else the last finished one; null when none has run.</summary>
    public AllocationReportData? Report()
    {
        lock (_gate)
        {
            if (_active is { } running)
            {
                return new AllocationReportData(running.Aggregator.Snapshot(), Stopwatch.GetElapsedTime(running.StartTimestamp), true, running.Planned);
            }

            return _last;
        }
    }

    // Caller holds _gate.
    private void Finish(Session session)
    {
        _active = null;
        session.Timer?.Dispose();
        session.Listener.Dispose();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(session.StartTimestamp);
        _last = new AllocationReportData(session.Aggregator.Snapshot(), elapsed, false, session.Planned);
        _logger?.LogInformation("allocation sampling stopped after {Seconds:F1} s: {Events} events, {Rows} rows", elapsed.TotalSeconds, _last.Snapshot.TotalEvents, _last.Snapshot.DistinctTypes);
    }

    internal void StopIfCurrent(Guid sessionId)
    {
        lock (_gate)
        {
            if (_active is { } session && session.Id == sessionId)
            {
                Finish(session);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_active is { } session)
            {
                Finish(session);
            }
        }
    }
}

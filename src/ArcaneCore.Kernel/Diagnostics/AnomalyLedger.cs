using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>How bad an <see cref="AnomalyLedger"/> report is; it picks the log level of the throttled line.</summary>
public enum AnomalySeverity
{
    /// <summary>Expected now and then (a bot gave up one route); worth a count, logged at Information.</summary>
    Info = 0,

    /// <summary>Something did not do what it should (a bot never boarded the car it waited for); logged at Warning.</summary>
    Warning = 1,

    /// <summary>State the server should never reach (a unit below the world, a position that is not finite); logged at Error.</summary>
    Error = 2,
}

/// <summary>
/// The process-wide ledger of <em>outcome</em> failures: checks that found the world in a state it should not be in, with no
/// exception thrown (a bot that waited at a lift and never boarded, a ride that ended at the wrong level, a unit that moved through
/// a closed door, a write queue that dropped a row after its retries). <see cref="FaultLedger"/> counts the exceptions the server
/// caught; this ledger counts the silent failures, so "the bot just turned back" becomes a counted, logged, inspectable line.
/// <para>
/// Key: <c>subsystem/code</c>, both normalised (<see cref="Normalise"/>: trimmed, lower case, spaces to '-', at most
/// <see cref="MaxNameLength"/> characters), e.g. <c>bot-elevator/board-missed</c>. A <paramref name="subject"/> passed to
/// <see cref="Report"/> is the stable identity of what failed (<c>bot Thrall (guid 1234)</c>, <c>creature 3100 spawn 812</c>): the
/// ledger counts distinct subjects per key (up to <see cref="MaxTrackedSubjects"/>), so positions, timings and other changing context
/// belong in <c>detail</c>, never in the subject.
/// </para>
/// <para>
/// Bounds and throttling: at most <see cref="AnomalyLedgerLimits.MaxKeys"/> keys; a report beyond that is counted in the single
/// <see cref="OverflowKey"/> bucket, never dropped. Each key logs its first <see cref="AnomalyLedgerLimits.LogLimit"/> reports, then at
/// most one line a minute carrying how many were held back. Subject and detail are cut to <see cref="MaxTextLength"/> characters.
/// </para>
/// <para>
/// Threads: callable from any thread. Lookups are lock-free, a new key takes one gate, a report takes its key's own lock for a few
/// assignments. The log line, the <see cref="AnomalyNoticed"/> event and the <see cref="Reported"/> event run on the reporting thread;
/// a handler or logger that throws is ignored (a report must never become a fault of its own).
/// </para>
/// </summary>
public sealed class AnomalyLedger
{
    /// <summary>The key of the bucket that counts reports beyond the key cap.</summary>
    public const string OverflowKey = "overflow/overflow";

    /// <summary>Longest subject or detail kept (characters).</summary>
    public const int MaxTextLength = 300;

    /// <summary>Longest subsystem or code name kept (characters).</summary>
    public const int MaxNameLength = 64;

    /// <summary>Distinct subjects remembered per key (<see cref="AnomalySnapshot.DistinctSubjects"/> saturates here).</summary>
    public const int MaxTrackedSubjects = 256;

    /// <summary>The event id of every anomaly log line (category <c>ArcaneCore.Diagnostics.Anomaly</c>).</summary>
    public static readonly EventId LogEventId = new(7201, "Anomaly");

    private readonly ConcurrentDictionary<string, AnomalyEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _addGate = new();
    private readonly TimeProvider _time;
    private readonly AnomalyEntry _overflow;
    private volatile int _maxKeys;
    private volatile int _logLimit;
    private volatile ILogger? _logger;
    private long _total;

    /// <summary>A ledger with the default bounds on the system clock and no logger (the host attaches one, <see cref="Configure"/>).</summary>
    public AnomalyLedger()
        : this(new AnomalyLedgerLimits(), TimeProvider.System)
    {
    }

    /// <summary>A ledger with explicit bounds and clock (tests).</summary>
    public AnomalyLedger(AnomalyLedgerLimits limits, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _maxKeys = Math.Max(1, limits.MaxKeys);
        _logLimit = Math.Max(0, limits.LogLimit);
        _overflow = new AnomalyEntry(OverflowKey, "overflow", "overflow");
    }

    /// <summary>The process-wide ledger.</summary>
    public static AnomalyLedger Shared { get; } = new();

    /// <summary>
    /// Raised for each report the ledger logs (the same throttle as the log line: a storm raises it rarely). Handlers must be quick;
    /// one that throws is ignored.
    /// </summary>
    public event Action<AnomalyNotice>? AnomalyNoticed;

    /// <summary>
    /// Raised for <em>every</em> report, throttled or not (tests and captures, <see cref="Capture"/>). Free when nobody listens.
    /// Handlers must be quick; one that throws is ignored.
    /// </summary>
    public event Action<AnomalyNotice>? Reported;

    /// <summary>Reports since start or the last <see cref="Reset"/> (every occurrence, logged or not).</summary>
    public long Total => Volatile.Read(ref _total);

    /// <summary>Distinct keys held (the overflow bucket is not counted).</summary>
    public int Distinct => _entries.Count;

    /// <summary>
    /// Attach the logger the throttled lines go to and the per-key log limit (the diagnostics host calls this at start with
    /// <c>Diagnostics:FaultLogLimit</c>; safe to call while reporting). A null logger keeps counting and raising events without logging.
    /// </summary>
    public void Configure(ILogger? logger, int logLimit = AnomalyLedgerLimits.DefaultLogLimit, int? maxKeys = null)
    {
        _logger = logger;
        _logLimit = Math.Max(0, logLimit);
        if (maxKeys is { } keys) _maxKeys = Math.Max(1, keys);
    }

    /// <summary>
    /// Count one failed check under <c>subsystem/code</c> and log it unless throttled. <paramref name="subject"/>: the stable identity
    /// of what failed (no positions or times); <paramref name="detail"/>: what was expected, what was found, and where.
    /// Never throws except <see cref="OutOfMemoryException"/>.
    /// </summary>
    public AnomalyRecording Report(string subsystem, string code, string subject, string detail, AnomalySeverity severity = AnomalySeverity.Warning)
    {
        try
        {
            return ReportCore(subsystem, code, subject, detail, severity);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new AnomalyRecording(OverflowKey, 0, false, 0);
        }
    }

    /// <summary>
    /// <see cref="Report"/> when <paramref name="condition"/> is false; returns <paramref name="condition"/>. The detail factory runs only
    /// on failure, so a check on a hot path costs a branch when it passes.
    /// </summary>
    public bool Expect(bool condition, string subsystem, string code, string subject, Func<string> detail, AnomalySeverity severity = AnomalySeverity.Warning)
    {
        if (condition) return true;
        string text;
        try
        {
            text = detail?.Invoke() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            text = "(detail threw " + ex.GetType().Name + ")";
        }

        Report(subsystem, code, subject, text, severity);
        return false;
    }

    /// <summary>Every key, most frequent first, followed by the overflow bucket when it holds anything.</summary>
    public IReadOnlyList<AnomalySnapshot> Snapshot()
    {
        var list = new List<AnomalySnapshot>(_entries.Count + 1);
        foreach (KeyValuePair<string, AnomalyEntry> pair in _entries)
        {
            AnomalySnapshot snap = pair.Value.ToSnapshot();
            if (snap.Count > 0) list.Add(snap);
        }

        list.Sort(static (a, b) =>
        {
            int byCount = b.Count.CompareTo(a.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
        });
        AnomalySnapshot overflow = _overflow.ToSnapshot();
        if (overflow.Count > 0) list.Add(overflow);
        return list;
    }

    /// <summary>The keys of one subsystem (normalised the way <see cref="Report"/> normalises it), most frequent first.</summary>
    public IReadOnlyList<AnomalySnapshot> Snapshot(string subsystem)
    {
        string wanted = Normalise(subsystem);
        return Snapshot().Where(s => string.Equals(s.Subsystem, wanted, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Per subsystem: how many keys and how many reports.</summary>
    public IReadOnlyDictionary<string, (int Codes, long Total)> BySubsystem()
    {
        var result = new SortedDictionary<string, (int Codes, long Total)>(StringComparer.Ordinal);
        foreach (AnomalySnapshot snap in Snapshot())
        {
            result[snap.Subsystem] = result.TryGetValue(snap.Subsystem, out (int Codes, long Total) agg)
                ? (agg.Codes + 1, agg.Total + snap.Count)
                : (1, snap.Count);
        }

        return result;
    }

    /// <summary>One key (<c>subsystem/code</c>, normalised) or null when it has no reports.</summary>
    public AnomalySnapshot? Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        string trimmed = key.Trim();
        if (string.Equals(trimmed, OverflowKey, StringComparison.Ordinal))
        {
            AnomalySnapshot overflow = _overflow.ToSnapshot();
            return overflow.Count > 0 ? overflow : null;
        }

        int slash = trimmed.IndexOf('/');
        string normal = slash > 0 ? KeyOf(trimmed[..slash], trimmed[(slash + 1)..]) : trimmed.ToLowerInvariant();
        return _entries.TryGetValue(normal, out AnomalyEntry? entry) && entry.ToSnapshot() is { Count: > 0 } snap ? snap : null;
    }

    /// <summary>How many reports <paramref name="key"/> has (0 when none).</summary>
    public long CountOf(string key) => Find(key)?.Count ?? 0;

    /// <summary>Forget every key and the total (the operator command, tests). Metrics counters are cumulative and keep their values.</summary>
    public void Reset()
    {
        lock (_addGate)
        {
            _entries.Clear();
            _overflow.Clear();
            Interlocked.Exchange(ref _total, 0);
        }
    }

    /// <summary>
    /// Collect every report matching <paramref name="filter"/> until the capture is disposed (tests: a reliable view of one scenario's
    /// reports in a ledger other tests share; filter on a subject that is unique to the scenario).
    /// </summary>
    public AnomalyCapture Capture(Func<AnomalyNotice, bool>? filter = null) => new(this, filter);

    /// <summary>The normalised <c>subsystem/code</c> key.</summary>
    public static string KeyOf(string subsystem, string code) => Normalise(subsystem) + "/" + Normalise(code);

    /// <summary>Trimmed, lower case, whitespace and '/' to '-', at most <see cref="MaxNameLength"/> characters; empty becomes <c>unknown</c>.</summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        ReadOnlySpan<char> span = name.AsSpan().Trim();
        var text = new StringBuilder(Math.Min(span.Length, MaxNameLength));
        foreach (char c in span)
        {
            if (text.Length >= MaxNameLength) break;
            text.Append(char.IsWhiteSpace(c) || c == '/' ? '-' : char.ToLowerInvariant(c));
        }

        return text.Length == 0 ? "unknown" : text.ToString();
    }

    internal static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= MaxTextLength ? text : string.Concat(text.AsSpan(0, MaxTextLength - 3), "...");
    }

    internal void AddReported(Action<AnomalyNotice> handler) => Reported += handler;

    internal void RemoveReported(Action<AnomalyNotice> handler) => Reported -= handler;

    private AnomalyRecording ReportCore(string subsystem, string code, string subject, string detail, AnomalySeverity severity)
    {
        string sub = Normalise(subsystem);
        string cod = Normalise(code);
        string key = sub + "/" + cod;
        string subj = Truncate(subject);
        string det = Truncate(detail);
        DateTimeOffset now = _time.GetUtcNow();
        Interlocked.Increment(ref _total);

        AnomalyEntry entry = Resolve(key, sub, cod);
        AnomalyRecording recording = entry.Note(now, _logLimit, severity, subj, det);
        AnomalyMeters.Count(sub, cod, severity);
        if (ReferenceEquals(entry, _overflow)) recording = recording with { Key = OverflowKey };

        Action<AnomalyNotice>? reported = Reported;
        Action<AnomalyNotice>? noticed = recording.ShouldLog ? AnomalyNoticed : null;
        if (reported is null && noticed is null && !recording.ShouldLog) return recording;

        var notice = new AnomalyNotice(key, sub, cod, severity, subj, det, recording.Count, recording.Suppressed);
        if (reported is not null) Raise(reported, notice);
        if (recording.ShouldLog)
        {
            Log(notice);
            if (noticed is not null) Raise(noticed, notice);
        }

        return recording;
    }

    private AnomalyEntry Resolve(string key, string subsystem, string code)
    {
        if (_entries.TryGetValue(key, out AnomalyEntry? existing)) return existing;
        lock (_addGate)
        {
            if (_entries.TryGetValue(key, out existing)) return existing;
            if (_entries.Count >= _maxKeys) return _overflow;
            var created = new AnomalyEntry(key, subsystem, code);
            _entries[key] = created;
            return created;
        }
    }

    private void Log(AnomalyNotice notice)
    {
        if (_logger is not { } logger) return;
        LogLevel level = notice.Severity switch
        {
            AnomalySeverity.Error => LogLevel.Error,
            AnomalySeverity.Info => LogLevel.Information,
            _ => LogLevel.Warning,
        };
        try
        {
            if (!logger.IsEnabled(level)) return;
            logger.Log(level, LogEventId,
                "Anomaly {AnomalyKey} {Subject}: {Detail} [count={Count} suppressed={Suppressed}]",
                notice.Key, notice.Subject, notice.Detail, notice.Count, notice.Suppressed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A logger that throws must not turn a report into a fault.
        }
    }

    private static void Raise(Action<AnomalyNotice> handlers, AnomalyNotice notice)
    {
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<AnomalyNotice>)handler)(notice);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Handlers must not throw; one that does is ignored.
            }
        }
    }

    private sealed class AnomalyEntry(string key, string subsystem, string code)
    {
        private readonly HashSet<string> _subjects = new(StringComparer.Ordinal);
        private DateTimeOffset _first;
        private DateTimeOffset _last;
        private DateTimeOffset _lastLog = DateTimeOffset.MinValue;
        private AnomalySeverity _worst;
        private string _subject = string.Empty;
        private string _detail = string.Empty;
        private long _count;
        private long _sinceLog;

        public AnomalyRecording Note(DateTimeOffset now, int logLimit, AnomalySeverity severity, string subject, string detail)
        {
            lock (this)
            {
                _count++;
                _last = now;
                if (_count == 1)
                {
                    _first = now;
                    _worst = severity;
                }
                else if (severity > _worst)
                {
                    _worst = severity;
                }

                _subject = subject;
                _detail = detail;
                if (subject.Length != 0 && _subjects.Count < MaxTrackedSubjects) _subjects.Add(subject);

                bool log;
                long suppressed = 0;
                if (_count <= logLimit)
                {
                    log = true;
                    _lastLog = now;
                }
                else if (_lastLog == DateTimeOffset.MinValue || now - _lastLog >= TimeSpan.FromMinutes(1))
                {
                    log = true;
                    suppressed = _sinceLog;
                    _sinceLog = 0;
                    _lastLog = now;
                }
                else
                {
                    log = false;
                    _sinceLog++;
                }

                return new AnomalyRecording(key, _count, log, suppressed);
            }
        }

        public void Clear()
        {
            lock (this)
            {
                _count = 0;
                _sinceLog = 0;
                _lastLog = DateTimeOffset.MinValue;
                _subjects.Clear();
                _subject = string.Empty;
                _detail = string.Empty;
                _worst = AnomalySeverity.Info;
            }
        }

        public AnomalySnapshot ToSnapshot()
        {
            lock (this)
            {
                return new AnomalySnapshot(key, subsystem, code, _worst, _count, _first, _last, _subject, _detail, _subjects.Count);
            }
        }
    }
}

/// <summary>Bounds of an <see cref="AnomalyLedger"/>.</summary>
public sealed record AnomalyLedgerLimits(int MaxKeys = AnomalyLedgerLimits.DefaultMaxKeys, int LogLimit = AnomalyLedgerLimits.DefaultLogLimit)
{
    /// <summary>Default key cap.</summary>
    public const int DefaultMaxKeys = 1024;

    /// <summary>Default per-key log lines before throttling to one a minute.</summary>
    public const int DefaultLogLimit = 10;
}

/// <summary>What <see cref="AnomalyLedger.Report"/> decided: the key, how often it has now occurred, whether it logged, and how many reports since the previous line were held back.</summary>
public readonly record struct AnomalyRecording(string Key, long Count, bool ShouldLog, long Suppressed);

/// <summary>One report as raised by <see cref="AnomalyLedger.Reported"/> and <see cref="AnomalyLedger.AnomalyNoticed"/>.</summary>
public sealed record AnomalyNotice(
    string Key,
    string Subsystem,
    string Code,
    AnomalySeverity Severity,
    string Subject,
    string Detail,
    long Count,
    long Suppressed);

/// <summary>A point-in-time copy of one ledger key (safe to hold and read from any thread).</summary>
/// <param name="Severity">The worst severity reported under this key.</param>
/// <param name="DistinctSubjects">Distinct subjects reported, saturating at <see cref="AnomalyLedger.MaxTrackedSubjects"/>.</param>
public sealed record AnomalySnapshot(
    string Key,
    string Subsystem,
    string Code,
    AnomalySeverity Severity,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string LastSubject,
    string LastDetail,
    int DistinctSubjects);

/// <summary>
/// Reports collected from an <see cref="AnomalyLedger"/> while the capture lives (<see cref="AnomalyLedger.Capture"/>). Thread-safe;
/// dispose it to stop collecting.
/// </summary>
public sealed class AnomalyCapture : IDisposable
{
    private readonly AnomalyLedger _ledger;
    private readonly Func<AnomalyNotice, bool>? _filter;
    private readonly ConcurrentQueue<AnomalyNotice> _notices = new();
    private int _disposed;

    internal AnomalyCapture(AnomalyLedger ledger, Func<AnomalyNotice, bool>? filter)
    {
        _ledger = ledger;
        _filter = filter;
        _ledger.AddReported(OnReported);
    }

    /// <summary>The reports collected so far, in arrival order.</summary>
    public IReadOnlyList<AnomalyNotice> Notices => [.. _notices];

    /// <summary>Whether any collected report has <paramref name="key"/> (<c>subsystem/code</c>; normalised).</summary>
    public bool Any(string key) => Count(key) > 0;

    /// <summary>How many collected reports have <paramref name="key"/> (<c>subsystem/code</c>; normalised).</summary>
    public int Count(string key)
    {
        string normal = NormalKey(key);
        return _notices.Count(n => string.Equals(n.Key, normal, StringComparison.Ordinal));
    }

    /// <summary>The collected reports of one subsystem (normalised).</summary>
    public IReadOnlyList<AnomalyNotice> OfSubsystem(string subsystem)
    {
        string wanted = AnomalyLedger.Normalise(subsystem);
        return _notices.Where(n => string.Equals(n.Subsystem, wanted, StringComparison.Ordinal)).ToList();
    }

    /// <summary>One line per collected report (test output).</summary>
    public string Describe()
        => _notices.IsEmpty ? "(no anomalies)" : string.Join(Environment.NewLine, _notices.Select(n => string.Create(CultureInfo.InvariantCulture, $"{n.Key} {n.Subject}: {n.Detail}")));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _ledger.RemoveReported(OnReported);
    }

    private void OnReported(AnomalyNotice notice)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_filter is null || _filter(notice)) _notices.Enqueue(notice);
    }

    private static string NormalKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "unknown/unknown";
        int slash = key.IndexOf('/');
        return slash > 0 ? AnomalyLedger.KeyOf(key[..slash], key[(slash + 1)..]) : AnomalyLedger.Normalise(key);
    }
}

/// <summary>The anomaly instruments (meter <c>ArcaneCore.Anomalies</c>, collected by the metrics store like every ArcaneCore.* meter).</summary>
public static class AnomalyMeters
{
    private static readonly Lazy<Meter> Instance = new(Create);
    private static Counter<long>? _anomalies;

    /// <summary>The anomaly meter (created once).</summary>
    public static Meter Meter => Instance.Value;

    internal static void Count(string subsystem, string code, AnomalySeverity severity)
    {
        _ = Meter;
        _anomalies?.Add(1,
            new KeyValuePair<string, object?>("subsystem", subsystem),
            new KeyValuePair<string, object?>("code", code),
            new KeyValuePair<string, object?>("severity", severity.ToString()));
    }

    private static Meter Create()
    {
        var meter = new Meter("ArcaneCore.Anomalies", "1.0");
        _anomalies = meter.CreateCounter<long>("arcanecore.anomalies", "{anomaly}",
            "Failed outcome checks (no exception thrown), by subsystem, code and severity.");
        meter.CreateObservableGauge("arcanecore.anomalies.distinct", () => (long)AnomalyLedger.Shared.Distinct, "{key}",
            "Distinct subsystem/code keys in the anomaly ledger.");
        return meter;
    }
}

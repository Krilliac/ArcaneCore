using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// The process-wide ledger of exceptions the server caught and survived (and the ones the crash hooks saw): each
/// distinct fault is counted once per <em>signature</em>, so an operator sees "this failed 40 000 times since
/// start" instead of 40 000 log lines, and the bot-QA publisher can file one issue per fault.
/// <para>
/// Signature: the exception type, the recording <c>site</c> (the catch site's name, e.g. <c>worldtick</c>) and the first <see cref="SignatureFrames"/> of
/// the up to <see cref="TopFrames"/> frames kept for display, inside <c>ArcaneCore.*</c> as <c>Type.Method</c> (no line numbers, no addresses; generic
/// arity, async state machines, lambdas and local functions are normalised away so one throw site is one
/// signature however it is compiled). The id is the first 8 bytes of the SHA-256 of that text, as 16 lower-case hex digits.
/// <see cref="AggregateException"/> and <see cref="TargetInvocationException"/> wrappers are unwrapped to the exception inside.
/// </para>
/// <para>
/// Bounds: at most <see cref="FaultLedgerLimits.MaxSignatures"/> distinct signatures are kept; a fault beyond that is
/// counted into the single <see cref="OverflowId"/> bucket, never dropped. A stored message is cut to <see cref="MaxMessageLength"/>.
/// </para>
/// <para>
/// Threads and ownership: <see cref="Shared"/> is the process-wide instance (the DI container registers the same object, the way
/// <see cref="Invariant"/> is static) and <see cref="Record(string, Exception)"/> is callable from any thread: lookups are
/// lock-free, a new signature takes one gate, a counter update takes the entry's own lock for a few assignments.
/// Recording walks the exception's stack once (tens of microseconds); it runs on the failure path only.
/// </para>
/// </summary>
public sealed class FaultLedger
{
    /// <summary>The id of the bucket that counts faults beyond the signature cap.</summary>
    public const string OverflowId = "0000000000000000";

    /// <summary>Frames inside ArcaneCore.* that make up a signature.</summary>
    public const int TopFrames = 5;

    /// <summary>Frames (the throw site and the two callers above it) that identify a signature; the rest are shown but do not split it.</summary>
    public const int SignatureFrames = 3;

    /// <summary>Longest sample message kept (characters).</summary>
    public const int MaxMessageLength = 300;

    private const string AppAssemblyPrefix = "ArcaneCore.";

    /// <summary>Distinct signatures remembered by the overflow bucket so each logs its first occurrence.</summary>
    private const int MaxOverflowSeen = 256;

    private readonly ConcurrentDictionary<string, FaultEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _addGate = new();
    private readonly TimeProvider _time;
    private readonly FaultEntry _overflow;
    private volatile int _maxSignatures;
    private volatile int _logLimit;
    private long _total;
    private long _lastTicks;

    /// <summary>A ledger with the default bounds (512 signatures, 10 logs per signature) on the system clock.</summary>
    public FaultLedger()
        : this(new FaultLedgerLimits(), TimeProvider.System)
    {
    }

    /// <summary>A ledger with explicit bounds and clock (tests).</summary>
    public FaultLedger(FaultLedgerLimits limits, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _maxSignatures = Math.Max(1, limits.MaxSignatures);
        _logLimit = Math.Max(0, limits.LogLimit);
        _overflow = new FaultEntry(OverflowId, "(overflow)", "(various)", [], time.GetUtcNow(), string.Empty);
    }

    /// <summary>The process-wide ledger.</summary>
    public static FaultLedger Shared { get; } = new();

    /// <summary>
    /// Raised on the recording thread, after the counts are updated, for each fault whose recording says the caller should log it (so a fault
    /// storm raises it as rarely as it logs: the first <c>FaultLogLimit</c> occurrences of a signature, then at most once a minute). Handlers must
    /// be quick and must not throw; one that throws is ignored.
    /// </summary>
    public event Action<FaultNotice>? FaultNoticed;

    /// <summary>Faults recorded since start or the last <see cref="Reset"/> (every occurrence, logged or not).</summary>
    public long Total => Volatile.Read(ref _total);

    /// <summary>Distinct signatures held (the overflow bucket is not counted).</summary>
    public int Distinct => _entries.Count;

    /// <summary>When the latest fault was recorded, or null when none was.</summary>
    public DateTimeOffset? LastSeen
    {
        get
        {
            long ticks = Volatile.Read(ref _lastTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Apply <c>Diagnostics:FaultMaxSignatures</c> and <c>Diagnostics:FaultLogLimit</c> (safe to call while recording).</summary>
    public void Configure(DiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxSignatures = Math.Max(1, options.FaultMaxSignatures);
        _logLimit = Math.Max(0, options.FaultLogLimit);
    }

    /// <summary>
    /// Count <paramref name="exception"/> as caught at <paramref name="site"/> and say whether the caller should log it now
    /// (<see cref="FaultRecording.ShouldLog"/>). Swallows its own failures except OutOfMemoryException: a caller that must not be interrupted wraps the call.
    /// </summary>
    public FaultRecording Record(string site, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            return RecordCore(string.IsNullOrWhiteSpace(site) ? "unknown" : site, exception);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The ledger must never turn a handled fault into a second one.
            return new FaultRecording(OverflowId, 0, true, 0);
        }
    }

    /// <summary>Record the fault and log it through <paramref name="logger"/> unless throttled (see <see cref="FaultLedgerLogging"/>).</summary>
    public void Log(ILogger logger, LogLevel level, string site, Exception exception, string message, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        FaultRecording recording = Record(site, exception);
        if (!recording.ShouldLog)
        {
            return;
        }

        string text = recording.Suppressed > 0
            ? message + " (" + recording.Suppressed.ToString(CultureInfo.InvariantCulture) + " more since)"
            : message;
        logger.Log(level, exception, text, args);
    }

    /// <summary>Every signature, most frequent first, followed by the overflow bucket when it holds anything.</summary>
    public IReadOnlyList<FaultSnapshot> Snapshot()
    {
        var list = new List<FaultSnapshot>(_entries.Count + 1);
        foreach (KeyValuePair<string, FaultEntry> pair in _entries)
        {
            list.Add(pair.Value.ToSnapshot());
        }

        list.Sort(static (a, b) =>
        {
            int byCount = b.Count.CompareTo(a.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Id, b.Id);
        });
        FaultSnapshot overflow = _overflow.ToSnapshot();
        if (overflow.Count > 0)
        {
            list.Add(overflow);
        }

        return list;
    }

    /// <summary>One signature by id, or null.</summary>
    public FaultSnapshot? Find(string id)
    {
        if (string.Equals(id, OverflowId, StringComparison.Ordinal))
        {
            FaultSnapshot overflow = _overflow.ToSnapshot();
            return overflow.Count > 0 ? overflow : null;
        }

        return id is not null && _entries.TryGetValue(id.ToLowerInvariant(), out FaultEntry? entry) ? entry.ToSnapshot() : null;
    }

    /// <summary>Forget every signature and the totals (the operator command, tests). Metrics counters are cumulative and keep their values.</summary>
    public void Reset()
    {
        lock (_addGate)
        {
            _entries.Clear();
            _overflow.Clear();
            Interlocked.Exchange(ref _total, 0);
            Interlocked.Exchange(ref _lastTicks, 0);
        }
    }

    /// <summary>The signature text for <paramref name="exception"/> at <paramref name="site"/> (exposed for tests and the show command).</summary>
    public static string SignatureOf(string site, Exception exception, out string type, out IReadOnlyList<string> frames)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception root = Unwrap(exception);
        type = root.GetType().FullName ?? root.GetType().Name;
        frames = CollectFrames(root);
        var text = new StringBuilder(128);
        text.Append(type).Append('|').Append(site);
        foreach (string frame in frames.Take(SignatureFrames))
        {
            text.Append('|').Append(frame);
        }

        return text.ToString();
    }

    private FaultRecording RecordCore(string site, Exception exception)
    {
        string signature = SignatureOf(site, exception, out string type, out IReadOnlyList<string> frames);
        string id = HashOf(signature);
        DateTimeOffset now = _time.GetUtcNow();
        FaultMeters.Count(site, type);

        if (!_entries.TryGetValue(id, out FaultEntry? entry))
        {
            lock (_addGate)
            {
                if (!_entries.TryGetValue(id, out entry))
                {
                    if (_entries.Count >= _maxSignatures)
                    {
                        entry = _overflow;
                    }
                    else
                    {
                        entry = new FaultEntry(id, site, type, frames, now, Truncate(exception.Message));
                        _entries[id] = entry;
                    }
                }
            }
        }

        Interlocked.Increment(ref _total);
        Interlocked.Exchange(ref _lastTicks, now.UtcTicks);
        FaultRecording recording = entry.Note(now, _logLimit, exception, ReferenceEquals(entry, _overflow) ? id : null);
        if (recording.ShouldLog && FaultNoticed is { } noticed)
        {
            var notice = new FaultNotice(recording.Id, site, type, Truncate(exception.Message), recording.Count, recording.Suppressed);
            foreach (Action<FaultNotice> handler in noticed.GetInvocationList())
            {
                try
                {
                    handler(notice);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A subscriber must never turn a handled fault into a second one, nor stop the subscribers after it.
                }
            }
        }

        return recording;
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while (true)
        {
            Exception? inner = current switch
            {
                AggregateException { InnerExceptions.Count: 1 } aggregate => aggregate.InnerExceptions[0],
                TargetInvocationException { InnerException: { } target } => target,
                _ => null,
            };
            if (inner is null)
            {
                return current;
            }

            current = inner;
        }
    }

    private static List<string> CollectFrames(Exception exception)
    {
        var frames = new List<string>(TopFrames);
        StackFrame[] all = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
        foreach (StackFrame frame in all)
        {
            MethodBase? method = frame.GetMethod();
            Type? declaring = method?.DeclaringType;
            string? ns = declaring?.Namespace;
            if (method is null || declaring is null || ns is null
                || !ns.StartsWith(AppAssemblyPrefix, StringComparison.Ordinal) && !string.Equals(ns, "ArcaneCore", StringComparison.Ordinal))
            {
                continue;
            }

            string name = NormalisedName(declaring, method);
            if (name.Length == 0 || frames.Count > 0 && frames[^1] == name)
            {
                continue; // a lambda and its owner, or recursion, is one frame
            }

            frames.Add(name);
            if (frames.Count == TopFrames)
            {
                break;
            }
        }

        return frames;
    }

    /// <summary>
    /// <c>Namespace-free Outer.Type.Method</c> with compiler artefacts removed: <c>Foo&lt;T&gt;</c> / <c>Foo`1</c> becomes <c>Foo</c>,
    /// <c>Foo.&lt;Bar&gt;d__3.MoveNext</c> becomes <c>Foo.Bar</c>, closures and lambdas become their owning method.
    /// </summary>
    internal static string NormalisedName(Type declaring, MethodBase method)
    {
        string typeName = StripArtefacts(TypeChain(declaring));
        string methodName = method.Name;

        // Async and iterator state machines: the "method" is MoveNext and the type is <Owner>d__N.
        if (methodName == "MoveNext" && declaring.Name.StartsWith('<'))
        {
            return typeName;
        }

        // Lambdas and local functions: <Owner>b__N_M / <Owner>g__Local|N_M (nested: <<Owner>g__Local|N_M>b__K).
        if (methodName.StartsWith('<'))
        {
            string owner = CleanName(methodName);
            int g = methodName.IndexOf(">g__", StringComparison.Ordinal);
            if (g >= 0)
            {
                string rest = methodName[(g + 4)..];
                int bar = rest.IndexOf('|');
                string local = CleanName(bar >= 0 ? rest[..bar] : rest);
                if (local.Length != 0 && local != owner)
                {
                    owner += "." + local;
                }
            }

            if (owner.Length != 0)
            {
                return typeName.Length == 0 ? owner : typeName + "." + owner;
            }
        }

        int tick = methodName.IndexOf('`');
        if (tick > 0)
        {
            methodName = methodName[..tick];
        }

        return typeName.Length == 0 ? methodName : typeName + "." + methodName;
    }

    private static string TypeChain(Type type)
    {
        var parts = new List<string>(3);
        for (Type? t = type; t is not null; t = t.DeclaringType)
        {
            parts.Add(t.Name);
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    private static string StripArtefacts(string chain)
    {
        var parts = new List<string>();
        foreach (string raw in chain.Split('.'))
        {
            string part = raw;
            if (part.StartsWith("<>c", StringComparison.Ordinal))
            {
                continue; // the closure class (<>c, <>c__DisplayClass4_0) adds nothing to the owner's name
            }

            part = CleanName(part);

            if (part.Length > 0)
            {
                parts.Add(part);
            }
        }

        return string.Join('.', parts);
    }

    /// <summary>
    /// A compiler-generated name reduced to the name the author wrote: <c>&lt;Owner&gt;b__0_0</c> and the async lambda state machine
    /// <c>&lt;&lt;Owner&gt;b__0_0&gt;d</c> become <c>Owner</c>; generic arity (<c>Foo`1</c>) is cut. Never contains '&lt;' or '&gt;'.
    /// </summary>
    internal static string CleanName(string name)
    {
        while (name.Length > 0 && name[0] == '<')
        {
            int depth = 0;
            int end = -1;
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] == '<')
                {
                    depth++;
                }
                else if (name[i] == '>' && --depth == 0)
                {
                    end = i;
                    break;
                }
            }

            if (end < 2)
            {
                return string.Empty; // <>c and other nameless artefacts
            }

            name = name[1..end];
        }

        int cut = name.IndexOfAny(['`', '<', '>']);
        return cut >= 0 ? name[..cut] : name;
    }

    private static string HashOf(string signature)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(signature), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    private static string Truncate(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
    }

    /// <summary>Mutable per-signature state; every field is guarded by <c>this</c>.</summary>
    private sealed class FaultEntry(string id, string site, string type, IReadOnlyList<string> frames, DateTimeOffset now, string message)
    {
        private DateTimeOffset _first = now;
        private DateTimeOffset _last = now;
        private DateTimeOffset _lastLog = DateTimeOffset.MinValue;
        private string _message = message;
        private long _count;
        private long _sinceLog;
        private HashSet<string>? _overflowSeen;

        public FaultRecording Note(DateTimeOffset now, int logLimit, Exception exception, string? overflowId)
        {
            lock (this)
            {
                _count++;
                _last = now;
                if (_count == 1)
                {
                    _first = now;
                    _message = Truncate(exception.Message);
                }

                // A brand-new signature that lands in the overflow bucket still logs its first occurrence (with the exception): the bucket keeps no frames.
                bool firstOverflow = overflowId is not null && (_overflowSeen ??= []).Count < MaxOverflowSeen && _overflowSeen.Add(overflowId);
                bool log;
                long suppressed = 0;
                if (firstOverflow || _count <= logLimit)
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

                return new FaultRecording(id, _count, log, suppressed);
            }
        }

        public void Clear()
        {
            lock (this)
            {
                _count = 0;
                _sinceLog = 0;
                _lastLog = DateTimeOffset.MinValue;
                _overflowSeen?.Clear();
                _message = string.Empty;
            }
        }

        public FaultSnapshot ToSnapshot()
        {
            lock (this)
            {
                return new FaultSnapshot(id, site, type, frames, _count, _first, _last, _message);
            }
        }
    }
}

/// <summary>Bounds of a <see cref="FaultLedger"/> (<c>Diagnostics:FaultMaxSignatures</c>, <c>Diagnostics:FaultLogLimit</c>).</summary>
public sealed record FaultLedgerLimits(int MaxSignatures = 512, int LogLimit = 10);

/// <summary>
/// What <see cref="FaultLedger.Record(string, Exception)"/> decided: the signature id, how often it has now occurred,
/// whether the caller should write its log line, and how many occurrences since the previous line were held back.
/// </summary>
public readonly record struct FaultRecording(string Id, long Count, bool ShouldLog, long Suppressed);

/// <summary>A fault the ledger wants logged now (<see cref="FaultLedger.FaultNoticed"/>): its signature id, where and what, how often so far and how many repeats were held back since the previous notice.</summary>
public sealed record FaultNotice(string Id, string Site, string ExceptionType, string Message, long Count, long Suppressed);

/// <summary>A point-in-time copy of one ledger entry (safe to hold and read from any thread).</summary>
public sealed record FaultSnapshot(
    string Id,
    string Site,
    string ExceptionType,
    IReadOnlyList<string> Frames,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string SampleMessage);

/// <summary>
/// Logs a caught exception through the ledger: the catch sites keep their message template and recovery, and gain a count,
/// a signature and throttled repeats (after <c>Diagnostics:FaultLogLimit</c> lines of one signature, at most one line a
/// minute with "(N more since)").
/// </summary>
public static class FaultLedgerLogging
{
    /// <summary>Record into <see cref="FaultLedger.Shared"/> and log at <paramref name="level"/> unless throttled.</summary>
    public static void LogFault(this ILogger logger, LogLevel level, string site, Exception exception, string message, params object?[] args)
        => FaultLedger.Shared.Log(logger, level, site, exception, message, args);

    /// <summary>Error-level <see cref="LogFault(ILogger, LogLevel, string, Exception, string, object?[])"/>.</summary>
    public static void LogFault(this ILogger logger, string site, Exception exception, string message, params object?[] args)
        => logger.LogFault(LogLevel.Error, site, exception, message, args);
}

/// <summary>The fault instruments (meter <c>ArcaneCore.Faults</c>, collected by the metrics store like every ArcaneCore.* meter).</summary>
public static class FaultMeters
{
    private static readonly Lazy<Meter> Instance = new(Create);
    private static Counter<long>? _faults;

    /// <summary>The fault meter (created once; the metrics host touches it at start so the gauges exist before the first fault).</summary>
    public static Meter Meter => Instance.Value;

    internal static void Count(string site, string type)
    {
        _ = Meter;
        _faults!.Add(1, new KeyValuePair<string, object?>("site", site), new KeyValuePair<string, object?>("type", type));
    }

    private static Meter Create()
    {
        var meter = new Meter("ArcaneCore.Faults", "1.0");
        _faults = meter.CreateCounter<long>("arcanecore.faults", "{fault}", "Exceptions caught by the server's catch-and-log sites, by site and exception type.");
        meter.CreateObservableGauge("arcanecore.faults.distinct", () => (long)FaultLedger.Shared.Distinct, "{signature}", "Distinct fault signatures in the fault ledger.");
        meter.CreateObservableCounter("arcanecore.invariant.failures", () => Invariant.FailureCount, "{failure}", "Failed Invariant.Assert and Invariant.Check calls since start.");
        return meter;
    }
}

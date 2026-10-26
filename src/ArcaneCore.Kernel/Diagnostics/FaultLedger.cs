using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// <see cref="Invariant"/> is static) and <see cref="Record(string, Exception, bool)"/> is callable from any thread: lookups are
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

    /// <summary>Distinct messages tracked per signature (bounded, Space-Saving: see <see cref="SubjectTally"/>).</summary>
    public const int MaxTrackedMessages = 16;

    /// <summary>Messages a snapshot lists with their counts.</summary>
    public const int SnapshotTopMessages = 5;

    /// <summary>Recent distinct messages a snapshot lists.</summary>
    public const int RecentMessageCount = 3;

    /// <summary>Longest message kept in the per-signature tally (characters), so 16 of them stay a few KB.</summary>
    private const int MaxTallyMessageLength = 160;

    private const string AppAssemblyPrefix = "ArcaneCore.";

    /// <summary>Distinct signatures remembered by the overflow bucket so each logs its first occurrence.</summary>
    private const int MaxOverflowSeen = 256;

    private static readonly ConditionalWeakTable<Exception, string> RecordedExceptions = new();

    private readonly ConcurrentDictionary<string, FaultEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _addGate = new();
    private readonly TimeProvider _time;
    private readonly FaultEntry _overflow;
    private volatile int _maxSignatures;
    private volatile int _logLimit;
    private volatile bool _beepOnException;
    private volatile string? _crashJournalPath;
    private long _total;
    private long _epoch;
    private long _lastTicks;
    private readonly RateWindow _rate = new();
    private long _firstTicks;

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
        _overflow = new FaultEntry(OverflowId, "(overflow)", "(various)", [], string.Empty, string.Empty, string.Empty);
    }

    /// <summary>The process-wide ledger.</summary>
    public static FaultLedger Shared { get; } = new();

    /// <summary>Whether <paramref name="exception"/> (or its unwrapped inner exception) has already been recorded in a <see cref="FaultLedger"/>.</summary>
    public static bool WasRecorded(Exception? exception)
        => exception is not null
            && (RecordedExceptions.TryGetValue(exception, out _) || RecordedExceptions.TryGetValue(Unwrap(exception), out _));

    /// <summary>Configure the file path where terminating faults (<c>unhandled</c>, <c>unobserved-task</c>) are appended so the next startup can replay them into QA.</summary>
    public void ConfigureCrashJournal(string? path)
        => _crashJournalPath = string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>
    /// Raised on the recording thread, after the counts are updated, for each fault whose recording says the caller should log it (so a fault
    /// storm raises it as rarely as it logs: the first <c>FaultLogLimit</c> occurrences of a signature, then at most once a minute). Handlers must
    /// be quick and must not throw; one that throws is ignored.
    /// </summary>
    public event Action<FaultNotice>? FaultNoticed;

    /// <summary>Faults recorded since start or the last <see cref="Reset"/> (every occurrence, logged or not).</summary>
    public long Total => Volatile.Read(ref _total);

    /// <summary>Changes each time <see cref="Reset"/> forgets the ledger, so a reader comparing two snapshots can tell a reset from a key that stopped firing.</summary>
    public long Epoch => Volatile.Read(ref _epoch);

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

    /// <summary>When the first fault since start or the last <see cref="Reset"/> was recorded, or null when none was.</summary>
    public DateTimeOffset? FirstSeen
    {
        get
        {
            long ticks = Volatile.Read(ref _firstTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>First and last seen and the faults of the last 15 minutes and the last hour, ledger-wide (no inter-arrival: that is kept per signature).</summary>
    public LedgerTiming Timing()
    {
        DateTimeOffset now = _time.GetUtcNow();
        return new LedgerTiming(FirstSeen, LastSeen, _rate.CountSince(now, 15), _rate.CountSince(now, 60));
    }

    /// <summary>Apply <c>Diagnostics:FaultMaxSignatures</c>, <c>Diagnostics:FaultLogLimit</c> and <c>Diagnostics:BeepOnException</c> (safe to call while recording).</summary>
    public void Configure(DiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxSignatures = Math.Max(1, options.FaultMaxSignatures);
        _logLimit = Math.Max(0, options.FaultLogLimit);
        _beepOnException = options.BeepOnException;
    }

    /// <summary>
    /// Count <paramref name="exception"/> as caught at <paramref name="site"/> and say whether the caller should log it now
    /// (<see cref="FaultRecording.ShouldLog"/>). Swallows its own failures except OutOfMemoryException: a caller that must not be interrupted wraps the call.
    /// <paramref name="terminating"/>: the process ends because of this fault (the crash hooks say so); carried on <see cref="FaultNotice.Terminating"/>.
    /// </summary>
    public FaultRecording Record(string site, Exception exception, bool terminating = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            return RecordCore(string.IsNullOrWhiteSpace(site) ? "unknown" : site, exception, terminating);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The ledger must never turn a handled fault into a second one.
            return new FaultRecording(OverflowId, 0, true, 0);
        }
    }

    /// <summary>
    /// Record <paramref name="exception"/> unless it represents an expected cancellation (<see cref="OperationCanceledException"/>
    /// with <paramref name="cancellationToken"/> cancelled, or <see cref="TaskCanceledException"/>).
    /// </summary>
    public FaultRecording RecordIfUnexpected(string site, Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException && (cancellationToken.IsCancellationRequested || exception is TaskCanceledException))
        {
            return new FaultRecording(OverflowId, 0, false, 0);
        }

        return Record(site, exception);
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

        string condensed = FormatCondensedStack(exception);
        string text = recording.Suppressed > 0
            ? message + " (" + recording.Suppressed.ToString(CultureInfo.InvariantCulture) + " more since)"
            : message;
        text = string.Create(CultureInfo.InvariantCulture,
            $"{text} [fault={recording.Id} count={recording.Count} stack={condensed}]{(recording.Timing is { } timing ? $" ({timing.Describe()})" : string.Empty)}");
        logger.Log(level, exception, text, args);
    }

    /// <summary>Every signature, most frequent first, followed by the overflow bucket when it holds anything.</summary>
    public IReadOnlyList<FaultSnapshot> Snapshot()
    {
        var list = new List<FaultSnapshot>(_entries.Count + 1);
        foreach (KeyValuePair<string, FaultEntry> pair in _entries)
        {
            list.Add(pair.Value.ToSnapshot(_time.GetUtcNow()));
        }

        list.Sort(static (a, b) =>
        {
            int byCount = b.Count.CompareTo(a.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Id, b.Id);
        });
        FaultSnapshot overflow = _overflow.ToSnapshot(_time.GetUtcNow());
        if (overflow.Count > 0)
        {
            list.Add(overflow);
        }

        return list;
    }

    /// <summary>Group recorded faults by canonical subsystem (<see cref="DiagnosticSubsystem.OfSite"/> of <see cref="FaultSnapshot.Site"/>).</summary>
    public IReadOnlyDictionary<string, (int Signatures, long TotalCount)> BySubsystem()
    {
        var result = new SortedDictionary<string, (int Signatures, long TotalCount)>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, FaultEntry> pair in _entries)
        {
            FaultSnapshot snap = pair.Value.ToSnapshot(_time.GetUtcNow());
            if (snap.Count <= 0)
            {
                continue;
            }

            string subsystem = DiagnosticSubsystem.OfSite(snap.Site);
            if (result.TryGetValue(subsystem, out (int Signatures, long TotalCount) agg))
            {
                result[subsystem] = (agg.Signatures + 1, agg.TotalCount + snap.Count);
            }
            else
            {
                result[subsystem] = (1, snap.Count);
            }
        }

        FaultSnapshot overflow = _overflow.ToSnapshot(_time.GetUtcNow());
        if (overflow.Count > 0)
        {
            if (result.TryGetValue(OverflowId, out (int Signatures, long TotalCount) agg))
            {
                result[OverflowId] = (agg.Signatures + 1, agg.TotalCount + overflow.Count);
            }
            else
            {
                result[OverflowId] = (1, overflow.Count);
            }
        }

        return result;
    }

    /// <summary>One signature by id, or null.</summary>
    public FaultSnapshot? Find(string id)
    {
        if (string.Equals(id, OverflowId, StringComparison.Ordinal))
        {
            FaultSnapshot overflow = _overflow.ToSnapshot(_time.GetUtcNow());
            return overflow.Count > 0 ? overflow : null;
        }

        return id is not null && _entries.TryGetValue(id.ToLowerInvariant(), out FaultEntry? entry) ? entry.ToSnapshot(_time.GetUtcNow()) : null;
    }

    /// <summary>Forget every signature and the totals (the operator command, tests). Metrics counters are cumulative and keep their values.</summary>
    public void Reset()
    {
        lock (_addGate)
        {
            _entries.Clear();
            _overflow.Clear();
            Interlocked.Exchange(ref _total, 0);
            Interlocked.Increment(ref _epoch);
            Interlocked.Exchange(ref _lastTicks, 0);
            Interlocked.Exchange(ref _firstTicks, 0);
            _rate.Clear();
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

    /// <summary>
    /// Formats up to <paramref name="maxFrames"/> <c>ArcaneCore.*</c> frames from the unwrapped exception with file name and line
    /// number when available (<c>Type.Method(File.cs:123) &lt;- Caller.Method(File.cs:45)</c>), falling back to <c>Type.Method</c>
    /// when <see cref="StackFrame.GetFileLineNumber"/> is 0.
    /// </summary>
    public static string FormatCondensedStack(Exception exception, int maxFrames = 6)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (maxFrames <= 0)
        {
            return string.Empty;
        }

        Exception? current = Unwrap(exception);
        var formatted = new List<string>(Math.Min(maxFrames, 8));
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        string? lastNormalised = null;
        bool lastHadLine = false;

        while (current is not null && formatted.Count < maxFrames && visited.Add(current))
        {
            StackFrame[]? all = new StackTrace(current, fNeedFileInfo: true).GetFrames();
            if (all is not null)
            {
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
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    int line = frame.GetFileLineNumber();
                    string? file = frame.GetFileName();
                    bool hasLine = line > 0 && !string.IsNullOrEmpty(file);
                    string entry = hasLine
                        ? string.Create(CultureInfo.InvariantCulture, $"{name}({Path.GetFileName(file!)}:{line})")
                        : name;

                    if (string.Equals(lastNormalised, name, StringComparison.Ordinal))
                    {
                        if (!lastHadLine && hasLine && formatted.Count > 0)
                        {
                            formatted[^1] = entry;
                            lastHadLine = true;
                        }

                        continue;
                    }

                    formatted.Add(entry);
                    lastNormalised = name;
                    lastHadLine = hasLine;
                    if (formatted.Count == maxFrames)
                    {
                        break;
                    }
                }
            }

            current = current.InnerException is { } next ? Unwrap(next) : null;
        }

        return string.Join(" <- ", formatted);
    }

    /// <summary>
    /// Formats outer-to-inner exception types and messages (<c>OuterType: msg &lt;- InnerType: innerMsg</c>),
    /// handling <see cref="AggregateException.InnerExceptions"/> as well.
    /// </summary>
    public static string FormatExceptionChain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var parts = new List<string>(4);
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        AppendExceptionChain(exception, parts, visited, maxNodes: 12);
        return string.Join(" <- ", parts);
    }

    private static void AppendExceptionChain(Exception current, List<string> parts, HashSet<Exception> visited, int maxNodes)
    {
        if (parts.Count >= maxNodes || !visited.Add(current))
        {
            return;
        }

        string message = Truncate(current.Message).ReplaceLineEndings(" ");
        parts.Add(message.Length == 0 ? current.GetType().Name : $"{current.GetType().Name}: {message}");

        if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (parts.Count >= maxNodes)
                {
                    break;
                }

                AppendExceptionChain(inner, parts, visited, maxNodes);
            }
        }
        else if (current.InnerException is { } inner)
        {
            AppendExceptionChain(inner, parts, visited, maxNodes);
        }
    }

    /// <summary>Compute the 16-hex signature id for an arbitrary signature string.</summary>
    public static string HashSignature(string signature) => HashOf(signature ?? string.Empty);

    /// <summary>
    /// Replay and remove any terminating crash faults previously written to <paramref name="path"/> (or the configured crash journal path).
    /// Returns the number of replayed crash records.
    /// </summary>
    public int ReplayCrashJournal(string? path = null)
    {
        string? journal = string.IsNullOrWhiteSpace(path) ? _crashJournalPath : path;
        if (string.IsNullOrWhiteSpace(journal) || !File.Exists(journal))
        {
            return 0;
        }

        int replayed = 0;
        try
        {
            string[] lines;
            lock (_addGate)
            {
                if (!File.Exists(journal))
                {
                    return 0;
                }

                lines = File.ReadAllLines(journal);
                File.Delete(journal);
            }

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    PersistedCrashFault? crash = JsonSerializer.Deserialize<PersistedCrashFault>(line);
                    if (crash is null || string.IsNullOrWhiteSpace(crash.Id))
                    {
                        continue;
                    }

                    RecordPersistedCrash(crash);
                    replayed++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Skip malformed crash journal lines.
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never fail startup because the crash journal could not be read or removed.
        }

        return replayed;
    }

    private void RecordPersistedCrash(PersistedCrashFault crash)
    {
        string id = crash.Id.ToLowerInvariant();
        string site = string.IsNullOrWhiteSpace(crash.Site) ? "unhandled" : crash.Site;
        string type = string.IsNullOrWhiteSpace(crash.ExceptionType) ? "System.Exception" : crash.ExceptionType;
        IReadOnlyList<string> frames = crash.Frames ?? [];
        DateTimeOffset at = crash.AtUtc == default ? _time.GetUtcNow() : crash.AtUtc;
        string msg = Truncate(crash.Message);
        string stack = crash.SampleStack ?? string.Empty;
        string chain = crash.SampleChain ?? string.Empty;

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
                        entry = new FaultEntry(id, site, type, frames, msg, stack, chain);
                        _entries[id] = entry;
                    }
                }
            }
        }

        Interlocked.Increment(ref _total);
        Interlocked.Exchange(ref _lastTicks, at.UtcTicks);
        _rate.Add(at);
        Interlocked.CompareExchange(ref _firstTicks, at.UtcTicks, 0);
        entry.NoteSynthetic(at, msg, stack, chain);
    }

    private void PersistTerminatingCrash(string path, string id, string site, string type, IReadOnlyList<string> frames, Exception exception, DateTimeOffset now)
    {
        try
        {
            var record = new PersistedCrashFault(
                id,
                site,
                type,
                [.. frames],
                Truncate(exception.Message),
                FormatCondensedStack(exception),
                FormatExceptionChain(exception),
                now);
            string json = JsonSerializer.Serialize(record) + "\n";
            lock (_addGate)
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(path, json);
            }
        }
        catch
        {
            // Never let crash persistence interfere with the crash handler.
        }
    }

    private sealed record PersistedCrashFault(
        string Id,
        string Site,
        string ExceptionType,
        string[] Frames,
        string Message,
        string SampleStack,
        string SampleChain,
        DateTimeOffset AtUtc);

    private FaultRecording RecordCore(string site, Exception exception, bool terminating)
    {
        string signature = SignatureOf(site, exception, out string type, out IReadOnlyList<string> frames);
        string id = HashOf(signature);
        RecordedExceptions.AddOrUpdate(exception, id);
        Exception root = Unwrap(exception);
        if (!ReferenceEquals(root, exception))
        {
            RecordedExceptions.AddOrUpdate(root, id);
        }

        DateTimeOffset now = _time.GetUtcNow();
        FaultMeters.Count(site, type);
        if (terminating && _crashJournalPath is { Length: > 0 } journalPath)
        {
            PersistTerminatingCrash(journalPath, id, site, type, frames, exception, now);
        }

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
                        entry = new FaultEntry(
                            id,
                            site,
                            type,
                            frames,
                            Truncate(exception.Message),
                            FormatCondensedStack(exception),
                            FormatExceptionChain(exception));
                        _entries[id] = entry;
                    }
                }
            }
        }

        Interlocked.Increment(ref _total);
        Interlocked.Exchange(ref _lastTicks, now.UtcTicks);
        _rate.Add(now);
        Interlocked.CompareExchange(ref _firstTicks, now.UtcTicks, 0);
        FaultRecording recording = entry.Note(now, _logLimit, exception, ReferenceEquals(entry, _overflow) ? id : null);
        if (recording.ShouldLog && _beepOnException)
        {
            CrashAlert.TryBeep(checkErrorStream: true);
        }

        if (recording.ShouldLog && FaultNoticed is { } noticed)
        {
            string condensed = FormatCondensedStack(exception);
            var notice = new FaultNotice(recording.Id, site, type, Truncate(exception.Message), recording.Count, recording.Suppressed, terminating, condensed) { Timing = recording.Timing };
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
        => CollectFrames(new StackTrace(exception, fNeedFileInfo: false).GetFrames(), TopFrames, skipCrashHooks: false);

    /// <summary>
    /// The innermost <c>ArcaneCore.*</c> frames of the calling thread's stack (the same names as a ledger signature: <c>Type.Method</c>, compiler artefacts
    /// removed), up to <paramref name="max"/>, innermost first. Called from a crash hook it skips the hook's own frames, so the first entry is the code that
    /// threw. Walks the whole stack once (tens of microseconds): for the failure path only.
    /// </summary>
    public static IReadOnlyList<string> CurrentStackFrames(int max = TopFrames)
        => max <= 0 ? [] : CollectFrames(new StackTrace(skipFrames: 1, fNeedFileInfo: false).GetFrames(), max, skipCrashHooks: true);

    private static List<string> CollectFrames(StackFrame[] all, int max, bool skipCrashHooks)
    {
        var frames = new List<string>(Math.Min(max, TopFrames));
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

            if (skipCrashHooks && (declaring == typeof(CrashHandler) || declaring == typeof(FaultObservers)))
            {
                continue;
            }

            string name = NormalisedName(declaring, method);
            if (name.Length == 0 || frames.Count > 0 && frames[^1] == name)
            {
                continue; // a lambda and its owner, or recursion, is one frame
            }

            frames.Add(name);
            if (frames.Count == max)
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

    /// <summary>Mutable per-signature state: the shared <see cref="LedgerEntryCore"/> plus the fault's own samples; every field is guarded by <c>this</c>.</summary>
    private sealed class FaultEntry(
        string id,
        string site,
        string type,
        IReadOnlyList<string> frames,
        string message,
        string stack,
        string chain)
    {
        private readonly LedgerEntryCore _core = new(MaxTrackedMessages, RecentMessageCount);
        private string _message = message;
        private string _stack = stack;
        private string _chain = chain;
        private HashSet<string>? _overflowSeen;
        private string? _lastRaw;
        private string _lastText = string.Empty;
        private string _lastLine = string.Empty;

        public FaultRecording Note(DateTimeOffset now, int logLimit, Exception exception, string? overflowId)
        {
            lock (this)
            {
                long count = _core.Occur(now);
                string raw = exception.Message ?? string.Empty;
                string text;
                if (_lastRaw is not null && string.Equals(raw, _lastRaw, StringComparison.Ordinal))
                {
                    // The same message as the previous occurrence (a storm repeats one): one dictionary hit, no truncation, copy or recent-list work.
                    text = _lastText;
                    if (_lastLine.Length != 0)
                    {
                        _core.Tally.Add(_lastLine);
                    }
                }
                else
                {
                    text = Truncate(raw);
                    string line = text.ReplaceLineEndings(" ");
                    string tallied = line.Length <= MaxTallyMessageLength ? line : line[..MaxTallyMessageLength];
                    _lastRaw = raw;
                    _lastText = text;
                    _lastLine = tallied;
                    if (line.Length != 0)
                    {
                        _core.Tally.Add(tallied);
                        _core.Recent.Add(line);
                    }
                }

                if (count == 1)
                {
                    _message = text;
                    if (overflowId is null)
                    {
                        _stack = FormatCondensedStack(exception);
                        _chain = FormatExceptionChain(exception);
                    }
                }

                // A brand-new signature that lands in the overflow bucket still logs its first occurrence (with the exception): the bucket keeps no frames.
                bool firstOverflow = overflowId is not null && (_overflowSeen ??= []).Count < MaxOverflowSeen && _overflowSeen.Add(overflowId);
                (bool log, long suppressed) = _core.Throttle(now, logLimit, firstOverflow);
                return new FaultRecording(id, count, log, suppressed) { Timing = log ? _core.TimingAt(now) : null };
            }
        }

        public void NoteSynthetic(DateTimeOffset now, string message, string stack, string chain)
        {
            lock (this)
            {
                long count = _core.Occur(now);
                string line = message.ReplaceLineEndings(" ");
                string tallied = line.Length <= MaxTallyMessageLength ? line : line[..MaxTallyMessageLength];
                if (line.Length != 0)
                {
                    _core.Tally.Add(tallied);
                    _core.Recent.Add(line);
                }

                if (count == 1)
                {
                    _message = message;
                    _stack = stack;
                    _chain = chain;
                }
            }
        }

        public void Clear()
        {
            lock (this)
            {
                _core.Clear();
                _overflowSeen?.Clear();
                _lastRaw = null;
                _message = string.Empty;
                _stack = string.Empty;
                _chain = string.Empty;
            }
        }

        public FaultSnapshot ToSnapshot(DateTimeOffset now)
        {
            lock (this)
            {
                return new FaultSnapshot(id, site, type, frames, _core.Count, _core.First, _core.Last, _message, _stack, _chain)
                {
                    Timing = _core.TimingAt(now),
                    TopMessages = _core.Tally.Top(SnapshotTopMessages),
                    RecentMessages = _core.Recent.ToList(),
                };
            }
        }
    }
}

/// <summary>Bounds of a <see cref="FaultLedger"/> (<c>Diagnostics:FaultMaxSignatures</c>, <c>Diagnostics:FaultLogLimit</c>).</summary>
public sealed record FaultLedgerLimits(int MaxSignatures = 512, int LogLimit = 10);

/// <summary>
/// What <see cref="FaultLedger.Record(string, Exception, bool)"/> decided: the signature id, how often it has now occurred,
/// whether the caller should write its log line, and how many occurrences since the previous line were held back.
/// </summary>
public readonly record struct FaultRecording(string Id, long Count, bool ShouldLog, long Suppressed)
{
    /// <summary>The signature's first/last seen, rates and inter-arrival at this fault; set only when <see cref="ShouldLog"/> (the numbers cost a scan the throttled faults skip).</summary>
    public LedgerTiming? Timing { get; init; }
}

/// <summary>A fault the ledger wants logged now (<see cref="FaultLedger.FaultNoticed"/>): its signature id, where and what, how often so far and how many repeats were held back since the previous notice.</summary>
/// <param name="Terminating">The process ends because of this fault (an unhandled exception, or an unobserved task exception under a policy other than Log).</param>
/// <param name="CondensedStack">The condensed <c>ArcaneCore.*</c> call chain with file and line numbers when available.</param>
public sealed record FaultNotice(
    string Id,
    string Site,
    string ExceptionType,
    string Message,
    long Count,
    long Suppressed,
    bool Terminating = false,
    string CondensedStack = "")
{
    /// <summary>The signature's timing at this fault (rates, inter-arrival); set for every notice.</summary>
    public LedgerTiming? Timing { get; init; }
}

/// <summary>A point-in-time copy of one ledger entry (safe to hold and read from any thread).</summary>
public sealed record FaultSnapshot(
    string Id,
    string Site,
    string ExceptionType,
    IReadOnlyList<string> Frames,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string SampleMessage,
    string SampleStack = "",
    string SampleChain = "")
{
    /// <summary>First and last seen, faults in the last 15 minutes and hour (rate per hour) and the p50/p99 gap between occurrences.</summary>
    public LedgerTiming Timing { get; init; }

    /// <summary>The messages this signature threw most, with counts (single line, cut to 160 characters; bounded by <see cref="FaultLedger.MaxTrackedMessages"/>, Space-Saving), highest first, at most <see cref="FaultLedger.SnapshotTopMessages"/>.</summary>
    public IReadOnlyList<SubjectCount> TopMessages { get; init; } = [];

    /// <summary>The newest distinct messages (deduplicated), most recent first, at most <see cref="FaultLedger.RecentMessageCount"/>.</summary>
    public IReadOnlyList<string> RecentMessages { get; init; } = [];
}

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

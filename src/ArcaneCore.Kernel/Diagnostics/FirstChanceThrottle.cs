using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// Decides which first-chance exceptions get a full <see cref="CrashReport"/> (<c>Diagnostics:FirstChanceExceptions</c>, Debug Kernel builds).
/// Without it one fault filled the live log: a busy SQLite write rethrown through each awaiting frame raises a first-chance event per frame, and its
/// <c>DbUpdateException</c> wrapper again per frame (live 2026-10-26: 6 gate timeouts became 93 full reports, two thirds of all log lines).
/// <list type="bullet">
/// <item>The same exception object is reported once: every later first-chance event for it is a rethrow (<see cref="TryMarkNew"/>).</item>
/// <item>One signature (exception type and origin frame, <see cref="FirstChanceSite.Origin"/>) is reported at most once per <see cref="WindowMs"/>;
/// the next report of it says how many repeats were suppressed.</item>
/// <item>At most <see cref="MaxReportsPerWindow"/> reports per window across all signatures; the next report says how many were dropped over that cap.</item>
/// </list>
/// Thread-safe. Nothing here throws on the hook's path except out-of-memory.
/// </summary>
public sealed class FirstChanceThrottle
{
    /// <summary>The window of the per-signature and global limits (milliseconds).</summary>
    public const int WindowMs = 60_000;

    /// <summary>Full reports per window across all signatures.</summary>
    public const int MaxReportsPerWindow = 20;

    /// <summary>Signatures remembered; past this the table starts over (a storm of distinct signatures still meets the global cap).</summary>
    public const int MaxSignatures = 1024;

    private static readonly object Marker = new();

    private readonly Func<long> _nowMs;
    private readonly ConditionalWeakTable<Exception, object> _seen = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _bySignature = new(StringComparer.Ordinal);
    private long _windowStartMs;
    private int _reportsInWindow;
    private long _droppedOverCap;

    /// <summary>A throttle on <paramref name="nowMs"/> (monotonic milliseconds; default <see cref="Environment.TickCount64"/>).</summary>
    public FirstChanceThrottle(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
        _windowStartMs = _nowMs();
    }

    /// <summary>
    /// True the first time <paramref name="exception"/> is seen; false for every later first-chance event of the same object (an await, a
    /// <c>throw;</c> or an <c>ExceptionDispatchInfo</c> rethrow). Cheap: checked before any stack walk.
    /// </summary>
    public bool TryMarkNew(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return _seen.TryAdd(exception, Marker);
    }

    /// <summary>Whether a new exception with <paramref name="signature"/> gets a full report now, and what was suppressed before it.</summary>
    public FirstChanceDecision Decide(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        long now = _nowMs();
        lock (_lock)
        {
            if (now - _windowStartMs >= WindowMs)
            {
                _windowStartMs = now;
                _reportsInWindow = 0;
            }

            if (!_bySignature.TryGetValue(signature, out Entry? entry))
            {
                if (_bySignature.Count >= MaxSignatures)
                {
                    _bySignature.Clear();
                }

                entry = new Entry();
                _bySignature[signature] = entry;
            }

            if (entry.Reported && now - entry.LastReportMs < WindowMs)
            {
                entry.Suppressed++;
                return default;
            }

            if (_reportsInWindow >= MaxReportsPerWindow)
            {
                _droppedOverCap++;
                return default;
            }

            _reportsInWindow++;
            var decision = new FirstChanceDecision(true, entry.Suppressed, entry.Reported ? now - entry.LastReportMs : -1, _droppedOverCap);
            entry.Suppressed = 0;
            entry.LastReportMs = now;
            entry.Reported = true;
            _droppedOverCap = 0;
            return decision;
        }
    }

    private sealed class Entry
    {
        public bool Reported;
        public long LastReportMs;
        public long Suppressed;
    }
}

/// <summary>
/// The outcome of <see cref="FirstChanceThrottle.Decide"/>. <see cref="SuppressedRepeats"/>: earlier exceptions with this signature that got no report
/// since <see cref="SinceLastReportMs"/> ago (-1: never reported). <see cref="DroppedOverCap"/>: exceptions of any signature dropped by the global cap since the previous report.
/// </summary>
public readonly record struct FirstChanceDecision(bool Report, long SuppressedRepeats, long SinceLastReportMs, long DroppedOverCap);

/// <summary>
/// Where a first-chance exception was thrown, read from the throwing thread's own stack (the hook runs on it). The exception's stack is not enough:
/// an exception completed into a task without ever being thrown (a <c>Task.WaitAsync</c> timeout, a canceled source) first surfaces at the await that
/// rethrows it, and its stack then holds only <c>ExceptionDispatchInfo.Throw</c>. The thread's stack still names the awaiting method.
/// </summary>
public static class FirstChanceSite
{
    private const int MaxFrames = 40;

    /// <summary>
    /// The first frame of <paramref name="trace"/> outside the runtime and framework libraries (<c>System.*</c>, <c>Microsoft.*</c>) and outside this
    /// hook, as <c>Namespace.Type.Method</c> (an async or iterator state machine is shown as the method it was compiled from); the first frame when
    /// every frame is library code; "(unknown)" when there is none.
    /// </summary>
    public static string Origin(StackTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        string? first = null;
        foreach (StackFrame frame in trace.GetFrames())
        {
            if (frame.GetMethod() is not { } method)
            {
                continue;
            }

            Type? type = method.DeclaringType;
            if (type == typeof(FirstChanceSite) || type == typeof(CrashHandler) || type == typeof(FirstChanceThrottle))
            {
                continue;
            }

            string name = Describe(method);
            first ??= name;
            string ns = OwnerType(type)?.Namespace ?? "";
            if (!IsLibrary(ns))
            {
                return name;
            }
        }

        return first ?? "(unknown)";
    }

    /// <summary>The report section: the origin, the throttle's counts, and the throwing thread's stack (at most 40 frames, with file and line when symbols exist).</summary>
    public static string Render(string origin, StackTrace trace, FirstChanceDecision decision)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(trace);
        var sb = new StringBuilder(1024);
        sb.Append("  origin: ").Append(origin).Append('\n');
        sb.Append("  repeats of this type and origin suppressed: ").Append(decision.SuppressedRepeats.ToString(CultureInfo.InvariantCulture));
        if (decision.SinceLastReportMs >= 0)
        {
            sb.Append(" since its previous report ").Append((decision.SinceLastReportMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s ago");
        }

        sb.Append(" (one report per type and origin per ").Append(FirstChanceThrottle.WindowMs / 1000).Append(" s; rethrows of a reported exception are never reported)\n");
        if (decision.DroppedOverCap > 0)
        {
            sb.Append("  other first-chance exceptions dropped over the cap of ").Append(FirstChanceThrottle.MaxReportsPerWindow).Append(" reports per ")
                .Append(FirstChanceThrottle.WindowMs / 1000).Append(" s: ").Append(decision.DroppedOverCap.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        sb.Append("  thread stack at the throw:\n");
        int shown = 0;
        foreach (StackFrame frame in trace.GetFrames())
        {
            if (frame.GetMethod() is not { } method || method.DeclaringType == typeof(CrashHandler) || method.DeclaringType == typeof(FirstChanceSite))
            {
                continue;
            }

            if (shown++ == MaxFrames)
            {
                sb.Append("    ... more frames omitted\n");
                break;
            }

            sb.Append("    at ").Append(Describe(method));
            if (frame.GetFileName() is { Length: > 0 } file)
            {
                sb.Append(" in ").Append(Path.GetFileName(file)).Append(':').Append(frame.GetFileLineNumber().ToString(CultureInfo.InvariantCulture));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static bool IsLibrary(string ns)
        => ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal);

    /// <summary>The type a compiler-generated state machine or closure belongs to (itself for ordinary types).</summary>
    private static Type? OwnerType(Type? type)
    {
        while (type is not null && type.Name.StartsWith('<') && type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    private static string Describe(MethodBase method)
    {
        Type? type = method.DeclaringType;
        string name = method.Name;
        // "<SaveAsync>d__12.MoveNext" -> "SaveAsync"; a lambda "<Run>b__0" keeps its compiler name inside the owning type.
        if (type is not null && type.Name.StartsWith('<') && type.Name.IndexOf('>') is > 1 and var close)
        {
            name = type.Name[1..close];
        }

        Type? owner = OwnerType(type);
        return owner is null ? name : (owner.FullName ?? owner.Name) + "." + name;
    }
}

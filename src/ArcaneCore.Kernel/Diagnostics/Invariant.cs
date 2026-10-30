using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// Preconditions and state-machine invariants the code relies on, in two strengths
/// (docs/ops/invariants.md lists every one and why it holds):
/// <list type="bullet">
/// <item><see cref="Assert(bool, string, string, string, int)"/>: <c>[Conditional("DEBUG")]</c>. A Debug build
/// of the calling assembly evaluates it and a failure throws <see cref="InvariantViolationException"/>
/// (or aborts under <c>Diagnostics:OnInvariant=FailFast</c>); a Release build does not even evaluate the
/// condition, the call is removed by the compiler. For checks that are too hot or too obvious to pay for
/// in production, or whose failure the surrounding code cannot act on.</item>
/// <item><see cref="Check(bool, string, string, string, int)"/>: always compiled. A failure is logged (up to
/// <c>Diagnostics:InvariantLogLimit</c> times per call site) and counted, then either the caller goes on
/// with its own fail-closed handling (<c>Continue</c>, the default; the method returns false) or the process
/// aborts (<c>FailFast</c>, the mangos <c>MANGOS_ASSERT</c> behaviour).</item>
/// </list>
/// The interpolated-string overloads cost nothing when the condition holds: the message is formatted
/// only on failure (<see cref="InvariantMessageHandler"/>). The pass path of every overload is a
/// branch and no allocation, so these may sit on packet and tick paths.
/// <para>
/// Ownership and threads: process-wide static state. <see cref="Configure"/> is called by the host at
/// start (and once more when the logger exists); the checks run on any thread. Counters use
/// <see cref="Interlocked"/>, the per-site table is a <see cref="ConcurrentDictionary{TKey,TValue}"/> touched
/// only on the failure path, as is the flow-scoped <see cref="Capture"/>. Before <see cref="Configure"/> runs
/// (tests, tools) failures are written to standard error.
/// </para>
/// </summary>
public static class Invariant
{
    private static readonly ConcurrentDictionary<InvariantSite, SiteCounter> Sites = new();
    private static readonly AsyncLocal<InvariantCapture?> ActiveCapture = new();
    private static long _failures;
    private static long _epoch;
    private static volatile InvariantPolicy _policy = InvariantPolicy.Continue;
    private static volatile bool _breakOnFailure;
    private static volatile int _logLimit = 10;
    private static volatile ILogger? _logger;

    /// <summary>Failures recorded since the process started (every Assert and Check, logged or not).</summary>
    public static long FailureCount => Volatile.Read(ref _failures);

    /// <summary>Changes each time <see cref="ResetCounters"/> forgets the counters, so a reader of <see cref="Failures()"/> can tell a reset from a site that stopped failing.</summary>
    public static long Epoch => Volatile.Read(ref _epoch);

    /// <summary>The policy in force (<c>Diagnostics:OnInvariant</c>).</summary>
    public static InvariantPolicy Policy => _policy;

    /// <summary>
    /// Apply <c>Diagnostics:*</c> and route failure reports to <paramref name="logger"/> (null keeps the
    /// standard-error fallback). Safe to call again when the real logger becomes available.
    /// </summary>
    public static void Configure(DiagnosticsOptions options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _policy = options.OnInvariant;
        _breakOnFailure = options.BreakOnInvariant;
        _logLimit = Math.Max(0, options.InvariantLogLimit);
        _logger = logger;
    }

    /// <summary>Forget the counters (tests). The configuration is kept.</summary>
    public static void ResetCounters()
    {
        Sites.Clear();
        Interlocked.Exchange(ref _failures, 0);
        Interlocked.Increment(ref _epoch);
    }

    /// <summary>Every call site that has failed, most frequent first (part of each crash report).</summary>
    public static IReadOnlyList<InvariantFailure> Failures()
    {
        var list = new List<InvariantFailure>(Sites.Count);
        foreach (KeyValuePair<InvariantSite, SiteCounter> pair in Sites)
        {
            list.Add(new InvariantFailure(pair.Key.Member, pair.Key.File, pair.Key.Line, pair.Value.Count, pair.Value.LastMessage)
            {
                FirstSeen = pair.Value.FirstSeen,
                LastSeen = pair.Value.LastSeen,
            });
        }

        list.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        return list;
    }

    /// <summary>
    /// Record the failures raised in the current logical call flow (this thread and the tasks it awaits or
    /// starts) until the result is disposed. For tests: <see cref="FailureCount"/> and <see cref="Failures()"/>
    /// are process-wide and move when an unrelated test class fails a check in parallel, so an exact count is
    /// asserted on a capture. The pass path of every check is unaffected (the scope is read on failure only).
    /// </summary>
    public static InvariantCapture Capture()
    {
        var capture = new InvariantCapture(ActiveCapture.Value);
        ActiveCapture.Value = capture;
        return capture;
    }

    internal static void EndCapture(InvariantCapture capture)
    {
        if (ReferenceEquals(ActiveCapture.Value, capture))
        {
            ActiveCapture.Value = capture.Outer;
        }
    }

    /// <summary>
    /// Debug-only precondition: evaluated by Debug builds of the calling assembly, removed from Release
    /// builds together with its condition. Throws <see cref="InvariantViolationException"/> on failure.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Assert(
        [DoesNotReturnIf(false)] bool condition,
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (!condition)
        {
            FailAssert(message, member, file, line);
        }
    }

    /// <summary>
    /// <see cref="Assert(bool, string, string, string, int)"/> with a message that is formatted only when the
    /// condition is false (nothing is allocated while it holds).
    /// </summary>
    [Conditional("DEBUG")]
    public static void Assert(
        [DoesNotReturnIf(false)] bool condition,
        [InterpolatedStringHandlerArgument("condition")] ref InvariantMessageHandler message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (!condition)
        {
            FailAssert(message.ToStringAndClear(), member, file, line);
        }
    }

    /// <summary>
    /// Release-mode invariant: always evaluated. Returns the condition, so a caller writes
    /// <c>if (!Invariant.Check(...)) { refuse; }</c> and keeps its own fail-closed handling under the default
    /// <c>Continue</c> policy; <c>FailFast</c> never returns from a failure.
    /// </summary>
    public static bool Check(
        bool condition,
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (condition)
        {
            return true;
        }

        FailCheck(message, member, file, line);
        return false;
    }

    /// <summary>
    /// <see cref="Check(bool, string, string, string, int)"/> with a message that is formatted only when the
    /// condition is false (nothing is allocated while it holds).
    /// </summary>
    public static bool Check(
        bool condition,
        [InterpolatedStringHandlerArgument("condition")] ref InvariantMessageHandler message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (condition)
        {
            return true;
        }

        FailCheck(message.ToStringAndClear(), member, file, line);
        return false;
    }

    /// <summary>
    /// Release-mode non-null invariant: returns <c>true</c> when <paramref name="value"/> is non-null;
    /// otherwise records a check failure and returns <c>false</c>.
    /// </summary>
    public static bool CheckNotNull<T>(
        [NotNullWhen(true)] T? value,
        string name,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        where T : class
    {
        if (value is not null)
        {
            return true;
        }

        FailCheck($"Expected '{name}' to be non-null.", member, file, line);
        return false;
    }

    /// <summary>
    /// Release-mode finite-float invariant: returns <c>true</c> when <see cref="float.IsFinite(float)"/> holds;
    /// otherwise records a check failure and returns <c>false</c>.
    /// </summary>
    public static bool CheckFinite(
        float value,
        string name,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (float.IsFinite(value))
        {
            return true;
        }

        FailCheck($"Expected '{name}' to be finite, got {value}.", member, file, line);
        return false;
    }

    /// <summary>
    /// Release-mode world-coordinate invariant: returns <c>true</c> when <paramref name="x"/>, <paramref name="y"/>,
    /// and <paramref name="z"/> are finite and within <c>[-64000, 64000]</c>; otherwise records a check failure and returns <c>false</c>.
    /// </summary>
    public static bool CheckFiniteCoordinates(
        float x,
        float y,
        float z,
        string context = "coordinates",
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            && MathF.Abs(x) <= 64_000f && MathF.Abs(y) <= 64_000f && MathF.Abs(z) <= 64_000f)
        {
            return true;
        }

        FailCheck($"Invalid {context} ({x}, {y}, {z}).", member, file, line);
        return false;
    }

    /// <summary>
    /// Release-mode inclusive integer range invariant: returns <c>true</c> when <paramref name="value"/> lies in
    /// <c>[min..max]</c>; otherwise records a check failure and returns <c>false</c>.
    /// </summary>
    public static bool CheckRange(
        int value,
        int min,
        int max,
        string name,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (value >= min && value <= max)
        {
            return true;
        }

        FailCheck($"Expected '{name}' in [{min}..{max}], got {value}.", member, file, line);
        return false;
    }

    /// <summary>
    /// Stop in the attached debugger, when one is attached and <c>Diagnostics:BreakOnInvariant</c> is on.
    /// Otherwise nothing: an unattended process never waits for a debugger. Called by every failure
    /// path; callers may also call it directly at a point they want to inspect.
    /// </summary>
    public static void DebugBreak()
    {
        if (_breakOnFailure && Debugger.IsAttached)
        {
            Debugger.Break();
        }
    }

    private static void FailAssert(string message, string member, string file, int line)
    {
        string fileName = Record(message, member, file, line, "Assert");
        throw new InvariantViolationException($"Invariant violated at {member} ({fileName}:{line}): {message}", member, fileName, line);
    }

    private static void FailCheck(string message, string member, string file, int line)
        => Record(message, member, file, line, "Check");

    /// <summary>The common failure path: count, log (bounded per site), break, and honour the policy. Returns the file name.</summary>
    private static string Record(string message, string member, string file, int line, string kind)
    {
        string fileName = Path.GetFileName(file);
        long total = Interlocked.Increment(ref _failures);
        SiteCounter site = Sites.GetOrAdd(new InvariantSite(member, fileName, line), static _ => new SiteCounter());
        long count = site.Record(message);

        if (count <= _logLimit)
        {
            string suppressed = count == _logLimit ? " (further failures of this site are counted, not logged)" : string.Empty;
            ILogger? logger = _logger;
            if (logger is not null)
            {
                logger.LogError(
                    "Invariant {Kind} failed at {Member} ({File}:{Line}), failure {Count} of this site, {Total} in total{Suppressed}: {Message}",
                    kind, member, fileName, line, count, total, suppressed, message);
            }
            else
            {
                Console.Error.WriteLine($"Invariant {kind} failed at {member} ({fileName}:{line}), failure {count} of this site, {total} in total{suppressed}: {message}");
            }
        }

        ActiveCapture.Value?.Add(new InvariantFailureEvent(kind, member, fileName, line, message));
        DebugBreak();

        if (_policy == InvariantPolicy.FailFast)
        {
            // The in-process crash dump (Diagnostics:CrashDump) first; it is skipped when the runtime's own FailFast dump is enabled.
            CrashHandler.Current?.WriteCrashDump(failFast: true);
            Environment.FailFast($"Invariant violated at {member} ({fileName}:{line}): {message}");
        }

        return fileName;
    }

    private readonly record struct InvariantSite(string Member, string File, int Line);

    private sealed class SiteCounter
    {
        private long _count;
        private long _firstTicks;
        private long _lastTicks;

        public long Count => Volatile.Read(ref _count);

        public string? LastMessage { get; private set; }

        public DateTimeOffset? FirstSeen => Volatile.Read(ref _firstTicks) is var ticks and not 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

        public DateTimeOffset? LastSeen => Volatile.Read(ref _lastTicks) is var ticks and not 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

        public long Record(string message)
        {
            long now = DateTimeOffset.UtcNow.UtcTicks;
            LastMessage = message;
            Interlocked.CompareExchange(ref _firstTicks, now, 0);
            Interlocked.Exchange(ref _lastTicks, now);
            return Interlocked.Increment(ref _count);
        }
    }
}

/// <summary>One call site that has failed at least once.</summary>
public sealed record InvariantFailure(string Member, string File, int Line, long Count, string? LastMessage)
{
    /// <summary>The site's first failure since start or the last reset (null for a failure built by hand, such as a test's).</summary>
    public DateTimeOffset? FirstSeen { get; init; }

    /// <summary>The site's latest failure (null for a failure built by hand).</summary>
    public DateTimeOffset? LastSeen { get; init; }
}

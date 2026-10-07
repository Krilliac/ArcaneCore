using System.Diagnostics;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// A monotonic microsecond clock. Every watchdog monitor takes its time from here so tests drive
/// them with a fake clock; production uses <see cref="System"/>. Reading it never allocates.
/// </summary>
public abstract class WatchdogClock
{
    /// <summary>Microseconds since an arbitrary origin; never decreases, never wraps within a process lifetime.</summary>
    public abstract long NowMicros { get; }

    /// <summary>The process clock (<see cref="Stopwatch.GetTimestamp"/>).</summary>
    public static WatchdogClock System { get; } = new StopwatchClock();

    private sealed class StopwatchClock : WatchdogClock
    {
        // Double factor: a long multiply by 1_000_000 would overflow after ~3 hours of nanosecond timestamps on Linux.
        private static readonly double MicrosPerTick = 1_000_000.0 / Stopwatch.Frequency;

        public override long NowMicros => (long)(Stopwatch.GetTimestamp() * MicrosPerTick);
    }
}

/// <summary>Microsecond helpers shared by the monitors.</summary>
internal static class Micros
{
    public const long PerMilli = 1_000;

    public const long PerSecond = 1_000_000;

    public static long FromMillis(long ms) => ms * PerMilli;

    public static long FromSeconds(long seconds) => seconds * PerSecond;

    public static long ToMillis(long micros) => micros / PerMilli;
}

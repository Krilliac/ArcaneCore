using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArcaneCore.Game.Maps;

/// <summary>How the world thread waits for its next tick (<c>World:TickTimer</c>).</summary>
public enum WorldTickTimer
{
    /// <summary>
    /// Sleep until shortly before the due time, then yield/spin the rest (at most <see cref="WorldTickWaiter.DefaultSpinMarginMicros"/>
    /// per tick). On Windows the sleep is a high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Windows 10
    /// 1803 and later), so no process-wide timeBeginPeriod is needed; a Windows without that timer falls back to
    /// <see cref="Legacy"/>. Elsewhere the event wait (already precise to well under a millisecond) sleeps the whole
    /// milliseconds and the spin covers the sub-millisecond rest.
    /// </summary>
    Precise,

    /// <summary>The plain event wait in whole milliseconds (the behaviour before 2026-10-08; ~15.6 ms steps on Windows).</summary>
    Legacy,
}

/// <summary>
/// The world thread's wait until a due time on its <see cref="Stopwatch"/> clock, interruptible by the stop signal. One
/// instance per world thread; not thread-safe.
/// </summary>
public sealed class WorldTickWaiter : IDisposable
{
    /// <summary>
    /// The part of a wait that is spun (with yields) instead of slept on the high-resolution timer, which wakes late by up to
    /// about a millisecond, never early. Measured in docs/integration/perf-limits-20261008.md.
    /// </summary>
    public const long DefaultSpinMarginMicros = 0;

    private readonly ManualResetEventSlim _stop;
    private readonly WorldTickTimer _mode;
    private readonly long _spinMarginTicks;
    private readonly HighResolutionTimer? _timer;
    private readonly WaitHandle[]? _handles;

    public WorldTickWaiter(WorldTickTimer mode, ManualResetEventSlim stop, long spinMarginMicros = DefaultSpinMarginMicros)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentOutOfRangeException.ThrowIfNegative(spinMarginMicros);
        _stop = stop;
        _mode = mode;
        _spinMarginTicks = spinMarginMicros * Stopwatch.Frequency / 1_000_000;
        if (mode == WorldTickTimer.Precise && OperatingSystem.IsWindows())
        {
            _timer = HighResolutionTimer.TryCreate();
            if (_timer is not null)
            {
                _handles = [_timer, stop.WaitHandle];
            }
            else
            {
                _mode = WorldTickTimer.Legacy;
            }
        }
    }

    /// <summary>The mode in effect (a Windows without the high-resolution timer runs <see cref="WorldTickTimer.Legacy"/>).</summary>
    public WorldTickTimer Mode => _mode;

    /// <summary>Whether the precise wait sleeps on a high-resolution waitable timer (Windows) rather than the event wait.</summary>
    public bool HighResolution => _timer is not null;

    /// <summary>
    /// Wait until <paramref name="clock"/> reaches <paramref name="dueTicks"/> (Stopwatch ticks) or the stop signal is set.
    /// <paramref name="legacyWaitMs"/> is the whole-millisecond wait the legacy mode uses.
    /// </summary>
    public void WaitUntil(Stopwatch clock, long dueTicks, int legacyWaitMs)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (_mode == WorldTickTimer.Legacy)
        {
            if (legacyWaitMs > 0)
            {
                _stop.Wait(legacyWaitMs);
            }

            return;
        }

        long sleep = dueTicks - (_timer is not null ? _spinMarginTicks : 0) - clock.ElapsedTicks;
        if (sleep > 0)
        {
            if (_timer is not null)
            {
                // Relative due time in 100 ns units (negative = relative).
                _timer.Set(-Math.Max(1, sleep * 10_000_000 / Stopwatch.Frequency));
                if (WaitHandle.WaitAny(_handles!) == 1)
                {
                    return;
                }
            }
            else
            {
                int ms = (int)(sleep * 1000 / Stopwatch.Frequency);
                if (ms > 0 && _stop.Wait(ms))
                {
                    return;
                }
            }
        }

        // The rest (the margin, or the sub-millisecond remainder of the event wait): yield while far, spin while near.
        long yieldUntil = dueTicks - (200 * Stopwatch.Frequency / 1_000_000);
        while (true)
        {
            long now = clock.ElapsedTicks;
            if (now >= dueTicks || _stop.IsSet)
            {
                return;
            }

            if (now < yieldUntil)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    /// <summary>A Windows high-resolution waitable timer as a <see cref="WaitHandle"/> (WaitAny with the stop event).</summary>
    private sealed class HighResolutionTimer : WaitHandle
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;

        private HighResolutionTimer(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }

        public static HighResolutionTimer? TryCreate()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                return null;
            }

            SafeWaitHandle handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return new HighResolutionTimer(handle);
        }

        public void Set(long relativeDue100Ns)
        {
            if (!SetWaitableTimer(SafeWaitHandle, ref relativeDue100Ns, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                throw new InvalidOperationException($"SetWaitableTimer failed ({Marshal.GetLastPInvokeError()})");
            }
        }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks; these calls run only on Windows behind OperatingSystem checks
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine,
            IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
#pragma warning restore SYSLIB1054
    }
}

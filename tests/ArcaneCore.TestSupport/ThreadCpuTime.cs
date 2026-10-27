using System.Globalization;
using System.Runtime.InteropServices;

namespace ArcaneCore.TestSupport;

/// <summary>
/// CPU time the calling thread has run, for cost bounds in performance tests. Unlike wall time it does not grow while the thread waits for
/// a core, so a bound on it holds on a runner shared with other test processes (Node1 runs up to five gates at once; one measured pass of
/// PacketHeuristicsPerformanceTests read 5.2 ms per tick of wall time there against a 5 ms bound). Linux reads
/// <c>/proc/thread-self/schedstat</c> (ns); Windows calls <c>GetThreadTimes</c> (kernel plus user, 100 ns units, advanced at the scheduler
/// tick, so measure windows of a second or so). Null where neither is available: the caller then falls back to wall time and says so.
/// </summary>
public static class ThreadCpuTime
{
    /// <summary>The calling thread's CPU time so far, or null when the platform gives none.</summary>
    public static TimeSpan? Now()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                // "<ns on cpu> <ns waiting> <timeslices>"; a line of another shape gives no CPU time rather than an exception.
                string text = File.ReadAllText("/proc/thread-self/schedstat");
                int space = text.IndexOf(' ');
                return space > 0 && long.TryParse(text.AsSpan(0, space), NumberStyles.None, CultureInfo.InvariantCulture, out long ns)
                    ? TimeSpan.FromTicks(ns / 100) : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user))
            return TimeSpan.FromTicks(kernel + user);
        return null;
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks; these two calls run only on Windows behind OperatingSystem.IsWindows
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
#pragma warning restore SYSLIB1054
}

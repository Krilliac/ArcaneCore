namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// The audible alert of the crash and fault paths (<see cref="CrashHandler"/>, <see cref="FaultLedger"/>, the host start-up
/// beep): a short console beep when running in an interactive terminal. Lives in Diagnostics so the crash paths do not
/// reach into the operator console namespace.
/// </summary>
public static class CrashAlert
{
    private static long s_lastBeepTickMs;

    /// <summary>Optional test hook invoked instead of <see cref="Console.Beep()"/> when set.</summary>
    public static Action? BeepOverride { get; set; }

    /// <summary>
    /// Emits a short console beep when running in an interactive terminal (or invokes <see cref="BeepOverride"/> in tests),
    /// rate-limited to at most once per second unless <paramref name="bypassCooldown"/> is true. Never throws.
    /// </summary>
    public static bool TryBeep(bool checkErrorStream = false, bool bypassCooldown = false)
    {
        Action? overrideHook = BeepOverride;
        if (overrideHook is null)
        {
            if (!Environment.UserInteractive || (checkErrorStream ? Console.IsErrorRedirected : Console.IsOutputRedirected))
            {
                return false;
            }
        }

        if (!bypassCooldown)
        {
            long nowMs = Environment.TickCount64;
            long lastMs = Interlocked.Read(ref s_lastBeepTickMs);
            if (lastMs != 0 && nowMs - lastMs < 1000)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref s_lastBeepTickMs, nowMs, lastMs) != lastMs)
            {
                return false;
            }
        }

        try
        {
            if (overrideHook is not null)
            {
                overrideHook();
            }
            else
            {
                Console.Beep();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

namespace ArcaneCore.Kernel.Ops.ServerConsole;

/// <summary>
/// Names a daemon's console window (worldserver, realmserver) so an operator running several can tell them apart. Only an
/// interactive console is titled: under a supervisor, a service or redirected output
/// there is no window, and the title is skipped.
/// </summary>
public static class ServerConsoleTitle
{
    /// <summary>The title: the daemon's name, then the port it listens on, if known.</summary>
    public static string Compose(string daemon, string? port) =>
        string.IsNullOrWhiteSpace(port) ? $"ArcaneCore {daemon}" : $"ArcaneCore {daemon} :{port.Trim()}";

    /// <summary>Titles this process's console window when it has one. Never throws: a title is cosmetic.</summary>
    public static void TrySet(string daemon, string? port)
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            Console.Title = Compose(daemon, port);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // No console to title (a detached process): nothing to do.
        }
    }

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

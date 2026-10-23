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
}

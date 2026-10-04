using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Decides whether the console sink may emit ANSI colour. Colour is on only when the mode asks for it, stdout is a terminal (not a
/// file or pipe), <c>NO_COLOR</c> is unset or empty (https://no-color.org) and, on Windows, the console accepts virtual terminal
/// processing (enabled here through <c>SetConsoleMode</c>; any failure, including no console at all, means plain output).
/// The two halves are separate: <see cref="TerminalAllowsColor()"/> is the environment's verdict, fixed for the process, and the
/// mode is the configuration's, which reloads. Pure where it can be: the overloads taking explicit facts are the tested core.
/// </summary>
public static class ConsoleColorSupport
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>The decision for the running process, for the mode it runs in now.</summary>
    public static bool Resolve(ConsoleMode mode)
        => Resolve(mode, Console.IsOutputRedirected, Environment.GetEnvironmentVariable("NO_COLOR"), TryEnableWindowsVirtualTerminal);

    /// <summary>The decision from explicit facts (tests). <paramref name="enableTerminal"/> runs only when everything else allows colour.</summary>
    public static bool Resolve(ConsoleMode mode, bool outputRedirected, string? noColor, Func<bool> enableTerminal)
        => mode == ConsoleMode.Color && TerminalAllowsColor(outputRedirected, noColor, enableTerminal);

    /// <summary>
    /// The environment's half of the decision, independent of the configured mode: stdout is a terminal, <c>NO_COLOR</c> is unset or
    /// empty, and the terminal accepts SGR. The provider probes it once (the first time <c>Color</c> is asked for) and combines it
    /// with the mode of every reload, so Plain to Color works on a real terminal.
    /// </summary>
    public static bool TerminalAllowsColor()
        => TerminalAllowsColor(Console.IsOutputRedirected, Environment.GetEnvironmentVariable("NO_COLOR"), TryEnableWindowsVirtualTerminal);

    /// <summary>The capability from explicit facts (tests). <paramref name="enableTerminal"/> runs only when the two cheap facts allow colour.</summary>
    public static bool TerminalAllowsColor(bool outputRedirected, string? noColor, Func<bool> enableTerminal)
    {
        if (outputRedirected || !string.IsNullOrEmpty(noColor))
        {
            return false;
        }

        return enableTerminal();
    }

    /// <summary>On Windows, turns on VT processing for stdout; true elsewhere (every supported Linux terminal understands SGR).</summary>
    public static bool TryEnableWindowsVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return EnableWindowsVirtualTerminal();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool EnableWindowsVirtualTerminal()
    {
        IntPtr handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        if (!GetConsoleMode(handle, out uint mode))
        {
            return false;
        }

        if ((mode & EnableVirtualTerminalProcessing) != 0)
        {
            return true;
        }

        return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks; these three calls run only on Windows behind OperatingSystem.IsWindows
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
#pragma warning restore SYSLIB1054
}

namespace ArcaneCore.Kernel.Ops;

/// <summary>
/// The process exit-code contract shared by supervisors (systemd, Docker, Windows service
/// recovery, scripts). 0, 1 and 2 are the vmangos ShutdownExitCode values (World.h:76-81:
/// SHUTDOWN_EXIT_CODE, ERROR_EXIT_CODE, RESTART_EXIT_CODE). A supervisor that loops on code 2
/// reproduces the vmangos run-mangosd restart loop.
/// </summary>
public static class ExitCodes
{
    /// <summary>Normal stop (vmangos SHUTDOWN_EXIT_CODE).</summary>
    public const int Success = 0;

    /// <summary>Runtime failure (vmangos ERROR_EXIT_CODE).</summary>
    public const int Failure = 1;

    /// <summary>Restart requested (vmangos RESTART_EXIT_CODE). Only a restart request may use 2.</summary>
    public const int Restart = 2;

    /// <summary>
    /// The configuration is invalid (sysexits EX_CONFIG, an ArcaneCore addition: vmangos has only 0/1/2).
    /// Supervisors must not restart on it; systemd: RestartPreventExitStatus=78.
    /// </summary>
    public const int InvalidConfiguration = 78;

    /// <summary>Unknown operations verb or bad verb arguments (sysexits EX_USAGE). Never 2: that means restart.</summary>
    public const int Usage = 64;

    /// <summary>
    /// An exception escaped every handler and Diagnostics:OnUnhandled (or OnUnobservedTask) is Exit (sysexits EX_SOFTWARE, an ArcaneCore addition). The crash report
    /// precedes it on standard error and in the log. The default policy, FailFast, aborts instead (134 on Linux, 0x80131623 on Windows) and leaves a dump when
    /// DOTNET_DbgEnableMiniDump is set. Supervisors may restart on either.
    /// </summary>
    public const int UnhandledException = 70;

    /// <summary>Largest code a shutdown command may ask for: 126-255 belong to shells (vmangos ServerCommands.cpp:424-430).</summary>
    public const int MaxRequested = 125;

    private static int _current;

    /// <summary>
    /// The code the process should return from Main; set by whatever decided to stop it. Only a shutdown
    /// request sets it, and a request is capped at <see cref="MaxRequested"/> (vmangos ServerCommands.cpp:424-430),
    /// so a value outside 0..125 is a caller bug; it is still stored, so the stop is not lost.
    /// </summary>
    public static int Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            Diagnostics.Invariant.Check(value >= 0 && value <= MaxRequested, $"exit code {value} is outside 0..{MaxRequested}");
            Volatile.Write(ref _current, value);
        }
    }
}

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

    /// <summary>Largest code a shutdown command may ask for: 126-255 belong to shells (vmangos ServerCommands.cpp:424-430).</summary>
    public const int MaxRequested = 125;

    private static int _current;

    /// <summary>The code the process should return from Main; set by whatever decided to stop it.</summary>
    public static int Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}

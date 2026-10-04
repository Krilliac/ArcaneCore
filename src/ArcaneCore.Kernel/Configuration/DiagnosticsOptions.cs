namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>Diagnostics</c> configuration section: what a failed invariant does, and what ends the
/// process when an exception escapes every handler (docs/ops/invariants.md). Every default keeps
/// the process behaving as the .NET runtime would without this section, except that each event is
/// now written to the log first. Read once when the daemon starts; none of these keys is live.
/// </summary>
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>
    /// What a failed <c>Invariant.Check</c> (the release-mode check) does after it is logged and counted.
    /// <c>Continue</c> (default): the caller goes on with its own fail-closed handling (refuse the packet,
    /// drop the connection, throw). <c>FailFast</c>: the process aborts at once with the message, as the
    /// mangos <c>MANGOS_ASSERT</c> macro does in every build. Debug-build <c>Invariant.Assert</c> throws
    /// <c>InvariantViolationException</c> under <c>Continue</c> and aborts under <c>FailFast</c>.
    /// </summary>
    public InvariantPolicy OnInvariant { get; set; } = InvariantPolicy.Continue;

    /// <summary>
    /// Stop in the debugger on a failed invariant (<c>Debugger.Break</c>). Only acts when a debugger is
    /// attached, so it is safe to leave on in a development configuration; default off, because an
    /// unattended process must never wait for a debugger prompt.
    /// </summary>
    public bool BreakOnInvariant { get; set; }

    /// <summary>
    /// How many failures of one invariant (one call site) are written to the log; later failures of
    /// that site are only counted (the counters are part of every crash report). 0 logs none, every
    /// failure is still counted. Default 10: a hot-path check that fails per packet cannot flood the log.
    /// </summary>
    public int InvariantLogLimit { get; set; } = 10;

    /// <summary>
    /// What ends the process after an unhandled exception (<c>AppDomain.UnhandledException</c>): the
    /// report is always written first. <c>FailFast</c> (default): <c>Environment.FailFast</c>, which is
    /// what the runtime does on its own; the process aborts (exit status 134, SIGABRT, on Linux;
    /// 0x80131623 / Watson on Windows) and a dump is written when <c>DOTNET_DbgEnableMiniDump=1</c>.
    /// <c>Exit</c>: <c>Environment.Exit(70)</c> (<c>ExitCodes.UnhandledException</c>, sysexits
    /// EX_SOFTWARE), an exit code a supervisor can match; no dump.
    /// </summary>
    public UnhandledExceptionPolicy OnUnhandled { get; set; } = UnhandledExceptionPolicy.FailFast;

    /// <summary>
    /// What an exception a faulted <c>Task</c> nobody awaited does when the finalizer finds it
    /// (<c>TaskScheduler.UnobservedTaskException</c>). <c>Log</c> (default): the report is written and the
    /// exception marked observed, which is the .NET runtime's own behaviour (since .NET 4.5) made visible.
    /// <c>Exit</c> and <c>FailFast</c> end the process as <c>OnUnhandled</c> describes.
    /// </summary>
    public UnobservedTaskPolicy OnUnobservedTask { get; set; } = UnobservedTaskPolicy.Log;

    /// <summary>
    /// Log every exception at the moment it is thrown (<c>AppDomain.FirstChanceException</c>), before any
    /// handler sees it, at Debug level. A development aid for finding swallowed exceptions; it is honoured
    /// only by a Debug build of the Kernel (the hook is compiled out of Release), where a Release daemon
    /// logs once that the key is ignored. Default off: the hook runs on every throw, caught or not.
    /// </summary>
    public bool FirstChanceExceptions { get; set; }
}

/// <summary>What a failed release-mode invariant does after it is logged and counted.</summary>
public enum InvariantPolicy
{
    /// <summary>Log, count, and let the caller's own fail-closed handling run (default).</summary>
    Continue = 0,

    /// <summary>Abort the process with the message (the mangos MANGOS_ASSERT behaviour).</summary>
    FailFast = 1,
}

/// <summary>How the process ends after an unhandled exception; the report is always written first.</summary>
public enum UnhandledExceptionPolicy
{
    /// <summary>Environment.FailFast: the runtime's own behaviour (abort, dump when enabled).</summary>
    FailFast = 0,

    /// <summary>Environment.Exit with ExitCodes.UnhandledException (70).</summary>
    Exit = 1,
}

/// <summary>What an unobserved task exception does; the report is always written first.</summary>
public enum UnobservedTaskPolicy
{
    /// <summary>Mark it observed and go on (the runtime's own behaviour, now logged).</summary>
    Log = 0,

    /// <summary>Environment.Exit with ExitCodes.UnhandledException (70).</summary>
    Exit = 1,

    /// <summary>Environment.FailFast.</summary>
    FailFast = 2,
}

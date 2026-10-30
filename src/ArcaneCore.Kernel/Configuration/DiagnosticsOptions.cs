namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>Diagnostics</c> configuration section: what a failed invariant does, and what ends the
/// process when an exception escapes every handler (docs/ops/invariants.md). Every default keeps
/// the process behaving as the .NET runtime would without this section, except that each event is
/// now written to the log first. Read once when the daemon starts; none of these keys is live.
/// </summary>
[ConfigDaemons(ConfigDaemon.Both)]
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
    /// 0x80131623 / Watson on Windows). <c>Exit</c>: <c>Environment.Exit(70)</c> (<c>ExitCodes.UnhandledException</c>, sysexits
    /// EX_SOFTWARE), an exit code a supervisor can match. Under both, the daemon writes a dump of itself first
    /// (<c>Diagnostics:CrashDump</c>); the runtime's own <c>DOTNET_DbgEnableMiniDump=1</c> dump covers a FailFast too, and then this one is skipped.
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

    /// <summary>
    /// How many occurrences of one fault (an exception type thrown from one place, caught at one of the
    /// server's catch-and-log sites) are logged in full. After that the site logs that fault at most once a
    /// minute, with "(N more since)"; every occurrence is still counted in the fault ledger
    /// (<c>.server faults</c>, the <c>arcanecore_faults_total</c> metric). 0 logs the first occurrence and then
    /// once a minute. Default 10.
    /// </summary>
    public int FaultLogLimit { get; set; } = 10;

    /// <summary>
    /// How many distinct faults the fault ledger keeps. A fault beyond the cap is counted into one overflow
    /// entry, never dropped. Default 512; a lower value bounds memory on a server that throws a new exception
    /// signature per request.
    /// </summary>
    public int FaultMaxSignatures { get; set; } = 512;

    /// <summary>
    /// Emit a short console beep when the daemon finishes starting in an interactive console session
    /// (<c>Environment.UserInteractive</c> and standard output not redirected), matching the classic MaNGOS
    /// <c>BeepAtStart</c> cue. Never beeps under a service, test runner or redirected output. Default off.
    /// </summary>
    public bool BeepOnStartup { get; set; }

    /// <summary>
    /// Emit a short console beep when the crash hooks or the fault ledger log an exception in an interactive
    /// console session (<c>Environment.UserInteractive</c> and standard error not redirected), rate-limited to at
    /// most one beep per second so a fault storm cannot stall or flood the terminal. Default off.
    /// </summary>
    public bool BeepOnException { get; set; }

    /// <summary>
    /// Show a dialog (a Win32 message box) when the process is about to end because of an unhandled exception, an unobserved task
    /// exception under an <c>OnUnobservedTask</c> policy that terminates, or a world tick hang the watchdog ends. It names the exception, its
    /// message and full stack with inner exceptions, the daemon and version, thread, time, fault-ledger site and signature, and the crash
    /// report file (a <c>crash-report.*.txt</c> file written next to the daemon), crash journal and log file. It
    /// appears after the crash is logged and recorded, and the exit policy runs when it is closed. Only a Windows console session with no
    /// redirected standard stream, outside CI, opens one; a service, test run, redirected or non-Windows process is unaffected. Default off:
    /// an unattended process must never wait on a dialog.
    /// </summary>
    public bool CrashDialog { get; set; }

    /// <summary>
    /// Also show a dialog the first time each fault signature is caught and survived (the fault ledger, <c>.server faults</c>), on a
    /// background thread so the faulting thread never waits, at most one open at a time (faults while one is open are only logged).
    /// Needs the same interactive Windows console session as <c>CrashDialog</c>; takes effect once the host has started. Default off: a
    /// fault storm of new signatures still means one box after another.
    /// </summary>
    public bool FaultDialog { get; set; }

    /// <summary>
    /// The dump the daemon writes of itself when an unhandled exception (or an unobserved task exception under an <c>OnUnobservedTask</c>
    /// policy that ends the process, or a world tick hang the watchdog ends) is about to end it, so a crash leaves a <c>.dmp</c> even when
    /// the executable was started by hand (<c>Diagnostics:CrashDump</c>).
    /// </summary>
    public CrashDumpOptions CrashDump { get; } = new();
}

/// <summary>What <c>Diagnostics:CrashDump:Mode</c> writes when the process crashes.</summary>
public enum CrashDumpMode
{
    /// <summary>No in-process crash dump (the runtime's <c>DOTNET_DbgEnableMiniDump</c> dump still applies when set).</summary>
    Off = 0,

    /// <summary>Threads, modules and stacks without the heap (<c>DumpType.Normal</c>). Small and fast.</summary>
    Mini = 1,

    /// <summary>Managed heap and what it needs (<c>DumpType.WithHeap</c>). Some hosts refuse it with E_INVALIDARG, then the dump falls back to Mini.</summary>
    Heap = 2,

    /// <summary>All memory (<c>DumpType.Full</c>); about the size of the working set (about 7 GB and a minute on a loaded world).</summary>
    Full = 3,
}

/// <summary>
/// The <c>Diagnostics:CrashDump</c> section. On a terminating crash, after the crash report is written and before the process ends, the daemon
/// asks its own runtime for a dump (the same mechanism as <c>.debug dump</c>), trying Full, then Heap, then Mini until one is written. A dump is
/// skipped when <c>DOTNET_DbgEnableMiniDump=1</c> is set and the process ends by FailFast (the runtime writes that one), but is still written
/// under the <c>Exit</c> policy, where the runtime writes none. SECURITY: a dump holds everything in the process memory, session keys and
/// password material in flight included. It stays on the local disk in the daemon's own directory, like the launcher's dumps; keep the directory
/// private, do not attach a dump to a public issue, and delete the files after the analysis (<c>MaxFiles</c> bounds how many are kept).
/// LIMITS: native and fatal runtime errors (an access violation in native code, a stack overflow, an out-of-memory abort of the runtime) never
/// reach a managed handler, so only the runtime's own <c>DOTNET_DbgEnableMiniDump=1</c> (with <c>DOTNET_DbgMiniDumpName</c> and
/// <c>DOTNET_DbgMiniDumpType</c>) covers them; keep that launcher setting as well. Read once when the daemon starts.
/// </summary>
public sealed class CrashDumpOptions
{
    /// <summary>
    /// What to write: <c>Off</c>, <c>Mini</c>, <c>Heap</c> or <c>Full</c>. Default <c>Full</c>: it only acts when the process is already
    /// crashing, so it changes nothing for a healthy server. A failure of the chosen type falls back Full, Heap, Mini and is logged.
    /// </summary>
    public CrashDumpMode Mode { get; set; } = CrashDumpMode.Full;

    /// <summary>
    /// The directory the dump files are written to, named <c>&lt;daemon&gt;-&lt;pid&gt;-&lt;yyyyMMdd-HHmmss&gt;.dmp</c> (UTC). Empty (default)
    /// means <c>dumps</c> beside the log file (the resolved <c>Logging:ArcaneCore:File:Path</c>, so <c>logs/dumps</c> by default, the directory
    /// <c>.debug dump</c> uses), or <c>dumps</c> in the daemon's own directory when file logging is off. Created on the first dump, readable by
    /// the daemon's account only on Linux and macOS.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// How many of this feature's dump files are kept in the directory: after a dump is written the oldest beyond this count are deleted. Only files
    /// named by the pattern above are touched. 0 keeps every file. Default 5.
    /// </summary>
    public int MaxFiles { get; set; } = 5;

    /// <summary>
    /// Free space, in GiB, the dump volume must have for a Full dump; below it Full falls back to Heap. Default 16, the launcher's threshold
    /// (<c>tools/ops/live/LiveDiagnostics.ps1</c>). 0 never falls back for space.
    /// </summary>
    public int FullMinFreeGB { get; set; } = 16;

    /// <summary>
    /// The most seconds one dump attempt may take before the daemon gives up on it and ends the process anyway; a dump that times out is not
    /// retried with a smaller type, because a runtime that cannot answer once will not answer again. A Full dump of a 7 GB process takes about a
    /// minute. Default 120.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
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

using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>Shows a crash or fault dialog and returns when the person dismissed it. A seam so tests never open a real window.</summary>
public interface ICrashDialogPresenter
{
    /// <summary>Show <paramref name="text"/> under <paramref name="title"/> and block until it is dismissed. May throw; the caller records the failure and carries on.</summary>
    void Show(string title, string text);
}

/// <summary>
/// The Win32 <c>MessageBoxW</c> (user32.dll): an error icon, an OK button, top-most and brought to the foreground. No WinForms or WPF
/// dependency; on any other platform <see cref="Show"/> throws <see cref="PlatformNotSupportedException"/> (the caller never gets there,
/// see <see cref="CrashDialog.ShouldShow"/>). Ctrl+C in a message box copies its whole text to the clipboard.
/// </summary>
public sealed class MessageBoxCrashDialogPresenter : ICrashDialogPresenter
{
    // winuser.h: MB_OK 0x0, MB_ICONERROR (MB_ICONHAND) 0x10, MB_SETFOREGROUND 0x10000, MB_TOPMOST 0x40000.
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopMost = 0x00040000;

    private MessageBoxCrashDialogPresenter()
    {
    }

    public static MessageBoxCrashDialogPresenter Instance { get; } = new();

    public void Show(string title, string text)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the crash dialog is a Win32 message box and exists only on Windows");
        }

        // MessageBoxW returns 0 when it could not create the box (no desktop, a non-interactive window station).
        if (MessageBoxW(IntPtr.Zero, text, title, MbOk | MbIconError | MbSetForeground | MbTopMost) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks; this call runs only on Windows behind OperatingSystem.IsWindows
    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
#pragma warning restore SYSLIB1054
}

/// <summary>What decides whether this process may open a dialog: the platform and whether a person is sitting at it.</summary>
/// <param name="IsWindows">The dialog is a Win32 message box.</param>
/// <param name="UserInteractive"><see cref="Environment.UserInteractive"/>: false under a Windows service (session 0).</param>
/// <param name="InputRedirected">Standard input is a pipe or a file (a launcher script, a test runner).</param>
/// <param name="OutputRedirected">Standard output is a pipe or a file.</param>
/// <param name="ErrorRedirected">Standard error is a pipe or a file.</param>
/// <param name="ContinuousIntegration">The <c>CI</c> or <c>TF_BUILD</c> environment variable is set (GitHub Actions, Azure Pipelines and most CI systems set one).</param>
public readonly record struct CrashDialogEnvironment(
    bool IsWindows,
    bool UserInteractive,
    bool InputRedirected,
    bool OutputRedirected,
    bool ErrorRedirected,
    bool ContinuousIntegration)
{
    /// <summary>Read this process's environment. Never throws: a probe that fails counts as "not interactive".</summary>
    public static CrashDialogEnvironment Detect()
    {
        return new CrashDialogEnvironment(
            OperatingSystem.IsWindows(),
            Probe(static () => Environment.UserInteractive, fallback: false),
            Probe(static () => Console.IsInputRedirected, fallback: true),
            Probe(static () => Console.IsOutputRedirected, fallback: true),
            Probe(static () => Console.IsErrorRedirected, fallback: true),
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")));
    }

    /// <summary>Why no dialog can be shown here, or null when one can.</summary>
    public string? WhyNot()
    {
        if (!IsWindows)
        {
            return "not Windows";
        }

        if (!UserInteractive)
        {
            return "not an interactive session (a service?)";
        }

        if (InputRedirected || OutputRedirected || ErrorRedirected)
        {
            return "standard input, output or error is redirected";
        }

        return ContinuousIntegration ? "a CI environment variable (CI, TF_BUILD) is set" : null;
    }

    private static bool Probe(Func<bool> read, bool fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A console that cannot be queried is no interactive console: the fallback says "no dialog".
            return fallback;
        }
    }
}

/// <summary>Everything a fatal crash dialog says, gathered by <see cref="CrashDialog"/> on the crashing thread (tests build one directly).</summary>
public sealed record CrashDialogFacts
{
    public required CrashKind Kind { get; init; }

    public Exception? Exception { get; init; }

    /// <summary>The process aborts (<see cref="Environment.FailFast(string)"/>) once the box is closed; otherwise it exits with code 70.</summary>
    public required bool FailFast { get; init; }

    /// <summary>What decided that, e.g. <c>Diagnostics:OnUnhandled = Exit</c>.</summary>
    public required string DecidedBy { get; init; }

    public required string Daemon { get; init; }

    public string? Version { get; init; }

    public int ProcessId { get; init; }

    public int ThreadId { get; init; }

    public string? ThreadName { get; init; }

    public required DateTime TimeUtc { get; init; }

    /// <summary>The fault-ledger site the crash hook recorded (<c>unhandled</c>, <c>unobserved-task</c>), or null when nothing was recorded.</summary>
    public string? FaultSite { get; init; }

    /// <summary>The fault-ledger signature id of that record, or null.</summary>
    public string? FaultId { get; init; }

    /// <summary>The file holding this text and the full crash report, or null when it could not be written.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Why the report file could not be written (set when <see cref="ReportPath"/> is null).</summary>
    public string? ReportError { get; init; }

    /// <summary>The terminating-crash journal of this daemon (<see cref="CrashJournalPaths"/>), or null when none is configured.</summary>
    public string? JournalPath { get; init; }

    /// <summary>The configured log file in either format (the resolved <c>Logging:ArcaneCore:File:Path</c>, see <c>LogFileSinkPlan</c>) when the file sink is enabled, or null.</summary>
    public string? LogPath { get; init; }

    /// <summary>Where the runtime writes a dump, or null when no dump is configured (<c>DOTNET_DbgEnableMiniDump</c>).</summary>
    public string? DumpNote { get; init; }
}

/// <summary>The title and text of a dialog: <see cref="Text"/> is complete (it goes into the report file), <see cref="BoxText"/> is cut to fit a screen.</summary>
public readonly record struct CrashDialogText(string Title, string Text, string BoxText);

/// <summary>What <see cref="CrashDialog.TryShowFatal"/> did.</summary>
public enum CrashDialogOutcome
{
    /// <summary><c>Diagnostics:CrashDialog</c> is off: nothing was done (the default).</summary>
    Disabled,

    /// <summary>The option is on but this session cannot show a dialog (<see cref="CrashDialogEnvironment.WhyNot"/>): nothing was done.</summary>
    NotInteractive,

    /// <summary>The dialog was shown and dismissed.</summary>
    Shown,

    /// <summary>The dialog (or its report file) failed; the failure was recorded and the crash path went on.</summary>
    Failed,
}

/// <summary>
/// The opt-in crash dialog (<c>Diagnostics:CrashDialog</c>, default off) and new-fault dialog (<c>Diagnostics:FaultDialog</c>, default off) for
/// interactive local runs, where a console scrolling past a crash is easy to miss. <see cref="CrashHandler"/> calls <see cref="TryShowFatal"/>
/// after the crash report is written and the fault recorded, and before the exit policy runs; the dialog never changes that policy or the exit
/// code, and a dialog that fails is recorded (<c>diagnostics.crash-dialog</c>) and skipped. Only a Windows desktop session whose standard
/// streams are all attached to a console, outside CI, ever opens one (<see cref="ShouldShow"/>): a service, a test run, a redirected or
/// Linux process does exactly what it did before.
/// <para>
/// Seams: <see cref="Presenter"/> and <see cref="EnvironmentProbe"/> are process-wide and replaced only by tests and the diagnostics probe,
/// the way <see cref="CrashAlert.BeepOverride"/> is. <see cref="LogFilePath"/> is set by the diagnostics hosted service once the host starts.
/// </para>
/// </summary>
public static class CrashDialog
{
    /// <summary>The site key a failing dialog is recorded under in the fault ledger.</summary>
    public const string FailureSite = "diagnostics.crash-dialog";

    /// <summary>Lines of text the box itself shows; the rest is in the report file. A taller box would push its OK button off the screen.</summary>
    public const int MaxBoxLines = 40;

    /// <summary>Characters of one line the box shows before cutting it.</summary>
    public const int MaxBoxLineLength = 400;

    private static readonly object FaultGate = new();
    private static int s_faultDialogOpen;
    private static ICrashDialogPresenter s_presenter = MessageBoxCrashDialogPresenter.Instance;
    private static Func<CrashDialogEnvironment> s_environmentProbe = CrashDialogEnvironment.Detect;

    /// <summary>What opens the dialog (the Win32 message box; tests substitute a recorder).</summary>
    public static ICrashDialogPresenter Presenter
    {
        get => Volatile.Read(ref s_presenter);
        set => Volatile.Write(ref s_presenter, value ?? MessageBoxCrashDialogPresenter.Instance);
    }

    /// <summary>How the environment is read (tests substitute a fixed one).</summary>
    public static Func<CrashDialogEnvironment> EnvironmentProbe
    {
        get => Volatile.Read(ref s_environmentProbe);
        set => Volatile.Write(ref s_environmentProbe, value ?? CrashDialogEnvironment.Detect);
    }

    /// <summary>Where the crash report file is written; null is <see cref="AppContext.BaseDirectory"/>, next to the crash journal.</summary>
    public static string? ReportDirectory { get; set; }

    /// <summary>The configured log file (text or JSON lines), shown in the dialog; set when the host starts.</summary>
    public static string? LogFilePath { get; set; }

    /// <summary>Whether a dialog opens: the option is on, the platform is Windows, and a person is at an interactive console session.</summary>
    public static bool ShouldShow(bool enabled, CrashDialogEnvironment environment) => enabled && environment.WhyNot() is null;

    /// <summary>
    /// Show the fatal-crash dialog when <paramref name="enabled"/> and the session allows it: write the full text and <paramref name="report"/>
    /// to a report file, then open the box and wait until it is dismissed. Never throws; a failure is recorded under <see cref="FailureSite"/>
    /// and written to standard error, and the caller goes on to its exit policy unchanged.
    /// </summary>
    public static CrashDialogOutcome TryShowFatal(bool enabled, CrashKind kind, Exception? exception, bool failFast, string decidedBy, string? faultSite, string? faultId, string? report)
    {
        if (!enabled)
        {
            return CrashDialogOutcome.Disabled;
        }

        try
        {
            if (!ShouldShow(enabled, EnvironmentProbe()))
            {
                return CrashDialogOutcome.NotInteractive;
            }

            DateTime nowUtc = DateTime.UtcNow;
            CrashDialogFacts facts = GatherFacts(kind, exception, failFast, decidedBy, faultSite, faultId, nowUtc);
            string directory = ReportDirectory ?? AppContext.BaseDirectory;
            string path = CrashJournalPaths.ReportPathFor(directory, facts.Daemon, nowUtc);
            facts = facts with { ReportPath = path };
            CrashDialogText text = Compose(facts);
            bool failed = false;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, text.Text + "\n\n" + (report ?? "(no crash report was rendered)") + "\n");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed = true;
                RecordFailure(ex, "could not write the crash report file " + path);
                text = Compose(facts with { ReportPath = null, ReportError = ex.GetType().Name + ": " + ex.Message });
            }

            Presenter.Show(text.Title, text.BoxText);
            return failed ? CrashDialogOutcome.Failed : CrashDialogOutcome.Shown;
        }
        catch (Exception ex)
        {
            // Deliberately a catch-all, out-of-memory included: the dialog must never mask the crash or skip the exit policy.
            RecordFailure(ex, "the crash dialog failed");
            return CrashDialogOutcome.Failed;
        }
    }

    /// <summary>
    /// Open the new-fault dialog for <paramref name="notice"/> on a background thread when <paramref name="enabled"/>, the session allows it,
    /// the notice is the first occurrence of its signature (count 1), it is not a terminating fault (the crash dialog covers those), it is not
    /// the overflow bucket or a fault of the dialogs themselves (subsystem <c>diagnostics</c>), and no fault dialog is open already. Returns
    /// whether a dialog was started; the recording thread never waits for it. A presenter that throws reaches <paramref name="onFailure"/>.
    /// </summary>
    public static bool TryShowFault(bool enabled, FaultNotice notice, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (!enabled || notice.Terminating || notice.Count != 1 || notice.Id == FaultLedger.OverflowId
            || DiagnosticSubsystem.OfSite(notice.Site) == "diagnostics")
        {
            return false;
        }

        if (!ShouldShow(enabled, EnvironmentProbe()))
        {
            return false;
        }

        lock (FaultGate)
        {
            if (s_faultDialogOpen != 0)
            {
                return false; // one at a time: the faults behind it are in the log and the ledger
            }

            s_faultDialogOpen = 1;
        }

        try
        {
            StartFaultDialog(ComposeFault(notice, CrashJournalPaths.ProcessName(), LogFilePath), Presenter, onFailure);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The thread could not start: free the slot, then let the ledger's notice handler see the failure.
            lock (FaultGate)
            {
                s_faultDialogOpen = 0;
            }

            throw;
        }
    }

    private static void StartFaultDialog(CrashDialogText text, ICrashDialogPresenter presenter, Action<Exception>? onFailure)
    {
        var thread = new Thread(() =>
        {
            try
            {
                presenter.Show(text.Title, text.BoxText);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onFailure?.Invoke(ex);
            }
            finally
            {
                lock (FaultGate)
                {
                    s_faultDialogOpen = 0;
                }
            }
        })
        {
            IsBackground = true, // never keeps the process alive
            Name = "ArcaneCore.FaultDialog",
        };
        thread.Start();
    }

    /// <summary>The fatal dialog's title and text. Pure.</summary>
    public static CrashDialogText Compose(CrashDialogFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        string what = facts.Kind switch
        {
            CrashKind.Unhandled => "an unhandled exception",
            CrashKind.UnobservedTask => "an unobserved task exception",
            _ => "an exception",
        };
        string title = facts.Daemon + " crashed: " + (facts.Exception?.GetType().Name ?? "fatal error");

        var head = new StringBuilder(1024);
        head.Append(facts.Daemon).Append(" stopped because of ").Append(what).Append(".\n");
        head.Append("When you close this box the process ").Append(DescribeOutcome(facts.FailFast, facts.DecidedBy)).Append(".\n\n");
        head.Append("Exception: ").Append(facts.Exception?.GetType().FullName ?? "(no managed exception object)").Append('\n');
        if (facts.Exception is not null)
        {
            head.Append("Message: ").Append(facts.Exception.Message).Append('\n');
        }

        head.Append("Daemon: ").Append(facts.Daemon);
        if (!string.IsNullOrEmpty(facts.Version))
        {
            head.Append(' ').Append(facts.Version);
        }

        head.Append(" (pid ").Append(facts.ProcessId.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        head.Append("Thread: ").Append(facts.ThreadId.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(facts.ThreadName is { Length: > 0 } name ? "\"" + name + "\"" : "(unnamed)").Append('\n');
        head.Append("Time: ").Append(facts.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC (")
            .Append(facts.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" local)\n");
        head.Append("Fault ledger: ");
        if (facts.FaultSite is { Length: > 0 } site)
        {
            head.Append("site ").Append(site);
            if (facts.FaultId is { Length: > 0 } id)
            {
                head.Append(", signature ").Append(id);
            }

            head.Append('\n');
        }
        else
        {
            head.Append("(not recorded)\n");
        }

        head.Append("Full report: ").Append(facts.ReportPath ?? ("(not written: " + (facts.ReportError ?? "unknown error") + ")")).Append('\n');
        if (facts.JournalPath is { Length: > 0 } journal)
        {
            head.Append("Crash journal: ").Append(journal).Append('\n');
        }

        if (facts.LogPath is { Length: > 0 } log)
        {
            head.Append("Log file: ").Append(log).Append('\n');
        }

        if (facts.DumpNote is { Length: > 0 } dump)
        {
            head.Append("Dump: ").Append(dump).Append('\n');
        }

        head.Append("\nStack trace (outer exception first, inner exceptions after it):\n");
        string stack = facts.Exception?.ToString() ?? "(none)";
        string full = head + stack.Replace("\r\n", "\n", StringComparison.Ordinal);
        return new CrashDialogText(title, full, Fit(full, facts.ReportPath));
    }

    /// <summary>The new-fault dialog's title and text; reads only the current thread and the clock.</summary>
    public static CrashDialogText ComposeFault(FaultNotice notice, string daemon, string? logPath)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var sb = new StringBuilder(512);
        sb.Append(daemon).Append(" caught a new fault and kept running.\n");
        sb.Append("Only the first occurrence of each fault signature opens a box, and one box at a time (Diagnostics:FaultDialog).\n\n");
        sb.Append("Exception: ").Append(notice.ExceptionType).Append('\n');
        sb.Append("Message: ").Append(notice.Message).Append('\n');
        sb.Append("Fault ledger: site ").Append(notice.Site).Append(", signature ").Append(notice.Id).Append(" (.server faults show ").Append(notice.Id).Append(")\n");
        sb.Append("Thread: ").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(Thread.CurrentThread.Name is { Length: > 0 } name ? "\"" + name + "\"" : "(unnamed)").Append('\n');
        sb.Append("Time: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\n");
        sb.Append("Log file: ").Append(logPath ?? "(none configured; the full exception is on the console)").Append('\n');
        sb.Append("\nArcaneCore frames (the full stack is in the log):\n");
        sb.Append(string.IsNullOrEmpty(notice.CondensedStack) ? "(none)" : notice.CondensedStack.Replace(" <- ", "\n  <- ", StringComparison.Ordinal));
        string text = sb.ToString();
        return new CrashDialogText(daemon + " fault: " + ShortType(notice.ExceptionType) + " at " + notice.Site, text, Fit(text, logPath));
    }

    /// <summary>What a crash does next, for the dialog, naming what decided it (<paramref name="decidedBy"/>).</summary>
    public static string DescribeOutcome(bool failFast, string decidedBy)
        => failFast
            ? "aborts (Environment.FailFast; " + decidedBy + ")"
            : "exits with code " + Ops.ExitCodes.UnhandledException.ToString(CultureInfo.InvariantCulture) + " (" + decidedBy + ")";

    private static CrashDialogFacts GatherFacts(CrashKind kind, Exception? exception, bool failFast, string decidedBy, string? faultSite, string? faultId, DateTime nowUtc)
    {
        Thread current = Thread.CurrentThread;
        return new CrashDialogFacts
        {
            Kind = kind,
            Exception = exception,
            FailFast = failFast,
            DecidedBy = decidedBy,
            Daemon = Guard(CrashJournalPaths.ProcessName, "ArcaneCore"),
            Version = Guard(static () => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, null),
            ProcessId = Environment.ProcessId,
            ThreadId = current.ManagedThreadId,
            ThreadName = current.Name,
            TimeUtc = nowUtc,
            FaultSite = faultId is null ? null : faultSite,
            FaultId = faultId,
            JournalPath = Guard(static () => FaultLedger.Shared.CrashJournalPath, null),
            LogPath = LogFilePath,
            DumpNote = DescribeDump(failFast),
        };
    }

    private static string? DescribeDump(bool failFast)
    {
        string? enabled = Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump");
        if (enabled != "1")
        {
            return null;
        }

        if (!failFast)
        {
            return "DOTNET_DbgEnableMiniDump=1 is set, but an exit writes no dump (only FailFast does)";
        }

        string? name = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName");
        return "the runtime writes one when the process aborts: " + (string.IsNullOrEmpty(name) ? "the runtime's default location (the temp directory)" : name);
    }

    /// <summary>Cuts <paramref name="text"/> to <see cref="MaxBoxLines"/> lines of <see cref="MaxBoxLineLength"/> characters, naming <paramref name="fullTextPath"/> when lines were dropped.</summary>
    public static string Fit(string text, string? fullTextPath)
    {
        string[] lines = text.Split('\n');
        var sb = new StringBuilder(Math.Min(text.Length, MaxBoxLines * (MaxBoxLineLength + 2)));
        int shown = Math.Min(lines.Length, MaxBoxLines);
        for (int i = 0; i < shown; i++)
        {
            string line = lines[i].TrimEnd('\r');
            sb.Append(line.Length > MaxBoxLineLength ? string.Concat(line.AsSpan(0, MaxBoxLineLength), "...") : line);
            if (i < shown - 1)
            {
                sb.Append('\n');
            }
        }

        if (lines.Length > MaxBoxLines)
        {
            sb.Append("\n... ").Append((lines.Length - MaxBoxLines).ToString(CultureInfo.InvariantCulture)).Append(" more line(s)");
            sb.Append(fullTextPath is null ? "" : " in " + fullTextPath);
        }

        return sb.ToString();
    }

    private static string ShortType(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        return dot >= 0 && dot < fullName.Length - 1 ? fullName[(dot + 1)..] : fullName;
    }

    private static void RecordFailure(Exception ex, string what)
    {
        try
        {
            FaultLedger.Shared.Record(FailureSite, ex);
            Console.Error.WriteLine($"crash dialog: {what}: {ex.GetType().Name}: {ex.Message}");
            Console.Error.Flush();
        }
        catch
        {
            // Deliberately a catch-all: reporting the dialog's own failure must not stop the crash path either.
        }
    }

    private static T Guard<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return fallback;
        }
    }
}

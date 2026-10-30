using System.Globalization;
using System.Text;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>A crash report file a child wrote (<c>crash-report.&lt;process&gt;.&lt;yyyyMMdd-HHmmss&gt;Z.txt</c>) and when it was last written.</summary>
public readonly record struct SupervisorCrashReportFile(string Path, DateTime LastWriteTimeUtc);

/// <summary>The file-system access of <see cref="SupervisorCrashDialog"/>, a seam so tests never touch the disk. Neither member may throw.</summary>
public interface ISupervisorCrashFiles
{
    /// <summary>The crash report files <paramref name="processName"/> wrote into <paramref name="directory"/>; empty when there are none or the directory is unreadable.</summary>
    IReadOnlyList<SupervisorCrashReportFile> FindReports(string directory, string processName);

    /// <summary>The first <paramref name="maxChars"/> characters of <paramref name="path"/>, whether the file was longer, or null when it cannot be read.</summary>
    (string Text, bool Truncated)? ReadText(string path, int maxChars);
}

/// <summary>The real disk.</summary>
public sealed class SystemSupervisorCrashFiles : ISupervisorCrashFiles
{
    public static SystemSupervisorCrashFiles Instance { get; } = new();

    public IReadOnlyList<SupervisorCrashReportFile> FindReports(string directory, string processName)
    {
        try
        {
            string prefix = SupervisorCrashDialog.ReportFilePrefix(processName);
            var found = new List<SupervisorCrashReportFile>();
            foreach (string path in Directory.EnumerateFiles(directory, prefix + "*Z.txt"))
            {
                found.Add(new SupervisorCrashReportFile(path, File.GetLastWriteTimeUtc(path)));
            }

            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    public (string Text, bool Truncated)? ReadText(string path, int maxChars)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxChars + 1];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            return read > maxChars ? (new string(buffer, 0, maxChars), true) : (new string(buffer, 0, read), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>What the supervisor knows about a child that exited unexpectedly.</summary>
/// <param name="Name">The child's process name in the supervisor configuration.</param>
/// <param name="Role">The child's role (<see cref="SupervisedRoles"/>).</param>
/// <param name="ProcessId">The pid of the incarnation that exited.</param>
/// <param name="ExitCode">Its exit code (nonzero).</param>
/// <param name="StartedUtc">When the supervisor launched that incarnation; an older crash report is not its report.</param>
/// <param name="Decision">What the supervisor does next, e.g. <c>restarting in 1 s (restart #1)</c> or <c>giving up (crash loop)</c>.</param>
/// <param name="ExecutablePath">The child's executable, as configured.</param>
/// <param name="Arguments">The child's command line arguments, as configured.</param>
/// <param name="WorkingDirectory">The child's configured working directory, or null.</param>
public sealed record SupervisorChildExit(
    string Name,
    string Role,
    int ProcessId,
    int ExitCode,
    DateTimeOffset StartedUtc,
    string Decision,
    string ExecutablePath,
    string Arguments,
    string? WorkingDirectory);

/// <summary>
/// The crash dialog of the cluster supervisor. A supervised child's standard streams are always redirected, so its own crash dialog
/// (<see cref="CrashDialog"/>) never opens; this one opens in the supervisor's own interactive console session when a child exits
/// unexpectedly with a nonzero exit code, shows the crash report that child wrote (when it wrote one) and what the supervisor does next.
/// <para>
/// It never delays supervision: <see cref="OnChildExited"/> does a cheap gate check and starts a background thread
/// (<c>ArcaneCore.SupervisorCrashDialog</c>) that finds the report, composes the text and blocks in the presenter. One box is open at a time
/// across all children; an exit while one is open is only logged (the supervisor log already records it). A presenter or file failure is
/// recorded with <see cref="FaultSite"/> and never propagates.
/// </para>
/// <para>Thread affinity: <see cref="OnChildExited"/> may be called from any monitor thread; all other work runs on the dialog thread. Instances are thread-safe.</para>
/// </summary>
public sealed class SupervisorCrashDialog
{
    /// <summary>The fault-ledger site a failing dialog is recorded under.</summary>
    public const string FaultSite = "cluster.supervisor.crash-dialog";

    /// <summary>Characters of the child's crash report the box shows; the rest is in the report file.</summary>
    public const int MaxReportChars = 12_000;

    private const string ThreadName = "ArcaneCore.SupervisorCrashDialog";
    private const string ReportTimestampSuffix = "yyyyMMdd-HHmmssZ.txt";

    private readonly bool _enabled;
    private readonly ILogger _logger;
    private readonly ICrashDialogPresenter? _presenter;
    private readonly Func<CrashDialogEnvironment>? _environmentProbe;
    private readonly ISupervisorCrashFiles _files;
    private readonly string? _dumpNote;
    private int _open;
    private int _refusalLogged;

    /// <param name="enabled">The supervisor process's own <c>Diagnostics:CrashDialog</c>.</param>
    /// <param name="logger">Where refusals and failures are logged (null: nowhere, the fault ledger still records failures).</param>
    /// <param name="presenter">Opens the box; null uses <see cref="CrashDialog.Presenter"/> at the time of the crash.</param>
    /// <param name="environmentProbe">Reads the supervisor's session; null uses <see cref="CrashDialog.EnvironmentProbe"/>.</param>
    /// <param name="files">Report lookup; null uses the real disk.</param>
    /// <param name="dumpNote">A configured crash-dump location to mention when a child left no report, or null.</param>
    public SupervisorCrashDialog(
        bool enabled,
        ILogger? logger = null,
        ICrashDialogPresenter? presenter = null,
        Func<CrashDialogEnvironment>? environmentProbe = null,
        ISupervisorCrashFiles? files = null,
        string? dumpNote = null)
    {
        _enabled = enabled;
        _logger = logger ?? NullLogger.Instance;
        _presenter = presenter;
        _environmentProbe = environmentProbe;
        _files = files ?? SystemSupervisorCrashFiles.Instance;
        _dumpNote = dumpNote;
    }

    /// <summary>True while a box is open (or about to open).</summary>
    public bool IsOpen => Volatile.Read(ref _open) != 0;

    /// <summary>Logs once, at start, whether child crash dialogs can open in this session (only when <c>Diagnostics:CrashDialog</c> is on).</summary>
    public void LogAvailability()
    {
        if (!_enabled)
        {
            return;
        }

        string? whyNot = (_environmentProbe ?? CrashDialog.EnvironmentProbe)().WhyNot();
        _logger.LogInformation(
            "Supervisor: Diagnostics:CrashDialog is on, so a child that exits unexpectedly opens a dialog here: {Availability}",
            whyNot is null ? "dialogs open in this session" : "no dialog opens in this session (" + whyNot + ")");
    }

    /// <summary>
    /// Starts the dialog for <paramref name="exit"/> when the option is on, the session allows it and no other box is open. Returns whether a dialog
    /// was started. Never throws and never waits for the box.
    /// </summary>
    public bool OnChildExited(SupervisorChildExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        if (exit.ExitCode == 0)
        {
            return false;
        }

        try
        {
            if (!_enabled)
            {
                LogRefusalOnce("Diagnostics:CrashDialog is off in the supervisor");
                return false;
            }

            string? whyNot = (_environmentProbe ?? CrashDialog.EnvironmentProbe)().WhyNot();
            if (whyNot is not null)
            {
                LogRefusalOnce(whyNot);
                return false;
            }

            if (Interlocked.CompareExchange(ref _open, 1, 0) != 0)
            {
                _logger.LogInformation(
                    "Supervisor: a crash dialog is already open; the exit of {Process} (pid {ProcessId}, code {ExitCode}) is only logged",
                    exit.Name, exit.ProcessId, exit.ExitCode);
                return false;
            }

            try
            {
                new Thread(() => Run(exit)) { IsBackground = true, Name = ThreadName }.Start();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Volatile.Write(ref _open, 0);
                throw;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault(FaultSite, ex, "the supervisor crash dialog for {Process} could not start", exit.Name);
            return false;
        }
    }

    private void LogRefusalOnce(string why)
    {
        if (Interlocked.Exchange(ref _refusalLogged, 1) == 0)
        {
            _logger.LogInformation("Supervisor: no crash dialog opens for child exits ({Why}); further refusals are not logged", why);
        }
    }

    private void Run(SupervisorChildExit exit)
    {
        try
        {
            ICrashDialogPresenter presenter = _presenter ?? CrashDialog.Presenter;
            presenter.Show(Title(exit), Compose(exit));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault(FaultSite, ex, "the supervisor crash dialog for {Process} failed", exit.Name);
        }
        finally
        {
            Volatile.Write(ref _open, 0);
        }
    }

    /// <summary>The box title.</summary>
    public static string Title(SupervisorChildExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        return exit.Name + " crashed (exit code " + exit.ExitCode.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>The box text: the facts, what the supervisor does next, and the child's newest crash report written since it started (or why there is none).</summary>
    public string Compose(SupervisorChildExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        var sb = new StringBuilder(1024);
        sb.Append("The supervised ").Append(exit.Role).Append(" process '").Append(exit.Name).Append("' (pid ")
            .Append(exit.ProcessId.ToString(CultureInfo.InvariantCulture)).Append(") exited unexpectedly with exit code ")
            .Append(exit.ExitCode.ToString(CultureInfo.InvariantCulture)).Append(" (0x").Append(exit.ExitCode.ToString("X8", CultureInfo.InvariantCulture)).Append(").\n");
        sb.Append("Supervisor: ").Append(exit.Decision).Append(". This box does not delay it.\n");
        sb.Append("Started: ").Append(exit.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\n\n");

        string? directory = ReportDirectoryOf(exit);
        string processName = ProcessNameOf(exit);
        SupervisorCrashReportFile? newest = null;
        if (directory is not null)
        {
            foreach (SupervisorCrashReportFile file in _files.FindReports(directory, processName))
            {
                if (file.LastWriteTimeUtc >= exit.StartedUtc.UtcDateTime && (newest is null || file.LastWriteTimeUtc > newest.Value.LastWriteTimeUtc))
                {
                    newest = file;
                }
            }
        }

        if (newest is { } report && _files.ReadText(report.Path, MaxReportChars) is { } read)
        {
            sb.Append("Crash report ").Append(report.Path).Append(":\n\n").Append(read.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
            if (read.Truncated)
            {
                sb.Append("\n... full report: ").Append(report.Path);
            }

            return sb.ToString();
        }

        sb.Append("The child left no crash report for this run");
        sb.Append(directory is null ? " (its report directory could not be determined)" : " in " + directory);
        sb.Append(". A native crash, a stack overflow or Environment.FailFast ends the process before a report is written.\n");
        sb.Append("See the supervisor log and the child's own log for its last output.");
        if (!string.IsNullOrEmpty(_dumpNote))
        {
            sb.Append('\n').Append(_dumpNote);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The directory the child's crash dialog writes its report to: <c>CrashDialog.ReportDirectory ?? AppContext.BaseDirectory</c> in the child
    /// (<c>CrashDialog.TryShowFatal</c>). Nothing sets <c>ReportDirectory</c>, so it is the child's <see cref="AppContext.BaseDirectory"/>: the
    /// directory of its apphost executable, or of the assembly when it runs as <c>dotnet &lt;assembly&gt;.dll</c>. Null when it cannot be worked out.
    /// </summary>
    public static string? ReportDirectoryOf(SupervisorChildExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        string? target = EntryFileOf(exit);
        if (target is null)
        {
            return null;
        }

        try
        {
            string baseDirectory = string.IsNullOrWhiteSpace(exit.WorkingDirectory) ? Directory.GetCurrentDirectory() : exit.WorkingDirectory;
            return Path.GetDirectoryName(Path.GetFullPath(target, baseDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>The child's <see cref="CrashJournalPaths.ProcessName"/>: the entry assembly's simple name, i.e. the apphost's or the dll's file name without extension.</summary>
    public static string ProcessNameOf(SupervisorChildExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        return StemOf(EntryFileOf(exit) ?? exit.ExecutablePath);
    }

    /// <summary>The file name prefix (<c>crash-report.&lt;name&gt;.</c>) of the reports of <paramref name="processName"/>, derived from <see cref="CrashJournalPaths.ReportPathFor"/> so the naming stays in one place.</summary>
    public static string ReportFilePrefix(string processName)
    {
        string sample = Path.GetFileName(CrashJournalPaths.ReportPathFor(string.Empty, processName, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        return sample[..^ReportTimestampSuffix.Length];
    }

    /// <summary>The file name without a trailing <c>.exe</c> or <c>.dll</c> only: a Linux apphost called <c>ArcaneCore.World</c> keeps its dot.</summary>
    private static string StemOf(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    /// <summary>The assembly or apphost the child runs: the first <c>.dll</c> argument when the executable is <c>dotnet</c>, else the executable.</summary>
    private static string? EntryFileOf(SupervisorChildExit exit)
    {
        if (string.IsNullOrWhiteSpace(exit.ExecutablePath))
        {
            return null;
        }

        if (!string.Equals(StemOf(exit.ExecutablePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return exit.ExecutablePath;
        }

        string args = exit.Arguments.TrimStart();
        string first;
        if (args.StartsWith('"'))
        {
            int close = args.IndexOf('"', 1);
            first = close > 0 ? args[1..close] : args[1..];
        }
        else
        {
            int space = args.IndexOfAny([' ', '\t']);
            first = space > 0 ? args[..space] : args;
        }

        return first.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? first : null;
    }
}

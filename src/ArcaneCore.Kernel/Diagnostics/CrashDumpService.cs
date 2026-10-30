using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>Writes a dump of this process; the seam the dump tests replace so no real dump is written.</summary>
public interface ICrashDumpWriter
{
    /// <summary>Write the dump file synchronously (blocking). <paramref name="mode"/> is never <see cref="CrashDumpMode.Off"/>. Throws when the runtime refuses.</summary>
    void Write(CrashDumpMode mode, string path);
}

/// <summary>The real writer: <see cref="DiagnosticsClient.WriteDump(DumpType, string, bool)"/> against this process's own diagnostics endpoint. Shared by the crash dump and by <c>.debug dump</c>.</summary>
public sealed class DiagnosticsClientCrashDumpWriter : ICrashDumpWriter
{
    public void Write(CrashDumpMode mode, string path)
    {
        DumpType type = mode switch
        {
            CrashDumpMode.Full => DumpType.Full,
            CrashDumpMode.Heap => DumpType.WithHeap,
            CrashDumpMode.Mini => DumpType.Normal,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        new DiagnosticsClient(Environment.ProcessId).WriteDump(type, path, logDumpGeneration: false);
    }
}

/// <summary>What <see cref="CrashDumpService.Run"/> did: the file written (null when none), the type that succeeded, and the one-line note for the crash dialog.</summary>
public sealed record CrashDumpOutcome(string? Path, CrashDumpMode Mode, string Note);

/// <summary>
/// The in-process crash dump (<c>Diagnostics:CrashDump</c>): picks the directory and the file name, walks the Full, Heap, Mini fallback chain
/// with a timeout per attempt, restricts the file to its owner on Unix, and prunes the oldest files beyond <c>MaxFiles</c>.
/// <para>
/// Ownership: one instance per <see cref="CrashHandler"/>; no static state. Threading: <see cref="Run"/> is called on the crashing thread, which
/// blocks while the dump is written on a dedicated background thread (never the thread pool, which a crash may have starved), so a hung runtime
/// costs at most the timeout. Nothing here throws to the caller except out-of-memory; every failure is logged through the supplied callback.
/// </para>
/// </summary>
public sealed class CrashDumpService
{
    private readonly ICrashDumpWriter _writer;
    private readonly Func<string, long?> _freeBytes;

    /// <param name="writer">The dump writer; null uses the runtime's diagnostics client.</param>
    /// <param name="freeBytes">Free bytes on the volume of a path, or null when unknown (Full is then attempted); null uses the drive list.</param>
    public CrashDumpService(ICrashDumpWriter? writer = null, Func<string, long?>? freeBytes = null)
    {
        _writer = writer ?? new DiagnosticsClientCrashDumpWriter();
        _freeBytes = freeBytes ?? FreeBytesOnVolumeOf;
    }

    /// <summary>Whether the runtime itself writes a dump when the process aborts (<c>DOTNET_DbgEnableMiniDump=1</c>, or its <c>COMPlus_</c> spelling).</summary>
    public static bool RuntimeWritesDump()
        => Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump") == "1" || Environment.GetEnvironmentVariable("COMPlus_DbgEnableMiniDump") == "1";

    /// <summary>
    /// Which path writes the dump for a crash that ends by FailFast (<paramref name="failFast"/>) or by Exit: this service, the runtime, or none.
    /// Rendered into the crash report before the dump is attempted, so the report says who writes it.
    /// </summary>
    public static string DescribePlan(CrashDumpOptions options, bool failFast)
    {
        bool runtime = failFast && RuntimeWritesDump();
        if (runtime)
        {
            return "written by the runtime (DOTNET_DbgEnableMiniDump=1, createdump); no in-process dump (Diagnostics:CrashDump:Mode = " + options.Mode + ")";
        }

        return options.Mode == CrashDumpMode.Off
            ? "none (Diagnostics:CrashDump:Mode = Off" + (failFast ? "; DOTNET_DbgEnableMiniDump is not 1" : "; an exit writes no runtime dump") + ")"
            : "written in-process by ArcaneCore (Diagnostics:CrashDump:Mode = " + options.Mode + ") before the process ends"
                + (OperatingSystem.IsWindows() ? "; if Windows Error Reporting LocalDumps is configured for this executable it can write a second dump" : string.Empty);
    }

    /// <summary>The directory dumps go to: the configured one, else <c>dumps</c> beside the log file, else <c>dumps</c> in the daemon's directory.</summary>
    public static string ResolveDirectory(CrashDumpOptions options, string? logFilePath)
    {
        if (!string.IsNullOrWhiteSpace(options.Directory))
        {
            return Path.GetFullPath(options.Directory);
        }

        if (!string.IsNullOrWhiteSpace(logFilePath) && Path.GetDirectoryName(Path.GetFullPath(logFilePath)) is { Length: > 0 } logs)
        {
            return Path.Combine(logs, "dumps");
        }

        return Path.Combine(AppContext.BaseDirectory, "dumps");
    }

    /// <summary><c>&lt;daemon&gt;-&lt;pid&gt;-&lt;yyyyMMdd-HHmmss&gt;.dmp</c>, the daemon name made safe for a file name.</summary>
    public static string FileNameFor(string daemon, int processId, DateTime utc)
        => string.Create(CultureInfo.InvariantCulture, $"{CrashJournalPaths.SafeName(daemon)}-{processId}-{utc:yyyyMMdd-HHmmss}.dmp");

    /// <summary>
    /// Write the crash dump for a process that is about to end. <paramref name="failFast"/> says how it ends. Returns null when this service writes
    /// nothing (Mode Off, or the runtime writes the FailFast dump); otherwise the outcome, success or not.
    /// </summary>
    public CrashDumpOutcome? Run(CrashDumpOptions options, bool failFast, string daemon, string directory, DateTime utcNow, Action<LogLevel, string> log, Action<int>? announceDumping = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        if (options.Mode == CrashDumpMode.Off)
        {
            return null;
        }

        if (failFast && RuntimeWritesDump())
        {
            string name = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName") ?? Environment.GetEnvironmentVariable("COMPlus_DbgMiniDumpName") ?? string.Empty;
            string where = name.Length == 0 ? "the runtime's default location (the temp directory)" : name;
            log(LogLevel.Warning, "crash dump: skipped, the runtime writes one at FailFast (DOTNET_DbgEnableMiniDump=1) to " + where);
            return new CrashDumpOutcome(null, CrashDumpMode.Off, "the runtime writes one when the process aborts (DOTNET_DbgEnableMiniDump=1): " + where);
        }

        try
        {
            return WriteChain(options, daemon, directory, utcNow, log, announceDumping);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log(LogLevel.Error, "crash dump: failed before a dump was attempted: " + ex.GetType().Name + ": " + ex.Message);
            return new CrashDumpOutcome(null, CrashDumpMode.Off, "the in-process dump failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private CrashDumpOutcome WriteChain(CrashDumpOptions options, string daemon, string directory, DateTime utcNow, Action<LogLevel, string> log, Action<int>? announceDumping)
    {
        EnsureDirectory(directory, log);
        string path = UniquePath(directory, FileNameFor(daemon, Environment.ProcessId, utcNow));
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        CrashDumpMode requested = options.Mode;
        CrashDumpMode attempt = requested;
        if (attempt == CrashDumpMode.Full && options.FullMinFreeGB > 0)
        {
            long? free = _freeBytes(directory);
            long needed = options.FullMinFreeGB * 1024L * 1024 * 1024;
            if (free is { } freeBytes && freeBytes < needed)
            {
                log(LogLevel.Warning, string.Create(CultureInfo.InvariantCulture,
                    $"crash dump: {freeBytes / (1024d * 1024 * 1024):F1} GiB free in {directory}, below Diagnostics:CrashDump:FullMinFreeGB = {options.FullMinFreeGB}; Full falls back to Heap"));
                attempt = CrashDumpMode.Heap;
            }
        }

        string lastError = "no attempt was made";
        announceDumping?.Invoke(MaxWaitSeconds(options));
        for (; attempt >= CrashDumpMode.Mini; attempt--)
        {
            log(LogLevel.Critical, string.Create(CultureInfo.InvariantCulture,
                $"crash dump: writing a {attempt} dump to {path}. The process is suspended while it is written (a Full dump takes about a minute at 7 GB); waiting up to {(int)timeout.TotalSeconds} s for the runtime to answer, and for as long as the file keeps growing up to {MaxWaitSeconds(options)} s."));
            long start = Stopwatch.GetTimestamp();
            AttemptResult result = Attempt(attempt, path, timeout, MaxWaitSeconds(options));
            TimeSpan took = Stopwatch.GetElapsedTime(start);
            if (result.Error is null)
            {
                long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                RestrictToOwner(path, log);
                string fellBack = attempt == requested ? string.Empty : " (fell back from " + requested + ")";
                log(LogLevel.Critical, string.Create(CultureInfo.InvariantCulture, $"crash dump: written {path}, {attempt}{fellBack}, {bytes / (1024 * 1024)} MiB in {took.TotalSeconds:F1} s. It holds process memory, session keys included: keep it private."));
                Prune(options, daemon, directory, log);
                return new CrashDumpOutcome(path, attempt, "written by ArcaneCore in-process (" + attempt + fellBack + "): " + path);
            }

            lastError = result.Error;
            log(LogLevel.Error, string.Create(CultureInfo.InvariantCulture, $"crash dump: the {attempt} dump failed after {took.TotalSeconds:F1} s: {result.Error}"));
            if (result.StopChain)
            {
                // A timeout or an unreachable diagnostics endpoint will not be different for a smaller type (and each try can cost 30 s).
                RestrictToOwner(path, log);
                log(LogLevel.Error, "crash dump: giving up without trying a smaller type; a partial file may remain at " + path + " (it holds process memory)");
                break;
            }

            DeletePartial(path, log);
        }

        return new CrashDumpOutcome(null, CrashDumpMode.Off, "the in-process dump failed (" + lastError + "); see the log");
    }

    /// <summary>The most seconds the crash path waits for a dump in total: the answer timeout, then as long as the file grows, capped at three times the timeout. The heartbeat grace of the supervisors is sized from this.</summary>
    public static int MaxWaitSeconds(CrashDumpOptions options) => checked(3 * Math.Max(1, options.TimeoutSeconds));

    private readonly record struct AttemptResult(string? Error, bool StopChain);

    private AttemptResult Attempt(CrashDumpMode mode, string path, TimeSpan timeout, int maxWaitSeconds)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _writer.Write(mode, path);
            }
            catch (Exception ex)
            {
                // Deliberately a catch-all on this dedicated thread: an escaping exception here would end the crashing process before it logs the cause.
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "ArcaneCore.CrashDump",
        };
        thread.Start();
        if (!thread.Join(timeout))
        {
            // The runtime suspends the waiting thread too, so a long dump can outlast the timeout and still be completing. Only a file that
            // is missing, empty or no longer growing is a failure; one that grows is waited for, up to the cap.
            DateTime capAt = DateTime.UtcNow + TimeSpan.FromSeconds(maxWaitSeconds) - timeout;
            long last = SizeOf(path);
            while (true)
            {
                if (last <= 0)
                {
                    return new AttemptResult("no answer within " + (int)timeout.TotalSeconds + " s and no file was started", StopChain: true);
                }

                if (thread.Join(TimeSpan.FromSeconds(3)))
                {
                    break;
                }

                long now = SizeOf(path);
                if (now <= last)
                {
                    return new AttemptResult("no answer within " + (int)timeout.TotalSeconds + " s and the file stopped growing at " + now + " bytes", StopChain: true);
                }

                if (DateTime.UtcNow >= capAt)
                {
                    return new AttemptResult("still writing after " + maxWaitSeconds + " s (" + now + " bytes); not waiting any longer", StopChain: true);
                }

                last = now;
            }
        }

        Exception? caught = Volatile.Read(ref failure);
        return caught is null ? new AttemptResult(null, false) : new AttemptResult(caught.GetType().Name + ": " + caught.Message, IsEndpointFailure(caught));
    }

    /// <summary>Whether <paramref name="ex"/> says the diagnostics endpoint cannot be reached or did not answer, which a smaller dump type cannot fix (E_INVALIDARG and the like can).</summary>
    public static bool IsEndpointFailure(Exception ex) => ex is ServerNotAvailableException or TimeoutException;

    private static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string UniquePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        for (int n = 1; File.Exists(path) && n < 1000; n++)
        {
            path = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + "-" + n.ToString(CultureInfo.InvariantCulture) + ".dmp");
        }

        return path;
    }

    private static void EnsureDirectory(string directory, Action<LogLevel, string> log)
    {
        if (Directory.Exists(directory))
        {
            WarnIfReadableByOthers(directory, log);
            return;
        }

        string? parent = Path.GetDirectoryName(directory);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (OperatingSystem.IsWindows())
        {
            CreateOwnerOnlyDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Create the directory with a protected DACL: the current user, SYSTEM and Administrators, full control, nothing inherited from the parent.</summary>
    [SupportedOSPlatform("windows")]
    private static void CreateOwnerOnlyDirectory(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        SecurityIdentifier? user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        }

        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
    }

    private static void WarnIfReadableByOthers(string directory, Action<LogLevel, string> log)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(directory);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                log(LogLevel.Warning, "crash dump: " + directory + " is readable or writable by its group or by others (" + mode + "); a dump holds session keys, so restrict it (chmod 700)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log(LogLevel.Warning, "crash dump: could not read the mode of " + directory + ": " + ex.Message);
        }
    }

    private static void RestrictToOwner(string path, Action<LogLevel, string> log)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return; // on Windows the file inherits the directory's ACL
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log(LogLevel.Warning, "crash dump: could not restrict " + path + " to its owner: " + ex.Message);
        }
    }

    // A partial dump holds the same secrets as a whole one and is of no use: remove it.
    private static void DeletePartial(string path, Action<LogLevel, string> log)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                log(LogLevel.Warning, "crash dump: removed the partial file " + path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log(LogLevel.Error, "crash dump: the partial file " + path + " could not be removed; delete it by hand (it holds process memory): " + ex.Message);
        }
    }

    /// <summary>Delete the oldest files of this daemon's dump pattern beyond <see cref="CrashDumpOptions.MaxFiles"/>; returns how many were deleted.</summary>
    public static int Prune(CrashDumpOptions options, string daemon, string directory, Action<LogLevel, string> log)
    {
        if (options.MaxFiles <= 0)
        {
            return 0;
        }

        var pattern = new Regex("^" + Regex.Escape(CrashJournalPaths.SafeName(daemon)) + @"-\d+-\d{8}-\d{6}(?:-\d+)?\.dmp$", RegexOptions.CultureInvariant);
        int deleted = 0;
        try
        {
            List<FileInfo> ours = [.. new DirectoryInfo(directory).EnumerateFiles("*.dmp").Where(f => pattern.IsMatch(f.Name))];
            ours.Sort(static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = options.MaxFiles; i < ours.Count; i++)
            {
                try
                {
                    ours[i].Delete();
                    deleted++;
                    log(LogLevel.Information, "crash dump: pruned the old dump " + ours[i].FullName + " (Diagnostics:CrashDump:MaxFiles = " + options.MaxFiles + ")");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log(LogLevel.Warning, "crash dump: could not prune " + ours[i].FullName + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log(LogLevel.Warning, "crash dump: could not list " + directory + " to prune: " + ex.Message);
        }

        return deleted;
    }

    /// <summary>Free bytes on the volume that holds <paramref name="path"/>: the ready drive whose root is the longest prefix of the path; null when unknown.</summary>
    internal static long? FreeBytesOnVolumeOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            DriveInfo? best = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string root = drive.RootDirectory.FullName;
                if (drive.IsReady && full.StartsWith(root, comparison) && (best is null || root.Length > best.RootDirectory.FullName.Length))
                {
                    best = drive;
                }
            }

            return best?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

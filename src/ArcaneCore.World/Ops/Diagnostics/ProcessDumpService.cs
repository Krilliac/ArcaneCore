using ArcaneCore.Kernel.Diagnostics;
using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>What a dump contains. <see cref="Full"/> is the default: heap-type dumps fail on the production box (E_INVALIDARG).</summary>
public enum DumpKind
{
    /// <summary>All memory (DumpType.Full). About the size of the working set; the only type that worked on the 5.5 GB live heap.</summary>
    Full = 0,

    /// <summary>Managed heap and what it needs (DumpType.WithHeap). Smaller, but the runtime refused it on the live box with E_INVALIDARG.</summary>
    Heap = 1,

    /// <summary>Threads, modules and stacks without the heap (DumpType.Normal).</summary>
    Mini = 2,
}

/// <summary>Writes a dump of this process; the seam the dump tests replace so no real dump is written.</summary>
public interface IProcessDumpWriter
{
    /// <summary>Write the dump file synchronously (blocking; the caller runs it off the world thread). Throws when the runtime refuses.</summary>
    void Write(DumpKind kind, string path);
}

/// <summary>The real writer: the Kernel's <see cref="DiagnosticsClientCrashDumpWriter"/> (the one the crash dump uses) against this process's own diagnostics endpoint.</summary>
internal sealed class DiagnosticsClientDumpWriter : IProcessDumpWriter
{
    private readonly DiagnosticsClientCrashDumpWriter _inner = new();

    public void Write(DumpKind kind, string path)
    {
        CrashDumpMode mode = kind switch
        {
            DumpKind.Full => CrashDumpMode.Full,
            DumpKind.Heap => CrashDumpMode.Heap,
            DumpKind.Mini => CrashDumpMode.Mini,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        _inner.Write(mode, path);
    }
}

/// <summary>How a dump finished: where, how big, how long, and the error when it failed.</summary>
public sealed record DumpOutcome(bool Success, string Path, long Bytes, TimeSpan Duration, string? Error);

/// <summary>
/// The answer to a dump request. <see cref="Started"/> false: refused for <see cref="Message"/> and nothing was written.
/// <see cref="Completion"/> is non-null when started and completes (never faults) when the dump has been written or has failed.
/// </summary>
public sealed record DumpRequestResult(bool Started, string Message, string? Path, Task<DumpOutcome>? Completion);

/// <summary>
/// The gate and runner behind <c>.debug dump</c> (docs/ops/diagnostics.md). A dump holds every secret in memory, suspends the process while it is
/// written and can fill a disk, so a request is refused unless <c>Ops:Diagnostics:Dumps:Enabled</c> is set, no other dump is running, the
/// minimum interval has passed and the target volume has at least twice the process working set free. The write runs on the thread pool, never
/// on the world thread.
/// <para>
/// Ownership: the service owns nothing disposable. Threading: <see cref="Request"/> may be called from any thread; the single-dump rule is an
/// interlocked flag and the interval check reads one timestamp under a lock.
/// </para>
/// </summary>
public sealed class ProcessDumpService
{
    /// <summary>A dump needs this many times the working set free on the target volume.</summary>
    public const int FreeSpaceFactor = 2;

    /// <summary>The wording of the process-suspension warning, shared by the reply and the docs.</summary>
    public const string SuspensionWarning = "A full dump suspends the whole process (the world thread included) for roughly 1 minute at about 7 GB; expect a TickHang line when it ends.";

    private readonly DumpDiagnosticsOptions _options;
    private readonly string _directory;
    private readonly IProcessDumpWriter _writer;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<string, long?> _freeSpace;
    private readonly Func<long> _workingSet;
    private readonly object _gate = new();
    private long _lastStartTimestamp;
    private bool _hasStarted;
    private int _running;

    /// <param name="options">The <c>Ops:Diagnostics:Dumps</c> values.</param>
    /// <param name="directory">The resolved dump directory (see <see cref="ResolveDirectory"/>).</param>
    /// <param name="writer">The dump writer; null uses the runtime's diagnostics client.</param>
    /// <param name="logger">Receives the start, completion and failure lines.</param>
    /// <param name="time">The clock of the minimum-interval check; null uses the system clock.</param>
    /// <param name="freeSpace">Free bytes on the volume of a path, or null when unknown (a dump is then refused); null uses the drive list.</param>
    /// <param name="workingSet">The process working set in bytes; null uses <see cref="Environment.WorkingSet"/>.</param>
    public ProcessDumpService(DumpDiagnosticsOptions options, string directory, ILogger logger, IProcessDumpWriter? writer = null,
        TimeProvider? time = null, Func<string, long?>? freeSpace = null, Func<long>? workingSet = null)
    {
        _options = options;
        _directory = directory;
        _logger = logger;
        _writer = writer ?? new DiagnosticsClientDumpWriter();
        _time = time ?? TimeProvider.System;
        _freeSpace = freeSpace ?? FreeBytesOnVolumeOf;
        _workingSet = workingSet ?? (() => Environment.WorkingSet);
    }

    /// <summary>Whether <c>Ops:Diagnostics:Dumps:Enabled</c> is set.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>The directory dumps are written to.</summary>
    public string Directory => _directory;

    /// <summary>
    /// The configured directory, or (when empty) <c>dumps</c> beside the log file <paramref name="logFilePath"/>
    /// (the resolved <c>Logging:ArcaneCore:File:Path</c>, either format). Relative paths resolve against the working directory.
    /// </summary>
    public static string ResolveDirectory(DumpDiagnosticsOptions options, string logFilePath)
    {
        if (!string.IsNullOrWhiteSpace(options.Directory))
        {
            return Path.GetFullPath(options.Directory);
        }

        string logs = Path.GetDirectoryName(Path.GetFullPath(string.IsNullOrWhiteSpace(logFilePath) ? "logs/arcanecore.log" : logFilePath)) ?? Path.GetFullPath("logs");
        return Path.Combine(logs, "dumps");
    }

    /// <summary>Check the gates and, when they pass, start writing a dump of <paramref name="kind"/> in the background.</summary>
    public DumpRequestResult Request(DumpKind kind)
    {
        if (!_options.Enabled)
        {
            return Refused("Process dumps are disabled. A dump contains session keys and passwords held in memory; set Ops:Diagnostics:Dumps:Enabled=true and restart to allow them.");
        }

        if (Volatile.Read(ref _running) != 0)
        {
            return Refused("A dump is already being written.");
        }

        lock (_gate)
        {
            long minInterval = Math.Max(0, _options.MinIntervalSeconds);
            if (_hasStarted && minInterval > 0)
            {
                TimeSpan since = _time.GetElapsedTime(_lastStartTimestamp);
                if (since.TotalSeconds < minInterval)
                {
                    return Refused(string.Create(CultureInfo.InvariantCulture,
                        $"The last dump started {since.TotalSeconds:F0} s ago; the minimum interval is {minInterval} s (Ops:Diagnostics:Dumps:MinIntervalSeconds)."));
                }
            }

            try
            {
                System.IO.Directory.CreateDirectory(_directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return Refused("The dump directory " + _directory + " cannot be created: " + ex.Message);
            }

            long workingSet = _workingSet();
            long? free = _freeSpace(_directory);
            if (free is not { } freeBytes)
            {
                return Refused("The free space of " + _directory + " cannot be determined, so no dump is written.");
            }

            long needed = checked(workingSet * FreeSpaceFactor);
            if (freeBytes < needed)
            {
                return Refused(string.Create(CultureInfo.InvariantCulture,
                    $"Not enough free disk space for a dump: {freeBytes / (1024d * 1024 * 1024):F1} GiB free in {_directory}, {FreeSpaceFactor}x the working set ({workingSet / (1024d * 1024 * 1024):F1} GiB) is required."));
            }

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return Refused("A dump is already being written.");
            }

            _lastStartTimestamp = _time.GetTimestamp();
            _hasStarted = true;
        }

        string path = Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture,
            $"arcanecore-world-{kind.ToString().ToLowerInvariant()}-{_time.GetUtcNow():yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.dmp"));
        _logger.LogWarning("writing a {Kind} process dump to {Path} (the process is suspended while it is written)", kind, path);
        Task<DumpOutcome> completion = Task.Run(() => Write(kind, path));
        string message = $"Dump started: {kind.ToString().ToLowerInvariant()} -> {path}. {SuspensionWarning} The result is logged.";
        return new DumpRequestResult(true, message, path, completion);
    }

    private DumpOutcome Write(DumpKind kind, string path)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            _writer.Write(kind, path);
            TimeSpan duration = Stopwatch.GetElapsedTime(start);
            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            _logger.LogWarning("process dump written: {Path}, {SizeMiB} MiB in {Seconds:F1} s", path, bytes / (1024 * 1024), duration.TotalSeconds);
            return new DumpOutcome(true, path, bytes, duration, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("ops.process-dump", ex);
            TimeSpan duration = Stopwatch.GetElapsedTime(start);
            _logger.LogError(ex, "process dump {Path} failed after {Seconds:F1} s", path, duration.TotalSeconds);
            DeletePartial(path);
            lock (_gate)
            {
                _hasStarted = false; // a failed attempt does not block a retry with another type (heap dumps can fail where full ones work)
            }

            return new DumpOutcome(false, path, 0, duration, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    // A partial dump holds the same secrets as a complete one and is of no use: remove it.
    private void DeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogWarning("removed the partial dump {Path}", path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "the partial dump {Path} could not be removed; delete it by hand (it holds process memory)", path);
        }
    }

    private static DumpRequestResult Refused(string message) => new(false, message, null, null);

    /// <summary>The text a command prints when the dump finished.</summary>
    public static string DescribeOutcome(DumpOutcome outcome) => outcome.Success
        ? string.Create(CultureInfo.InvariantCulture, $"Dump written: {outcome.Path}, {outcome.Bytes / (1024d * 1024):F0} MiB in {outcome.Duration.TotalSeconds:F1} s.")
        : string.Create(CultureInfo.InvariantCulture, $"Dump failed after {outcome.Duration.TotalSeconds:F1} s: {outcome.Error}. If a heap dump was asked for, try full.");

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it, matching whole path segments (/mnt/data is not a root of /mnt/database).</summary>
    internal static bool IsUnder(string path, string root, StringComparison comparison)
    {
        if (!path.StartsWith(root, comparison))
        {
            return false;
        }

        return root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            || path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar;
    }

    /// <summary>Free bytes on the volume that holds <paramref name="path"/>: the ready drive whose root is the longest prefix of the path.</summary>
    internal static long? FreeBytesOnVolumeOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            DriveInfo? best = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && IsUnder(full, drive.RootDirectory.FullName, comparison)
                    && (best is null || drive.RootDirectory.FullName.Length > best.RootDirectory.FullName.Length))
                {
                    best = drive;
                }
            }

            return best?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("dump.free-space", ex); // static helper without a logger; the caller treats an unknown size as "cannot check"
            return null;
        }
    }
}

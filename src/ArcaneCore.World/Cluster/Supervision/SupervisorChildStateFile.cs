using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// One child the supervisor owned: its name, role, pid and the process start time that tells a recycled pid from the same process.
/// <paramref name="Standby"/> is the persisted blue/green role: true when the child runs (or is to be relaunched) as a standby
/// (<c>--Cluster:Worker:Standby=true</c>), so a restarted supervisor keeps the swap. A standby whose process is not running is kept with
/// <paramref name="ProcessId"/> 0 and <paramref name="StartTimeUtcMs"/> 0; such a record names no process and is never adopted or killed.
/// </summary>
public sealed record SupervisedChildRecord(string Name, string Role, int ProcessId, long StartTimeUtcMs, bool Standby = false);

/// <summary>
/// Looks at and terminates OS processes by pid, for the supervisor's restart reconciliation. The system implementation uses <see cref="Process"/>;
/// tests substitute a fake so no real process is touched.
/// </summary>
public interface ISupervisorProcessInspector
{
    /// <summary>The start time (UTC) of the live process with this pid, or false when no such process is running.</summary>
    bool TryGetStartTimeUtc(int processId, out DateTimeOffset startTimeUtc);

    /// <summary>Terminates the process and its tree and waits for the exit to be confirmed; false when it is still running afterwards.</summary>
    bool TryKill(int processId);
}

/// <summary>The OS-backed <see cref="ISupervisorProcessInspector"/>.</summary>
public sealed class SystemSupervisorProcessInspector : ISupervisorProcessInspector
{
    private static readonly TimeSpan KillConfirm = TimeSpan.FromSeconds(15);

    public bool TryGetStartTimeUtc(int processId, out DateTimeOffset startTimeUtc)
    {
        startTimeUtc = default;
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
                return false;

            startTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false; // no such pid, already gone, or not readable: not something to adopt or kill
        }
    }

    public bool TryKill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(KillConfirm);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return !TryGetStartTimeUtc(processId, out _);
        }
    }
}

/// <summary>
/// The supervisor's record of the children it owns (name, role, pid, start time), kept on disk so a restarted supervisor can find the ones a crashed
/// predecessor left running (orphan workers keep their leases and the writer lock). Thread affinity: callers serialise (the supervisor writes under its
/// own gate). A write replaces the file atomically (temp file, then move), so a crash mid-write leaves the previous list.
/// </summary>
public sealed class SupervisorChildStateFile
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public SupervisorChildStateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }

    /// <summary>The recorded children; empty when the file is missing, unreadable or not a list (a damaged file must not stop the supervisor).</summary>
    public IReadOnlyList<SupervisedChildRecord> Load()
    {
        try
        {
            if (!File.Exists(Path))
                return [];

            using FileStream stream = File.OpenRead(Path);
            List<SupervisedChildRecord>? records = JsonSerializer.Deserialize<List<SupervisedChildRecord>>(stream, Json);
            return records is null ? [] : [.. records.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name) && (r.ProcessId > 0 || (r.Standby && r.ProcessId == 0)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Replaces the file with <paramref name="records"/>; an empty list deletes it.</summary>
    public void Save(IReadOnlyCollection<SupervisedChildRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            if (File.Exists(Path))
                File.Delete(Path);

            return;
        }

        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>Start-time comparison shared by the state file readers and <see cref="AdoptedOrphanProcess"/>.</summary>
public static class SupervisedChildIdentity
{
    /// <summary>Clocks and file-time rounding differ by up to a second between readings of one process's start time; a recycled pid starts much later.</summary>
    public const long StartTimeToleranceMs = 2000;

    /// <summary>True when <paramref name="actual"/> is the start time recorded in <paramref name="recordedStartMs"/> (Unix milliseconds, UTC), within <see cref="StartTimeToleranceMs"/>.</summary>
    public static bool SameStart(long recordedStartMs, DateTimeOffset actual)
        => Math.Abs(actual.ToUnixTimeMilliseconds() - recordedStartMs) <= StartTimeToleranceMs;
}

/// <summary>
/// A child a previous supervisor launched that is still running and was adopted by this one. Its stdio pipes belonged to the dead supervisor, so it cannot be
/// asked to drain or heard from: a drain request kills it, and its exit code is unknown (reported as -1, so the monitor relaunches it as a crash would be).
/// Identity is pid plus start time, so a recycled pid is reported as exited, never killed. Thread-safe (no mutable state beyond one disposal flag).
/// </summary>
public sealed class AdoptedOrphanProcess : ISupervisedProcess
{
    /// <summary>Exit code reported once the adopted child is gone (the real one is not observable for a process this supervisor did not start).</summary>
    public const int UnknownExitCode = -1;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ISupervisorProcessInspector _inspector;
    private readonly long _startMs;
    private int _disposed;

    public AdoptedOrphanProcess(int processId, DateTimeOffset startTimeUtc, ISupervisorProcessInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ProcessId = processId;
        StartTimeUtc = startTimeUtc;
        _startMs = startTimeUtc.ToUnixTimeMilliseconds();
        _inspector = inspector;
    }

    public int ProcessId { get; }

    public DateTimeOffset? StartTimeUtc { get; }

    public bool HasExited => !(_inspector.TryGetStartTimeUtc(ProcessId, out DateTimeOffset start) && SupervisedChildIdentity.SameStart(_startMs, start));

    public int? ExitCode => HasExited ? UnknownExitCode : null;

    /// <summary>Never raised: the child's output pipes were closed with the supervisor that started it.</summary>
    public event Action<string, bool>? OutputLineReceived
    {
        add { }
        remove { }
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        while (!HasExited)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return UnknownExitCode;
    }

    public Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        KillOwnedProcess(); // no stdin to carry a drain line: killing the adopted child is the only stop there is
        return Task.FromResult(HasExited);
    }

    public bool TrySendLine(string line) => false;

    public void KillOwnedProcess()
    {
        if (HasExited)
            return; // gone, or the pid now belongs to another process: never kill that one

        _inspector.TryKill(ProcessId);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        KillOwnedProcess(); // the same contract as the system wrapper: disposing an owned child ends it
    }
}

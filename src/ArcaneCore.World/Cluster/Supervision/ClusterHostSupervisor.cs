using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// Lifecycle state of a child process managed by <see cref="ClusterHostSupervisor"/>.
/// </summary>
public enum SupervisedProcessState
{
    Stopped = 0,
    Running = 1,
    BackingOff = 2,
    Draining = 3,
    Drained = 4,
    Faulted = 5,
}

/// <summary>
/// Result of a host capacity and map-ownership admission check before launching a supervised child process.
/// </summary>
public readonly record struct CapacityAdmissionResult(bool Allowed, string? Reason = null)
{
    public static CapacityAdmissionResult Permit() => new(true, null);
    public static CapacityAdmissionResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Reads available host memory for supervisor capacity admission checks.
/// </summary>
public interface IClusterCapacityProbe
{
    long GetAvailableMemoryMb();
}

/// <summary>
/// Default host capacity probe backed by <see cref="GC.GetGCMemoryInfo()"/>.
/// </summary>
public sealed class SystemClusterCapacityProbe : IClusterCapacityProbe
{
    public long GetAvailableMemoryMb()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long totalBytes = info.TotalAvailableMemoryBytes;
        long loadBytes = info.MemoryLoadBytes;
        if (totalBytes <= 0)
            return 8192;

        long freeBytes = Math.Max(0L, totalBytes - (loadBytes > 0 ? loadBytes : Environment.WorkingSet));
        return freeBytes / (1024L * 1024L);
    }
}

/// <summary>
/// Represents a single running or exited child process owned by <see cref="ClusterHostSupervisor"/>.
/// </summary>
public interface ISupervisedProcess : IDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    int? ExitCode { get; }

    /// <summary>
    /// Fired when the child writes a line to stdout (<c>isStdErr = false</c>) or stderr (<c>isStdErr = true</c>).
    /// </summary>
    event Action<string, bool>? OutputLineReceived;

    Task<int> WaitForExitAsync(CancellationToken ct = default);

    /// <summary>
    /// Requests the child process to drain gracefully and waits up to <paramref name="timeout"/> for it to exit.
    /// Returns <c>true</c> if the process exited cleanly within the timeout.
    /// </summary>
    Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Terminates this supervisor-owned child process if it is still running. The system implementation kills the whole process tree
    /// and blocks the calling thread (up to 15 s) until the exit is confirmed, so it is meant for the shutdown path only.
    /// </summary>
    void KillOwnedProcess();
}

/// <summary>
/// Abstraction for launching supervised child processes (allows deterministic in-memory and real OS subprocess tests).
/// </summary>
public interface ISupervisedProcessLauncher
{
    ISupervisedProcess Launch(SupervisedProcessConfig config);
}

/// <summary>
/// The marker the host supervisor puts in every child's environment, so a World process started as a supervised
/// child can refuse to turn into a second supervisor (a fork bomb on a copied <c>Cluster:Supervisor:Enabled</c> key), and so
/// the child reads the supervisor's <c>drain</c> line on stdin (<see cref="SupervisedChildDrain"/>).
/// </summary>
public static class SupervisedChild
{
    /// <summary>Environment variable set on every supervised child; its value is the child's process name.</summary>
    public const string EnvironmentVariable = SupervisedChildDrain.EnvironmentVariable;
}

/// <summary>
/// Launches real OS child processes via <see cref="Process"/> and tracks supervisor-owned PIDs so only
/// owned child processes can ever be terminated.
/// </summary>
public sealed class SystemProcessSupervisedLauncher : ISupervisedProcessLauncher
{
    /// <summary>
    /// Pids of children this launcher started whose exit it has not yet observed. A pid leaves only when the OS reports the exit
    /// (never on a cancelled wait, never on the kill call itself); a child whose kill failed stays here and is reported as a fault.
    /// In memory only: it does not survive a supervisor restart (M15.4 restart reconciliation is an open gap).
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _ownedPids = new();

    /// <summary>The live owned children; empty after a completed drain or dispose.</summary>
    public IReadOnlyCollection<int> OwnedProcessIds => [.. _ownedPids.Keys];

    /// <summary>Test seam: replaces the tree kill (a kill that does nothing models one the OS refused).</summary>
    internal Action<Process>? KillOverride { get; init; }

    /// <summary>Test seam: how long a kill waits for the OS to confirm the exit (default 15 s).</summary>
    internal TimeSpan KillConfirmTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for <paramref name="config"/>, applying the per-process
    /// <see cref="SupervisedConsoleMode"/> (<c>CreateNoWindow = false</c> for <see cref="SupervisedConsoleMode.Window"/>,
    /// <c>CreateNoWindow = true</c> for <see cref="SupervisedConsoleMode.Merged"/> and <see cref="SupervisedConsoleMode.None"/>).
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(SupervisedProcessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        var startInfo = new ProcessStartInfo
        {
            FileName = config.ExecutablePath,
            Arguments = config.Arguments,
            CreateNoWindow = config.Console != SupervisedConsoleMode.Window,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        startInfo.Environment[SupervisedChild.EnvironmentVariable] = config.Name;

        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            startInfo.WorkingDirectory = config.WorkingDirectory;
        }

        return startInfo;
    }

    public ISupervisedProcess Launch(SupervisedProcessConfig config)
    {
        ProcessStartInfo startInfo = BuildStartInfo(config);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start supervised process '{config.Name}' ('{config.ExecutablePath}').");
        }

        int pid = process.Id;
        _ownedPids[pid] = 1;
        return new OwnedSystemSupervisedProcess(process, pid, _ownedPids, KillOverride, KillConfirmTimeout);
    }

    private sealed class OwnedSystemSupervisedProcess : ISupervisedProcess
    {
        /// <summary>How long a kill waits for the OS to confirm the exit before the pid is reported as un-killable.</summary>
        private readonly TimeSpan _killConfirmTimeout;
        private readonly Action<Process> _killProcess;

        private readonly Process _process;
        private readonly int _pid;
        private readonly ConcurrentDictionary<int, byte> _ownedPids;
        private readonly Task _stdoutPump;
        private readonly Task _stderrPump;
        private readonly Lock _sync = new();
        private bool _exited;      // set once, by OnExited, under _sync
        private int? _exitCode;    // read while the handle was still open
        private bool _disposed;    // after this no Process member is called; HasExited/ExitCode answer from the cache
        private int _disposeStarted;

        public OwnedSystemSupervisedProcess(Process process, int pid, ConcurrentDictionary<int, byte> ownedPids, Action<Process>? killOverride, TimeSpan killConfirmTimeout)
        {
            _killProcess = killOverride ?? (p => p.Kill(entireProcessTree: true));
            _killConfirmTimeout = killConfirmTimeout;
            _process = process;
            _pid = pid;
            _ownedPids = ownedPids;

            // Launch set EnableRaisingEvents before Start, so the wait thread exists; Exited is the one observer that removes the pid.
            _process.Exited += (_, _) => OnExited();
            _stdoutPump = Task.Run(() => PumpStreamAsync(_process.StandardOutput, isStdErr: false));
            _stderrPump = Task.Run(() => PumpStreamAsync(_process.StandardError, isStdErr: true));

            // The exit may have been raised before the handler above was attached (Process raises it once); observe it directly.
            if (ProcessHasExited())
                OnExited();
        }

        public int ProcessId => _pid;

        public bool HasExited
        {
            get
            {
                lock (_sync)
                {
                    if (_exited)
                        return true;
                    if (_disposed)
                        return false; // disposed without an observed exit: the kill failed; never report a live child as gone
                }

                if (ProcessHasExited())
                {
                    OnExited();
                    return true;
                }

                return false;
            }
        }

        public int? ExitCode
        {
            get
            {
                if (!HasExited)
                    return null;
                lock (_sync)
                {
                    return _exitCode;
                }
            }
        }

        public event Action<string, bool>? OutputLineReceived;

        private bool ProcessHasExited()
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false; // no associated process (handle closed): nothing was observed
            }
        }

        /// <summary>The one place a pid leaves the owned set: the OS reported the exit.</summary>
        private void OnExited()
        {
            lock (_sync)
            {
                if (_exited)
                    return;
                _exited = true;
                try
                {
                    _exitCode = _process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    // Handle already closed. Dispose confirms the exit before closing, so this is unreachable in practice; the code stays null.
                }
            }

            _ownedPids.TryRemove(_pid, out _);
        }

        private async Task PumpStreamAsync(StreamReader reader, bool isStdErr)
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    OutputLineReceived?.Invoke(line, isStdErr);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Stream closed on process exit or dispose.
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken ct = default)
        {
            if (!HasExited)
            {
                // A cancellation here abandons this wait only: the child, and its pid in the owned set, are untouched.
                await _process.WaitForExitAsync(ct).ConfigureAwait(false);
                OnExited();
            }

            await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(ct).ConfigureAwait(false);
            lock (_sync)
            {
                return _exitCode ?? throw new ObjectDisposedException(nameof(OwnedSystemSupervisedProcess), $"Supervised child pid {_pid} exited, but its handle was closed before the exit code was read.");
            }
        }

        public async Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            if (HasExited)
                return true;

            SendDrainAndCloseStdin();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                OnExited();
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return HasExited; // the drain timeout: the child is still ours, still owned; the caller kills it
            }
            catch (InvalidOperationException)
            {
                return HasExited; // disposed concurrently; Dispose has already killed and confirmed
            }
        }

        /// <summary>
        /// Writes the drain line and closes stdin so the child also sees EOF. Synchronous and token-free on purpose: six bytes into a fresh
        /// pipe never block (4 KiB on Windows, 64 KiB on Linux), and the close must happen whatever the write did.
        /// </summary>
        private void SendDrainAndCloseStdin()
        {
            StreamWriter stdin;
            try
            {
                stdin = _process.StandardInput;
            }
            catch (InvalidOperationException)
            {
                return; // not redirected or no associated process
            }

            try
            {
                stdin.WriteLine(SupervisedChildDrain.DrainLine);
                stdin.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The child does not read stdin, or already closed it (a second drain request).
            }
            finally
            {
                try
                {
                    stdin.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Already closed.
                }
            }
        }

        public void KillOwnedProcess()
        {
            if (HasExited)
                return; // idempotent: nothing to do for an observed exit

            lock (_sync)
            {
                if (_disposed)
                    return; // handle gone; Dispose already ran the kill below
            }

            try
            {
                // The children are our own daemons (or an operator's wrapper around one); nothing in their tree may outlive them.
                _killProcess(_process);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
            }
            catch (Exception ex) when (ex is Win32Exception or AggregateException or NotSupportedException)
            {
                FaultLedger.Shared.Record("cluster.supervisor.kill", ex);
            }

            // Confirm before anyone closes the handle: WaitForExit raises Exited, which removes the pid and completes every pending WaitForExitAsync.
            bool exited;
            try
            {
                exited = _process.WaitForExit((int)_killConfirmTimeout.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                exited = ProcessHasExited();
            }

            if (exited)
            {
                OnExited();
            }
            else
            {
                FaultLedger.Shared.Record("cluster.supervisor.kill",
                    new InvalidOperationException($"Supervised child pid {_pid} is still running {_killConfirmTimeout.TotalSeconds:0.#} s after Kill; it stays in the owned set."));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;

            KillOwnedProcess(); // exit confirmed (or recorded as a fault) while the handle is still open

            lock (_sync)
            {
                _disposed = true;
            }

            try
            {
                _process.StandardInput.Close(); // Process.Close leaves a stdin we accessed open
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
            {
                // Never opened or already closed.
            }

            _process.Dispose();
        }
    }

}

/// <summary>
/// Outcome of <see cref="ClusterHostSupervisor.PlanStartAll"/>: the processes a start would launch, in
/// start order (<see cref="SupervisedProcessConfig.StartOrder"/>, then key), and the ones admission would refuse with the reason.
/// </summary>
public sealed record SupervisorStartPlan(IReadOnlyList<string> Admitted, IReadOnlyList<(string Name, string Reason)> Denied);

/// <summary>
/// Immutable point-in-time status snapshot of a supervised process.
/// </summary>
public sealed record SupervisedProcessSnapshot(
    string Name,
    string Role,
    SupervisedProcessState State,
    int? ProcessId,
    long Incarnation,
    int RestartCount,
    int? LastExitCode,
    int LastBackoffDelayMs,
    SupervisedConsoleMode ConsoleMode,
    long RequiredMemoryMb,
    ImmutableArray<uint> MapAssignments);

/// <summary>
/// M15.4 Host Supervisor: manages process admission, windowless/windowed console multiplexing,
/// capped exponential restart backoff (<c>1s</c> to <c>60s</c>), graceful drain without false restarts,
/// and worker map assignment on a single host.
/// </summary>
public sealed class ClusterHostSupervisor : IAsyncDisposable, IDisposable
{
    private readonly Lock _gate = new();
    private readonly ClusterSupervisorOptions _options;
    private readonly ISupervisedProcessLauncher _launcher;
    private readonly SupervisedOutputMultiplexer _multiplexer;
    private readonly IClusterCapacityProbe _capacityProbe;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Dictionary<string, ManagedEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _supervisorStop = new();
    private bool _disposed;

    public ClusterHostSupervisor(
        ClusterSupervisorOptions options,
        ISupervisedProcessLauncher launcher,
        SupervisedOutputMultiplexer? multiplexer = null,
        IClusterCapacityProbe? capacityProbe = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);

        _options = options;
        _launcher = launcher;
        _multiplexer = multiplexer ?? new SupervisedOutputMultiplexer();
        _capacityProbe = capacityProbe ?? new SystemClusterCapacityProbe();
        _delayAsync = delayAsync ?? Task.Delay;

        foreach ((string key, SupervisedProcessConfig config) in _options.Processes)
        {
            if (string.IsNullOrWhiteSpace(config.Name))
                config.Name = key;

            config.Validate();
            _entries[config.Name] = new ManagedEntry(config, key);
        }
    }

    public SupervisedOutputMultiplexer Multiplexer => _multiplexer;

    /// <summary>
    /// The one start order: ascending <see cref="SupervisedProcessConfig.StartOrder"/>, ties broken by configuration key (ordinal).
    /// Configuration binding enumerates keys sorted, not as written, so the order is never the order of the file. Call under <c>_gate</c>.
    /// </summary>
    private List<ManagedEntry> OrderedEntriesUnderLock()
    {
        List<ManagedEntry> ordered = [.. _entries.Values];
        ordered.Sort((a, b) =>
        {
            int byOrder = a.Config.StartOrder.CompareTo(b.Config.StartOrder);
            return byOrder != 0 ? byOrder : string.CompareOrdinal(a.Key, b.Key);
        });
        return ordered;
    }

    /// <summary>
    /// Evaluates whether <paramref name="candidate"/> can be admitted under the host's process ceiling,
    /// memory budget, free RAM reserve, and single-writer map ownership constraints.
    /// </summary>
    public CapacityAdmissionResult CheckAdmission(SupervisedProcessConfig candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.Validate();

        lock (_gate)
        {
            ManagedEntry[] active = _entries.Values
                .Where(e => !string.Equals(e.Config.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
                            && e.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff)
                .ToArray();

            if (active.Length + 1 > _options.MaxConcurrentProcesses)
            {
                return CapacityAdmissionResult.Deny(
                    $"Cannot admit process '{candidate.Name}': active process count ({active.Length + 1}) would exceed MaxConcurrentProcesses ({_options.MaxConcurrentProcesses}).");
            }

            long committedMb = active.Sum(e => e.Config.RequiredMemoryMb);
            long projectedTotalMb = committedMb + candidate.RequiredMemoryMb;

            if (_options.MaxTotalAllocatedMemoryMb is { } maxTotalMb && projectedTotalMb > maxTotalMb)
            {
                return CapacityAdmissionResult.Deny(
                    $"Cannot admit process '{candidate.Name}': projected allocated memory ({projectedTotalMb} MiB) exceeds MaxTotalAllocatedMemoryMb ({maxTotalMb} MiB).");
            }

            long availableMb = _capacityProbe.GetAvailableMemoryMb();
            if (availableMb - candidate.RequiredMemoryMb < _options.MinFreeMemoryReserveMb)
            {
                return CapacityAdmissionResult.Deny(
                    $"Cannot admit process '{candidate.Name}': available host memory ({availableMb} MiB) minus RequiredMemoryMb ({candidate.RequiredMemoryMb} MiB) is below MinFreeMemoryReserveMb ({_options.MinFreeMemoryReserveMb} MiB).");
            }

            if (string.Equals(candidate.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase)
                && candidate.MapAssignments.Count > 0)
            {
                foreach (ManagedEntry other in active)
                {
                    if (!string.Equals(other.Config.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (uint mapId in candidate.MapAssignments)
                    {
                        if (other.MapAssignments.Contains(mapId))
                        {
                            return CapacityAdmissionResult.Deny(
                                $"Cannot admit worker '{candidate.Name}': map {mapId} is already assigned to active worker '{other.Config.Name}'.");
                        }
                    }
                }
            }

            return CapacityAdmissionResult.Permit();
        }
    }

    /// <summary>
    /// Registers or updates a supervised process configuration before it is started.
    /// </summary>
    public void RegisterProcess(SupervisedProcessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        lock (_gate)
        {
            if (_entries.TryGetValue(config.Name, out ManagedEntry? existing)
                && existing.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff or SupervisedProcessState.Draining)
            {
                throw new InvalidOperationException($"Cannot replace running supervised process '{config.Name}'.");
            }

            _options.Processes[config.Name] = config;
            _entries[config.Name] = new ManagedEntry(config, config.Name);
        }
    }

    /// <summary>
    /// Validates capacity admission and launches the named supervised process.
    /// </summary>
    public Task<SupervisedProcessSnapshot> StartProcessAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ct.ThrowIfCancellationRequested();

        ManagedEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(name, out entry!))
                throw new KeyNotFoundException($"Unknown supervised process '{name}'.");

            if (entry.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff)
                return Task.FromResult(entry.ToSnapshot());

            CapacityAdmissionResult admission = CheckAdmission(entry.Config);
            if (!admission.Allowed)
                throw new InvalidOperationException(admission.Reason);

            entry.StopCts?.Dispose();
            entry.StopCts = CancellationTokenSource.CreateLinkedTokenSource(_supervisorStop.Token);
            LaunchEntryUnderLock(entry);
            entry.MonitorTask = Task.Run(() => MonitorProcessLoopAsync(entry, entry.StopCts.Token));
            return Task.FromResult(entry.ToSnapshot());
        }
    }

    /// <summary>
    /// Simulates <see cref="StartAllAsync"/> without launching anything: walks the registered processes in configuration
    /// start order (<see cref="SupervisedProcessConfig.StartOrder"/>, then key) and applies the admission rules cumulatively (process count, total allocated memory, the free-memory reserve
    /// against one probe read, single-writer map ownership), so a caller can refuse a start that cannot reach its minimum
    /// before the first child is launched.
    /// </summary>
    public SupervisorStartPlan PlanStartAll()
    {
        lock (_gate)
        {
            var admitted = new List<string>();
            var denied = new List<(string Name, string Reason)>();
            long availableMb = _capacityProbe.GetAvailableMemoryMb();
            int count = 0;
            long committedMb = 0; // memory of entries this plan would start; running entries are already inside availableMb (as in CheckAdmission)
            long allocatedMb = 0; // everything counted against MaxTotalAllocatedMemoryMb, running entries included
            var mapOwners = new Dictionary<uint, string>();

            foreach (ManagedEntry entry in OrderedEntriesUnderLock())
            {
                SupervisedProcessConfig cfg = entry.Config;
                bool alreadyActive = entry.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff;
                bool isWorker = string.Equals(cfg.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase);

                if (!alreadyActive)
                {
                    string? reason = null;
                    if (count + 1 > _options.MaxConcurrentProcesses)
                    {
                        reason = $"active process count ({count + 1}) would exceed MaxConcurrentProcesses ({_options.MaxConcurrentProcesses}).";
                    }
                    else if (_options.MaxTotalAllocatedMemoryMb is { } maxTotalMb && allocatedMb + cfg.RequiredMemoryMb > maxTotalMb)
                    {
                        reason = $"projected allocated memory ({allocatedMb + cfg.RequiredMemoryMb} MiB) exceeds MaxTotalAllocatedMemoryMb ({maxTotalMb} MiB).";
                    }
                    else if (availableMb - committedMb - cfg.RequiredMemoryMb < _options.MinFreeMemoryReserveMb)
                    {
                        reason = $"available host memory ({availableMb} MiB) minus memory already planned ({committedMb} MiB) and RequiredMemoryMb ({cfg.RequiredMemoryMb} MiB) is below MinFreeMemoryReserveMb ({_options.MinFreeMemoryReserveMb} MiB).";
                    }
                    else if (isWorker)
                    {
                        foreach (uint mapId in entry.MapAssignments)
                        {
                            if (mapOwners.TryGetValue(mapId, out string? owner))
                            {
                                reason = $"map {mapId} is already assigned to worker '{owner}'.";
                                break;
                            }
                        }
                    }

                    if (reason is not null)
                    {
                        denied.Add((cfg.Name, reason));
                        continue;
                    }
                }

                admitted.Add(cfg.Name);
                count++;
                allocatedMb += cfg.RequiredMemoryMb;
                if (!alreadyActive)
                    committedMb += cfg.RequiredMemoryMb;
                if (isWorker)
                {
                    foreach (uint mapId in entry.MapAssignments)
                    {
                        mapOwners.TryAdd(mapId, cfg.Name);
                    }
                }
            }

            return new SupervisorStartPlan(admitted, denied);
        }
    }

    /// <summary>
    /// Starts all registered supervised processes in start order (<see cref="SupervisedProcessConfig.StartOrder"/>, then key).
    /// </summary>
    public async Task<IReadOnlyList<SupervisedProcessSnapshot>> StartAllAsync(CancellationToken ct = default)
    {
        string[] names;
        lock (_gate)
        {
            names = [.. OrderedEntriesUnderLock().Select(e => e.Config.Name)];
        }

        var snapshots = new List<SupervisedProcessSnapshot>(names.Length);
        foreach (string name in names)
        {
            snapshots.Add(await StartProcessAsync(name, ct).ConfigureAwait(false));
        }

        return snapshots;
    }

    /// <summary>
    /// Gracefully drains a single supervised process without triggering restart backoff, and terminates
    /// the owned child process if it does not exit within <see cref="SupervisedProcessConfig.DrainTimeoutMs"/>.
    /// </summary>
    public async Task<SupervisedProcessSnapshot> DrainProcessAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        ManagedEntry entry;
        ISupervisedProcess? process;
        Task? monitorTask;
        CancellationTokenSource? stopCts;

        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out entry!))
                throw new KeyNotFoundException($"Unknown supervised process '{name}'.");

            if (entry.State is SupervisedProcessState.Stopped or SupervisedProcessState.Drained)
                return entry.ToSnapshot();

            entry.State = SupervisedProcessState.Draining;
            process = entry.Process;
            monitorTask = entry.MonitorTask;
            stopCts = entry.StopCts;
        }

        if (process is not null)
        {
            var timeout = TimeSpan.FromMilliseconds(entry.Config.DrainTimeoutMs);
            bool exitedCleanly = false;
            try
            {
                exitedCleanly = await process.RequestDrainAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller (the host's shutdown timeout) gave up waiting; the child still gets its kill below, never a half-finished drain.
            }
            catch
            {
                process.KillOwnedProcess();
                throw;
            }

            if (!exitedCleanly)
            {
                process.KillOwnedProcess();
            }
        }

        bool stillAlive = process is not null && !process.HasExited;
        if (stillAlive)
        {
            _multiplexer.RouteLine(
                entry.Config,
                $"ERROR: Process '{entry.Config.Name}' (pid {process!.ProcessId}) is still running after its drain timeout and a kill; it remains in the owned set.",
                isStdErr: true);
            AnomalyLedger.Shared.Report("cluster", "supervisor-kill-failed", entry.Config.Name, $"pid={process.ProcessId}", AnomalySeverity.Error);
        }

        if (stopCts is not null)
        {
            await stopCts.CancelAsync().ConfigureAwait(false);
        }

        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling backoff during drain.
            }
        }

        ISupervisedProcess? toDispose;
        SupervisedProcessSnapshot snapshot;
        lock (_gate)
        {
            entry.LastExitCode = process?.ExitCode ?? entry.LastExitCode;
            toDispose = entry.Process;
            entry.Process = null;
            entry.State = stillAlive ? SupervisedProcessState.Faulted : SupervisedProcessState.Drained;
            snapshot = entry.ToSnapshot();
        }

        // A second DrainProcessAsync on an entry that ended Faulted finds no process and marks it Drained (follow-up: keep Faulted until reconciled).
        // Outside _gate: the wrapper's Dispose kills again and can block (up to the confirm timeout) when a kill was refused; no one else may wait on that.
        toDispose?.Dispose();
        return snapshot;
    }

    /// <summary>
    /// Drains and stops all active supervised processes in reverse launch order (workers first, then gateway/social/coordinator/realm).
    /// </summary>
    public async Task DrainAndStopAsync(CancellationToken ct = default)
    {
        string[] names;
        lock (_gate)
        {
            names = [.. OrderedEntriesUnderLock().Select(e => e.Config.Name).Reverse()];
        }

        foreach (string name in names)
        {
            await DrainProcessAsync(name, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the current snapshot for <paramref name="name"/>.
    /// </summary>
    public SupervisedProcessSnapshot GetSnapshot(string name)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out ManagedEntry? entry))
                throw new KeyNotFoundException($"Unknown supervised process '{name}'.");

            return entry.ToSnapshot();
        }
    }

    /// <summary>
    /// Returns snapshots for all registered supervised processes.
    /// </summary>
    public IReadOnlyList<SupervisedProcessSnapshot> GetSnapshots()
    {
        lock (_gate)
        {
            return [.. OrderedEntriesUnderLock().Select(e => e.ToSnapshot())];
        }
    }

    /// <summary>
    /// Finds the running worker assigned to <paramref name="mapId"/>, or <c>null</c> if unassigned.
    /// </summary>
    public SupervisedProcessSnapshot? ResolveWorkerForMap(uint mapId)
    {
        lock (_gate)
        {
            ManagedEntry? match = _entries.Values.FirstOrDefault(e =>
                e.State == SupervisedProcessState.Running
                && string.Equals(e.Config.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase)
                && e.MapAssignments.Contains(mapId));

            return match?.ToSnapshot();
        }
    }

    /// <summary>
    /// Places a new or empty map onto the running worker with the fewest assigned maps (ties broken by lowest required memory, then name).
    /// If the map is already assigned to a running worker, returns that worker's snapshot.
    /// </summary>
    public SupervisedProcessSnapshot AssignEmptyMapToLeastLoadedWorker(uint mapId)
    {
        lock (_gate)
        {
            ManagedEntry? existing = _entries.Values.FirstOrDefault(e =>
                e.State == SupervisedProcessState.Running
                && string.Equals(e.Config.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase)
                && e.MapAssignments.Contains(mapId));

            if (existing is not null)
                return existing.ToSnapshot();

            ManagedEntry? candidate = _entries.Values
                .Where(e => e.State == SupervisedProcessState.Running
                            && string.Equals(e.Config.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.MapAssignments.Count)
                .ThenBy(e => e.Config.RequiredMemoryMb)
                .ThenBy(e => e.Config.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (candidate is null)
            {
                throw new InvalidOperationException($"Cannot place map {mapId}: no running worker processes are available.");
            }

            candidate.MapAssignments.Add(mapId);
            return candidate.ToSnapshot();
        }
    }

    private void LaunchEntryUnderLock(ManagedEntry entry)
    {
        ISupervisedProcess process = _launcher.Launch(entry.Config);
        process.OutputLineReceived += (line, isStdErr) => _multiplexer.RouteLine(entry.Config, line, isStdErr);
        entry.Process = process;
        entry.Incarnation++;
        entry.State = SupervisedProcessState.Running;
    }

    private async Task MonitorProcessLoopAsync(ManagedEntry entry, CancellationToken ct)
    {
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            ISupervisedProcess? currentProcess;
            lock (_gate)
            {
                if (entry.State is SupervisedProcessState.Draining or SupervisedProcessState.Drained or SupervisedProcessState.Stopped)
                    return;

                currentProcess = entry.Process;
            }

            if (currentProcess is null)
                return;

            int exitCode;
            try
            {
                exitCode = await currentProcess.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Stop or dispose: a cancelled wait, or a handle closed by the wrapper's Dispose after it confirmed the exit. Not a fault.
                return;
            }

            int delayMs;
            lock (_gate)
            {
                entry.LastExitCode = exitCode;
                if (entry.State is SupervisedProcessState.Draining or SupervisedProcessState.Drained or SupervisedProcessState.Stopped
                    || ct.IsCancellationRequested)
                {
                    return;
                }

                if (exitCode == ExitCodes.InvalidConfiguration)
                {
                    // EX_CONFIG: the child refused its own configuration; a restart would fail the same way, forever.
                    entry.State = SupervisedProcessState.Faulted;
                    entry.Process?.Dispose();
                    entry.Process = null;
                    _multiplexer.RouteLine(
                        entry.Config,
                        $"FATAL: Process '{entry.Config.Name}' exited with code {exitCode} (invalid configuration); not restarting.",
                        isStdErr: true);
                    return;
                }

                consecutiveFailures++;
                entry.RestartCount++;
                delayMs = ClusterSupervisorOptions.ComputeBackoffDelayMs(
                    consecutiveFailures,
                    entry.Config.RestartBackoffMinMs,
                    entry.Config.RestartBackoffMaxMs);
                entry.LastBackoffDelayMs = delayMs;
                entry.State = SupervisedProcessState.BackingOff;
                entry.Process?.Dispose();
                entry.Process = null;
            }

            // Surface unexpected process termination through the multiplexer as an error so it is always visible in the merged console.
            _multiplexer.RouteLine(
                entry.Config,
                $"ERROR: Process '{entry.Config.Name}' (incarnation {entry.Incarnation}) exited unexpectedly with code {exitCode}; restarting in {delayMs} ms (restart #{entry.RestartCount}).",
                isStdErr: true);

            try
            {
                await _delayAsync(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            lock (_gate)
            {
                if (entry.State is SupervisedProcessState.Draining or SupervisedProcessState.Drained or SupervisedProcessState.Stopped
                    || ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    LaunchEntryUnderLock(entry);
                }
                catch (Exception ex)
                {
                    FaultLedger.Shared.Record("cluster.supervisor", ex);
                    AnomalyLedger.Shared.Report("cluster", "gateway-fault", entry.Config.Name, $"phase=relaunch error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
                    entry.State = SupervisedProcessState.Faulted;
                    _multiplexer.RouteLine(
                        entry.Config,
                        $"FATAL: Failed to relaunch process '{entry.Config.Name}': {ex.Message}",
                        isStdErr: true);
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            await DrainAndStopAsync().ConfigureAwait(false);
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// Kills any child still running (whole tree, exit confirmed) and waits for the monitors. Blocks the calling thread, so it belongs on the shutdown
    /// path only; <see cref="DisposeAsync"/> drains first.
    /// </summary>
    public void Dispose()
    {
        List<ManagedEntry> entries;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            entries = [.. _entries.Values];
            foreach (ManagedEntry entry in entries)
            {
                if (entry.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff or SupervisedProcessState.Draining)
                    entry.State = SupervisedProcessState.Stopped; // the monitor loop returns at its next check instead of relaunching
            }
        }

        // Outside _gate from here: the monitors take _gate after their waits complete, and the waits complete because each wrapper's
        // Dispose kills the child (whole tree) and confirms the exit before it closes the handle. Nothing here depends on cancellation timing.
        _supervisorStop.Cancel(); // ends backoff delays and parked waits
        foreach (ManagedEntry entry in entries)
        {
            ISupervisedProcess? process;
            lock (_gate)
            {
                process = entry.Process;
                entry.Process = null;
            }

            process?.Dispose();
        }

        Task[] monitors = [.. entries.Select(e => e.MonitorTask).OfType<Task>()];
        try
        {
            Task.WaitAll(monitors, TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // Cancellations and dispose races surfacing from the monitors; the children are already confirmed gone or recorded as faults.
        }

        lock (_gate)
        {
            foreach (ManagedEntry entry in entries)
            {
                entry.StopCts?.Dispose();
                entry.StopCts = null;
                entry.MonitorTask = null;
            }

            _supervisorStop.Dispose();
            _multiplexer.Dispose();
        }
    }

    private sealed class ManagedEntry
    {
        public SupervisedProcessConfig Config { get; }
        public string Key { get; }
        public List<uint> MapAssignments { get; }
        public SupervisedProcessState State { get; set; } = SupervisedProcessState.Stopped;
        public ISupervisedProcess? Process { get; set; }
        public long Incarnation { get; set; }
        public int RestartCount { get; set; }
        public int? LastExitCode { get; set; }
        public int LastBackoffDelayMs { get; set; }
        public CancellationTokenSource? StopCts { get; set; }
        public Task? MonitorTask { get; set; }

        public ManagedEntry(SupervisedProcessConfig config, string key)
        {
            Config = config;
            Key = key;
            MapAssignments = [.. config.MapAssignments];
        }

        public SupervisedProcessSnapshot ToSnapshot() => new(
            Config.Name,
            Config.Role,
            State,
            Process?.ProcessId,
            Incarnation,
            RestartCount,
            LastExitCode,
            LastBackoffDelayMs,
            Config.Console,
            Config.RequiredMemoryMb,
            [.. MapAssignments]);
    }
}

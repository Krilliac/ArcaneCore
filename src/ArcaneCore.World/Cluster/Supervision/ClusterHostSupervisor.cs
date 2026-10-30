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

    /// <summary>The child stopped sending world-tick heartbeats; the supervisor is capturing, draining and killing it before it relaunches it.</summary>
    Hung = 6,
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
    /// Writes one line to the child's standard input (the supervisor's forwarded lifecycle command, or the answer to a child's request). Returns false
    /// when the line could not be delivered (the child has exited, stdin is closed, or the process does not take input). Never throws.
    /// </summary>
    bool TrySendLine(string line) => false;

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

    /// <summary>
    /// Launches the child with <paramref name="onOutputLine"/> already subscribed, so a line the child writes straight after it starts is never missed.
    /// The handler receives the launched process, the line and whether it came from stderr. The default subscribes after <see cref="Launch(SupervisedProcessConfig)"/>
    /// returns (fine for launchers whose output cannot race the launch); launchers that start output pumps override it to subscribe before the pumps run.
    /// </summary>
    ISupervisedProcess Launch(SupervisedProcessConfig config, Action<ISupervisedProcess, string, bool> onOutputLine)
    {
        ISupervisedProcess process = Launch(config);
        process.OutputLineReceived += (line, isStdErr) => onOutputLine(process, line, isStdErr);
        return process;
    }
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

        // The line protocol (SupervisorProtocol): the child beats on stdout, announces its stop, and may ask for a cross-world lifecycle command.
        startInfo.Environment[SupervisorProtocol.EnvironmentVariable] = "1";
        if (!string.IsNullOrEmpty(config.ProtocolToken))
        {
            startInfo.Environment[SupervisorProtocol.TokenEnvironmentVariable] = config.ProtocolToken;
        }

        if (config.RealmId > 0)
        {
            startInfo.Environment[SupervisorProtocol.RealmIdEnvironmentVariable] = config.RealmId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            startInfo.Environment.Remove(SupervisorProtocol.RealmIdEnvironmentVariable); // an inherited value must not name a realm this child was not given
        }

        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            startInfo.WorkingDirectory = config.WorkingDirectory;
        }

        return startInfo;
    }

    public ISupervisedProcess Launch(SupervisedProcessConfig config) => Launch(config, null);

    public ISupervisedProcess Launch(SupervisedProcessConfig config, Action<ISupervisedProcess, string, bool>? onOutputLine)
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
        return new OwnedSystemSupervisedProcess(process, pid, _ownedPids, KillOverride, KillConfirmTimeout, onOutputLine);
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

        public OwnedSystemSupervisedProcess(Process process, int pid, ConcurrentDictionary<int, byte> ownedPids, Action<Process>? killOverride, TimeSpan killConfirmTimeout, Action<ISupervisedProcess, string, bool>? onOutputLine)
        {
            _killProcess = killOverride ?? (p => p.Kill(entireProcessTree: true));
            _killConfirmTimeout = killConfirmTimeout;
            _process = process;
            _pid = pid;
            _ownedPids = ownedPids;
            if (onOutputLine is not null)
                OutputLineReceived += (line, isStdErr) => onOutputLine(this, line, isStdErr); // subscribed before the pumps start: no early line is lost

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
                // LF-delimited (a lone CR does not end a line): the protocol lines are matched whole, so a CR inside logged text cannot start a second one.
                await SupervisorLineReader.ReadLinesAsync(reader, line => OutputLineReceived?.Invoke(line, isStdErr)).ConfigureAwait(false);
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

        private readonly Lock _stdinSync = new();

        public bool TrySendLine(string line)
        {
            lock (_sync)
            {
                if (_exited || _disposed)
                    return false;
            }

            try
            {
                lock (_stdinSync)
                {
                    StreamWriter stdin = _process.StandardInput;
                    stdin.WriteLine(line);
                    stdin.Flush();
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false; // stdin closed (a drain was sent), or the process is gone
            }
        }

        public async Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            if (HasExited)
                return true;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout); // armed before the write: a full child stdin pipe must not delay the timeout
            _ = Task.Run(SendDrainAndCloseStdinObserved, CancellationToken.None); // off-thread; a blocked write ends when the kill breaks the pipe
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
        /// Writes the drain line and closes stdin so the child also sees EOF. Runs on a thread-pool thread (see <see cref="RequestDrainAsync"/>), because the
        /// write blocks when the child has stopped reading and its stdin pipe is full; the drain timeout is already armed, and the close happens in a
        /// finally whatever the write did. A write left blocked at the timeout ends when the caller kills the child and the pipe breaks.
        /// </summary>
        private void SendDrainAndCloseStdinObserved()
        {
            try
            {
                SendDrainAndCloseStdin();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster.supervisor.drain-write", ex); // observed here: nothing awaits this task
            }
        }

        /// <summary>
        /// The drain write itself; see <see cref="SendDrainAndCloseStdinObserved"/>.
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

            lock (_stdinSync) // one writer at a time with TrySendLine
            {
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
/// Raised once when fewer than <see cref="ClusterSupervisorOptions.MinClusterProcesses"/> children are left running
/// (<see cref="ClusterSupervisorOptions.StopWhenBelowMinimum"/>). <paramref name="AnyGaveUp"/> is true when a child was given up on (Faulted).
/// </summary>
public sealed record SupervisorBelowMinimum(int Running, int Minimum, bool AnyGaveUp);

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
    ImmutableArray<uint> MapAssignments,
    int ConsecutiveFailures = 0,
    SupervisedExitDisposition? LastDisposition = null,
    long? LastBeatAgeMs = null);

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
    private readonly TimeProvider _time;
    private readonly ISupervisedHangCapture? _hangCapture;
    private readonly SupervisorCrashDialog? _crashDialog;
    private readonly string _protocolToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    private int _belowMinimumRaised;
    private int _minimumWatchArmed;
    private readonly Dictionary<string, ManagedEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _supervisorStop = new();
    private bool _disposed;

    public ClusterHostSupervisor(
        ClusterSupervisorOptions options,
        ISupervisedProcessLauncher launcher,
        SupervisedOutputMultiplexer? multiplexer = null,
        IClusterCapacityProbe? capacityProbe = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeProvider? timeProvider = null,
        ISupervisedHangCapture? hangCapture = null,
        SupervisorCrashDialog? crashDialog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);

        _options = options;
        _launcher = launcher;
        _multiplexer = multiplexer ?? new SupervisedOutputMultiplexer();
        _capacityProbe = capacityProbe ?? new SystemClusterCapacityProbe();
        _time = timeProvider ?? TimeProvider.System;
        _delayAsync = delayAsync ?? ((delay, ct) => Task.Delay(delay, _time, ct));
        _hangCapture = hangCapture;
        _crashDialog = crashDialog;

        foreach ((string key, SupervisedProcessConfig config) in _options.Processes)
        {
            if (string.IsNullOrWhiteSpace(config.Name))
                config.Name = key;

            config.Validate();
            _entries[config.Name] = new ManagedEntry(config, key, _time);
        }
    }

    public SupervisedOutputMultiplexer Multiplexer => _multiplexer;

    /// <summary>
    /// Raised (once, on a thread-pool thread, outside the supervisor's lock) when fewer than <see cref="ClusterSupervisorOptions.MinClusterProcesses"/>
    /// children are left running and <see cref="ClusterSupervisorOptions.StopWhenBelowMinimum"/> is on.
    /// </summary>
    public event Action<SupervisorBelowMinimum>? BelowMinimum;

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
                            && e.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff or SupervisedProcessState.Hung)
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
                && existing.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff or SupervisedProcessState.Hung or SupervisedProcessState.Draining)
            {
                throw new InvalidOperationException($"Cannot replace running supervised process '{config.Name}'.");
            }

            _options.Processes[config.Name] = config;
            _entries[config.Name] = new ManagedEntry(config, config.Name, _time);
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

            if (entry.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff or SupervisedProcessState.Hung)
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
                bool alreadyActive = entry.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff or SupervisedProcessState.Hung;
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
        entry.Config.ProtocolToken = _protocolToken;
        DateTimeOffset launchedUtc = _time.GetUtcNow(); // before the launch: a crash report the child writes is never older than this
        ISupervisedProcess process = _launcher.Launch(entry.Config, (p, line, isStdErr) => OnChildLine(entry, p, line, isStdErr)); // subscribed inside Launch: an early line is not lost
        entry.LaunchUtc = launchedUtc;
        entry.BeginIncarnation(_time.GetTimestamp());
        entry.Process = process;
        entry.Incarnation++;
        entry.State = SupervisedProcessState.Running;
        StartHangWatchUnderLock(entry, process);
    }

    // ---- the line protocol (child stdout) ----

    /// <summary>
    /// One line a child wrote. Heartbeats, the stop announcement and cross-world lifecycle requests are the supervisor's own protocol: consumed here
    /// (stdout only) and never routed to the console. Everything else, including a line that only looks like the protocol, is ordinary output.
    /// </summary>
    private void OnChildLine(ManagedEntry entry, ISupervisedProcess process, string line, bool isStdErr)
    {
        if (!isStdErr)
        {
            if (SupervisorProtocol.TryParseHeartbeat(line, out SupervisorHeartbeatState beat))
            {
                RecordBeat(entry, process, beat);
                return;
            }

            // The stop announcement is authenticated like a request: child output includes logged text a player or GM influences, so a token-less
            // (or wrong-token) stopping line is ordinary output and never starts the stop grace or sets the announced intent.
            if (SupervisorProtocol.TryParseStopping(line, out string stoppingToken, out SupervisorStopIntent intent, out _) && TokenMatches(stoppingToken))
            {
                RecordStopping(entry, process, intent);
                return;
            }

            if (SupervisorProtocol.TryParseLifecycleRequest(line, out SupervisorLifecycleRequest request))
            {
                HandleLifecycleRequest(entry, process, request);
                return;
            }
        }

        _multiplexer.RouteLine(entry.Config, line, isStdErr);
    }

    private void RecordBeat(ManagedEntry entry, ISupervisedProcess process, SupervisorHeartbeatState beat)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(entry.Process, process))
                return; // a line from an incarnation that has been replaced

            // A heartbeat is liveness only, whatever state it names: the authenticated stopping line is what suspends the hang watch.
            entry.LastBeatTimestamp = _time.GetTimestamp();
            if (beat == SupervisorHeartbeatState.Alive && !entry.Armed)
            {
                entry.Armed = true; // the first world-tick beat arms the hang watch (ready is sent before the world ticks)
                entry.WakeWatcher();
            }
        }
    }

    private void RecordStopping(ManagedEntry entry, ISupervisedProcess process, SupervisorStopIntent intent)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(entry.Process, process))
                return;

            entry.AnnouncedIntent = intent;
            entry.StopAnnouncedTimestamp ??= _time.GetTimestamp();
            entry.WakeWatcher();
        }
    }

    /// <summary>
    /// A child asked for a lifecycle command on another child (a GM's cross-world <c>.server shutdown|restart</c>). Validates the per-run token and the
    /// target realm id, forwards the command on the target's stdin and tells the issuer what happened. The write runs off the issuing child's output
    /// pump so a slow target cannot stall it.
    /// </summary>
    private void HandleLifecycleRequest(ManagedEntry issuer, ISupervisedProcess issuerProcess, SupervisorLifecycleRequest request)
    {
        SupervisorLifecycleStatus status;
        string reason;
        ManagedEntry? target = null;
        ISupervisedProcess? targetProcess = null;

        lock (_gate)
        {
            if (!ReferenceEquals(issuer.Process, issuerProcess))
                return;

            if (!TokenMatches(request.Token))
            {
                status = SupervisorLifecycleStatus.Rejected;
                reason = "its token is not this supervisor's";
            }
            else
            {
                target = _entries.Values.FirstOrDefault(e => e.Config.RealmId > 0 && e.Config.RealmId == request.RealmId);
                if (target is null)
                {
                    status = SupervisorLifecycleStatus.UnknownRealm;
                    reason = $"unknown realm {request.RealmId}";
                }
                else if (target.State != SupervisedProcessState.Running || target.Process is null)
                {
                    status = SupervisorLifecycleStatus.Rejected;
                    reason = $"realm {request.RealmId} ('{target.Config.Name}') is not running";
                }
                else
                {
                    status = SupervisorLifecycleStatus.Forwarded;
                    reason = $"forwarded to realm {request.RealmId} ('{target.Config.Name}')";
                    targetProcess = target.Process;
                }
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (status == SupervisorLifecycleStatus.Forwarded && targetProcess is not null && !targetProcess.TrySendLine(SupervisorProtocol.FormatForwardedLifecycle(request)))
                {
                    status = SupervisorLifecycleStatus.Rejected;
                    reason = $"realm {request.RealmId} could not be written to";
                }

                string text = status == SupervisorLifecycleStatus.Forwarded
                    ? $"Lifecycle {request.Action.ToString().ToLowerInvariant()} (delay {request.DelaySeconds} s, exit code {request.ExitCode}) requested by {request.By}: {reason}."
                    : $"WARNING: Ignored the lifecycle {request.Action.ToString().ToLowerInvariant()} requested by {request.By}: {reason}.";
                _multiplexer.RouteLine(issuer.Config, text, isStdErr: status != SupervisorLifecycleStatus.Forwarded);
                issuerProcess.TrySendLine(SupervisorProtocol.FormatLifecycleResult(new SupervisorLifecycleResult(request.RealmId, status, request.By)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster.supervisor.lifecycle-request", ex);
            }
        });
    }

    private bool TokenMatches(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        byte[] expected = System.Text.Encoding.UTF8.GetBytes(_protocolToken);
        byte[] given = System.Text.Encoding.UTF8.GetBytes(token);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, given);
    }

    // ---- the hang watch ----

    private enum WatchAction
    {
        None,
        WarnNoBeat,
        Hang,
        StopGraceKill,
    }

    private void StartHangWatchUnderLock(ManagedEntry entry, ISupervisedProcess process)
    {
        if (entry.Config.HangTimeoutSeconds <= 0 || entry.StopCts is null)
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(entry.StopCts.Token);
        entry.WatchCts = cts;
        CancellationToken token = cts.Token;
        _ = Task.Run(() => HangWatchAsync(entry, process, token));
    }

    /// <summary>
    /// Watches one incarnation of a child. Armed by its first <c>alive</c> beat, it acts when no beat has come for <c>HangTimeoutSeconds</c>; it is
    /// suspended once the child has announced that it is stopping (then only the stop grace applies); a child that never beats gets one warning after
    /// <c>HeartbeatGraceSeconds</c> and is never killed for silence. It sleeps until the next moment it could have to act, or until a protocol line
    /// that changes what it waits for, so a healthy child costs one wake-up per timeout.
    /// </summary>
    private async Task HangWatchAsync(ManagedEntry entry, ISupervisedProcess process, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                WatchAction action = WatchAction.None;
                TimeSpan wait = Timeout.InfiniteTimeSpan;
                Task wake;
                TimeSpan silence = TimeSpan.Zero;

                lock (_gate)
                {
                    if (!ReferenceEquals(entry.Process, process) || entry.State != SupervisedProcessState.Running)
                        return;

                    SupervisedProcessConfig config = entry.Config;
                    long now = _time.GetTimestamp();
                    wake = entry.NewWakeSignal();

                    if (entry.StopAnnouncedTimestamp is { } stoppedAt)
                    {
                        TimeSpan grace = TimeSpan.FromMilliseconds(config.StopGraceMs > 0 ? config.StopGraceMs : config.DrainTimeoutMs);
                        TimeSpan left = grace - _time.GetElapsedTime(stoppedAt, now);
                        if (left <= TimeSpan.Zero)
                        {
                            action = WatchAction.StopGraceKill;
                            entry.KillKind = SupervisorKillKind.StopGrace;
                        }
                        else
                        {
                            wait = left;
                        }
                    }
                    else if (!entry.Armed)
                    {
                        TimeSpan left = TimeSpan.FromSeconds(config.HeartbeatGraceSeconds) - _time.GetElapsedTime(entry.LaunchTimestamp, now);
                        if (entry.GraceWarned)
                        {
                            wait = Timeout.InfiniteTimeSpan; // warned; only the first beat or a stop announcement changes anything
                        }
                        else if (left <= TimeSpan.Zero)
                        {
                            action = WatchAction.WarnNoBeat;
                            entry.GraceWarned = true;
                        }
                        else
                        {
                            wait = left;
                        }
                    }
                    else
                    {
                        silence = _time.GetElapsedTime(entry.LastBeatTimestamp ?? entry.LaunchTimestamp, now);
                        TimeSpan left = TimeSpan.FromSeconds(config.HangTimeoutSeconds) - silence;
                        if (left <= TimeSpan.Zero)
                        {
                            action = WatchAction.Hang;
                            entry.KillKind = SupervisorKillKind.Hang;
                            entry.State = SupervisedProcessState.Hung;
                            entry.HangSilence = silence; // with the kill kind, so an early self-exit is never shown as a hang of 0 s
                        }
                        else
                        {
                            wait = left;
                        }
                    }
                }

                switch (action)
                {
                    case WatchAction.None:
                        await WaitForWakeOrDelayAsync(wait, wake, ct).ConfigureAwait(false);
                        break;

                    case WatchAction.WarnNoBeat:
                        _multiplexer.RouteLine(
                            entry.Config,
                            $"WARNING: Process '{entry.Config.Name}' has sent no heartbeat {entry.Config.HeartbeatGraceSeconds} s after it was launched; hang detection stays off until its first beat (is Ops:Watchdog off, or is it still starting?).",
                            isStdErr: true);
                        break;

                    case WatchAction.StopGraceKill:
                        await KillStuckStopAsync(entry, process).ConfigureAwait(false);
                        return;

                    case WatchAction.Hang:
                        await RecoverHungChildAsync(entry, process, silence, ct).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!ct.IsCancellationRequested)
            {
                FaultLedger.Shared.Record("cluster.supervisor.hang-watch", ex);
            }
        }
    }

    private async Task WaitForWakeOrDelayAsync(TimeSpan wait, Task wake, CancellationToken ct)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task delay = _delayAsync(wait, delayCts.Token);
        try
        {
            await Task.WhenAny(delay, wake).ConfigureAwait(false);
        }
        finally
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await delay.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Released by the cancel above, or the watch is ending: the loop condition decides.
        }
    }

    /// <summary>The child announced that it is stopping and has not exited within its grace: kill it (no second drain request) and let the monitor classify the exit by what it announced.</summary>
    private async Task KillStuckStopAsync(ManagedEntry entry, ISupervisedProcess process)
    {
        _multiplexer.RouteLine(
            entry.Config,
            $"ERROR: Process '{entry.Config.Name}' (pid {process.ProcessId}) announced that it is stopping but has not exited; killing it.",
            isStdErr: true);
        await Task.Run(process.KillOwnedProcess).ConfigureAwait(false);
        FaultIfStillAlive(entry, process);
    }

    /// <summary>No world tick for the hang timeout: capture (optional, bounded), ask the child to drain, kill it, and leave the relaunch to the monitor (with backoff). A kill that does not take leaves the child Faulted and unrestarted.</summary>
    private async Task RecoverHungChildAsync(ManagedEntry entry, ISupervisedProcess process, TimeSpan silence, CancellationToken ct)
    {
        SupervisedProcessConfig config = entry.Config;
        _multiplexer.RouteLine(
            config,
            $"ERROR: Process '{config.Name}' (pid {process.ProcessId}) sent no world tick heartbeat for {silence.TotalSeconds:0} s (hang timeout {config.HangTimeoutSeconds} s); draining and restarting it.",
            isStdErr: true);
        AnomalyLedger.Shared.Report("cluster", "supervisor-hang", config.Name, $"pid={process.ProcessId} silent={silence.TotalSeconds:0}s", AnomalySeverity.Error);

        if (config.HangCapture != SupervisedHangCaptureMode.None && _hangCapture is not null)
        {
            await CaptureHangAsync(entry, process, ct).ConfigureAwait(false);
        }

        bool exited = false;
        try
        {
            exited = await process.RequestDrainAsync(TimeSpan.FromMilliseconds(config.HangDrainTimeoutMs), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // the supervisor is stopping; its own drain takes over
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.supervisor.hang-drain", ex);
        }

        if (!exited)
        {
            lock (_gate)
            {
                if (ReferenceEquals(entry.Process, process)) // a restart may have replaced this incarnation while the drain ran
                    entry.HangKillIssued = true; // before the kill: the monitor only sees the exit after it, so it can tell a kill from a self-exit
            }

            await Task.Run(process.KillOwnedProcess).ConfigureAwait(false);
        }

        FaultIfStillAlive(entry, process);
    }

    private void SetHangNote(ManagedEntry entry, ISupervisedProcess process, string note)
    {
        lock (_gate)
        {
            if (ReferenceEquals(entry.Process, process))
                entry.HangCaptureNote = note;
        }
    }

    private async Task CaptureHangAsync(ManagedEntry entry, ISupervisedProcess process, CancellationToken ct)
    {
        SupervisedProcessConfig config = entry.Config;
        string? tool = string.IsNullOrWhiteSpace(_options.StackCaptureTool) ? null : _options.StackCaptureTool;
        var request = new SupervisedHangCaptureRequest(config.Name, process.ProcessId, config.HangCapture, _options.HangCaptureDirectory, tool);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task capture = _hangCapture!.CaptureAsync(request, cts.Token);
        Task timeout = _delayAsync(TimeSpan.FromSeconds(_options.HangCaptureTimeoutSeconds), cts.Token);
        Task winner;
        try
        {
            winner = await Task.WhenAny(capture, timeout).ConfigureAwait(false);
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        // Neither loser is awaited (a capture that ignores its token must not hold up the kill): both are observed so a late fault is not unobserved.
        _ = capture.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        _ = timeout.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        if (!ReferenceEquals(winner, capture))
        {
            SetHangNote(entry, process, $"the {config.HangCapture} capture did not finish within {_options.HangCaptureTimeoutSeconds} s");
            _multiplexer.RouteLine(
                config,
                $"WARNING: The hang capture of process '{config.Name}' did not finish within {_options.HangCaptureTimeoutSeconds} s; draining it anyway.",
                isStdErr: true);
            return;
        }

        try
        {
            await capture.ConfigureAwait(false);
            SetHangNote(entry, process, $"{config.HangCapture} capture written to {_options.HangCaptureDirectory}");
            _multiplexer.RouteLine(config, $"Hang capture ({config.HangCapture}) of process '{config.Name}' written to {_options.HangCaptureDirectory}.", isStdErr: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            SetHangNote(entry, process, $"the {config.HangCapture} capture failed: {ex.Message}");
            FaultLedger.Shared.Record("cluster.supervisor.hang-capture", ex);
            _multiplexer.RouteLine(config, $"WARNING: The hang capture of process '{config.Name}' failed: {ex.Message}", isStdErr: true);
        }
    }

    /// <summary>After a kill: a child that is still running cannot be restarted (it still owns its pid); mark it Faulted, report it, and let the monitor see no exit.</summary>
    private void FaultIfStillAlive(ManagedEntry entry, ISupervisedProcess process)
    {
        if (process.HasExited)
            return;

        lock (_gate)
        {
            if (!ReferenceEquals(entry.Process, process) || entry.State is SupervisedProcessState.Draining or SupervisedProcessState.Drained or SupervisedProcessState.Stopped)
                return;

            entry.State = SupervisedProcessState.Faulted;
            entry.LastDisposition = SupervisedExitDisposition.Fault;
        }

        _multiplexer.RouteLine(
            entry.Config,
            $"FATAL: Process '{entry.Config.Name}' (pid {process.ProcessId}) is still running after a drain request and a kill; not restarting it, and it remains in the owned set.",
            isStdErr: true);
        AnomalyLedger.Shared.Report("cluster", "supervisor-kill-failed", entry.Config.Name, $"pid={process.ProcessId}", AnomalySeverity.Error);
        NotifyIfBelowMinimum();
    }

    // ---- exits and restarts ----

    /// <summary>
    /// Waits for each exit of the child and decides what happens next from <see cref="SupervisedExitClassifier"/>: stay down, relaunch at once, relaunch
    /// after a capped exponential backoff, or fault. A crash loop (more than <c>CrashLoopMaxRestarts</c> failures inside <c>CrashLoopWindowSeconds</c>)
    /// and a relaunch that cannot start are failures too, so they back off and eventually give up instead of looping or faulting at the first error.
    /// </summary>
    private async Task MonitorProcessLoopAsync(ManagedEntry entry, CancellationToken ct)
    {
        bool relaunchPending = false;
        int delayMs = 0;

        while (!ct.IsCancellationRequested)
        {
            string? announcement = null;
            bool announcementIsError = true;

            if (!relaunchPending)
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

                bool notify = false;
                SupervisorChildExit? crashExit = null; // an unexpected nonzero exit the supervisor did not cause: offered to the crash dialog after the lock
                lock (_gate)
                {
                    entry.LastExitCode = exitCode;
                    if (entry.State is SupervisedProcessState.Draining or SupervisedProcessState.Drained or SupervisedProcessState.Stopped or SupervisedProcessState.Faulted
                        || ct.IsCancellationRequested)
                    {
                        return;
                    }

                    long now = _time.GetTimestamp();
                    bool healthy = _time.GetElapsedTime(entry.LaunchTimestamp, now) >= TimeSpan.FromSeconds(_options.HealthyUptimeSeconds);
                    SupervisedExitDisposition disposition = SupervisedExitClassifier.Classify(exitCode, entry.KillKind, entry.AnnouncedIntent);
                    SupervisorKillKind killedBy = entry.KillKind;
                    entry.LastDisposition = disposition;
                    entry.EndIncarnation();
                    entry.Process?.Dispose();
                    entry.Process = null;

                    switch (disposition)
                    {
                        case SupervisedExitDisposition.StayDown:
                            entry.State = SupervisedProcessState.Stopped;
                            entry.ConsecutiveFailures = 0;
                            entry.LastBackoffDelayMs = 0;
                            announcement = $"Process '{entry.Config.Name}' exited with code {exitCode}; leaving it down.";
                            announcementIsError = false;
                            notify = true;
                            break;

                        case SupervisedExitDisposition.Fault:
                            entry.State = SupervisedProcessState.Faulted;
                            announcement = $"FATAL: Process '{entry.Config.Name}' exited with code {exitCode} ({(exitCode == ExitCodes.InvalidConfiguration ? "invalid configuration" : "usage error")}); not restarting.";
                            notify = true;
                            crashExit = NewCrashExit(entry, currentProcess, exitCode, "not restarting it (a restart would fail the same way)");
                            break;

                        case SupervisedExitDisposition.RelaunchNow:
                            entry.ConsecutiveFailures = 0;
                            if (!healthy && GiveUpOnCrashLoopUnderLock(entry, now, exitCode, out announcement))
                            {
                                notify = true;
                                break;
                            }

                            entry.RestartCount++;
                            delayMs = _options.RestartDelayMs;
                            entry.LastBackoffDelayMs = delayMs;
                            entry.State = SupervisedProcessState.BackingOff;
                            announcement = killedBy == SupervisorKillKind.StopGrace
                                ? $"Process '{entry.Config.Name}' (incarnation {entry.Incarnation}) was killed after it announced a restart; relaunching it (restart #{entry.RestartCount})."
                                : $"Process '{entry.Config.Name}' (incarnation {entry.Incarnation}) exited with code {exitCode} (restart requested); relaunching it (restart #{entry.RestartCount}).";
                            announcementIsError = false;
                            relaunchPending = true;
                            break;

                        default:
                            if (healthy)
                                entry.ConsecutiveFailures = 0;

                            entry.ConsecutiveFailures++;
                            if (GiveUpOnCrashLoopUnderLock(entry, now, exitCode, out announcement))
                            {
                                notify = true;
                                if (killedBy != SupervisorKillKind.StopGrace)
                                    crashExit = NewCrashExit(entry, currentProcess, exitCode, "giving up on it (crash loop), not restarting it");
                                break;
                            }

                            entry.RestartCount++;
                            delayMs = ClusterSupervisorOptions.ComputeBackoffDelayMs(entry.ConsecutiveFailures, entry.Config.RestartBackoffMinMs, entry.Config.RestartBackoffMaxMs);
                            entry.LastBackoffDelayMs = delayMs;
                            entry.State = SupervisedProcessState.BackingOff;
                            if (killedBy != SupervisorKillKind.StopGrace)
                                crashExit = NewCrashExit(entry, currentProcess, exitCode, $"restarting it in {delayMs / 1000.0:0.##} s (restart #{entry.RestartCount})");
                            announcement = killedBy == SupervisorKillKind.Hang
                                ? $"ERROR: Process '{entry.Config.Name}' (incarnation {entry.Incarnation}) was killed as hung (exit code {exitCode}); restarting in {delayMs} ms (restart #{entry.RestartCount})."
                                : $"ERROR: Process '{entry.Config.Name}' (incarnation {entry.Incarnation}) exited unexpectedly with code {exitCode}; restarting in {delayMs} ms (restart #{entry.RestartCount}).";
                            relaunchPending = true;
                            break;
                    }
                }

                // Surface the exit through the multiplexer: an error is always visible in the merged console.
                if (announcement is not null)
                {
                    _multiplexer.RouteLine(entry.Config, announcement, isStdErr: announcementIsError);
                }

                if (crashExit is not null)
                {
                    OfferToCrashDialog(crashExit);
                }

                if (notify)
                {
                    NotifyIfBelowMinimum();
                    return;
                }
            }

            if (delayMs > 0)
            {
                try
                {
                    await _delayAsync(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }

            bool gaveUp = false;
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
                    relaunchPending = false;
                    announcement = null;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("cluster.supervisor", ex);
                    AnomalyLedger.Shared.Report("cluster", "gateway-fault", entry.Config.Name, $"phase=relaunch error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
                    entry.ConsecutiveFailures++;
                    if (GiveUpOnCrashLoopUnderLock(entry, _time.GetTimestamp(), null, out announcement))
                    {
                        gaveUp = true;
                    }
                    else
                    {
                        delayMs = ClusterSupervisorOptions.ComputeBackoffDelayMs(entry.ConsecutiveFailures, entry.Config.RestartBackoffMinMs, entry.Config.RestartBackoffMaxMs);
                        entry.LastBackoffDelayMs = delayMs;
                        entry.State = SupervisedProcessState.BackingOff;
                        announcement = $"ERROR: Failed to relaunch process '{entry.Config.Name}': {ex.Message}; trying again in {delayMs} ms.";
                    }
                }
            }

            if (announcement is not null)
            {
                _multiplexer.RouteLine(entry.Config, announcement, isStdErr: true);
            }

            if (gaveUp)
            {
                NotifyIfBelowMinimum();
                return;
            }
        }
    }

    /// <summary>The facts of an unexpected exit for <see cref="SupervisorCrashDialog"/>. Call under <c>_gate</c>, before the incarnation's launch time is replaced.</summary>
    private static SupervisorChildExit NewCrashExit(ManagedEntry entry, ISupervisedProcess process, int exitCode, string decision)
        => new(entry.Config.Name, entry.Config.Role, process.ProcessId, exitCode, entry.LaunchUtc, decision,
            entry.Config.ExecutablePath, entry.Config.Arguments, entry.Config.WorkingDirectory,
            entry.KillKind == SupervisorKillKind.Hang ? entry.HangSilence ?? TimeSpan.Zero : null, entry.KillKind == SupervisorKillKind.Hang ? entry.HangCaptureNote : null, entry.KillKind == SupervisorKillKind.Hang && !entry.HangKillIssued);

    /// <summary>Hands an unexpected exit to the crash dialog, which starts its own thread and never throws; nothing here may delay the restart.</summary>
    private void OfferToCrashDialog(SupervisorChildExit exit)
    {
        if (_crashDialog is null || (exit.ExitCode == 0 && exit.HangSilence is null))
            return;

        _crashDialog.OnChildExited(exit); // never throws: its own failures are recorded there with LogFault
    }

    /// <summary>
    /// Records one failure of <paramref name="entry"/> at <paramref name="now"/> and, when that is more than <c>CrashLoopMaxRestarts</c> inside the window,
    /// marks the child Faulted, writes the ledgers and returns true with the message to show. Call under <c>_gate</c>.
    /// </summary>
    private bool GiveUpOnCrashLoopUnderLock(ManagedEntry entry, long now, int? exitCode, out string? message)
    {
        TimeSpan window = TimeSpan.FromSeconds(_options.CrashLoopWindowSeconds);
        entry.FailureStamps.Enqueue(now);
        while (entry.FailureStamps.Count > 0 && _time.GetElapsedTime(entry.FailureStamps.Peek(), now) > window)
        {
            entry.FailureStamps.Dequeue();
        }

        if (entry.FailureStamps.Count <= _options.CrashLoopMaxRestarts)
        {
            message = null;
            return false;
        }

        entry.State = SupervisedProcessState.Faulted;
        entry.LastDisposition = SupervisedExitDisposition.Fault;
        string last = exitCode is { } code ? $"the last exit code was {code}" : "the last relaunch could not start the process";
        message = $"FATAL: Process '{entry.Config.Name}' failed {entry.FailureStamps.Count} times within {_options.CrashLoopWindowSeconds} s (crash loop; {last}); giving up, not restarting it.";
        AnomalyLedger.Shared.Report("cluster", "supervisor-crash-loop", entry.Config.Name, $"failures={entry.FailureStamps.Count} window={_options.CrashLoopWindowSeconds}s {last}", AnomalySeverity.Error);
        FaultLedger.Shared.Record("cluster.supervisor.crash-loop", new InvalidOperationException(message));
        return true;
    }

    /// <summary>Starts watching the child count: until this is called an exit during startup cannot make the supervisor stop itself, and the first call evaluates the state at once.</summary>
    public void BeginMinimumWatch()
    {
        Volatile.Write(ref _minimumWatchArmed, 1);
        NotifyIfBelowMinimum();
    }

    /// <summary>Raises <see cref="BelowMinimum"/> once when fewer than <see cref="ClusterSupervisorOptions.MinClusterProcesses"/> children are alive (running, backing off, hung or draining).</summary>
    private void NotifyIfBelowMinimum()
    {
        SupervisorBelowMinimum? report = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _minimumWatchArmed) == 0 || !_options.StopWhenBelowMinimum || _disposed || _belowMinimumRaised != 0)
                return;

            int alive = _entries.Values.Count(e => e.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff or SupervisedProcessState.Hung or SupervisedProcessState.Draining);
            if (alive >= _options.MinClusterProcesses)
                return;

            _belowMinimumRaised = 1;
            report = new SupervisorBelowMinimum(alive, _options.MinClusterProcesses, _entries.Values.Any(e => e.State == SupervisedProcessState.Faulted));
        }

        BelowMinimum?.Invoke(report);
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
                if (entry.State is SupervisedProcessState.Running or SupervisedProcessState.BackingOff or SupervisedProcessState.Hung or SupervisedProcessState.Draining)
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
                entry.EndIncarnation();
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
        public int ConsecutiveFailures { get; set; }
        public SupervisedExitDisposition? LastDisposition { get; set; }
        public long? LastBeatTimestamp { get; set; }
        public CancellationTokenSource? StopCts { get; set; }
        public Task? MonitorTask { get; set; }

        // Per-incarnation hang-watch state; all of it is read and written under the supervisor's _gate.
        public long LaunchTimestamp { get; set; }
        public DateTimeOffset LaunchUtc { get; set; }
        public TimeSpan? HangSilence { get; set; }
        public string? HangCaptureNote { get; set; }
        public bool HangKillIssued { get; set; }
        public bool Armed { get; set; }
        public bool GraceWarned { get; set; }
        public long? StopAnnouncedTimestamp { get; set; }
        public SupervisorStopIntent? AnnouncedIntent { get; set; }
        public SupervisorKillKind KillKind { get; set; }
        public CancellationTokenSource? WatchCts { get; set; }
        public Queue<long> FailureStamps { get; } = new();

        private TaskCompletionSource _wake = NewSignal();

        private readonly TimeProvider _time;

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Resets the per-incarnation state for a fresh launch.</summary>
        public void BeginIncarnation(long now)
        {
            EndIncarnation();
            LaunchTimestamp = now;
            LastBeatTimestamp = null;
            Armed = false;
            GraceWarned = false;
            StopAnnouncedTimestamp = null;
            AnnouncedIntent = null;
            KillKind = SupervisorKillKind.None;
            HangSilence = null;
            HangCaptureNote = null;
            HangKillIssued = false;
            _wake = NewSignal();
        }

        /// <summary>Ends the hang watch of the current incarnation (cancels it and releases anything waiting on it).</summary>
        public void EndIncarnation()
        {
            CancellationTokenSource? cts = WatchCts;
            WatchCts = null;
            if (cts is not null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // already torn down
                }

                cts.Dispose();
            }

            _wake.TrySetResult();
        }

        /// <summary>A task that completes at the next protocol line that changes what the watch waits for. Call under _gate.</summary>
        public Task NewWakeSignal()
        {
            if (_wake.Task.IsCompleted)
                _wake = NewSignal();

            return _wake.Task;
        }

        public void WakeWatcher() => _wake.TrySetResult();

        public ManagedEntry(SupervisedProcessConfig config, string key, TimeProvider time)
        {
            Config = config;
            Key = key;
            _time = time;
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
            [.. MapAssignments],
            ConsecutiveFailures,
            LastDisposition,
            LastBeatTimestamp is { } beat ? (long)_time.GetElapsedTime(beat).TotalMilliseconds : null);
    }
}

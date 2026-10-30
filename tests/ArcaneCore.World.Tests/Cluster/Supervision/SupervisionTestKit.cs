using System.Collections.Concurrent;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>Polls for a condition that another thread makes true; the bound only matters when the test is already failing.</summary>
internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            await Task.Delay(2);
        }
    }
}

/// <summary>A clock that only moves when the test says so (timestamps and UTC), so uptime, beat age and crash windows need no sleeping.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks = 1_000_000 * TimeSpan.TicksPerSecond;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>
/// The supervisor's delay seam under test control. A delay either completes at once (<see cref="AutoComplete"/>) or stays pending until
/// <see cref="ReleaseAll"/>/<see cref="Release"/>; a cancelled token cancels its pending delay. <see cref="Requested"/> records every request.
/// </summary>
internal sealed class ManualDelays
{
    private readonly object _gate = new();
    private readonly List<(TimeSpan Duration, TaskCompletionSource Tcs)> _pending = [];
    private int _requests;

    public ConcurrentQueue<TimeSpan> Requested { get; } = new();

    /// <summary>Delays this returns true for complete at once (backoff waits in tests that do not care about them).</summary>
    public Func<TimeSpan, bool> AutoComplete { get; set; } = _ => false;

    public int RequestCount => Volatile.Read(ref _requests);

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>The backoff waits (a minute or less) requested so far, in order.</summary>
    public TimeSpan[] Backoffs => [.. Requested.Where(IsBackoff)];

    /// <summary>A wait of at most a minute: the restart backoff (the hang watch waits for longer, or forever).</summary>
    public static bool IsBackoff(TimeSpan duration) => duration >= TimeSpan.Zero && duration <= TimeSpan.FromSeconds(60);

    public Task Delay(TimeSpan duration, CancellationToken ct)
    {
        Requested.Enqueue(duration);
        Interlocked.Increment(ref _requests);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled(ct);
        }

        if (AutoComplete(duration))
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending.Add((duration, tcs));
        }

        ct.Register(() =>
        {
            lock (_gate)
            {
                _pending.RemoveAll(p => p.Tcs == tcs);
            }

            tcs.TrySetCanceled(ct);
        });
        return tcs.Task;
    }

    public void ReleaseAll() => Release(_ => true);

    public void Release(Func<TimeSpan, bool> match)
    {
        List<TaskCompletionSource> toRelease;
        lock (_gate)
        {
            toRelease = [.. _pending.Where(p => match(p.Duration)).Select(p => p.Tcs)];
            _pending.RemoveAll(p => match(p.Duration));
        }

        foreach (TaskCompletionSource tcs in toRelease)
        {
            tcs.TrySetResult();
        }
    }

    /// <summary>Releases every pending delay and waits until something requests a new one: the loop that was waiting has run once more.</summary>
    public async Task PulseAsync()
    {
        int before = RequestCount;
        ReleaseAll();
        await TestWait.UntilAsync(() => RequestCount > before, "the supervisor to request another delay");
    }

    public Task WaitForPendingAsync(Func<TimeSpan, bool> match, string what)
        => TestWait.UntilAsync(() =>
        {
            lock (_gate)
            {
                return _pending.Any(p => match(p.Duration));
            }
        }, what);
}

internal enum DrainBehaviour
{
    /// <summary>The child exits with <see cref="ScriptedProcess.DrainExitCode"/> as soon as it is asked to drain.</summary>
    ExitsOnDrain,

    /// <summary>The child ignores the drain; the drain call reports a timeout.</summary>
    IgnoresDrain,
}

/// <summary>A scripted child: the test emits its output lines, makes it exit, and reads what the supervisor did to it.</summary>
internal sealed class ScriptedProcess : ISupervisedProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _journal;
    private int _drains;
    private int _kills;
    private int _disposed;

    public ScriptedProcess(int processId, string name, string? token, ConcurrentQueue<string> journal)
    {
        ProcessId = processId;
        Name = name;
        Token = token;
        _journal = journal;
    }

    public string Name { get; }

    /// <summary>The protocol token the supervisor put in this child's configuration at launch.</summary>
    public string? Token { get; }

    public int ProcessId { get; }

    public DrainBehaviour Drain { get; set; } = DrainBehaviour.ExitsOnDrain;

    public int DrainExitCode { get; set; }

    /// <summary>When true the OS "refuses" a kill: the child stays running.</summary>
    public bool RefuseKill { get; set; }

    public TimeSpan? LastDrainTimeout { get; private set; }

    public int Drains => Volatile.Read(ref _drains);

    public int Kills => Volatile.Read(ref _kills);

    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    public ConcurrentQueue<string> Sent { get; } = new();

    public bool HasExited => _exit.Task.IsCompleted;

    public int? ExitCode => _exit.Task.IsCompleted ? _exit.Task.Result : null;

    public event Action<string, bool>? OutputLineReceived;

    public void Emit(string line, bool isStdErr = false) => OutputLineReceived?.Invoke(line, isStdErr);

    public void Beat(string state = "alive") => Emit($"heartbeat 2026-10-29T00:00:00.000Z pid={ProcessId} {state}");

    public void ExitWith(int code) => _exit.TrySetResult(code);

    public Task<int> WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

    public Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _drains);
        LastDrainTimeout = timeout;
        _journal.Enqueue("drain:" + Name);
        if (Drain == DrainBehaviour.ExitsOnDrain)
        {
            ExitWith(DrainExitCode);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public void KillOwnedProcess()
    {
        Interlocked.Increment(ref _kills);
        _journal.Enqueue("kill:" + Name);
        if (!RefuseKill)
        {
            ExitWith(-1);
        }
    }

    public bool TrySendLine(string line)
    {
        Sent.Enqueue(line);
        return !HasExited;
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

/// <summary>Hands out <see cref="ScriptedProcess"/> children and records every launch; a launch can be made to throw.</summary>
internal sealed class ScriptedLauncher : ISupervisedProcessLauncher
{
    private readonly List<ScriptedProcess> _processes = [];
    private int _launches;

    public ConcurrentQueue<string> Journal { get; } = new();

    /// <summary>Applied to each child as it is created (default behaviours, per launch index).</summary>
    public Action<int, ScriptedProcess>? Configure { get; set; }

    /// <summary>Returns an exception to throw for the launch with this zero-based index, or null to let it succeed.</summary>
    public Func<int, Exception?>? FailLaunch { get; set; }

    public int Launches => Volatile.Read(ref _launches);

    public IReadOnlyList<ScriptedProcess> Processes
    {
        get
        {
            lock (_processes)
            {
                return [.. _processes];
            }
        }
    }

    public ScriptedProcess Last => Processes[^1];

    public ISupervisedProcess Launch(SupervisedProcessConfig config)
    {
        int index = Interlocked.Increment(ref _launches) - 1;
        if (FailLaunch?.Invoke(index) is { } failure)
        {
            throw failure;
        }

        var process = new ScriptedProcess(30_000 + index, config.Name, config.ProtocolToken, Journal);
        Configure?.Invoke(index, process);
        lock (_processes)
        {
            _processes.Add(process);
        }

        return process;
    }

    public Task WaitForLaunchesAsync(int count) => TestWait.UntilAsync(() => Processes.Count >= count, $"{count} successful launch(es)");
}

internal sealed class FixedMemoryProbe(long availableMb = 100_000) : IClusterCapacityProbe
{
    public long GetAvailableMemoryMb() => availableMb;
}

internal sealed class RecordingHangCapture : ISupervisedHangCapture
{
    private readonly ConcurrentQueue<string> _journal;

    public RecordingHangCapture(ConcurrentQueue<string> journal)
    {
        _journal = journal;
    }

    /// <summary>When true the capture never finishes on its own (it ends only when the supervisor cancels it).</summary>
    public bool Hangs { get; set; }

    public ConcurrentQueue<SupervisedHangCaptureRequest> Requests { get; } = new();

    public async Task CaptureAsync(SupervisedHangCaptureRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        _journal.Enqueue("capture:" + request.Name);
        if (Hangs)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>One supervisor wired to scripted children, a manual clock and manual delays.</summary>
internal sealed class SupervisorRig : IAsyncDisposable
{
    public SupervisorRig(ClusterSupervisorOptions options, ScriptedLauncher? launcher = null, bool holdAllDelays = false, RecordingHangCapture? capture = null, SupervisorCrashDialog? crashDialog = null)
    {
        Launcher = launcher ?? new ScriptedLauncher();
        Options = options;
        Console = new StringWriter();
        Mux = new SupervisedOutputMultiplexer(Console);
        Capture = capture;
        Delays.AutoComplete = holdAllDelays ? _ => false : ManualDelays.IsBackoff;
        Supervisor = new ClusterHostSupervisor(options, Launcher, Mux, new FixedMemoryProbe(), Delays.Delay, Clock, capture, crashDialog);
    }

    public ClusterSupervisorOptions Options { get; }

    public ScriptedLauncher Launcher { get; }

    public ManualClock Clock { get; } = new();

    public ManualDelays Delays { get; } = new();

    public StringWriter Console { get; }

    public SupervisedOutputMultiplexer Mux { get; }

    public RecordingHangCapture? Capture { get; }

    public ClusterHostSupervisor Supervisor { get; }

    public string ConsoleText
    {
        get
        {
            lock (Console)
            {
                return Console.ToString();
            }
        }
    }

    public static ClusterSupervisorOptions OneProcess(string name = "world", Action<SupervisedProcessConfig>? configure = null, Action<ClusterSupervisorOptions>? options = null)
    {
        var o = new ClusterSupervisorOptions { MinClusterProcesses = 1 };
        var config = new SupervisedProcessConfig { Name = name, Role = SupervisedRoles.Combined, ExecutablePath = "x", RequiredMemoryMb = 1 };
        configure?.Invoke(config);
        o.Processes[name] = config;
        options?.Invoke(o);
        return o;
    }

    public async ValueTask DisposeAsync()
    {
        await Supervisor.DisposeAsync();
    }
}

internal static class ManualDelaysExtensions
{
    /// <summary>
    /// Releases every pending delay again and again until <paramref name="condition"/> holds. A loop that was between "decided to wait"
    /// and "registered the wait" when one release ran is caught by the next; an extra release only makes a loop re-check its state.
    /// </summary>
    public static async Task ReleaseUntilAsync(this ManualDelays delays, Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            delays.ReleaseAll();
            await Task.Delay(2);
        }
    }
}

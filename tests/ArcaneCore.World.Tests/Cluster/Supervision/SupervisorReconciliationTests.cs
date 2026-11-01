using System.Collections.Concurrent;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// Restart reconciliation of the host supervisor: the state file of owned children, adoption or termination of the ones a crashed predecessor left
/// running (found by pid and start time), and a second drain of a Faulted entry. Everything runs against a fake launcher and a fake process inspector;
/// no OS process is started or touched.
/// </summary>
public sealed class SupervisorReconciliationTests : IDisposable
{
    private static readonly DateTimeOffset ChildStart = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "supervisor-reconcile-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "children.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Launched_child_is_recorded_with_pid_and_start_time()
    {
        await using Harness h = new(StatePath, "gateway", SupervisedRoles.Gateway);

        await h.Supervisor.StartProcessAsync("gateway");

        IReadOnlyList<SupervisedChildRecord> recorded = new SupervisorChildStateFile(StatePath).Load();
        SupervisedChildRecord record = Assert.Single(recorded);
        Assert.Equal("gateway", record.Name);
        Assert.Equal(SupervisedRoles.Gateway, record.Role);
        Assert.Equal(h.Launcher.Last.ProcessId, record.ProcessId);
        Assert.Equal(ChildStart.ToUnixTimeMilliseconds(), record.StartTimeUtcMs);
    }

    [Fact]
    public async Task Restart_adopts_the_recorded_child_found_by_pid_and_start_time()
    {
        new SupervisorChildStateFile(StatePath).Save([new SupervisedChildRecord("gateway", SupervisedRoles.Gateway, 4242, ChildStart.ToUnixTimeMilliseconds())]);
        await using Harness h = new(StatePath, "gateway", SupervisedRoles.Gateway);
        h.Inspector.Alive[4242] = ChildStart.AddMilliseconds(300); // the same process, read back with a slightly different clock

        SupervisorReconciliationReport report = h.Supervisor.ReconcileOrphans();

        Assert.Equal(new[] { "gateway" }, report.Adopted);
        Assert.Empty(report.Terminated);
        Assert.Empty(report.Ignored);
        Assert.Empty(h.Inspector.Kills);
        SupervisedProcessSnapshot snapshot = h.Supervisor.GetSnapshot("gateway");
        Assert.Equal(SupervisedProcessState.Running, snapshot.State);
        Assert.Equal(4242, snapshot.ProcessId);

        await h.Supervisor.StartProcessAsync("gateway"); // already running: no second incarnation
        Assert.Equal(0, h.Launcher.Launches);
        Assert.Equal(4242, Assert.Single(new SupervisorChildStateFile(StatePath).Load()).ProcessId);
    }

    [Fact]
    public async Task Restart_ignores_a_recycled_pid_with_a_different_start_time()
    {
        new SupervisorChildStateFile(StatePath).Save([new SupervisedChildRecord("gateway", SupervisedRoles.Gateway, 4242, ChildStart.ToUnixTimeMilliseconds())]);
        await using Harness h = new(StatePath, "gateway", SupervisedRoles.Gateway);
        h.Inspector.Alive[4242] = ChildStart.AddHours(5); // some other program now owns pid 4242

        SupervisorReconciliationReport report = h.Supervisor.ReconcileOrphans();

        Assert.Equal(new[] { "gateway" }, report.Ignored);
        Assert.Empty(report.Adopted);
        Assert.Empty(report.Terminated);
        Assert.Empty(h.Inspector.Kills); // never touch a process that is not ours
        Assert.True(h.Inspector.Alive.ContainsKey(4242));
        Assert.Equal(SupervisedProcessState.Stopped, h.Supervisor.GetSnapshot("gateway").State);
        Assert.Empty(new SupervisorChildStateFile(StatePath).Load()); // the stale record is dropped
    }

    [Fact]
    public async Task Restart_ignores_a_recorded_child_that_is_gone()
    {
        new SupervisorChildStateFile(StatePath).Save([new SupervisedChildRecord("gateway", SupervisedRoles.Gateway, 4242, ChildStart.ToUnixTimeMilliseconds())]);
        await using Harness h = new(StatePath, "gateway", SupervisedRoles.Gateway);

        SupervisorReconciliationReport report = h.Supervisor.ReconcileOrphans();

        Assert.Equal(new[] { "gateway" }, report.Ignored);
        Assert.Empty(h.Inspector.Kills);
    }

    [Fact]
    public async Task Restart_terminates_an_orphaned_worker_instead_of_adopting_it()
    {
        new SupervisorChildStateFile(StatePath).Save([new SupervisedChildRecord("worker", SupervisedRoles.Worker, 5150, ChildStart.ToUnixTimeMilliseconds())]);
        await using Harness h = new(StatePath, "worker", SupervisedRoles.Worker);
        h.Inspector.Alive[5150] = ChildStart;

        SupervisorReconciliationReport report = h.Supervisor.ReconcileOrphans();

        Assert.Equal(new[] { "worker" }, report.Terminated);
        Assert.Equal(new[] { 5150 }, h.Inspector.Kills);
        Assert.False(h.Inspector.Alive.ContainsKey(5150));
        Assert.Equal(SupervisedProcessState.Stopped, h.Supervisor.GetSnapshot("worker").State);
        Assert.Empty(new SupervisorChildStateFile(StatePath).Load());
    }

    [Fact]
    public async Task Restart_that_cannot_terminate_an_orphan_reports_it_and_keeps_it_recorded()
    {
        new SupervisorChildStateFile(StatePath).Save([new SupervisedChildRecord("worker", SupervisedRoles.Worker, 5150, ChildStart.ToUnixTimeMilliseconds())]);
        await using Harness h = new(StatePath, "worker", SupervisedRoles.Worker);
        h.Inspector.Alive[5150] = ChildStart;
        h.Inspector.RefuseKill = true;

        SupervisorReconciliationReport report = h.Supervisor.ReconcileOrphans();

        Assert.Equal(new[] { "worker" }, report.Failed);
        Assert.Equal(5150, Assert.Single(new SupervisorChildStateFile(StatePath).Load()).ProcessId);
    }

    [Fact]
    public async Task Second_drain_of_a_Faulted_entry_leaves_it_Faulted()
    {
        await using Harness h = new(StatePath, "gateway", SupervisedRoles.Gateway);
        await h.Supervisor.StartProcessAsync("gateway");
        StampedProcess child = h.Launcher.Last;
        child.IgnoresDrain = true;
        child.RefuseKill = true; // the OS refuses the kill: the child survives its drain

        SupervisedProcessSnapshot first = await h.Supervisor.DrainProcessAsync("gateway");
        Assert.Equal(SupervisedProcessState.Faulted, first.State);

        SupervisedProcessSnapshot second = await h.Supervisor.DrainProcessAsync("gateway");
        Assert.Equal(SupervisedProcessState.Faulted, second.State);
        Assert.Equal(SupervisedProcessState.Faulted, h.Supervisor.GetSnapshot("gateway").State);
        Assert.Equal(child.ProcessId, Assert.Single(new SupervisorChildStateFile(StatePath).Load()).ProcessId); // the survivor stays recorded
    }

    [Fact]
    public async Task Drain_of_a_cleanly_exited_child_still_ends_Drained_and_clears_the_record()
    {
        await using Harness h = new(StatePath, "gateway", SupervisedRoles.Gateway);
        await h.Supervisor.StartProcessAsync("gateway");

        SupervisedProcessSnapshot drained = await h.Supervisor.DrainProcessAsync("gateway");

        Assert.Equal(SupervisedProcessState.Drained, drained.State);
        Assert.Empty(new SupervisorChildStateFile(StatePath).Load());
    }

    [Fact]
    public void State_file_load_tolerates_a_missing_or_damaged_file()
    {
        var file = new SupervisorChildStateFile(StatePath);
        Assert.Empty(file.Load());

        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ not json");
        Assert.Empty(file.Load());
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(string statePath, string name, string role)
        {
            ClusterSupervisorOptions options = SupervisorRig.OneProcess(name, c => c.Role = role);
            Delays.AutoComplete = ManualDelays.IsBackoff;
            Supervisor = new ClusterHostSupervisor(
                options, Launcher, new SupervisedOutputMultiplexer(new StringWriter()), new FixedMemoryProbe(), Delays.Delay, Clock,
                childStateFile: new SupervisorChildStateFile(statePath), processInspector: Inspector);
        }

        public StampedLauncher Launcher { get; } = new();

        public FakeInspector Inspector { get; } = new();

        public ManualClock Clock { get; } = new();

        public ManualDelays Delays { get; } = new();

        public ClusterHostSupervisor Supervisor { get; }

        public async ValueTask DisposeAsync() => await Supervisor.DisposeAsync();
    }

    private sealed class FakeInspector : ISupervisorProcessInspector
    {
        public ConcurrentDictionary<int, DateTimeOffset> Alive { get; } = new();

        public ConcurrentQueue<int> KillQueue { get; } = new();

        public int[] Kills => [.. KillQueue];

        public bool RefuseKill { get; set; }

        public bool TryGetStartTimeUtc(int processId, out DateTimeOffset startTimeUtc) => Alive.TryGetValue(processId, out startTimeUtc);

        public bool TryKill(int processId)
        {
            KillQueue.Enqueue(processId);
            if (RefuseKill)
                return false;

            Alive.TryRemove(processId, out _);
            return true;
        }
    }

    private sealed class StampedLauncher : ISupervisedProcessLauncher
    {
        private readonly List<StampedProcess> _processes = [];

        public int Launches
        {
            get
            {
                lock (_processes)
                {
                    return _processes.Count;
                }
            }
        }

        public StampedProcess Last
        {
            get
            {
                lock (_processes)
                {
                    return _processes[^1];
                }
            }
        }

        public ISupervisedProcess Launch(SupervisedProcessConfig config)
        {
            lock (_processes)
            {
                var process = new StampedProcess(31_000 + _processes.Count);
                _processes.Add(process);
                return process;
            }
        }
    }

    private sealed class StampedProcess(int processId) : ISupervisedProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId { get; } = processId;

        public DateTimeOffset? StartTimeUtc => ChildStart;

        public bool IgnoresDrain { get; set; }

        public bool RefuseKill { get; set; }

        public bool HasExited => _exit.Task.IsCompleted;

        public int? ExitCode => _exit.Task.IsCompleted ? _exit.Task.Result : null;

        public event Action<string, bool>? OutputLineReceived
        {
            add { }
            remove { }
        }

        public Task<int> WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

        public Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            if (IgnoresDrain)
                return Task.FromResult(false);

            _exit.TrySetResult(0);
            return Task.FromResult(true);
        }

        public void KillOwnedProcess()
        {
            if (!RefuseKill)
                _exit.TrySetResult(-1);
        }

        public void Dispose()
        {
        }
    }
}

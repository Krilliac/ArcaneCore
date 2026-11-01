using System.Collections.Concurrent;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// The supervisor's restart of a child by name (<c>lifecycle restart-child</c>), optionally as a standby (<c>--Cluster:Worker:Standby=true</c>), and the persisted
/// blue/green role in <see cref="SupervisorChildStateFile"/>. Everything runs against the scripted launcher (the process boundary) and a manual clock; no OS process
/// is started. The protocol lines are the real ones, written by the scripted gateway child exactly as a world would write them.
/// </summary>
public sealed class SupervisorRestartByNameTests : IDisposable
{
    private const string Worker = "worker:EK";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "supervisor-restart-name-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "children.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---- protocol lines ----

    [Fact]
    public void ChildRestartRequest_HasThePinnedLineAndRoundTrips()
    {
        var request = new SupervisorChildRestartRequest("TOK", "worker:EK", Standby: true, "Admin");

        string line = SupervisorProtocol.FormatChildRestartRequest(request);

        Assert.Equal("lifecycle restart-child token=TOK name=worker:EK standby=true by=Admin", line);
        Assert.True(SupervisorProtocol.TryParseChildRestartRequest(line, out SupervisorChildRestartRequest parsed));
        Assert.Equal(request, parsed);
        Assert.True(SupervisorProtocol.TryParseChildRestartRequest(
            SupervisorProtocol.FormatChildRestartRequest(request with { Standby = false }), out SupervisorChildRestartRequest active));
        Assert.False(active.Standby);
    }

    [Theory]
    [InlineData("lifecycle restart-child token=T name=a standby=maybe by=x")]
    [InlineData("lifecycle restart-child token=T name=a standby=true")]
    [InlineData("lifecycle restart-child token=T name=a standby=true by=x extra=1")]
    [InlineData("lifecycle restart-child token= name=a standby=true by=x")]
    [InlineData("lifecycle restart-child token=T name= standby=true by=x")]
    [InlineData("lifecycle restart-child  token=T name=a standby=true by=x")]
    [InlineData("lifecycle restart-child token=T name=a standby=true by=x\n")]
    [InlineData("lifecycle restart token=T name=a standby=true by=x")]
    public void ChildRestartRequest_RejectsMalformedLines(string line)
    {
        Assert.False(SupervisorProtocol.TryParseChildRestartRequest(line, out _));
    }

    [Theory]
    [InlineData(SupervisorLifecycleStatus.Forwarded, "forwarded")]
    [InlineData(SupervisorLifecycleStatus.UnknownChild, "unknown-child")]
    [InlineData(SupervisorLifecycleStatus.Rejected, "rejected")]
    public void ChildRestartResult_HasThePinnedLineAndRoundTrips(SupervisorLifecycleStatus status, string text)
    {
        var result = new SupervisorChildRestartResult("worker:EK", status, "Admin");

        string line = SupervisorProtocol.FormatChildRestartResult(result);

        Assert.Equal($"lifecycle child-result name=worker:EK status={text} by=Admin", line);
        Assert.True(SupervisorProtocol.TryParseChildRestartResult(line, out SupervisorChildRestartResult parsed));
        Assert.Equal(result, parsed);
    }

    [Fact]
    public void ChildRestartLines_AreNotMistakenForTheRealmLines()
    {
        string request = SupervisorProtocol.FormatChildRestartRequest(new SupervisorChildRestartRequest("T", "a", false, "x"));
        string result = SupervisorProtocol.FormatChildRestartResult(new SupervisorChildRestartResult("a", SupervisorLifecycleStatus.Forwarded, "x"));

        Assert.False(SupervisorProtocol.TryParseLifecycleRequest(request, out _));
        Assert.False(SupervisorProtocol.TryParseLifecycleResult(result, out _));
        Assert.False(SupervisorProtocol.TryParseChildRestartRequest(result, out _));
        Assert.False(SupervisorProtocol.TryParseChildRestartResult(request, out _));
    }

    [Theory]
    [InlineData("--a --b", true, "--a --b --Cluster:Worker:Standby=true")]
    [InlineData("", true, "--Cluster:Worker:Standby=true")]
    [InlineData("--a --Cluster:Worker:Standby=true --b", true, "--a --Cluster:Worker:Standby=true --b")]
    [InlineData("--a --Cluster:Worker:Standby=true", false, "--a")]
    [InlineData("--Cluster:Worker:Standby=true", false, "")]
    [InlineData("--a --b", false, "--a --b")]
    public void StandbyArgument_IsAppendedAndRemovedAtTheEnd(string arguments, bool standby, string expected)
    {
        Assert.Equal(expected, ClusterHostSupervisor.WithStandbyArgument(arguments, standby));
    }

    // ---- the supervisor ----

    [Fact]
    public async Task RestartByName_AsStandby_DrainsTheChildAndRelaunchesItWithTheStandbyArgument()
    {
        await using Rig rig = await Rig.StartedAsync();
        ScriptedProcess gateway = rig.Launcher.Processes.Single(p => p.Name == "gateway");
        ScriptedProcess first = rig.Launcher.Processes.Single(p => p.Name == Worker);

        gateway.Emit(SupervisorProtocol.FormatChildRestartRequest(new SupervisorChildRestartRequest(gateway.Token!, Worker, Standby: true, "Admin")));

        await TestWait.UntilAsync(() => rig.Launcher.Processes.Count(p => p.Name == Worker) == 2, "the worker to be relaunched");
        Assert.Equal(1, first.Drains);
        Assert.Equal(["--w", "--w --Cluster:Worker:Standby=true"], rig.ArgumentsOf(Worker));
        Assert.Equal(["--g"], rig.ArgumentsOf("gateway")); // nothing else was touched
        Assert.Equal(0, gateway.Drains);

        await TestWait.UntilAsync(() => !gateway.Sent.IsEmpty, "the issuer to be answered");
        Assert.True(SupervisorProtocol.TryParseChildRestartResult(Assert.Single(gateway.Sent), out SupervisorChildRestartResult result));
        Assert.Equal(new SupervisorChildRestartResult(Worker, SupervisorLifecycleStatus.Forwarded, "Admin"), result);

        SupervisedProcessSnapshot snapshot = rig.Supervisor.GetSnapshot(Worker);
        Assert.Equal(SupervisedProcessState.Running, snapshot.State);
        Assert.True(snapshot.Standby);
        Assert.DoesNotContain("lifecycle restart-child", rig.ConsoleText, StringComparison.Ordinal); // protocol lines never reach the console
    }

    [Fact]
    public async Task RestartByName_NotAsStandby_RelaunchesAnActiveWorkerAndDropsTheStandbyArgument()
    {
        await using Rig rig = await Rig.StartedAsync();

        await rig.Supervisor.RestartProcessAsync(Worker, standby: true);
        Assert.True(rig.Supervisor.GetSnapshot(Worker).Standby);

        await rig.Supervisor.RestartProcessAsync(Worker, standby: false);

        Assert.Equal(["--w", "--w --Cluster:Worker:Standby=true", "--w"], rig.ArgumentsOf(Worker));
        Assert.False(rig.Supervisor.GetSnapshot(Worker).Standby);
        Assert.Equal(SupervisedProcessState.Running, rig.Supervisor.GetSnapshot(Worker).State);
    }

    [Fact]
    public async Task RestartByName_OfAChildThatIsDown_StartsIt()
    {
        await using Rig rig = await Rig.StartedAsync();
        await rig.Supervisor.DrainProcessAsync(Worker);
        Assert.Equal(SupervisedProcessState.Drained, rig.Supervisor.GetSnapshot(Worker).State);

        await rig.Supervisor.RestartProcessAsync(Worker, standby: true);

        Assert.Equal(["--w", "--w --Cluster:Worker:Standby=true"], rig.ArgumentsOf(Worker));
        Assert.Equal(SupervisedProcessState.Running, rig.Supervisor.GetSnapshot(Worker).State);
    }

    [Fact]
    public async Task RestartByName_UnknownName_IsReportedAndNothingIsLaunchedOrDrained()
    {
        await using Rig rig = await Rig.StartedAsync();
        ScriptedProcess gateway = rig.Launcher.Processes.Single(p => p.Name == "gateway");

        gateway.Emit(SupervisorProtocol.FormatChildRestartRequest(new SupervisorChildRestartRequest(gateway.Token!, "worker:Nowhere", Standby: true, "Admin")));

        await TestWait.UntilAsync(() => !gateway.Sent.IsEmpty, "the issuer to be told");
        Assert.True(SupervisorProtocol.TryParseChildRestartResult(Assert.Single(gateway.Sent), out SupervisorChildRestartResult result));
        Assert.Equal(new SupervisorChildRestartResult("worker:Nowhere", SupervisorLifecycleStatus.UnknownChild, "Admin"), result);
        await TestWait.UntilAsync(() => rig.ConsoleText.Contains("unknown child 'worker:Nowhere'", StringComparison.Ordinal), "the refusal to be logged");
        Assert.Equal(2, rig.Launcher.Launches);
        Assert.All(rig.Launcher.Processes, p => Assert.Equal(0, p.Drains));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Supervisor.RestartProcessAsync("worker:Nowhere", standby: false));
    }

    [Fact]
    public async Task RestartByName_WithAWrongToken_IsRejectedAndTheChildIsLeftAlone()
    {
        await using Rig rig = await Rig.StartedAsync();
        ScriptedProcess gateway = rig.Launcher.Processes.Single(p => p.Name == "gateway");
        ScriptedProcess worker = rig.Launcher.Processes.Single(p => p.Name == Worker);

        gateway.Emit(SupervisorProtocol.FormatChildRestartRequest(new SupervisorChildRestartRequest("not-the-token", Worker, Standby: true, "Admin")));

        await TestWait.UntilAsync(() => !gateway.Sent.IsEmpty, "the issuer to be told");
        Assert.True(SupervisorProtocol.TryParseChildRestartResult(Assert.Single(gateway.Sent), out SupervisorChildRestartResult result));
        Assert.Equal(SupervisorLifecycleStatus.Rejected, result.Status);
        await TestWait.UntilAsync(() => rig.ConsoleText.Contains("its token is not this supervisor's", StringComparison.Ordinal), "the refusal to be logged");
        Assert.Equal(0, worker.Drains);
        Assert.Equal(2, rig.Launcher.Launches);
        Assert.False(rig.Supervisor.GetSnapshot(Worker).Standby);
    }

    [Fact]
    public async Task RestartByName_AsStandby_IsRefusedForAChildThatIsNotAWorker()
    {
        await using Rig rig = await Rig.StartedAsync();
        ScriptedProcess gateway = rig.Launcher.Processes.Single(p => p.Name == "gateway");

        gateway.Emit(SupervisorProtocol.FormatChildRestartRequest(new SupervisorChildRestartRequest(gateway.Token!, "gateway", Standby: true, "Admin")));

        await TestWait.UntilAsync(() => !gateway.Sent.IsEmpty, "the issuer to be told");
        Assert.True(SupervisorProtocol.TryParseChildRestartResult(Assert.Single(gateway.Sent), out SupervisorChildRestartResult result));
        Assert.Equal(SupervisorLifecycleStatus.Rejected, result.Status);
        Assert.Equal(0, gateway.Drains);
        Assert.Equal(2, rig.Launcher.Launches);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Supervisor.RestartProcessAsync("gateway", standby: true));
    }

    [Fact]
    public async Task RestartByName_WhenTheOldProcessSurvivesItsKill_DoesNotRelaunch()
    {
        await using Rig rig = await Rig.StartedAsync();
        ScriptedProcess worker = rig.Launcher.Processes.Single(p => p.Name == Worker);
        worker.Drain = DrainBehaviour.IgnoresDrain;
        worker.RefuseKill = true;

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Supervisor.RestartProcessAsync(Worker, standby: true));

        Assert.Contains("still running", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, rig.Launcher.Launches); // two children of the pair of a still-running worker would both hold its scope
        Assert.False(rig.Supervisor.GetSnapshot(Worker).Standby);
    }

    // ---- the persisted swap ----

    [Fact]
    public async Task TheSwappedRole_IsWrittenToTheStateFile()
    {
        await using Rig rig = await Rig.StartedAsync(StatePath);

        await rig.Supervisor.RestartProcessAsync(Worker, standby: true);

        SupervisedChildRecord record = Assert.Single(new SupervisorChildStateFile(StatePath).Load(), r => r.Name == Worker);
        Assert.True(record.Standby);
    }

    [Fact]
    public async Task ASupervisorRestart_KeepsTheSwappedRole_AndLaunchesTheChildAsAStandby()
    {
        new SupervisorChildStateFile(StatePath).Save([new SupervisedChildRecord(Worker, SupervisedRoles.Worker, 0, 0, Standby: true)]);
        await using Rig rig = new(StatePath);

        SupervisorReconciliationReport report = rig.Supervisor.ReconcileOrphans();
        await rig.Supervisor.StartProcessAsync(Worker);
        await rig.Supervisor.StartProcessAsync("gateway");

        Assert.Empty(report.Adopted);
        Assert.Empty(report.Terminated);
        Assert.Empty(report.Failed);
        Assert.Empty(rig.Inspector.Kills); // a record with no process behind it is never looked up or killed
        Assert.Equal(["--w --Cluster:Worker:Standby=true"], rig.ArgumentsOf(Worker));
        Assert.Equal(["--g"], rig.ArgumentsOf("gateway"));
        Assert.True(rig.Supervisor.GetSnapshot(Worker).Standby);
        Assert.False(rig.Supervisor.GetSnapshot("gateway").Standby);
    }

    [Fact]
    public async Task ARoleThatWasNeverSwapped_IsNotWrittenAndAnOldFileStillLoads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """[{"Name":"gateway","Role":"gateway","ProcessId":4242,"StartTimeUtcMs":1}]""");

        SupervisedChildRecord old = Assert.Single(new SupervisorChildStateFile(StatePath).Load());
        Assert.False(old.Standby); // a file written before the role was recorded

        await using Rig rig = await Rig.StartedAsync(StatePath);
        Assert.All(new SupervisorChildStateFile(StatePath).Load(), r => Assert.False(r.Standby));
    }

    [Fact]
    public void AStandbyRecordWithNoProcess_IsLoadedButAPlainOneWithoutOneIsStillDropped()
    {
        new SupervisorChildStateFile(StatePath).Save(
        [
            new SupervisedChildRecord("a", SupervisedRoles.Worker, 0, 0, Standby: true),
            new SupervisedChildRecord("b", SupervisedRoles.Worker, 0, 0),
            new SupervisedChildRecord("c", SupervisedRoles.Worker, 77, 5),
        ]);

        IReadOnlyList<SupervisedChildRecord> loaded = new SupervisorChildStateFile(StatePath).Load();

        Assert.Equal(["a", "c"], loaded.Select(r => r.Name));
    }

    // ---- the rig ----

    /// <summary>Records the arguments each launch was given, then hands the launch to the scripted launcher.</summary>
    private sealed class ArgumentRecordingLauncher(ScriptedLauncher inner) : ISupervisedProcessLauncher
    {
        public ConcurrentQueue<(string Name, string Arguments)> Launched { get; } = new();

        public ISupervisedProcess Launch(SupervisedProcessConfig config)
        {
            Launched.Enqueue((config.Name, config.Arguments));
            return inner.Launch(config);
        }

        public ISupervisedProcess Launch(SupervisedProcessConfig config, Action<ISupervisedProcess, string, bool> onOutputLine)
        {
            Launched.Enqueue((config.Name, config.Arguments));
            return inner.Launch(config, onOutputLine);
        }
    }

    private sealed class NothingRunningInspector : ISupervisorProcessInspector
    {
        public ConcurrentQueue<int> KillQueue { get; } = new();

        public int[] Kills => [.. KillQueue];

        public bool TryGetStartTimeUtc(int processId, out DateTimeOffset startTimeUtc)
        {
            startTimeUtc = default;
            return false;
        }

        public bool TryKill(int processId)
        {
            KillQueue.Enqueue(processId);
            return true;
        }
    }

    /// <summary>A gateway and a worker under one supervisor, with scripted children, a manual clock and manual delays.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ArgumentRecordingLauncher _recording;
        private readonly StringWriter _console = new();

        public Rig(string? statePath = null)
        {
            Launcher = new ScriptedLauncher();
            _recording = new ArgumentRecordingLauncher(Launcher);
            var options = new ClusterSupervisorOptions { MinClusterProcesses = 1 };
            options.Processes["gateway"] = new SupervisedProcessConfig { Name = "gateway", Role = SupervisedRoles.Gateway, ExecutablePath = "x", Arguments = "--g", RequiredMemoryMb = 1, StartOrder = 0 };
            options.Processes[Worker] = new SupervisedProcessConfig { Name = Worker, Role = SupervisedRoles.Worker, ExecutablePath = "x", Arguments = "--w", RequiredMemoryMb = 1, StartOrder = 1 };
            Delays.AutoComplete = ManualDelays.IsBackoff;
            Supervisor = new ClusterHostSupervisor(
                options, _recording, new SupervisedOutputMultiplexer(_console), new FixedMemoryProbe(), Delays.Delay, Clock,
                childStateFile: statePath is null ? null : new SupervisorChildStateFile(statePath), processInspector: Inspector);
        }

        public static async Task<Rig> StartedAsync(string? statePath = null)
        {
            var rig = new Rig(statePath);
            await rig.Supervisor.StartProcessAsync("gateway");
            await rig.Supervisor.StartProcessAsync(Worker);
            return rig;
        }

        public ScriptedLauncher Launcher { get; }

        public NothingRunningInspector Inspector { get; } = new();

        public ManualClock Clock { get; } = new();

        public ManualDelays Delays { get; } = new();

        public ClusterHostSupervisor Supervisor { get; }

        public string ConsoleText
        {
            get
            {
                lock (_console)
                {
                    return _console.ToString();
                }
            }
        }

        public string[] ArgumentsOf(string name) => [.. _recording.Launched.Where(l => l.Name == name).Select(l => l.Arguments)];

        public async ValueTask DisposeAsync() => await Supervisor.DisposeAsync();
    }
}

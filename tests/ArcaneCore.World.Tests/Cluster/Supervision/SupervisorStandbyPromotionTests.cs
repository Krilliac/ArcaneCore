using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// What a blue/green swap leaves behind in <see cref="ClusterHostSupervisor"/> (design _wf/handoff-control-DESIGN.md, Q3): the promoted standby is the same process that was
/// started as the standby, so its hang watch must arm on its first world-tick beat, and the supervisor <c>RealmId</c> of the lease-holder slot must follow the active world.
/// Scripted children, a manual clock and manual delays; no OS process is started.
/// </summary>
public sealed class SupervisorStandbyPromotionTests
{
    private const string Active = "worker:EK";
    private const string StandbyName = "standby:EK";

    private static ClusterSupervisorOptions SlotOptions()
    {
        var options = new ClusterSupervisorOptions { MinClusterProcesses = 1 };
        options.Processes["gateway"] = new SupervisedProcessConfig { Name = "gateway", Role = SupervisedRoles.Gateway, ExecutablePath = "x", Arguments = "--g", RequiredMemoryMb = 1, StartOrder = 0 };
        options.Processes[Active] = new SupervisedProcessConfig
        {
            Name = Active, Role = SupervisedRoles.Worker, ExecutablePath = "x", Arguments = "--w --Cluster:Fencing:Shard=worker:EK", RequiredMemoryMb = 1, StartOrder = 1, RealmId = 1,
        };
        options.Processes[StandbyName] = new SupervisedProcessConfig
        {
            Name = StandbyName, Role = SupervisedRoles.Worker, ExecutablePath = "x", Arguments = "--s --Cluster:Fencing:Shard=worker:EK --Cluster:Worker:Standby=true",
            RequiredMemoryMb = 1, StartOrder = 1, // the launcher gives a standby the same HangTimeoutSeconds as a world (default 120 here)
        };
        return options;
    }

    [Fact]
    public async Task AStandby_IsNotKilledWhileItNeverBeats_AndTheHangWatchArmsWhenThePromotedProcessBeatsAndThenGoesSilent()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SlotOptions(), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync(StandbyName);
        ScriptedProcess standby = launcher.Last;

        // Standing by: no world tick, no beat. Silence alone never kills a child that has not beaten (it only gets a one-time warning).
        rig.Clock.Advance(TimeSpan.FromHours(1));
        await rig.Delays.PulseAsync();
        await rig.Delays.PulseAsync();
        Assert.Equal(0, standby.Drains);
        Assert.Equal(0, standby.Kills);
        Assert.Equal(SupervisedProcessState.Running, rig.Supervisor.GetSnapshot(StandbyName).State);

        // The handoff promotes this very process: its world ticks, so it beats. That arms the watch, and a promoted world that then stops ticking is
        // a hang like any other world's: drained and restarted.
        standby.Beat();
        rig.Clock.Advance(TimeSpan.FromSeconds(121));
        await rig.Delays.ReleaseUntilAsync(() => standby.Drains == 1, "the hang watch to act on the silent promoted standby");
    }

    private static async Task<SupervisorRig> StartedSlotAsync(ClusterSupervisorOptions options)
    {
        var rig = new SupervisorRig(options);
        await rig.Supervisor.StartProcessAsync("gateway");
        await rig.Supervisor.StartProcessAsync(Active);
        await rig.Supervisor.StartProcessAsync(StandbyName);
        return rig;
    }

    private static string RestartRequest(ScriptedProcess issuer, int realmId)
        => SupervisorProtocol.FormatLifecycleRequest(new SupervisorLifecycleRequest(issuer.Token!, realmId, SupervisorLifecycleAction.Restart, 5, ExitCodes.Restart, "Admin"));

    [Fact]
    public async Task WhenTheRealmHoldingWorldBecomesAStandby_TheRealmIdMovesToTheSlotsOtherWorker_AndLifecycleRequestsFollowIt()
    {
        await using SupervisorRig rig = await StartedSlotAsync(SlotOptions());
        ScriptedProcess gateway = rig.Launcher.Processes.Single(p => p.Name == "gateway");
        ScriptedProcess promoted = rig.Launcher.Processes.Single(p => p.Name == StandbyName);
        ScriptedProcess firstActive = rig.Launcher.Processes.Single(p => p.Name == Active);

        // Before the swap the realm is the active world's.
        gateway.Emit(RestartRequest(gateway, 1));
        await TestWait.UntilAsync(() => !firstActive.Sent.IsEmpty, "the request to reach the active world");
        Assert.Empty(promoted.Sent);

        // The gateway retires the active world as the slot's next standby (a handoff has promoted the standby process in place).
        await rig.Supervisor.RestartProcessAsync(Active, standby: true);
        ScriptedProcess relaunched = rig.Launcher.Processes.Last(p => p.Name == Active);
        Assert.NotSame(firstActive, relaunched);

        Assert.Equal(1, rig.Options.Processes[StandbyName].RealmId);
        Assert.Equal(0, rig.Options.Processes[Active].RealmId);

        gateway.Emit(RestartRequest(gateway, 1));
        await TestWait.UntilAsync(() => promoted.Sent.Any(line => line.StartsWith("lifecycle restart", StringComparison.Ordinal)), "the realm request to reach the promoted world");
        Assert.Empty(relaunched.Sent); // the world that is now a standby is no longer the realm's

        // The next swap hands the role back.
        await rig.Supervisor.RestartProcessAsync(StandbyName, standby: true);
        Assert.Equal(1, rig.Options.Processes[Active].RealmId);
        Assert.Equal(0, rig.Options.Processes[StandbyName].RealmId);
    }

    [Fact]
    public async Task ARealmIdStaysWhereItIs_WhenNoOtherWorkerNamesTheSameShard()
    {
        ClusterSupervisorOptions options = SlotOptions();
        options.Processes[StandbyName].Arguments = "--s --Cluster:Fencing:Shard=worker:Kalimdor --Cluster:Worker:Standby=true";
        await using SupervisorRig rig = await StartedSlotAsync(options);

        await rig.Supervisor.RestartProcessAsync(Active, standby: true);

        Assert.Equal(1, rig.Options.Processes[Active].RealmId);
        Assert.Equal(0, rig.Options.Processes[StandbyName].RealmId);
    }

    [Fact]
    public async Task RestartingAWorldAsActive_NeverMovesTheRealmId()
    {
        await using SupervisorRig rig = await StartedSlotAsync(SlotOptions());

        await rig.Supervisor.RestartProcessAsync(Active, standby: false);

        Assert.Equal(1, rig.Options.Processes[Active].RealmId);
        Assert.Equal(0, rig.Options.Processes[StandbyName].RealmId);
    }

    private sealed class NothingRunningInspector : ISupervisorProcessInspector
    {
        public bool TryGetStartTimeUtc(int processId, out DateTimeOffset startTimeUtc)
        {
            startTimeUtc = default;
            return false;
        }

        public bool TryKill(int processId) => throw new InvalidOperationException("no process may be killed by this test");
    }

    private static ClusterHostSupervisor NewPersistingSupervisor(ClusterSupervisorOptions options, ScriptedLauncher launcher, string statePath)
    {
        var delays = new ManualDelays { AutoComplete = ManualDelays.IsBackoff };
        return new ClusterHostSupervisor(
            options, launcher, new SupervisedOutputMultiplexer(new StringWriter()), new FixedMemoryProbe(), delays.Delay, new ManualClock(),
            childStateFile: new SupervisorChildStateFile(statePath), processInspector: new NothingRunningInspector());
    }

    // Regression: the promoted world kept its standby flag, so two swaps of one slot persisted both children as standbys and a supervisor restart
    // brought the slot back with no active world.
    [Fact]
    public async Task TwoSwapsOfOneSlot_LeaveExactlyOneStandby_InMemoryAndInTheStateFile_AndASupervisorRestartKeepsOneActiveWorld()
    {
        string dir = Path.Combine(Path.GetTempPath(), "supervisor-standby-promotion-" + Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(dir, "children.json");
        try
        {
            ClusterSupervisorOptions options = SlotOptions();
            var launcher = new ScriptedLauncher();
            await using (ClusterHostSupervisor supervisor = NewPersistingSupervisor(options, launcher, statePath))
            {
                await supervisor.StartProcessAsync("gateway");
                await supervisor.StartProcessAsync(Active);
                await supervisor.StartProcessAsync(StandbyName);

                await supervisor.RestartProcessAsync(Active, standby: true); // swap 1: the standby process is promoted
                Assert.True(supervisor.GetSnapshot(Active).Standby);
                Assert.False(supervisor.GetSnapshot(StandbyName).Standby);
                Assert.Equal([Active], StandbyNamesInFile(statePath));
                Assert.DoesNotContain(ClusterHostSupervisor.StandbyArgument, options.Processes[StandbyName].Arguments, StringComparison.Ordinal);

                await supervisor.RestartProcessAsync(StandbyName, standby: true); // swap 2: the first world is promoted back
                Assert.False(supervisor.GetSnapshot(Active).Standby);
                Assert.True(supervisor.GetSnapshot(StandbyName).Standby);
                Assert.Equal([StandbyName], StandbyNamesInFile(statePath));
                Assert.Equal(1, options.Processes[Active].RealmId);
            }

            // A restarted supervisor reads the file: the slot comes back with the realm-holding world active and the other one standing by.
            ClusterSupervisorOptions fresh = SlotOptions();
            await using ClusterHostSupervisor restarted = NewPersistingSupervisor(fresh, new ScriptedLauncher(), statePath);
            restarted.ReconcileOrphans();
            Assert.False(restarted.GetSnapshot(Active).Standby);
            Assert.True(restarted.GetSnapshot(StandbyName).Standby);
            Assert.Equal(1, fresh.Processes[Active].RealmId);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // Regression: start-cluster.ps1 gives only the lease-holder world a realm id; every other slot has RealmId 0 on both children, yet the gateway swaps those too.
    // The promoted child's standby flag must be cleared by shard, not by a realm id move.
    [Fact]
    public async Task TwoSwapsOfASlotWithNoRealmId_LeaveExactlyOneStandby_AndASupervisorRestartKeepsOneActiveWorld()
    {
        string dir = Path.Combine(Path.GetTempPath(), "supervisor-standby-norealm-" + Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(dir, "children.json");
        try
        {
            ClusterSupervisorOptions NoRealmOptions()
            {
                ClusterSupervisorOptions o = SlotOptions();
                o.Processes[Active].RealmId = 0;
                return o;
            }

            ClusterSupervisorOptions options = NoRealmOptions();
            var launcher = new ScriptedLauncher();
            await using (ClusterHostSupervisor supervisor = NewPersistingSupervisor(options, launcher, statePath))
            {
                await supervisor.StartProcessAsync("gateway");
                await supervisor.StartProcessAsync(Active);
                await supervisor.StartProcessAsync(StandbyName);

                await supervisor.RestartProcessAsync(Active, standby: true); // swap 1: the configured standby process is promoted
                Assert.True(supervisor.GetSnapshot(Active).Standby);
                Assert.False(supervisor.GetSnapshot(StandbyName).Standby);
                Assert.Equal([Active], StandbyNamesInFile(statePath));
                Assert.DoesNotContain(ClusterHostSupervisor.StandbyArgument, options.Processes[StandbyName].Arguments, StringComparison.Ordinal); // its next launch is a world
                Assert.Contains(ClusterHostSupervisor.StandbyArgument, options.Processes[Active].Arguments, StringComparison.Ordinal);

                await supervisor.RestartProcessAsync(StandbyName, standby: true); // swap 2: the first world is promoted back
                Assert.False(supervisor.GetSnapshot(Active).Standby);
                Assert.True(supervisor.GetSnapshot(StandbyName).Standby);
                Assert.Equal([StandbyName], StandbyNamesInFile(statePath));
                Assert.DoesNotContain(ClusterHostSupervisor.StandbyArgument, options.Processes[Active].Arguments, StringComparison.Ordinal);
                Assert.Equal(0, options.Processes[Active].RealmId);
                Assert.Equal(0, options.Processes[StandbyName].RealmId);
            }

            ClusterSupervisorOptions fresh = NoRealmOptions();
            await using ClusterHostSupervisor restarted = NewPersistingSupervisor(fresh, new ScriptedLauncher(), statePath);
            restarted.ReconcileOrphans();
            Assert.False(restarted.GetSnapshot(Active).Standby);
            Assert.True(restarted.GetSnapshot(StandbyName).Standby);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string[] StandbyNamesInFile(string statePath)
        => [.. new SupervisorChildStateFile(statePath).Load().Where(r => r.Standby).Select(r => r.Name)];

    // The requester resolves the realm id from the worker's own, fixed copy of the configuration (ClusterHostingRestart passes IOptions.Value): after a swap it still
    // names the realm id the typed child was configured with, and the supervisor's current table sends the request to the world that holds the realm now.
    [Fact]
    public async Task ARestartRequestedForTheConfiguredChildAfterASwap_ReachesTheWorldThatHoldsTheRealmNow()
    {
        await using SupervisorRig rig = await StartedSlotAsync(SlotOptions());
        ScriptedProcess gateway = rig.Launcher.Processes.Single(p => p.Name == "gateway");
        ScriptedProcess promoted = rig.Launcher.Processes.Single(p => p.Name == StandbyName);
        await rig.Supervisor.RestartProcessAsync(Active, standby: true);
        ScriptedProcess relaunched = rig.Launcher.Processes.Last(p => p.Name == Active);

        int realm = SupervisorWorkerRestartRequester.RealmIdOf(SlotOptions(), new WorkerNodeId(Active));
        gateway.Emit(RestartRequest(gateway, realm));

        Assert.Equal(1, realm);
        await TestWait.UntilAsync(() => promoted.Sent.Any(line => line.StartsWith("lifecycle restart", StringComparison.Ordinal)), "the request to reach the world that holds the realm now");
        Assert.Empty(relaunched.Sent);
    }
}

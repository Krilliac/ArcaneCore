using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// <see cref="ClusterHostSupervisor.StartInGroupsAsync"/> with <see cref="ClusterSupervisorOptions.StartGroupReadySeconds"/>: a start-order group is not launched before the previous group sent
/// its first alive heartbeat (the split topology's lease holder finishes its schema DDL before a validate-only world checks the schema), bounded by a timeout and by the child exiting.
/// </summary>
public sealed class SupervisorStartOrderReadinessTests
{
    private const int ReadySeconds = 777; // not a backoff-sized wait, and distinct from every other delay the supervisor requests
    private static readonly TimeSpan ReadyDelay = TimeSpan.FromSeconds(ReadySeconds);

    private static ClusterSupervisorOptions TwoGroups(int readySeconds)
    {
        var options = new ClusterSupervisorOptions { MinClusterProcesses = 1, StartGroupReadySeconds = readySeconds };
        options.Processes["holder"] = new SupervisedProcessConfig { Name = "holder", Role = SupervisedRoles.Combined, ExecutablePath = "x", RequiredMemoryMb = 1, StartOrder = 1 };
        options.Processes["validator"] = new SupervisedProcessConfig { Name = "validator", Role = SupervisedRoles.Combined, ExecutablePath = "x", RequiredMemoryMb = 1, StartOrder = 2 };
        return options;
    }

    private static readonly string[] Names = ["holder", "validator"];

    [Fact]
    public async Task TheNextGroup_WaitsForThePreviousGroupsFirstHeartbeat()
    {
        await using var rig = new SupervisorRig(TwoGroups(ReadySeconds), holdAllDelays: true);

        Task start = rig.Supervisor.StartInGroupsAsync(Names);
        await rig.Launcher.WaitForLaunchesAsync(1);
        await rig.Delays.WaitForPendingAsync(d => d == ReadyDelay, "the readiness wait of the first group");
        Assert.False(start.IsCompleted);
        Assert.Equal(1, rig.Launcher.Launches);

        rig.Launcher.Processes[0].Beat();
        await start.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, rig.Launcher.Launches);
    }

    [Fact]
    public async Task ASilentGroup_IsWaitedForOnlyUntilTheTimeout_ThenTheStartGoesOnWithAWarning()
    {
        await using var rig = new SupervisorRig(TwoGroups(ReadySeconds), holdAllDelays: true);

        Task start = rig.Supervisor.StartInGroupsAsync(Names);
        await rig.Delays.WaitForPendingAsync(d => d == ReadyDelay, "the readiness wait of the first group");
        rig.Delays.Release(d => d == ReadyDelay);
        await start.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, rig.Launcher.Launches);
        Assert.Contains("StartGroupReadySeconds", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupThatExitsBeforeItsFirstHeartbeat_DoesNotHoldTheStart()
    {
        await using var rig = new SupervisorRig(TwoGroups(ReadySeconds), holdAllDelays: true);

        Task start = rig.Supervisor.StartInGroupsAsync(Names);
        await rig.Launcher.WaitForLaunchesAsync(1);
        await rig.Delays.WaitForPendingAsync(d => d == ReadyDelay, "the readiness wait of the first group");
        rig.Launcher.Processes[0].ExitWith(1);
        await start.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, rig.Launcher.Launches);
    }

    [Fact]
    public async Task WithoutTheOption_NoGroupWaits()
    {
        await using var rig = new SupervisorRig(TwoGroups(0), holdAllDelays: true);

        await rig.Supervisor.StartInGroupsAsync(Names).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, rig.Launcher.Launches);
    }

    [Fact]
    public async Task AValidateOnlyWorld_DoesNotExitIntoARestartLoop_WhenTheHolderHasNotFinishedItsSchemaWork()
    {
        // A validate-only world finds the schema behind (and exits) when it starts before the holder's first heartbeat; with the group wait it starts after it.
        var launcher = new ScriptedLauncher();
        bool holderReady = false;
        launcher.Configure = (index, process) =>
        {
            if (index == 1 && !Volatile.Read(ref holderReady))
                process.ExitWith(1); // "schema is behind the code": the validate-only world refuses to start
        };

        await using var rig = new SupervisorRig(TwoGroups(ReadySeconds), launcher, holdAllDelays: true);
        Task start = rig.Supervisor.StartInGroupsAsync(Names);
        await rig.Launcher.WaitForLaunchesAsync(1);
        await rig.Delays.WaitForPendingAsync(d => d == ReadyDelay, "the readiness wait of the first group");

        Volatile.Write(ref holderReady, true); // the holder's DDL is done
        rig.Launcher.Processes[0].Beat();
        await start.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, rig.Launcher.Launches);
        Assert.False(rig.Launcher.Processes[1].HasExited);
        Assert.Equal(SupervisedProcessState.Running, rig.Supervisor.GetSnapshot("validator").State);
        Assert.Equal(0, rig.Supervisor.GetSnapshot("validator").RestartCount);
    }

    [Fact]
    public async Task AValidateOnlyWorld_ThatStartsBeforeTheHolder_ExitsAtOnce_WithoutTheGroupWait()
    {
        // The control for the test above: the same world launched without the wait exits at once (what the group wait prevents).
        var launcher = new ScriptedLauncher { Configure = (index, process) => { if (index == 1) process.ExitWith(1); } };

        await using var rig = new SupervisorRig(TwoGroups(0), launcher, holdAllDelays: true);
        await rig.Supervisor.StartInGroupsAsync(Names).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(rig.Launcher.Processes[1].HasExited);
    }

    [Fact]
    public void StartGroupReadySeconds_IsValidated()
    {
        var options = new ClusterSupervisorOptions { StartGroupReadySeconds = 3601 };
        Assert.Contains(options.Validate(), p => p.Contains("StartGroupReadySeconds", StringComparison.Ordinal));
        options.StartGroupReadySeconds = 0;
        Assert.DoesNotContain(options.Validate(), p => p.Contains("StartGroupReadySeconds", StringComparison.Ordinal));
    }
}

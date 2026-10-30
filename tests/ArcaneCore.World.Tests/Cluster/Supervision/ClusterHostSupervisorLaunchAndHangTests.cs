using System.Reflection;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// Two follow-up fixes in <see cref="ClusterHostSupervisor"/>, against scripted children: a line a child writes while it is being launched is
/// not lost (the output handler is subscribed inside the launch), and a hang drain that finishes after a restart does not mark the new incarnation.
/// </summary>
public sealed class ClusterHostSupervisorLaunchAndHangTests
{
    private static readonly TimeSpan Seconds121 = TimeSpan.FromSeconds(121);

    private static SupervisedProcessSnapshot Snap(SupervisorRig rig) => rig.Supervisor.GetSnapshot("world");

    /// <summary>The private per-child state holds <c>HangKillIssued</c>; no public surface shows it before the exit is classified.</summary>
    private static bool HangKillIssued(ClusterHostSupervisor supervisor)
    {
        FieldInfo entries = typeof(ClusterHostSupervisor).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ClusterHostSupervisor._entries was renamed; update this test.");
        var dictionary = (System.Collections.IDictionary)entries.GetValue(supervisor)!;
        object entry = dictionary["world"]!;
        PropertyInfo property = entry.GetType().GetProperty("HangKillIssued", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ManagedEntry.HangKillIssued was renamed; update this test.");
        return (bool)property.GetValue(entry)!;
    }

    [Fact]
    public async Task LinesTheChildWritesDuringLaunch_ReachTheSupervisor_OnStdoutAndStderr()
    {
        // A real launcher starts its output pumps inside Launch; a child that writes at once must not outrun the subscription.
        var launcher = new ScriptedLauncher
        {
            DuringLaunch = p =>
            {
                p.Emit("early stdout line");
                p.Emit("early stderr line", isStdErr: true);
            },
        };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.Console = SupervisedConsoleMode.Merged), launcher);

        await rig.Supervisor.StartProcessAsync("world");

        Assert.Contains("early stdout line", rig.ConsoleText, StringComparison.Ordinal);
        Assert.Contains("early stderr line", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinesTheChildWritesAfterLaunch_StillReachTheSupervisor_ExactlyOnce()
    {
        // Guards the new overload against double subscription: a later line must arrive once, not once per subscriber.
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.Console = SupervisedConsoleMode.Merged));
        await rig.Supervisor.StartProcessAsync("world");

        rig.Launcher.Last.Emit("late line");

        string text = rig.ConsoleText;
        int first = text.IndexOf("late line", StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.Equal(-1, text.IndexOf("late line", first + 1, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HangDrainThatFinishesAfterARestart_DoesNotMarkTheNewIncarnation()
    {
        var drainGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new ScriptedLauncher
        {
            Configure = (index, p) =>
            {
                p.Drain = DrainBehaviour.IgnoresDrain;
                if (index == 0)
                {
                    p.DrainOverride = _ => drainGate.Task; // the first incarnation's hang drain stays open until the test finishes it
                }
            },
        };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess first = launcher.Last;
        first.Beat();

        rig.Clock.Advance(Seconds121);
        await rig.Delays.ReleaseUntilAsync(() => first.Drains == 1, "the hang drain to start");

        first.ExitWith(1); // the child dies while the drain is open; the monitor relaunches it
        await rig.Delays.ReleaseUntilAsync(() => launcher.Launches >= 2, "the relaunch");
        await TestWait.UntilAsync(() => Snap(rig) is { State: SupervisedProcessState.Running, Incarnation: >= 2 }, "incarnation 2 to run");
        Assert.False(HangKillIssued(rig.Supervisor));

        drainGate.SetResult(false); // the stale drain gives up now and goes on to kill the old child
        await TestWait.UntilAsync(() => first.Kills >= 1, "the stale hang recovery to reach its kill");

        Assert.False(HangKillIssued(rig.Supervisor), "a drain of the old incarnation must not mark the new one as hang-killed");
        Assert.Equal(0, launcher.Processes[1].Kills);
        Assert.Equal(2, launcher.Launches);
    }

    [Fact]
    public async Task HangDrainThatTimesOut_MarksTheSameIncarnationBeforeTheKill()
    {
        // The control for the guard above: with no restart in between, the same flag is set (the kill is refused, so the incarnation stays and can be read).
        var launcher = new ScriptedLauncher { Configure = (_, p) => { p.Drain = DrainBehaviour.IgnoresDrain; p.RefuseKill = true; } };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        launcher.Last.Beat();

        rig.Clock.Advance(Seconds121);
        await rig.Delays.ReleaseUntilAsync(() => Snap(rig).State == SupervisedProcessState.Faulted, "the unkillable hung child to be faulted");

        Assert.True(HangKillIssued(rig.Supervisor));
    }
}

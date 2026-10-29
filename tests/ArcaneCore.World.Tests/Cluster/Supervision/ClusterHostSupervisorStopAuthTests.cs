using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// The stop announcement and the hang watch cannot be forged from child output: a stopping line needs the supervisor's per-run token, a heartbeat
/// <c>stopping</c> is liveness only, and the supervisor reads lines up to <c>\n</c> only (a lone <c>\r</c> in a logged GM command cannot start a second line).
/// </summary>
public sealed class ClusterHostSupervisorStopAuthTests
{
    private static readonly TimeSpan Seconds121 = TimeSpan.FromSeconds(121);

    private static SupervisedProcessSnapshot Snap(SupervisorRig rig) => rig.Supervisor.GetSnapshot("world");

    [Theory]
    [InlineData("lifecycle stopping intent=shutdown code=0")]
    [InlineData("lifecycle stopping token=wrong intent=shutdown code=0")]
    [InlineData("lifecycle stopping token= intent=shutdown code=0")]
    public async Task StoppingLine_WithoutTheRightToken_IsIgnored(string forged)
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 30_000), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess child = launcher.Last;
        child.Beat();
        child.Emit(forged);

        rig.Clock.Advance(TimeSpan.FromSeconds(40)); // past the 30 s stop grace a real announcement would have started
        await rig.Delays.PulseAsync();
        await rig.Delays.PulseAsync();
        Assert.Equal(0, child.Kills);
        Assert.Contains("lifecycle stopping", rig.ConsoleText, StringComparison.Ordinal); // shown as the ordinary output it is

        child.ExitWith(ExitCodes.Failure);
        await rig.Delays.ReleaseUntilAsync(() => launcher.Launches >= 2, "the crashed child to be relaunched after its backoff");
        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, Snap(rig).LastDisposition); // classified by its exit code, not by the forged intent
    }

    [Fact]
    public async Task EmbeddedCarriageReturn_CannotSmuggleAStoppingLine()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 30_000), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess child = launcher.Last;
        child.Beat();
        string valid = SupervisorProtocol.FormatStopping(child.Token!, SupervisorStopIntent.Shutdown, ExitCodes.Success);

        // What a pipe reader that split at a lone CR would have turned into two lines: logged text, then a well-formed (even correctly tokened) protocol line.
        child.Emit("GM command: say hi\r" + valid);
        child.Emit(valid + "\r");

        rig.Clock.Advance(TimeSpan.FromSeconds(40));
        await rig.Delays.PulseAsync();
        await rig.Delays.PulseAsync();
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task LineReader_SplitsOnLineFeedOnly()
    {
        var lines = new List<string>();

        await SupervisorLineReader.ReadLinesAsync(new StringReader("a\rb\nc\r\nd\n\ne"), lines.Add);

        Assert.Equal(["a\rb", "c", "d", "", "e"], lines);
    }

    [Fact]
    public async Task StoppingLine_WithTheToken_IsConsumed_NotShown()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 30_000), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess child = launcher.Last;

        child.Emit(SupervisorProtocol.FormatStopping(child.Token!, SupervisorStopIntent.Shutdown, ExitCodes.Success));

        Assert.DoesNotContain("lifecycle stopping", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeartbeatStopping_DoesNotArmStopGrace()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 30_000), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess child = launcher.Last;
        child.Beat();
        child.Beat("stopping");

        rig.Clock.Advance(TimeSpan.FromSeconds(40));
        await rig.Delays.PulseAsync();
        await rig.Delays.PulseAsync();
        Assert.Equal(0, child.Kills);
        Assert.Equal(0, child.Drains);

        rig.Clock.Advance(TimeSpan.FromSeconds(81)); // 121 s since the stopping beat: it is only a beat, so the hang watch still acts
        await rig.Delays.ReleaseUntilAsync(() => child.Drains == 1, "the hang watch to act on a silent child whose last beat said stopping");
    }

    [Fact]
    public async Task PlanStartAll_CountsAHungChildAsActive()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        var capture = new RecordingHangCapture(launcher.Journal) { Hangs = true };
        ClusterSupervisorOptions options = SupervisorRig.OneProcess(configure: c => c.HangCapture = SupervisedHangCaptureMode.Dump);
        await using var rig = new SupervisorRig(options, launcher, holdAllDelays: true, capture: capture);
        await rig.Supervisor.StartProcessAsync("world");
        options.MinFreeMemoryReserveMb = 1_000_000_000; // far above the probe's free memory: only a child that is already active is admitted
        launcher.Last.Beat();

        rig.Clock.Advance(Seconds121);
        // Release only the hang watch's own wait (the capture's 300 s bound stays held, so the child stays Hung); repeat in case the watcher had not registered it yet.
        await TestWait.UntilAsync(() =>
        {
            rig.Delays.Release(d => d <= TimeSpan.FromSeconds(200));
            return Snap(rig).State == SupervisedProcessState.Hung;
        }, "the child to be hung (capturing)");

        SupervisorStartPlan plan = rig.Supervisor.PlanStartAll();

        Assert.Equal(["world"], plan.Admitted);
        Assert.Empty(plan.Denied);
    }
}

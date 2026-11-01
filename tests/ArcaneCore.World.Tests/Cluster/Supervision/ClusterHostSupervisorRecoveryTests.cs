using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Tests.Ops;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// Crash and hang recovery of the cluster host supervisor, against scripted children, a manual clock and manual delays (no real sleeping):
/// what an exit code means, the crash-loop window, the heartbeat hang watch with its capture, drain and kill, and the cross-world lifecycle routing.
/// </summary>
public sealed class ClusterHostSupervisorRecoveryTests
{
    private static readonly TimeSpan Seconds121 = TimeSpan.FromSeconds(121);

    private static SupervisedProcessSnapshot Snap(SupervisorRig rig, string name = "world") => rig.Supervisor.GetSnapshot(name);

    // ---- exit policy ----

    [Fact]
    public async Task ChildExit0_StaysDown_NoDelayNoRelaunch()
    {
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess());
        await rig.Supervisor.StartProcessAsync("world");

        rig.Launcher.Last.ExitWith(ExitCodes.Success);
        await TestWait.UntilAsync(() => Snap(rig).State == SupervisedProcessState.Stopped, "the child to be left down");

        SupervisedProcessSnapshot snapshot = Snap(rig);
        Assert.Equal(0, snapshot.LastExitCode);
        Assert.Equal(SupervisedExitDisposition.StayDown, snapshot.LastDisposition);
        Assert.Equal(0, snapshot.RestartCount);
        Assert.Equal(0, snapshot.LastBackoffDelayMs);
        Assert.Equal(1, snapshot.Incarnation);
        Assert.Equal(1, rig.Launcher.Launches);
        Assert.Empty(rig.Delays.Backoffs);
    }

    [Fact]
    public async Task ChildExit2_RelaunchesWithoutBackoff_ResetsFailures()
    {
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess());
        await rig.Supervisor.StartProcessAsync("world");

        rig.Launcher.Last.ExitWith(1); // a crash first: one failure, 1 s backoff
        await rig.Launcher.WaitForLaunchesAsync(2);
        Assert.Equal(1, Snap(rig).ConsecutiveFailures);
        Assert.Equal(1000, Snap(rig).LastBackoffDelayMs);

        rig.Launcher.Last.ExitWith(ExitCodes.Restart); // then a restart request: straight back up, failures forgotten
        await rig.Launcher.WaitForLaunchesAsync(3);

        SupervisedProcessSnapshot snapshot = await WaitRunningAsync(rig, incarnation: 3);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(0, snapshot.LastBackoffDelayMs);
        Assert.Equal(SupervisedExitDisposition.RelaunchNow, snapshot.LastDisposition);
        Assert.Equal(2, snapshot.RestartCount);
        Assert.Equal([TimeSpan.FromSeconds(1)], rig.Delays.Backoffs); // only the crash waited
    }

    [Fact]
    public async Task ChildExit78_AndExit64_AreFaulted_NotRestarted()
    {
        foreach (int code in new[] { ExitCodes.InvalidConfiguration, ExitCodes.Usage })
        {
            await using var rig = new SupervisorRig(SupervisorRig.OneProcess());
            await rig.Supervisor.StartProcessAsync("world");

            rig.Launcher.Last.ExitWith(code);
            await TestWait.UntilAsync(() => Snap(rig).State == SupervisedProcessState.Faulted, $"exit {code} to fault the child");

            Assert.Equal(SupervisedExitDisposition.Fault, Snap(rig).LastDisposition);
            Assert.Equal(1, rig.Launcher.Launches);
        }
    }

    [Fact]
    public async Task CrashLoop_GivesUpAfterMaxInWindow_FaultsAndRecords()
    {
        long before = AnomalyLedger.Shared.CountOf(AnomalyLedger.KeyOf("cluster", "supervisor-crash-loop"));
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(options: o => o.CrashLoopMaxRestarts = 3));
        await rig.Supervisor.StartProcessAsync("world");

        for (int i = 0; i < 3; i++)
        {
            rig.Launcher.Processes[i].ExitWith(1); // three restarts are allowed inside the window
            await rig.Launcher.WaitForLaunchesAsync(i + 2);
        }

        rig.Launcher.Processes[3].ExitWith(1); // the fourth failure in the window is one too many
        await TestWait.UntilAsync(() => Snap(rig).State == SupervisedProcessState.Faulted, "the crash loop to be given up");

        Assert.Equal(4, rig.Launcher.Launches);
        Assert.Contains("crash loop", rig.ConsoleText, StringComparison.Ordinal);
        Assert.Equal(before + 1, AnomalyLedger.Shared.CountOf(AnomalyLedger.KeyOf("cluster", "supervisor-crash-loop")));
    }

    [Fact]
    public async Task CrashesSpreadBeyondWindow_KeepRestarting()
    {
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(options: o => o.CrashLoopMaxRestarts = 2));
        await rig.Supervisor.StartProcessAsync("world");

        for (int i = 0; i < 6; i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(400)); // each crash is outside the 300 s window of the one before
            rig.Launcher.Processes[i].ExitWith(1);
            await rig.Launcher.WaitForLaunchesAsync(i + 2);
        }

        SupervisedProcessSnapshot snapshot = await WaitRunningAsync(rig, incarnation: 7);
        Assert.Equal(SupervisedProcessState.Running, snapshot.State);
        Assert.Equal(7, rig.Launcher.Launches);
    }

    [Fact]
    public async Task HealthyUptime_ResetsBackoffToMinimum()
    {
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess());
        await rig.Supervisor.StartProcessAsync("world");

        rig.Launcher.Processes[0].ExitWith(1);
        await rig.Launcher.WaitForLaunchesAsync(2);
        rig.Launcher.Processes[1].ExitWith(1);
        await rig.Launcher.WaitForLaunchesAsync(3);
        Assert.Equal(2000, Snap(rig).LastBackoffDelayMs);
        Assert.Equal(2, Snap(rig).ConsecutiveFailures);

        rig.Clock.Advance(TimeSpan.FromSeconds(130)); // longer than HealthyUptimeSeconds (120)
        rig.Launcher.Processes[2].ExitWith(1);
        await rig.Launcher.WaitForLaunchesAsync(4);

        SupervisedProcessSnapshot snapshot = await WaitRunningAsync(rig, incarnation: 4);
        Assert.Equal(1000, snapshot.LastBackoffDelayMs);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task RelaunchLaunchFailure_BacksOff()
    {
        var launcher = new ScriptedLauncher { FailLaunch = i => i is 1 or 2 ? new InvalidOperationException("the executable is missing") : null };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), launcher);
        await rig.Supervisor.StartProcessAsync("world");

        launcher.Processes[0].ExitWith(1);
        await launcher.WaitForLaunchesAsync(2); // launch attempts 1 and 2 throw; the fourth attempt starts a child

        SupervisedProcessSnapshot snapshot = await WaitRunningAsync(rig, incarnation: 2);
        Assert.Equal(SupervisedProcessState.Running, snapshot.State);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], rig.Delays.Backoffs);
        Assert.Contains("Failed to relaunch", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupervisorDrain_ChildExit2Meanwhile_NotRelaunched()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.DrainExitCode = ExitCodes.Restart };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), launcher);
        await rig.Supervisor.StartProcessAsync("world");

        SupervisedProcessSnapshot snapshot = await rig.Supervisor.DrainProcessAsync("world");

        Assert.Equal(SupervisedProcessState.Drained, snapshot.State);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(0, snapshot.RestartCount);
    }

    [Fact]
    public async Task RealLauncher_FixtureExit2_IsRelaunched()
    {
        var counting = new CountingLauncher(new SystemProcessSupervisedLauncher());
        var options = new ClusterSupervisorOptions { MinClusterProcesses = 1 };
        options.Processes["child"] = new SupervisedProcessConfig
        {
            Name = "child",
            Role = SupervisedRoles.Worker,
            ExecutablePath = ConfProcess.DotnetMuxer(),
            Arguments = $"\"{Path.Combine(AppContext.BaseDirectory, "SupervisedChildFixture.dll")}\" --exit 2",
            Console = SupervisedConsoleMode.None,
            DrainTimeoutMs = 500,
            RequiredMemoryMb = 1,
        };
        using var mux = new SupervisedOutputMultiplexer(TextWriter.Null);
        await using var supervisor = new ClusterHostSupervisor(options, counting, mux, new FixedMemoryProbe());
        await supervisor.StartProcessAsync("child");

        await TestWait.UntilAsync(() => counting.Launches >= 2, "the real child that exited 2 to be launched again", timeoutMs: 60_000);
        await supervisor.DrainAndStopAsync();

        SupervisedProcessSnapshot snapshot = supervisor.GetSnapshot("child");
        Assert.Equal(SupervisedProcessState.Drained, snapshot.State);
        Assert.True(snapshot.RestartCount >= 1);
        Assert.Equal(0, snapshot.LastBackoffDelayMs); // a restart request never waits
        Assert.Equal(0, snapshot.ConsecutiveFailures);
    }

    // ---- protocol lines ----

    [Fact]
    public async Task HeartbeatLines_ConsumedNotRoutedToConsole()
    {
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess());
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess child = rig.Launcher.Last;

        child.Beat("ready");
        child.Beat();
        child.Emit("ordinary log line");
        child.Emit(SupervisorProtocol.FormatStopping(child.Token!, SupervisorStopIntent.Shutdown, ExitCodes.Success));
        rig.Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Contains("ordinary log line", rig.ConsoleText, StringComparison.Ordinal);
        Assert.DoesNotContain("heartbeat", rig.ConsoleText, StringComparison.Ordinal);
        Assert.DoesNotContain("lifecycle stopping", rig.ConsoleText, StringComparison.Ordinal);
        Assert.Equal(5000, Snap(rig).LastBeatAgeMs);
    }

    [Fact]
    public async Task MalformedHeartbeat_IsRoutedAsOutput()
    {
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess());
        await rig.Supervisor.StartProcessAsync("world");

        rig.Launcher.Last.Emit("heartbeat garbage");
        rig.Launcher.Last.Emit("lifecycle request nonsense");

        Assert.Contains("heartbeat garbage", rig.ConsoleText, StringComparison.Ordinal);
        Assert.Contains("lifecycle request nonsense", rig.ConsoleText, StringComparison.Ordinal);
        Assert.Null(Snap(rig).LastBeatAgeMs);
    }

    // ---- hang watch ----

    [Fact]
    public async Task Hang_NoBeatPastTimeout_DrainsThenKillsThenRelaunches()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess hung = launcher.Last;
        hung.Beat();

        rig.Clock.Advance(Seconds121);
        await rig.Delays.ReleaseUntilAsync(() => hung.Kills == 1, "the hung child to be killed");

        Assert.Equal(1, hung.Drains);
        Assert.Equal(TimeSpan.FromMilliseconds(15_000), hung.LastDrainTimeout); // HangDrainTimeoutMs, not DrainTimeoutMs
        Assert.Equal(["drain:world", "kill:world"], launcher.Journal.ToArray());

        await rig.Delays.ReleaseUntilAsync(() => launcher.Launches >= 2, "the relaunch after the backoff");
        SupervisedProcessSnapshot snapshot = await WaitRunningAsync(rig, incarnation: 2);
        Assert.Equal(1, snapshot.RestartCount);
        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, snapshot.LastDisposition);
        Assert.Equal(1000, snapshot.LastBackoffDelayMs);
        Assert.Contains("no world tick", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hang_ChildThatAnnouncedDumping_GetsTheDumpGrace_ThenIsTreatedAsHung()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess dumping = launcher.Last;
        dumping.Beat();
        dumping.Beat(SupervisorProtocol.FormatDumpingState(360, dumping.Token));

        rig.Clock.Advance(TimeSpan.FromSeconds(300)); // 300 s of silence: far past the 120 s hang timeout, inside the 420 s dump grace
        await rig.Delays.PulseAsync();
        await rig.Delays.PulseAsync();
        Assert.Equal(0, dumping.Drains);
        Assert.Equal(0, dumping.Kills);
        Assert.Equal(SupervisedProcessState.Running, Snap(rig).State);

        rig.Clock.Advance(TimeSpan.FromSeconds(130)); // 430 s since the announcement: the grace is spent, the child is wedged in its dump
        await rig.Delays.ReleaseUntilAsync(() => dumping.Drains == 1, "the hang watch to act once the dump grace is spent");
    }

    [Fact]
    public async Task Hang_UnauthenticatedDumpingLine_GetsNoDumpGrace()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess forged = launcher.Last;
        forged.Beat();
        forged.Beat(SupervisorProtocol.FormatDumpingState(360, "not-the-token"));
        forged.Beat("dumping max=360"); // no token at all

        rig.Clock.Advance(TimeSpan.FromSeconds(130)); // past the 120 s hang timeout, far inside the 420 s dump grace a real announcement would earn
        await rig.Delays.ReleaseUntilAsync(() => forged.Drains == 1, "the hang watch to act at once: a forged dumping line earns no grace");
    }

    [Fact]
    public async Task Hang_SlowButBeating_NeverActs()
    {
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess slow = rig.Launcher.Last;
        slow.Beat();

        for (int i = 0; i < 6; i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(100)); // each gap is under the 120 s timeout; the whole run is 600 s
            slow.Beat();
            await rig.Delays.PulseAsync();
            await rig.Delays.PulseAsync();
        }

        Assert.Equal(0, slow.Drains);
        Assert.Equal(0, slow.Kills);
        Assert.Equal(SupervisedProcessState.Running, Snap(rig).State);
        Assert.Equal(1, rig.Launcher.Launches);
    }

    [Fact]
    public async Task Hang_BeforeFirstBeat_NotArmed_WarnsOnceAfterGrace()
    {
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess silent = rig.Launcher.Last;

        rig.Clock.Advance(TimeSpan.FromSeconds(400)); // past the 300 s grace and far past the 120 s timeout, but no beat has ever come
        await rig.Delays.ReleaseUntilAsync(() => Count(rig.ConsoleText, "no heartbeat") == 1, "the one grace warning");
        rig.Clock.Advance(TimeSpan.FromSeconds(400));
        await rig.Delays.PulseAsync();
        await rig.Delays.PulseAsync();

        Assert.Equal(1, Count(rig.ConsoleText, "no heartbeat"));
        Assert.Equal(0, silent.Drains);
        Assert.Equal(0, silent.Kills);

        // The first beat arms the watch: from there on a silent child is hung.
        silent.Beat();
        rig.Clock.Advance(Seconds121);
        await rig.Delays.ReleaseUntilAsync(() => silent.Drains == 1, "the armed watch to act");
    }

    [Fact]
    public async Task StoppingLine_WithToken_SuspendsHangWatch_AndStopGraceKillUsesAnnouncedIntent()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 30_000), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess stopping = launcher.Last;
        stopping.Beat();
        rig.Clock.Advance(TimeSpan.FromSeconds(100));
        stopping.Emit(SupervisorProtocol.FormatStopping(stopping.Token!, SupervisorStopIntent.Restart, ExitCodes.Restart));

        rig.Clock.Advance(TimeSpan.FromSeconds(25)); // 125 s since the last beat, but only 25 s into a stop: the hang watch is suspended
        await rig.Delays.PulseAsync();
        await rig.Delays.PulseAsync();
        Assert.Equal(0, stopping.Drains);
        Assert.Equal(0, stopping.Kills);

        rig.Clock.Advance(TimeSpan.FromSeconds(10)); // 35 s into the stop: past the stop grace (DrainTimeoutMs, 30 s)
        await rig.Delays.ReleaseUntilAsync(() => launcher.Launches >= 2, "the stuck stop to be killed and relaunched");

        Assert.Equal(1, stopping.Kills);
        Assert.Equal(0, stopping.Drains); // already stopping: no drain request, just the kill
        SupervisedProcessSnapshot snapshot = await WaitRunningAsync(rig, incarnation: 2);
        Assert.Equal(SupervisedExitDisposition.RelaunchNow, snapshot.LastDisposition); // the announced restart, not a crash
        Assert.Equal(0, snapshot.LastBackoffDelayMs);
    }

    [Fact]
    public async Task Hang_KillUnconfirmed_FaultsNoRelaunch()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => { p.Drain = DrainBehaviour.IgnoresDrain; p.RefuseKill = true; } };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        launcher.Last.Beat();

        rig.Clock.Advance(Seconds121);
        // The FATAL line is routed before Faulted is published, so a poll that sees the state may read the console text at once.
        await rig.Delays.ReleaseUntilAsync(() => Snap(rig).State == SupervisedProcessState.Faulted, "the unkillable hung child to be faulted");

        Assert.Equal(1, launcher.Launches);
        Assert.True(launcher.Last.Kills >= 1);
        Assert.Contains("still running", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hang_CaptureRunsBeforeDrain()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        var capture = new RecordingHangCapture(launcher.Journal);
        ClusterSupervisorOptions options = SupervisorRig.OneProcess(configure: c => c.HangCapture = SupervisedHangCaptureMode.Stacks, options: o => o.StackCaptureTool = "dotnet-stack");
        await using var rig = new SupervisorRig(options, launcher, holdAllDelays: true, capture: capture);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess hung = launcher.Last;
        hung.Beat();

        rig.Clock.Advance(Seconds121);
        await rig.Delays.ReleaseUntilAsync(() => hung.Kills == 1, "the hung child to be killed");

        Assert.Equal(["capture:world", "drain:world", "kill:world"], launcher.Journal.ToArray());
        Assert.True(capture.Requests.TryPeek(out SupervisedHangCaptureRequest? request));
        Assert.Equal(SupervisedHangCaptureMode.Stacks, request.Mode);
        Assert.Equal(launcher.Processes[0].ProcessId, request.ProcessId);
        Assert.Equal(options.HangCaptureDirectory, request.Directory);
        Assert.Equal("dotnet-stack", request.StackTool);
        Assert.Equal(TimeSpan.FromSeconds(options.HangCaptureTimeoutSeconds), request.ToolTimeout);
    }

    [Fact]
    public async Task Hang_CaptureTimeout_StillKills()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        var capture = new RecordingHangCapture(launcher.Journal) { Hangs = true };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.HangCapture = SupervisedHangCaptureMode.Dump), launcher, holdAllDelays: true, capture: capture);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess hung = launcher.Last;
        hung.Beat();

        rig.Clock.Advance(Seconds121);
        await rig.Delays.ReleaseUntilAsync(() => hung.Kills == 1, "the child to be killed although its capture never finished");

        Assert.Equal(["capture:world", "drain:world", "kill:world"], launcher.Journal.ToArray());
        Assert.Contains(TimeSpan.FromSeconds(300), rig.Delays.Requested); // HangCaptureTimeoutSeconds bounded the capture
        Assert.Contains("capture", rig.ConsoleText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- cross-world lifecycle ----

    private static SupervisorRig TwoRealms(out ScriptedProcess a, out ScriptedProcess b)
    {
        var o = new ClusterSupervisorOptions { MinClusterProcesses = 1 };
        o.Processes["realm-a"] = new SupervisedProcessConfig { Name = "realm-a", Role = SupervisedRoles.Combined, ExecutablePath = "x", RequiredMemoryMb = 1, RealmId = 1, StartOrder = 0 };
        o.Processes["realm-b"] = new SupervisedProcessConfig { Name = "realm-b", Role = SupervisedRoles.Combined, ExecutablePath = "x", RequiredMemoryMb = 1, RealmId = 2, StartOrder = 1 };
        var rig = new SupervisorRig(o);
        rig.Supervisor.StartAllAsync().GetAwaiter().GetResult();
        a = rig.Launcher.Processes.Single(p => p.Name == "realm-a");
        b = rig.Launcher.Processes.Single(p => p.Name == "realm-b");
        return rig;
    }

    [Fact]
    public async Task LifecycleRequest_ForOtherRealm_IsForwardedToThatChild()
    {
        await using SupervisorRig rig = TwoRealms(out ScriptedProcess a, out ScriptedProcess b);

        a.Emit(SupervisorProtocol.FormatLifecycleRequest(new SupervisorLifecycleRequest(a.Token!, 2, SupervisorLifecycleAction.Restart, 60, ExitCodes.Restart, "Admin")));

        await TestWait.UntilAsync(() => !b.Sent.IsEmpty && !a.Sent.IsEmpty, "the request to be forwarded and answered");
        Assert.Equal("lifecycle restart delay=60 code=2 by=Admin", Assert.Single(b.Sent));
        Assert.True(SupervisorProtocol.TryParseLifecycleResult(Assert.Single(a.Sent), out SupervisorLifecycleResult result));
        Assert.Equal(new SupervisorLifecycleResult(2, SupervisorLifecycleStatus.Forwarded, "Admin"), result);
        Assert.DoesNotContain("lifecycle request", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleRequest_UnknownRealm_IsRejectedAndReported()
    {
        await using SupervisorRig rig = TwoRealms(out ScriptedProcess a, out ScriptedProcess b);

        a.Emit(SupervisorProtocol.FormatLifecycleRequest(new SupervisorLifecycleRequest(a.Token!, 9, SupervisorLifecycleAction.Shutdown, 0, ExitCodes.Success, "Admin")));

        await TestWait.UntilAsync(() => !a.Sent.IsEmpty, "the issuer to be told");
        Assert.True(SupervisorProtocol.TryParseLifecycleResult(Assert.Single(a.Sent), out SupervisorLifecycleResult result));
        Assert.Equal(new SupervisorLifecycleResult(9, SupervisorLifecycleStatus.UnknownRealm, "Admin"), result);
        Assert.Empty(b.Sent);
        Assert.Contains("unknown realm 9", rig.ConsoleText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LifecycleRequest_WrongToken_IsRejectedAndNeverForwarded()
    {
        await using SupervisorRig rig = TwoRealms(out ScriptedProcess a, out ScriptedProcess b);

        // Log text that happens to look like a request (a player-controlled string) does not know the supervisor's token.
        a.Emit(SupervisorProtocol.FormatLifecycleRequest(new SupervisorLifecycleRequest("guessed", 2, SupervisorLifecycleAction.Shutdown, 0, ExitCodes.Success, "Admin")));

        await TestWait.UntilAsync(() => !a.Sent.IsEmpty, "the issuer to be told");
        Assert.True(SupervisorProtocol.TryParseLifecycleResult(Assert.Single(a.Sent), out SupervisorLifecycleResult result));
        Assert.Equal(SupervisorLifecycleStatus.Rejected, result.Status);
        Assert.Empty(b.Sent);
    }

    [Fact]
    public void BuildStartInfo_SetsTheProtocolEnvironment()
    {
        var withRealm = new SupervisedProcessConfig { Name = "w", Role = SupervisedRoles.Combined, ExecutablePath = "dotnet", RealmId = 3, ProtocolToken = "tok" };
        var without = new SupervisedProcessConfig { Name = "w", Role = SupervisedRoles.Combined, ExecutablePath = "dotnet" };

        System.Diagnostics.ProcessStartInfo info = SystemProcessSupervisedLauncher.BuildStartInfo(withRealm);
        System.Diagnostics.ProcessStartInfo plain = SystemProcessSupervisedLauncher.BuildStartInfo(without);

        Assert.Equal("1", info.Environment[SupervisorProtocol.EnvironmentVariable]);
        Assert.Equal("tok", info.Environment[SupervisorProtocol.TokenEnvironmentVariable]);
        Assert.Equal("3", info.Environment[SupervisorProtocol.RealmIdEnvironmentVariable]);
        Assert.Equal("1", plain.Environment[SupervisorProtocol.EnvironmentVariable]);
        Assert.False(plain.Environment.ContainsKey(SupervisorProtocol.RealmIdEnvironmentVariable));
    }

    // ---- helpers ----

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<SupervisedProcessSnapshot> WaitRunningAsync(SupervisorRig rig, long incarnation)
    {
        await TestWait.UntilAsync(() => Snap(rig) is { State: SupervisedProcessState.Running } s && s.Incarnation >= incarnation, $"incarnation {incarnation} to be running");
        return Snap(rig);
    }

    private sealed class CountingLauncher(ISupervisedProcessLauncher inner) : ISupervisedProcessLauncher
    {
        private int _launches;

        public int Launches => Volatile.Read(ref _launches);

        public ISupervisedProcess Launch(SupervisedProcessConfig config)
        {
            Interlocked.Increment(ref _launches);
            return inner.Launch(config);
        }
    }
}

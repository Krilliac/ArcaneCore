using System.Collections.Concurrent;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// The supervisor's own crash dialog for children that exit unexpectedly: a fake presenter, fake files, scripted children and a manual clock.
/// No window opens and no process starts.
/// </summary>
public sealed class SupervisorCrashDialogTests
{
    private static readonly string ChildDirectory = Path.Combine(Path.GetTempPath(), "arcane-supervised-child");

    private static readonly CrashDialogEnvironment Interactive = new(IsWindows: true, UserInteractive: true, InputRedirected: false, OutputRedirected: false, ErrorRedirected: false, ContinuousIntegration: false);

    private sealed class FakePresenter : ICrashDialogPresenter
    {
        private readonly ManualResetEventSlim _release = new(initialState: true);

        public ConcurrentQueue<(string Title, string Text)> Shown { get; } = new();

        public Exception? Throw { get; set; }

        /// <summary>Makes the next boxes stay open until <see cref="Release"/>.</summary>
        public void Hold() => _release.Reset();

        public void Release() => _release.Set();

        public void Show(string title, string text)
        {
            Shown.Enqueue((title, text));
            _release.Wait(TimeSpan.FromSeconds(30));
            if (Throw is { } failure)
            {
                throw failure;
            }
        }
    }

    private sealed class FakeFiles : ISupervisorCrashFiles
    {
        public ConcurrentQueue<(string Directory, string ProcessName)> Lookups { get; } = new();

        public List<(SupervisorCrashReportFile File, string Text)> Reports { get; } = [];

        public IReadOnlyList<SupervisorCrashReportFile> FindReports(string directory, string processName)
        {
            Lookups.Enqueue((directory, processName));
            return [.. Reports.Select(r => r.File)];
        }

        public (string Text, bool Truncated)? ReadText(string path, int maxChars)
        {
            string? text = Reports.Where(r => r.File.Path == path).Select(r => r.Text).FirstOrDefault();
            if (text is null)
            {
                return null;
            }

            return text.Length > maxChars ? (text[..maxChars], true) : (text, false);
        }
    }

    private static SupervisorRig NewRig(SupervisorCrashDialog dialog, string executable = "ArcaneCore.World.exe")
        => new(SupervisorRig.OneProcess(configure: c =>
        {
            c.ExecutablePath = Path.Combine(ChildDirectory, executable);
        }), crashDialog: dialog);

    private static SupervisorCrashDialog NewDialog(FakePresenter presenter, FakeFiles files, bool enabled = true, CrashDialogEnvironment? environment = null)
        => new(enabled, presenter: presenter, environmentProbe: () => environment ?? Interactive, files: files, dumpNote: "dump note");

    private static async Task CrashAndWaitForRestartAsync(SupervisorRig rig, int code, int launchesAfter)
    {
        rig.Launcher.Last.ExitWith(code);
        await rig.Launcher.WaitForLaunchesAsync(launchesAfter);
    }

    [Fact]
    public async Task UnexpectedNonzeroExit_ShowsTheNewestReportWrittenSinceStart()
    {
        var presenter = new FakePresenter();
        var files = new FakeFiles();
        var dialog = NewDialog(presenter, files);
        await using SupervisorRig rig = NewRig(dialog);
        await rig.Supervisor.StartProcessAsync("world");
        DateTime start = rig.Clock.GetUtcNow().UtcDateTime;
        string older = Path.Combine(ChildDirectory, "crash-report.ArcaneCore.World.old.txt");
        string wanted = Path.Combine(ChildDirectory, "crash-report.ArcaneCore.World.new.txt");
        string newer = Path.Combine(ChildDirectory, "crash-report.ArcaneCore.World.newer.txt");
        files.Reports.Add((new SupervisorCrashReportFile(older, start - TimeSpan.FromHours(1)), "OLD REPORT"));
        files.Reports.Add((new SupervisorCrashReportFile(wanted, start + TimeSpan.FromSeconds(5)), "FIRST REPORT"));
        files.Reports.Add((new SupervisorCrashReportFile(newer, start + TimeSpan.FromSeconds(9)), "NEWEST REPORT line\r\nsecond line"));

        int pid = rig.Launcher.Last.ProcessId;
        await CrashAndWaitForRestartAsync(rig, code: 70, launchesAfter: 2);
        await TestWait.UntilAsync(() => !presenter.Shown.IsEmpty, "the crash dialog");

        Assert.True(presenter.Shown.TryPeek(out (string Title, string Text) box));
        Assert.Contains("world", box.Title, StringComparison.Ordinal);
        Assert.Contains("70", box.Title, StringComparison.Ordinal);
        Assert.Contains("pid " + pid, box.Text, StringComparison.Ordinal);
        Assert.Contains("exit code 70", box.Text, StringComparison.Ordinal);
        Assert.Contains("Combined", box.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restarting it in 1 s (restart #1)", box.Text, StringComparison.Ordinal);
        Assert.Contains("NEWEST REPORT line\nsecond line", box.Text, StringComparison.Ordinal);
        Assert.Contains(newer, box.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD REPORT", box.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST REPORT", box.Text, StringComparison.Ordinal);

        // The lookup used the directory and process name the child's own crash dialog uses (AppContext.BaseDirectory, entry assembly name).
        Assert.True(files.Lookups.TryPeek(out (string Directory, string ProcessName) lookup));
        Assert.Equal(Path.GetFullPath(ChildDirectory), lookup.Directory);
        Assert.Equal("ArcaneCore.World", lookup.ProcessName);
    }

    [Fact]
    public async Task ReportOlderThanTheChildStart_IsIgnored_AndTheNoReportTextIsShown()
    {
        var presenter = new FakePresenter();
        var files = new FakeFiles();
        await using SupervisorRig rig = NewRig(NewDialog(presenter, files));
        await rig.Supervisor.StartProcessAsync("world");
        DateTime start = rig.Clock.GetUtcNow().UtcDateTime;
        files.Reports.Add((new SupervisorCrashReportFile(Path.Combine(ChildDirectory, "crash-report.ArcaneCore.World.old.txt"), start - TimeSpan.FromSeconds(1)), "STALE"));

        await CrashAndWaitForRestartAsync(rig, code: 3, launchesAfter: 2);
        await TestWait.UntilAsync(() => !presenter.Shown.IsEmpty, "the crash dialog");

        presenter.Shown.TryPeek(out (string Title, string Text) box);
        Assert.DoesNotContain("STALE", box.Text, StringComparison.Ordinal);
        Assert.Contains("Full report: none", box.Text, StringComparison.Ordinal);
        Assert.Contains("FailFast", box.Text, StringComparison.Ordinal);
        Assert.Contains("dump note", box.Text, StringComparison.Ordinal);
        Assert.Contains(ChildDirectory, box.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongReport_IsCutToTheBoxSize_KeepingTheHeaderAndThePathOfTheFullReport()
    {
        var presenter = new FakePresenter();
        var files = new FakeFiles();
        await using SupervisorRig rig = NewRig(NewDialog(presenter, files));
        await rig.Supervisor.StartProcessAsync("world");
        string path = Path.Combine(ChildDirectory, "crash-report.ArcaneCore.World.big.txt");
        string report = string.Join('\n', Enumerable.Range(1, 300).Select(i => "report line " + i + " " + new string('x', 80)));
        files.Reports.Add((new SupervisorCrashReportFile(path, rig.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(1)), report));

        await CrashAndWaitForRestartAsync(rig, code: 1, launchesAfter: 2);
        await TestWait.UntilAsync(() => !presenter.Shown.IsEmpty, "the crash dialog");

        presenter.Shown.TryPeek(out (string Title, string Text) box);
        string[] lines = box.Text.Split('\n');
        Assert.True(lines.Length <= CrashDialog.MaxBoxLines + 1, "lines: " + lines.Length);
        Assert.All(lines, l => Assert.True(l.Length <= CrashDialog.MaxBoxLineLength + 3 + path.Length + 40, "line length " + l.Length));
        Assert.StartsWith("The supervised combined process 'world'", box.Text, StringComparison.Ordinal);
        Assert.Contains("Supervisor: restarting it in 1 s", box.Text, StringComparison.Ordinal);
        Assert.Contains("Full report: " + path, lines[..8]);
        Assert.Contains("report line 1 ", box.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("report line 200 ", box.Text, StringComparison.Ordinal);
        Assert.EndsWith(" more line(s) in " + path, box.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestedDrain_ShowsNoBox_EvenWithANonzeroExitCode()
    {
        var presenter = new FakePresenter();
        var dialog = NewDialog(presenter, new FakeFiles());
        await using SupervisorRig rig = NewRig(dialog);
        await rig.Supervisor.StartProcessAsync("world");
        rig.Launcher.Last.DrainExitCode = 9;

        await rig.Supervisor.DrainProcessAsync("world");

        Assert.Equal(SupervisedProcessState.Drained, rig.Supervisor.GetSnapshot("world").State);
        Assert.Equal(9, rig.Supervisor.GetSnapshot("world").LastExitCode);
        Assert.True(presenter.Shown.IsEmpty);
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public async Task ExitCodeZero_ShowsNoBox()
    {
        var presenter = new FakePresenter();
        var dialog = NewDialog(presenter, new FakeFiles());
        await using SupervisorRig rig = NewRig(dialog);
        await rig.Supervisor.StartProcessAsync("world");

        rig.Launcher.Last.ExitWith(0);
        await TestWait.UntilAsync(() => rig.Supervisor.GetSnapshot("world").State == SupervisedProcessState.Stopped, "the child to be left down");

        Assert.True(presenter.Shown.IsEmpty);
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public async Task RestartRequestExit_ShowsNoBox()
    {
        var presenter = new FakePresenter();
        await using SupervisorRig rig = NewRig(NewDialog(presenter, new FakeFiles()));
        await rig.Supervisor.StartProcessAsync("world");

        await CrashAndWaitForRestartAsync(rig, code: ArcaneCore.Kernel.Ops.ExitCodes.Restart, launchesAfter: 2);

        Assert.True(presenter.Shown.IsEmpty);
    }

    [Fact]
    public async Task OptionOff_ShowsNoBox()
    {
        var presenter = new FakePresenter();
        await using SupervisorRig rig = NewRig(NewDialog(presenter, new FakeFiles(), enabled: false));
        await rig.Supervisor.StartProcessAsync("world");

        await CrashAndWaitForRestartAsync(rig, code: 70, launchesAfter: 2);

        Assert.True(presenter.Shown.IsEmpty);
    }

    [Fact]
    public async Task NonInteractiveSession_ShowsNoBox()
    {
        var presenter = new FakePresenter();
        CrashDialogEnvironment redirected = Interactive with { OutputRedirected = true };
        await using SupervisorRig rig = NewRig(NewDialog(presenter, new FakeFiles(), environment: redirected));
        await rig.Supervisor.StartProcessAsync("world");

        await CrashAndWaitForRestartAsync(rig, code: 70, launchesAfter: 2);

        Assert.True(presenter.Shown.IsEmpty);
    }

    [Fact]
    public async Task SecondCrashWhileABoxIsOpen_IsNotShown_AndTheNextOneIsAfterItCloses()
    {
        var presenter = new FakePresenter();
        presenter.Hold();
        var dialog = NewDialog(presenter, new FakeFiles());
        await using SupervisorRig rig = NewRig(dialog);
        await rig.Supervisor.StartProcessAsync("world");

        await CrashAndWaitForRestartAsync(rig, code: 70, launchesAfter: 2);
        await TestWait.UntilAsync(() => presenter.Shown.Count == 1, "the first box");
        Assert.True(dialog.IsOpen);

        await CrashAndWaitForRestartAsync(rig, code: 71, launchesAfter: 3); // restarted although the first box is still open
        Assert.Single(presenter.Shown);

        presenter.Release();
        await TestWait.UntilAsync(() => !dialog.IsOpen, "the first box to close");

        await CrashAndWaitForRestartAsync(rig, code: 72, launchesAfter: 4);
        await TestWait.UntilAsync(() => presenter.Shown.Count == 2, "the box of the third crash");
        Assert.Contains("72", presenter.Shown.Last().Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowingPresenter_DoesNotBreakTheRestartPath()
    {
        var presenter = new FakePresenter { Throw = new InvalidOperationException("no desktop") };
        var dialog = NewDialog(presenter, new FakeFiles());
        await using SupervisorRig rig = NewRig(dialog);
        await rig.Supervisor.StartProcessAsync("world");

        await CrashAndWaitForRestartAsync(rig, code: 70, launchesAfter: 2);
        await TestWait.UntilAsync(() => presenter.Shown.Count == 1 && !dialog.IsOpen, "the failing box to end");

        await CrashAndWaitForRestartAsync(rig, code: 70, launchesAfter: 3);
        await TestWait.UntilAsync(() => presenter.Shown.Count == 2 && !dialog.IsOpen, "the second failing box to end");
        Assert.Equal(SupervisedProcessState.Running, await WaitForStateAsync(rig, SupervisedProcessState.Running));
    }

    [Fact]
    public async Task CrashLoopGiveUp_ShowsTheGivingUpDecision()
    {
        var presenter = new FakePresenter();
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.ExecutablePath = Path.Combine(ChildDirectory, "ArcaneCore.World.exe"), options: o => o.CrashLoopMaxRestarts = 3), crashDialog: NewDialog(presenter, new FakeFiles()));
        await rig.Supervisor.StartProcessAsync("world");

        for (int i = 0; i < 3; i++)
        {
            await CrashAndWaitForRestartAsync(rig, code: 70, launchesAfter: i + 2);
            await TestWait.UntilAsync(() => presenter.Shown.Count == i + 1, "the box of crash " + (i + 1));
        }

        rig.Launcher.Last.ExitWith(70); // the fourth failure inside the window is one too many
        await TestWait.UntilAsync(() => rig.Supervisor.GetSnapshot("world").State == SupervisedProcessState.Faulted, "the child to be given up on");
        await TestWait.UntilAsync(() => presenter.Shown.Count == 4, "the giving up box");

        Assert.Contains("giving up", presenter.Shown.Last().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HangKill_ShowsABoxWithTheHangFacts_AndTheCaptureNote()
    {
        var presenter = new FakePresenter();
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        var capture = new RecordingHangCapture(launcher.Journal);
        var options = SupervisorRig.OneProcess(configure: c =>
        {
            c.ExecutablePath = Path.Combine(ChildDirectory, "ArcaneCore.World.exe");
            c.HangCapture = SupervisedHangCaptureMode.Stacks;
        }, options: o => o.HangCaptureDirectory = Path.Combine(ChildDirectory, "hangstacks"));
        await using var rig = new SupervisorRig(options, launcher, holdAllDelays: true, capture: capture, crashDialog: NewDialog(presenter, new FakeFiles()));
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess hung = launcher.Last;
        hung.Beat();

        rig.Clock.Advance(TimeSpan.FromSeconds(121));
        await rig.Delays.ReleaseUntilAsync(() => hung.Kills == 1, "the hung child to be killed");
        await rig.Delays.ReleaseUntilAsync(() => launcher.Launches >= 2, "the relaunch after the backoff");
        await TestWait.UntilAsync(() => !presenter.Shown.IsEmpty, "the hang box");

        presenter.Shown.TryPeek(out (string Title, string Text) box);
        Assert.Contains("hung", box.Title, StringComparison.Ordinal);
        Assert.Contains("pid " + hung.ProcessId, box.Text, StringComparison.Ordinal);
        Assert.Contains("killed by the supervisor after a hang of 121 s", box.Text, StringComparison.Ordinal);
        Assert.Contains("Hang capture: Stacks capture written to " + Path.Combine(ChildDirectory, "hangstacks"), box.Text, StringComparison.Ordinal);
        Assert.Contains("restarting it in 1 s (restart #1)", box.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopGraceKill_ShowsNoBox()
    {
        var presenter = new FakePresenter();
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 30_000), launcher, holdAllDelays: true, crashDialog: NewDialog(presenter, new FakeFiles()));
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess stopping = launcher.Last;
        stopping.Beat();
        stopping.Emit(SupervisorProtocol.FormatStopping(stopping.Token!, SupervisorStopIntent.Shutdown, 0));

        rig.Clock.Advance(TimeSpan.FromSeconds(35));
        await rig.Delays.ReleaseUntilAsync(() => stopping.Kills == 1, "the stuck stop to be killed");
        await TestWait.UntilAsync(() => rig.Supervisor.GetSnapshot("world").State == SupervisedProcessState.Stopped, "the shutdown to leave the child down");

        Assert.True(presenter.Shown.IsEmpty);
        Assert.False(rig.Supervisor.GetSnapshot("world").LastExitCode is 0);
    }

    [Fact]
    public async Task StopGraceKill_WithAFailureIntent_ShowsNoBox()
    {
        var presenter = new FakePresenter();
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 30_000), launcher, holdAllDelays: true, crashDialog: NewDialog(presenter, new FakeFiles()));
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess stopping = launcher.Last;
        stopping.Beat();
        stopping.Emit(SupervisorProtocol.FormatStopping(stopping.Token!, SupervisorStopIntent.Failure, 70));

        rig.Clock.Advance(TimeSpan.FromSeconds(35));
        await rig.Delays.ReleaseUntilAsync(() => stopping.Kills == 1, "the stuck stop to be killed");
        await rig.Delays.ReleaseUntilAsync(() => launcher.Launches >= 2, "the relaunch with backoff");

        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, rig.Supervisor.GetSnapshot("world").LastDisposition);
        Assert.True(presenter.Shown.IsEmpty);
    }

    [Theory]
    [InlineData(ExitCodes.InvalidConfiguration)]
    [InlineData(ExitCodes.Usage)]
    public async Task FaultExit_ShowsABoxSayingItIsNotRestarted(int code)
    {
        var presenter = new FakePresenter();
        await using SupervisorRig rig = NewRig(NewDialog(presenter, new FakeFiles()));
        await rig.Supervisor.StartProcessAsync("world");

        rig.Launcher.Last.ExitWith(code);
        await TestWait.UntilAsync(() => !presenter.Shown.IsEmpty, "the crash dialog");

        presenter.Shown.TryPeek(out (string Title, string Text) box);
        Assert.Contains("not restarting it", box.Text, StringComparison.Ordinal);
        Assert.Contains("exit code " + code, box.Text, StringComparison.Ordinal);
        Assert.Equal(1, rig.Launcher.Launches);
    }

    [Fact]
    public async Task HangChildThatExitsWhenAskedToDrain_IsShownAsExitedNotKilled()
    {
        var presenter = new FakePresenter();
        var launcher = new ScriptedLauncher { Configure = (_, p) => { p.Drain = DrainBehaviour.ExitsOnDrain; p.DrainExitCode = 5; } };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.ExecutablePath = Path.Combine(ChildDirectory, "ArcaneCore.World.exe")), launcher, holdAllDelays: true, crashDialog: NewDialog(presenter, new FakeFiles()));
        await rig.Supervisor.StartProcessAsync("world");
        launcher.Last.Beat();

        rig.Clock.Advance(TimeSpan.FromSeconds(121));
        await rig.Delays.ReleaseUntilAsync(() => !presenter.Shown.IsEmpty, "the hang box");

        presenter.Shown.TryPeek(out (string Title, string Text) box);
        Assert.Contains("hung and exited", box.Title, StringComparison.Ordinal);
        Assert.Contains("exited when the supervisor asked it to drain after a hang of 121 s", box.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("was killed", box.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadText_RealFile_CutsAtTheLimit_SkipsTheBom_AndReadsAFileAnotherWriterHolds()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-sup-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "crash-report.ArcaneCore.World.20261029-010203Z.txt");
            File.WriteAllText(path, new string('a', 50), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            (string Text, bool Truncated)? cut = SystemSupervisorCrashFiles.Instance.ReadText(path, 20);
            Assert.Equal((new string('a', 20), true), cut);
            Assert.Equal((new string('a', 50), false), SystemSupervisorCrashFiles.Instance.ReadText(path, 50));

            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            Assert.Equal((new string('a', 50), false), SystemSupervisorCrashFiles.Instance.ReadText(path, 100));
            Assert.Null(SystemSupervisorCrashFiles.Instance.ReadText(Path.Combine(directory, "missing.txt"), 10));

            IReadOnlyList<SupervisorCrashReportFile> found = SystemSupervisorCrashFiles.Instance.FindReports(directory, "ArcaneCore.World");
            Assert.Equal(path, Assert.Single(found).Path);
            Assert.Empty(SystemSupervisorCrashFiles.Instance.FindReports(directory, "ArcaneCore.Realm"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [Theory]
    [InlineData("ArcaneCore.World.exe", "", "ArcaneCore.World")]
    [InlineData("ArcaneCore.World", "", "ArcaneCore.World")]
    [InlineData("dotnet", "ArcaneCore.World.dll --Foo=1", "ArcaneCore.World")]
    [InlineData("dotnet.exe", "\"ArcaneCore.World.dll\" --Foo=1", "ArcaneCore.World")]
    public void ProcessName_IsTheEntryAssemblyName(string executable, string arguments, string expected)
    {
        var exit = new SupervisorChildExit("w", "Combined", 1, 1, DateTimeOffset.UnixEpoch, "x", Path.Combine(ChildDirectory, executable), arguments, null);

        Assert.Equal(expected, SupervisorCrashDialog.ProcessNameOf(exit));
    }

    [Fact]
    public void ReportDirectory_ForDotnetDll_IsTheDirectoryOfTheDll_ResolvedAgainstTheWorkingDirectory()
    {
        var exit = new SupervisorChildExit("w", "Combined", 1, 1, DateTimeOffset.UnixEpoch, "x", "dotnet", "bin/ArcaneCore.World.dll", ChildDirectory);

        Assert.Equal(Path.GetFullPath(Path.Combine(ChildDirectory, "bin")), SupervisorCrashDialog.ReportDirectoryOf(exit));
    }

    [Fact]
    public void ReportFilePrefix_MatchesTheNamingOfCrashJournalPaths()
    {
        string name = Path.GetFileName(CrashJournalPaths.ReportPathFor(string.Empty, "ArcaneCore.World", new DateTime(2026, 10, 29, 1, 2, 3, DateTimeKind.Utc)));

        Assert.StartsWith(SupervisorCrashDialog.ReportFilePrefix("ArcaneCore.World"), name, StringComparison.Ordinal);
        Assert.Equal("crash-report.ArcaneCore.World.", SupervisorCrashDialog.ReportFilePrefix("ArcaneCore.World"));
    }

    private static async Task<SupervisedProcessState> WaitForStateAsync(SupervisorRig rig, SupervisedProcessState state)
    {
        await TestWait.UntilAsync(() => rig.Supervisor.GetSnapshot("world").State == state, "the child to be " + state);
        return rig.Supervisor.GetSnapshot("world").State;
    }
}

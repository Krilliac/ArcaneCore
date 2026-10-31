using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>
/// The opt-in crash and fault dialogs (<c>Diagnostics:CrashDialog</c>, <c>Diagnostics:FaultDialog</c>): the decision to show, the composed
/// text, and that a failing dialog only records itself. Every test swaps in a recording presenter and a fixed environment, so no real
/// window ever opens; the process-level ordering and exit codes are in <see cref="CrashProcessTests"/>.
/// </summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class CrashDialogTests : IDisposable
{
    private static readonly CrashDialogEnvironment Interactive = new(IsWindows: true, UserInteractive: true, InputRedirected: false, OutputRedirected: false, ErrorRedirected: false, ContinuousIntegration: false);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-crash-dialog-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingPresenter _presenter = new();

    public CrashDialogTests()
    {
        CrashDialog.Presenter = _presenter;
        CrashDialog.EnvironmentProbe = static () => Interactive;
        CrashDialog.ReportDirectory = _directory;
        FaultLedger.Shared.Reset();
    }

    public void Dispose()
    {
        CrashDialog.Presenter = null!;
        CrashDialog.EnvironmentProbe = null!;
        CrashDialog.SupervisedChildProbe = static () => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ArcaneCore.Kernel.Ops.ServerConsole.SupervisedChildDrain.EnvironmentVariable));
        CrashDialog.ReportDirectory = null;
        CrashDialog.LogFilePath = null;
        CrashHandler.Current?.Dispose();
        FaultLedger.Shared.Configure(new DiagnosticsOptions());
        FaultLedger.Shared.Reset();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Defaults_AreOff()
    {
        var options = new DiagnosticsOptions();
        Assert.False(options.CrashDialog);
        Assert.False(options.FaultDialog);
    }

    [Theory]
    [InlineData(true, true, true, false, false, false, false, true)]
    [InlineData(false, true, true, false, false, false, false, false)] // option off
    [InlineData(true, false, true, false, false, false, false, false)] // Linux / macOS
    [InlineData(true, true, false, false, false, false, false, false)] // a service (session 0)
    [InlineData(true, true, true, true, false, false, false, false)] // stdin redirected
    [InlineData(true, true, true, false, true, false, false, false)] // stdout redirected (a test runner)
    [InlineData(true, true, true, false, false, true, false, false)] // stderr redirected
    [InlineData(true, true, true, false, false, false, true, false)] // CI
    public void ShouldShow_NeedsTheOption_Windows_AndAnUnredirectedInteractiveSessionOutsideCi(
        bool enabled, bool windows, bool interactive, bool input, bool output, bool error, bool ci, bool expected)
    {
        var environment = new CrashDialogEnvironment(windows, interactive, input, output, error, ci);
        Assert.Equal(expected, CrashDialog.ShouldShow(enabled, environment));
        Assert.Equal(windows && interactive && !input && !output && !error && !ci, environment.WhyNot() is null);
    }

    [Fact]
    public void Detect_UnderTheTestRunner_SaysNoDialog()
    {
        // The test host redirects its streams (and CI sets CI); on Linux it is not Windows either. Whatever the reason, never a dialog here.
        Assert.NotNull(CrashDialogEnvironment.Detect().WhyNot());
    }

    [Fact]
    public void Compose_NamesTheException_InnerExceptions_TheContext_AndTheOutcome()
    {
        Exception exception = Thrown(new InvalidOperationException("outer message", Thrown(new FormatException("inner message"))));
        var facts = new CrashDialogFacts
        {
            Kind = CrashKind.Unhandled,
            Exception = exception,
            FailFast = false,
            DecidedBy = "Diagnostics:OnUnhandled = Exit",
            Daemon = "ArcaneCore.World",
            Version = "1.2.3+abc1234",
            ProcessId = 4242,
            ThreadId = 17,
            ThreadName = "World",
            TimeUtc = new DateTime(2026, 10, 27, 8, 30, 0, DateTimeKind.Utc),
            FaultSite = "unhandled",
            FaultId = "0123456789abcdef",
            ReportPath = "/srv/arcanecore/crash-report.ArcaneCore.World.20261027-083000Z.txt",
            JournalPath = "/srv/arcanecore/crash-faults.ArcaneCore.World.jsonl",
            LogPath = "/srv/arcanecore/logs/arcanecore.log",
            DumpNote = "the runtime writes one when the process aborts: /dumps/world.dmp",
        };

        CrashDialogText text = CrashDialog.Compose(facts);

        Assert.Equal("ArcaneCore.World crashed: InvalidOperationException", text.Title);
        Assert.Contains("ArcaneCore.World stopped because of an unhandled exception.", text.Text, StringComparison.Ordinal);
        Assert.Contains("When you close this box the process exits with code 70 (Diagnostics:OnUnhandled = Exit).", text.Text, StringComparison.Ordinal);
        Assert.Contains("Exception: System.InvalidOperationException\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Message: outer message\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Daemon: ArcaneCore.World 1.2.3+abc1234 (pid 4242)\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Thread: 17 \"World\"\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Time: 2026-10-27 08:30:00 UTC (", text.Text, StringComparison.Ordinal);
        Assert.Contains("Fault ledger: site unhandled, signature 0123456789abcdef\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Full report: " + facts.ReportPath + "\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Crash journal: " + facts.JournalPath + "\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Log file: " + facts.LogPath + "\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Dump: " + facts.DumpNote + "\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: outer message", text.Text, StringComparison.Ordinal);
        Assert.Contains("---> System.FormatException: inner message", text.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), text.Text, StringComparison.Ordinal); // the managed stack itself
        Assert.Contains("---> System.FormatException: inner message", text.BoxText, StringComparison.Ordinal); // short enough to show whole
        Assert.DoesNotContain("more line(s)", text.BoxText, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_UnderFailFast_WithoutARecordOrFile_SaysSo()
    {
        var facts = new CrashDialogFacts
        {
            Kind = CrashKind.UnobservedTask,
            Exception = null,
            FailFast = true,
            DecidedBy = "Diagnostics:OnUnobservedTask = FailFast",
            Daemon = "ArcaneCore.Realm",
            TimeUtc = DateTime.UtcNow,
            ReportError = "IOException: disk full",
        };

        CrashDialogText text = CrashDialog.Compose(facts);

        Assert.Equal("ArcaneCore.Realm crashed: fatal error", text.Title);
        Assert.Contains("because of an unobserved task exception.", text.Text, StringComparison.Ordinal);
        Assert.Contains("the process aborts (Environment.FailFast; Diagnostics:OnUnobservedTask = FailFast).", text.Text, StringComparison.Ordinal);
        Assert.Contains("Exception: (no managed exception object)", text.Text, StringComparison.Ordinal);
        Assert.Contains("Thread: 0 (unnamed)", text.Text, StringComparison.Ordinal);
        Assert.Contains("Fault ledger: (not recorded)", text.Text, StringComparison.Ordinal);
        Assert.Contains("Full report: (not written: IOException: disk full)", text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Log file:", text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dump:", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_CutsTheBoxToFitTheScreen_AndPointsAtTheFile()
    {
        var facts = new CrashDialogFacts
        {
            Kind = CrashKind.Unhandled,
            Exception = Recurse(60),
            FailFast = false,
            DecidedBy = "Diagnostics:OnUnhandled = Exit",
            Daemon = "ArcaneCore.World",
            TimeUtc = DateTime.UtcNow,
            ReportPath = "C:\\arcanecore\\crash-report.txt",
        };

        CrashDialogText text = CrashDialog.Compose(facts);

        string[] boxLines = text.BoxText.Split('\n');
        Assert.Equal(CrashDialog.MaxBoxLines + 1, boxLines.Length);
        Assert.Matches(@"^\.\.\. \d+ more line\(s\) in C:\\arcanecore\\crash-report\.txt$", boxLines[^1]);
        Assert.All(boxLines, line => Assert.True(line.Length <= CrashDialog.MaxBoxLineLength + 3 || line.StartsWith("...", StringComparison.Ordinal)));
        Assert.True(text.Text.Length > text.BoxText.Length);
    }

    [Fact]
    public void TryShowFatal_WhenDisabled_DoesNothing()
    {
        Assert.Equal(CrashDialogOutcome.Disabled, CrashDialog.TryShowFatal(false, CrashKind.Unhandled, new InvalidOperationException("x"), false, "Diagnostics:OnUnhandled = Exit", "unhandled", "id", "report"));
        Assert.Empty(_presenter.Shown);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void TryShowFatal_WhenHeadless_DoesNothing()
    {
        CrashDialog.EnvironmentProbe = static () => Interactive with { OutputRedirected = true };

        Assert.Equal(CrashDialogOutcome.NotInteractive, CrashDialog.TryShowFatal(true, CrashKind.Unhandled, new InvalidOperationException("x"), false, "Diagnostics:OnUnhandled = Exit", "unhandled", "id", "report"));
        Assert.Empty(_presenter.Shown);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void TryShowFatal_InASupervisedChildThatCannotShowABox_WritesTheReportFileOnly()
    {
        CrashDialog.EnvironmentProbe = static () => Interactive with { InputRedirected = true, OutputRedirected = true, ErrorRedirected = true };
        CrashDialog.SupervisedChildProbe = static () => true;
        Exception exception = Thrown(new InvalidOperationException("child probe"));

        CrashDialogOutcome outcome = CrashDialog.TryShowFatal(true, CrashKind.Unhandled, exception, false, "Diagnostics:OnUnhandled = Exit", "unhandled", "feedfacecafebeef", "==== the crash report ====");

        Assert.Equal(CrashDialogOutcome.ReportOnly, outcome);
        Assert.Empty(_presenter.Shown);
        string file = Assert.Single(Directory.GetFiles(_directory, "crash-report.*Z.txt"));
        string written = File.ReadAllText(file);
        Assert.Contains("Message: child probe", written, StringComparison.Ordinal);
        Assert.Contains("Full report: " + file, written, StringComparison.Ordinal);
        Assert.EndsWith("==== the crash report ====\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TryShowFatal_InASupervisedChild_WithTheOptionOff_WritesNothing()
    {
        CrashDialog.EnvironmentProbe = static () => Interactive with { OutputRedirected = true };
        CrashDialog.SupervisedChildProbe = static () => true;

        Assert.Equal(CrashDialogOutcome.Disabled, CrashDialog.TryShowFatal(false, CrashKind.Unhandled, new InvalidOperationException("x"), false, "Diagnostics:OnUnhandled = Exit", "unhandled", "id", "report"));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void TryShowFatal_InASupervisedChild_WhenTheFileCannotBeWritten_RecordsTheFailure_AndShowsNothing()
    {
        CrashDialog.EnvironmentProbe = static () => Interactive with { OutputRedirected = true };
        CrashDialog.SupervisedChildProbe = static () => true;
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "not a directory");
        try
        {
            Assert.Equal(CrashDialogOutcome.Failed, CrashDialog.TryShowFatal(true, CrashKind.Unhandled, new InvalidOperationException("x"), false, "Diagnostics:OnUnhandled = Exit", "unhandled", "id", "report"));
            Assert.Empty(_presenter.Shown);
            Assert.Contains(FaultLedger.Shared.Snapshot(), f => f.Site == CrashDialog.FailureSite);
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    [Fact]
    public void TryShowFatal_WhenInteractive_WritesTheReportFile_ThenShowsTheBox()
    {
        CrashDialog.LogFilePath = "/var/log/arcanecore.log";
        Exception exception = Thrown(new InvalidOperationException("fatal probe"));

        CrashDialogOutcome outcome = CrashDialog.TryShowFatal(true, CrashKind.Unhandled, exception, false, "Diagnostics:OnUnhandled = Exit", "unhandled", "feedfacecafebeef", "==== the crash report ====");

        Assert.Equal(CrashDialogOutcome.Shown, outcome);
        (string title, string text) = Assert.Single(_presenter.Shown);
        Assert.EndsWith(" crashed: InvalidOperationException", title, StringComparison.Ordinal);
        Assert.Contains("Message: fatal probe", text, StringComparison.Ordinal);
        Assert.Contains("Fault ledger: site unhandled, signature feedfacecafebeef", text, StringComparison.Ordinal);
        Assert.Contains("Log file: /var/log/arcanecore.log", text, StringComparison.Ordinal);
        Assert.Contains("Thread: " + Environment.CurrentManagedThreadId, text, StringComparison.Ordinal);
        string file = Assert.Single(Directory.GetFiles(_directory, "crash-report.*Z.txt"));
        Assert.Contains("Full report: " + file, text, StringComparison.Ordinal);
        string written = File.ReadAllText(file);
        Assert.Contains("Message: fatal probe", written, StringComparison.Ordinal);
        Assert.EndsWith("==== the crash report ====\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TryShowFatal_WithAThrowingPresenter_RecordsTheFailure_AndNeverThrows()
    {
        CrashDialog.Presenter = new ThrowingPresenter();

        CrashDialogOutcome outcome = CrashDialog.TryShowFatal(true, CrashKind.Unhandled, new InvalidOperationException("x"), true, "Diagnostics:OnUnhandled = FailFast", "unhandled", "id", "report");

        Assert.Equal(CrashDialogOutcome.Failed, outcome);
        FaultSnapshot recorded = Assert.Single(FaultLedger.Shared.Snapshot(), f => f.Site == CrashDialog.FailureSite);
        Assert.Equal(typeof(System.ComponentModel.Win32Exception).FullName, recorded.ExceptionType);
    }

    [Fact]
    public void TryShowFatal_WhenTheReportFileCannotBeWritten_StillShowsTheBox_AndRecordsTheFailure()
    {
        // A file where the directory should be: CreateDirectory fails.
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "not a directory");
        try
        {
            CrashDialogOutcome outcome = CrashDialog.TryShowFatal(true, CrashKind.Unhandled, new InvalidOperationException("x"), false, "Diagnostics:OnUnhandled = Exit", "unhandled", "id", "report");

            Assert.Equal(CrashDialogOutcome.Failed, outcome);
            (_, string text) = Assert.Single(_presenter.Shown);
            Assert.Contains("Full report: (not written: ", text, StringComparison.Ordinal);
            Assert.Contains(FaultLedger.Shared.Snapshot(), f => f.Site == CrashDialog.FailureSite);
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    [Fact]
    public void CrashHandler_ShowFatalDialog_FollowsTheOption()
    {
        CrashHandler handler = CrashHandler.Install(DumpFree.Options(), StandardErrorCrashSink.Instance);
        handler.ShowFatalDialog(CrashKind.Unhandled, new TimeoutException("hang"), failFast: false, "the world tick watchdog", "report");
        Assert.Empty(_presenter.Shown);

        handler.Reconfigure(DumpFree.Options(o => o.CrashDialog = true), StandardErrorCrashSink.Instance, providers: null);
        handler.ShowFatalDialog(CrashKind.Unhandled, new TimeoutException("hang"), failFast: false, "the world tick watchdog", "report");
        (_, string text) = Assert.Single(_presenter.Shown);
        Assert.Contains("exits with code 70 (the world tick watchdog)", text, StringComparison.Ordinal);
        Assert.Contains("Fault ledger: (not recorded)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TryShowFault_ShowsOnlyTheFirstOccurrence_OfANonTerminatingFault_OneAtATime()
    {
        var blocking = new BlockingPresenter();
        CrashDialog.Presenter = blocking;
        FaultNotice first = Notice("aaaaaaaaaaaaaaaa", count: 1);

        Assert.False(CrashDialog.TryShowFault(false, first));
        Assert.False(CrashDialog.TryShowFault(true, first with { Count = 2 }));
        Assert.False(CrashDialog.TryShowFault(true, first with { Terminating = true }));
        Assert.False(CrashDialog.TryShowFault(true, first with { Id = FaultLedger.OverflowId }));
        Assert.False(CrashDialog.TryShowFault(true, first with { Site = CrashDialog.FailureSite }));
        Assert.False(CrashDialog.TryShowFault(true, first with { Site = "diagnostics-crash-dialog" }));

        Assert.True(CrashDialog.TryShowFault(true, first));
        Assert.True(blocking.Opened.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(CrashDialog.TryShowFault(true, Notice("bbbbbbbbbbbbbbbb", count: 1))); // one open already

        blocking.Release.Set();
        Assert.True(SpinWait.SpinUntil(() => CrashDialog.TryShowFault(true, Notice("cccccccccccccccc", count: 1)), TimeSpan.FromSeconds(10)));
        blocking.Release.Set();
        Assert.True(SpinWait.SpinUntil(() => blocking.Count == 2 && blocking.Closed == 2, TimeSpan.FromSeconds(10)));
        Assert.Equal(CrashJournalPaths.ProcessName() + " fault: InvalidOperationException at map.visibility", blocking.Titles[0]);
    }

    [Fact]
    public void TryShowFault_WhenHeadless_ShowsNothing()
    {
        CrashDialog.EnvironmentProbe = static () => Interactive with { IsWindows = false };
        Assert.False(CrashDialog.TryShowFault(true, Notice("aaaaaaaaaaaaaaaa", count: 1)));
        Assert.Empty(_presenter.Shown);
    }

    [Fact]
    public void TryShowFault_APresenterThatThrows_ReachesTheFailureCallback_AndFreesTheSlot()
    {
        CrashDialog.Presenter = new ThrowingPresenter();
        using var failed = new ManualResetEventSlim();
        Exception? seen = null;

        Assert.True(CrashDialog.TryShowFault(true, Notice("aaaaaaaaaaaaaaaa", count: 1), ex =>
        {
            seen = ex;
            failed.Set();
        }));

        Assert.True(failed.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsType<System.ComponentModel.Win32Exception>(seen);
        CrashDialog.Presenter = _presenter;
        Assert.True(SpinWait.SpinUntil(() => CrashDialog.TryShowFault(true, Notice("bbbbbbbbbbbbbbbb", count: 1)), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ComposeFault_NamesTheFault_AndWhereToReadMore()
    {
        CrashDialogText text = CrashDialog.ComposeFault(Notice("0123456789abcdef", count: 1), "ArcaneCore.World", "/logs/world.log");

        Assert.Equal("ArcaneCore.World fault: InvalidOperationException at map.visibility", text.Title);
        Assert.Contains("ArcaneCore.World caught a new fault and kept running.", text.Text, StringComparison.Ordinal);
        Assert.Contains("Exception: System.InvalidOperationException\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Message: probe fault\n", text.Text, StringComparison.Ordinal);
        Assert.Contains("Fault ledger: site map.visibility, signature 0123456789abcdef (.server faults show 0123456789abcdef)", text.Text, StringComparison.Ordinal);
        Assert.Contains("Log file: /logs/world.log", text.Text, StringComparison.Ordinal);
        Assert.Contains("Map.Update(Map.cs:10)\n  <- WorldRuntime.Tick(WorldRuntime.cs:20)", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FaultDialog_Hosted_OpensForANewFault_AndUnsubscribesOnStop()
    {
        var blocking = new BlockingPresenter();
        CrashDialog.Presenter = blocking;
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            Args = ["--Diagnostics:FaultDialog=true"],
        });
        builder.Logging.ClearProviders();
        builder.UseArcaneDiagnostics();

        using IHost host = builder.Build();
        await host.StartAsync();
        try
        {
            FaultLedger.Shared.Record("dialog-test", new InvalidOperationException("hosted probe"));
            Assert.True(blocking.Opened.Wait(TimeSpan.FromSeconds(10)));
            Assert.Contains("hosted probe", blocking.Texts[0], StringComparison.Ordinal);
            blocking.Release.Set();
            Assert.True(SpinWait.SpinUntil(() => blocking.Closed == 1, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await host.StopAsync();
        }

        FaultLedger.Shared.Record("dialog-test-after-stop", new FormatException("after stop"));
        Thread.Sleep(200);
        Assert.Equal(1, blocking.Count);
    }

    [Fact]
    public void ReportPathFor_IsPerDaemonAndTimestamped()
    {
        string path = CrashJournalPaths.ReportPathFor("/srv", "ArcaneCore.World", new DateTime(2026, 10, 27, 8, 30, 5, DateTimeKind.Utc));
        Assert.Equal(Path.Combine("/srv", "crash-report.ArcaneCore.World.20261027-083005Z.txt"), path);
    }

    private static FaultNotice Notice(string id, long count)
        => new(id, "map.visibility", "System.InvalidOperationException", "probe fault", count, 0, Terminating: false,
            CondensedStack: "Map.Update(Map.cs:10) <- WorldRuntime.Tick(WorldRuntime.cs:20)");

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    private static Exception Recurse(int depth)
    {
        try
        {
            Dive(depth);
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Dive(int depth)
    {
        if (depth == 0)
        {
            throw new InvalidOperationException("deep " + new string('x', 400));
        }

        Dive(depth - 1);
    }

    private sealed class RecordingPresenter : ICrashDialogPresenter
    {
        public List<(string Title, string Text)> Shown { get; } = [];

        public void Show(string title, string text)
        {
            lock (Shown)
            {
                Shown.Add((title, text));
            }
        }
    }

    private sealed class ThrowingPresenter : ICrashDialogPresenter
    {
        public void Show(string title, string text) => throw new System.ComponentModel.Win32Exception(1400, "Invalid window handle.");
    }

    /// <summary>Stays "open" until <see cref="Release"/> is set, like a box nobody has closed yet.</summary>
    private sealed class BlockingPresenter : ICrashDialogPresenter
    {
        private int _count;
        private int _closed;

        public ManualResetEventSlim Opened { get; } = new();

        public AutoResetEvent Release { get; } = new(false);

        public List<string> Titles { get; } = [];

        public List<string> Texts { get; } = [];

        public int Count => Volatile.Read(ref _count);

        public int Closed => Volatile.Read(ref _closed);

        public void Show(string title, string text)
        {
            lock (Titles)
            {
                Titles.Add(title);
                Texts.Add(text);
            }

            Interlocked.Increment(ref _count);
            Opened.Set();
            Release.WaitOne(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref _closed);
        }
    }
}

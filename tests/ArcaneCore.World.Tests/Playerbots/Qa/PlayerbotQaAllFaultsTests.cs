using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// Every server exception and fault reaches QA: a Warning with an exception, an Error of a framework category, repeated first-chance exceptions, unobserved task exceptions,
/// a hung world thread; one recurring fault is one finding; a fault the ledger recorded is not filed twice; server faults are not starved by the bot cap.
/// </summary>
public sealed class PlayerbotQaAllFaultsTests : IDisposable
{
    private const string TokenVariable = "ARCANECORE_GITHUB_TOKEN";
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception Thrower(string message = "boom")
    {
        try { throw new InvalidOperationException(message); } catch (Exception ex) { return ex; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception OtherThrower()
    {
        try { throw new InvalidOperationException("boom"); } catch (Exception ex) { return ex; }
    }

    private static List<QaSignal> Drained(PlayerbotQaSignalBus bus)
    {
        var list = new List<QaSignal>();
        bus.Drain(list, 1000);
        return list;
    }

    // ----- log tap ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AWarningWithAnException_BecomesAServerFaultSignal_AndAPlainWarningDoesNot()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);
        ILogger pets = tap.CreateLogger("ArcaneCore.World.Pets.PetsFeature");

        pets.LogWarning("a plain warning {Id}", 5);
        Assert.Equal(0, bus.Received);

        Exception ex = Thrower();
        pets.LogWarning(ex, "persistent hunter pet save failed for character {Character}", 7872);

        QaSignal signal = Assert.Single(Drained(bus));
        Assert.Equal(QaSignalKind.ServerFault, signal.Kind);
        Assert.Equal(typeof(InvalidOperationException).FullName, signal.ExceptionType);
        Assert.Equal(PlayerbotQaFaultIds.Describe(ex, out _, out _, out _), signal.FaultId);
        Assert.Equal(string.Empty, signal.Message); // a fault never carries the log text
    }

    [Fact]
    public void AnErrorOfAFrameworkCategory_AndAWarningWithAnExceptionOfOne_BecomeSignals()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);
        ILogger ef = tap.CreateLogger("Microsoft.EntityFrameworkCore.Update");
        ILogger system = tap.CreateLogger("System.Net.Http.HttpClient.Github");

        Assert.True(ef.IsEnabled(LogLevel.Warning));
        Assert.False(ef.IsEnabled(LogLevel.Information));
        ef.LogWarning("a plain framework warning");
        ef.LogError(Thrower("db"), "An exception occurred in the database while saving changes for context type '{Context}'.", "CharacterDbContext");
        system.LogWarning(OtherThrower(), "request failed");
        ef.LogError("Something broke without an exception in {Place}", "here");

        List<QaSignal> signals = Drained(bus);
        Assert.Equal(3, signals.Count);
        Assert.All(signals, s => Assert.Equal(QaSignalKind.ServerFault, s.Kind));
        Assert.Contains(signals, s => s.Goal == "log:Update" && s.ExceptionType == typeof(InvalidOperationException).FullName);
        Assert.Contains(signals, s => s.Goal == "log:Github" && s.ExceptionType == typeof(InvalidOperationException).FullName);
        Assert.Contains(signals, s => s.ExceptionType == "LogError");
    }

    [Fact]
    public void TheSameFaultLoggedByTwoCategories_IsOneFinding_WithTheTypeAndTopFrameAsItsIdentity()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);
        Exception first = Thrower("one");
        Exception second = Thrower("two (a different message)");
        tap.CreateLogger("ArcaneCore.World.Pets.PetsFeature").LogWarning(first, "x");
        tap.CreateLogger("Microsoft.EntityFrameworkCore.Update").LogError(second, "y");
        Exception elsewhere = OtherThrower();
        tap.CreateLogger("ArcaneCore.World.Pets.PetsFeature").LogWarning(elsewhere, "z");

        List<QaSignal> signals = Drained(bus);
        Assert.Equal(signals[0].FaultId, signals[1].FaultId);
        Assert.NotEqual(signals[0].FaultId, signals[2].FaultId); // another throw site is another fault

        var aggregator = new PlayerbotQaAggregator(100, 5);
        foreach (QaSignal s in signals) aggregator.Ingest(s, new FakeFacts(), QaData.T0);
        Assert.Equal(2, aggregator.Snapshot().Count);
        Assert.Equal(2, aggregator.Snapshot().Max(f => f.Count));
    }

    [Fact]
    public void AFaultAlreadyRecordedInTheLedger_IsNotReportedAgain_AtWarningOrError()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);
        var ledger = new FaultLedger();
        Exception recorded = Thrower();
        ledger.Record("pet.persistence", recorded);
        ILogger pets = tap.CreateLogger("ArcaneCore.World.Pets.PetsFeature");

        pets.LogWarning(recorded, "warn after recording");
        pets.LogError(recorded, "error after recording");
        pets.LogError("line from the ledger's own logger [fault=0123456789abcdef count=1 stack=x]");

        Assert.Equal(0, bus.Received);
    }

    [Fact]
    public void ADisabledLogExceptionsOption_KeepsTheTapToArcaneCoreErrors()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null, new PlayerbotQaFaultOptions { LogExceptions = false });

        Assert.IsType<NullLogger>(tap.CreateLogger("Microsoft.EntityFrameworkCore.Update"));
        ILogger pets = tap.CreateLogger("ArcaneCore.World.Pets.PetsFeature");
        Assert.False(pets.IsEnabled(LogLevel.Warning));
        Assert.True(pets.IsEnabled(LogLevel.Error));
        Assert.IsType<NullLogger>(tap.CreateLogger("ArcaneCore.Diagnostics.Crash"));
        Assert.IsType<NullLogger>(tap.CreateLogger("ArcaneCore.World.Playerbots.Qa.PlayerbotQaFeature"));
    }

    [Fact]
    public void TheQaAndDiagnosticsCategories_AreNeverTapped_EvenWithAnException()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);

        tap.CreateLogger("ArcaneCore.World.Playerbots.Qa.PlayerbotQaPublisher").LogError(Thrower(), "qa failed");
        tap.CreateLogger("ArcaneCore.Diagnostics.Crash").LogError(Thrower(), "crash report");

        Assert.Equal(0, bus.Received);
    }

    [Fact]
    public void ALoggedSqlStatement_KeepsItsShape_AndNeverItsValues()
    {
        var bus = new PlayerbotQaSignalBus(16);
        using var tap = new PlayerbotQaLogTap(bus, true, null, null);
        ILogger ef = tap.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        ef.LogError("Failed executing DbCommand (5ms) [Parameters=[@p0='Huzond' (Size = 6), @p1='42'], CommandType='Text'] UPDATE characters SET name = 'Huzond', level = 60 WHERE guid = 42");

        QaSignal signal = Assert.Single(Drained(bus));
        Assert.DoesNotContain("Huzond", signal.Diag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42", signal.Diag, StringComparison.Ordinal);
        Assert.DoesNotContain("60", signal.Diag, StringComparison.Ordinal);
        Assert.Contains("update characters set name = '?', level = ? where guid = ?", signal.Diag, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripSqlValues_ReplacesParametersAndLiterals_AndLeavesOtherTextAlone()
    {
        string stripped = PlayerbotQaRedactor.StripSqlValues(
            "[Parameters=[@p8='?' (DbType = Int32), @p0='Zorblax' (Size = 7), @p1='it''s'], CommandType='Text'] INSERT INTO items (guid, owner) VALUES (12, 'Zorblax')");
        Assert.DoesNotContain("Zorblax", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("it''s", stripped, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO items (guid, owner) VALUES (?, '?')", stripped, StringComparison.Ordinal);
        Assert.Equal("the queue retained 3 writes for guild 'Alpha'", PlayerbotQaRedactor.StripSqlValues("the queue retained 3 writes for guild 'Alpha'"));
        Assert.Equal(string.Empty, PlayerbotQaRedactor.StripSqlValues(null));

        string fault = new PlayerbotQaRedactor().RedactFault("UPDATE characters SET name = 'Huzond' WHERE guid = 42");
        Assert.DoesNotContain("Huzond", fault, StringComparison.Ordinal);
    }

    // ----- first-chance, unobserved and unhandled -----------------------------------------------------------------------------------------------

    private static PlayerbotQaFaultOptions Faults(int repeats = 3, string[]? deny = null)
        => new() { FirstChanceMinRepeats = repeats, FirstChanceWindowSeconds = 600, FirstChanceDenylist = deny ?? [.. PlayerbotQaFaultOptions.DefaultFirstChanceDenylist] };

    [Fact]
    public void RepeatedFirstChanceExceptions_GroupIntoOneFingerprint_ReportedOnTheThirdThrow_AndGrowByFlush()
    {
        var bus = new PlayerbotQaSignalBus(64);
        var time = new ManualTime(QaData.T0);
        var watch = new PlayerbotQaFirstChanceWatch(bus, Faults(), time);

        for (int i = 0; i < 2; i++) watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        Assert.Equal(0, bus.Received); // handled and rare: not an issue yet

        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        QaSignal first = Assert.Single(Drained(bus));
        Assert.Equal(3, first.Occurrences);
        Assert.Equal("first-chance", first.Goal);
        Assert.Equal(PlayerbotQaFaultIds.Of(typeof(InvalidOperationException).FullName!, "Combat.Resolve"), first.FaultId);

        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        watch.ObserveFirstChance(Thrower(), "Spells.Cast"); // another site: its own group, below the threshold
        Assert.Equal(1, watch.Flush());
        QaSignal more = Assert.Single(Drained(bus));
        Assert.Equal(2, more.Occurrences);
        Assert.Equal(first.FaultId, more.FaultId);
        Assert.Equal(0, watch.Flush());

        var aggregator = new PlayerbotQaAggregator(100, 5);
        aggregator.Ingest(first, new FakeFacts(), QaData.T0);
        aggregator.Ingest(more, new FakeFacts(), QaData.T0);
        Assert.Equal(5, Assert.Single(aggregator.Snapshot()).Count);
    }

    [Fact]
    public void FirstChanceThrows_SpreadOverMoreThanTheWindow_NeverReachTheThreshold()
    {
        var bus = new PlayerbotQaSignalBus(64);
        var time = new ManualTime(QaData.T0);
        var watch = new PlayerbotQaFirstChanceWatch(bus, Faults(repeats: 3), time);

        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        time.Advance(TimeSpan.FromSeconds(601));
        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");

        Assert.Equal(0, bus.Received);
    }

    [Fact]
    public void ADenylistedFirstChanceType_DoesNotReport_NotEvenWhenDerivedOrWrapped()
    {
        var bus = new PlayerbotQaSignalBus(64);
        var watch = new PlayerbotQaFirstChanceWatch(bus, Faults(repeats: 2));

        for (int i = 0; i < 10; i++)
        {
            watch.ObserveFirstChance(new OperationCanceledException(), "Net.Read");
            watch.ObserveFirstChance(new TaskCanceledException(), "Net.Read"); // derived from a listed base
            watch.ObserveFirstChance(new System.Net.Sockets.SocketException(10054), "Net.Read"); // a client that reset
            watch.ObserveFirstChance(new IOException("wrapped", new System.Net.Sockets.SocketException(10053)), "Net.Read");
        }

        Assert.Equal(0, bus.Received);
        Assert.Equal(40, watch.Denied);

        // The same four throws are reported once the default list is replaced by an empty one.
        var open = new PlayerbotQaFirstChanceWatch(bus, Faults(repeats: 2, deny: []));
        open.ObserveFirstChance(new System.Net.Sockets.SocketException(10054), "Net.Read");
        open.ObserveFirstChance(new System.Net.Sockets.SocketException(10054), "Net.Read");
        Assert.Equal(1, bus.Received);
    }

    [Fact]
    public void ADenylistEntryWithASite_DeniesTheTypeOnlyThere()
    {
        var bus = new PlayerbotQaSignalBus(64);
        var watch = new PlayerbotQaFirstChanceWatch(bus, Faults(repeats: 2, deny: ["System.InvalidOperationException@Shutdown."]));

        watch.ObserveFirstChance(Thrower(), "Shutdown.Drain");
        watch.ObserveFirstChance(Thrower(), "Shutdown.Drain");
        Assert.Equal(0, bus.Received);

        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        watch.ObserveFirstChance(Thrower(), "Combat.Resolve");
        Assert.Equal(1, bus.Received);
    }

    [Fact]
    public void TheObserverInterface_FindsTheThrowSiteFromTheStack_AndGroupsOneCallSite()
    {
        var bus = new PlayerbotQaSignalBus(64);
        IFaultObserver watch = new PlayerbotQaFirstChanceWatch(bus, Faults(repeats: 4));

        for (int i = 0; i < 4; i++) watch.Observe(FaultObservation.FirstChance, Thrower(), ledgerRecorded: false);

        QaSignal signal = Assert.Single(Drained(bus));
        Assert.Equal(4, signal.Occurrences);
        Assert.Contains(nameof(PlayerbotQaAllFaultsTests), signal.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PlayerbotQaFirstChanceWatch), signal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnobservedTaskException_AlwaysReports_EvenOfADenylistedType_AndAnUnhandledOneToo()
    {
        var bus = new PlayerbotQaSignalBus(64);
        IFaultObserver watch = new PlayerbotQaFirstChanceWatch(bus, Faults(repeats: 1000));

        Exception cancelled = ThrowCancelled();
        watch.Observe(FaultObservation.UnobservedTask, cancelled, ledgerRecorded: false);
        watch.Observe(FaultObservation.Unhandled, Thrower(), ledgerRecorded: false);

        List<QaSignal> signals = Drained(bus);
        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s.Goal == "unobserved-task" && s.ExceptionType == typeof(TaskCanceledException).FullName && s.Occurrences == 1);
        Assert.Contains(signals, s => s.Goal == "unhandled" && s.ExceptionType == typeof(InvalidOperationException).FullName);
        Assert.All(signals, s => Assert.Equal(QaSignalKind.ServerFault, s.Kind));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowCancelled()
    {
        try { throw new TaskCanceledException("gone"); } catch (Exception ex) { return ex; }
    }

    [Fact]
    public void AnUnobservedException_TheLedgerRecorded_IsNotReportedTwice_ButTheLedgerPathStillFilesIt()
    {
        var bus = new PlayerbotQaSignalBus(64);
        IFaultObserver watch = new PlayerbotQaFirstChanceWatch(bus, Faults());
        var ledger = new FaultLedger();
        Exception ex = Thrower();
        ledger.Record("unobserved-task", ex);

        watch.Observe(FaultObservation.UnobservedTask, ex, ledgerRecorded: true);

        Assert.Equal(0, bus.Received);
        QaSignal fromLedger = Assert.Single(new PlayerbotQaFaultFeed(ledger).Pull());
        Assert.Equal("unobserved-task", fromLedger.Goal);
    }

    [Fact]
    public void TheObserverRegistry_DeliversToRegisteredObservers_AndStopsAfterDispose()
    {
        var seen = new List<FaultObservation>();
        IDisposable registration = FaultObservers.Add(new Recorder(seen));
        try
        {
            FaultObservers.Notify(FaultObservation.UnobservedTask, Thrower(), ledgerRecorded: false);
        }
        finally
        {
            registration.Dispose();
        }

        FaultObservers.Notify(FaultObservation.UnobservedTask, Thrower(), ledgerRecorded: false);
        Assert.Equal([FaultObservation.UnobservedTask], seen);
    }

    private sealed class Recorder(List<FaultObservation> seen) : IFaultObserver
    {
        public void Observe(FaultObservation kind, Exception exception, bool ledgerRecorded) => seen.Add(kind);
    }

    // ----- world hang ------------------------------------------------------------------------------------------------------------------------

    private const string StackReport = """
        # 2026-10-24 13:34:33 UTC, process 29880, dotnet-stack exit 0
        # world tick running for 2336 ms (hang threshold 2000 ms)
        # The world thread is the one inside ArcaneCore.Game.Maps.WorldRuntime.RunTick; the capture starts when the hang is first reported.
        Thread (0xEC4C):
          [Native Frames]
          System.Private.CoreLib.il!System.Threading.WaitHandle.WaitForMultipleObjectsIgnoringSyncContext(int*,int32,bool,int32,bool)
          ArcaneCore.World!Program.<Main>(class System.String[])

        Thread (0x5880):
          [Native Frames]
          System.Private.CoreLib.il!System.Threading.Thread.PollGC()
          ArcaneCore.Game!ArcaneCore.Game.Spells.SpellSystem.UpdateAuras(class ArcaneCore.Game.Spells.UnitSpellState,unsigned int32)
          ArcaneCore.Game!ArcaneCore.Game.Spells.SpellSystem.<UpdateState>g__Local|3_0(unsigned int32)
          ArcaneCore.Game!ArcaneCore.Game.Spells.SpellSystem.Update(unsigned int32)
          ArcaneCore.World!ArcaneCore.World.Spells.SpellFeature.Update()
          ArcaneCore.World!ArcaneCore.World.Spells.SpellFeature.OnWorldUpdated(unsigned int32)
          ArcaneCore.Game!ArcaneCore.Game.Maps.WorldRuntime.RunTick(unsigned int32,class System.Action`2<class ArcaneCore.Game.Maps.Map,class ArcaneCore.Game.Maps.MapUpdateDiagnostics>)
          ArcaneCore.Game!ArcaneCore.Game.Maps.WorldRuntime.Run()

        Thread (0x15740):
          [Native Frames]
          ArcaneCore.Game!ArcaneCore.Game.Maps.MapUpdateScheduler.WorkerLoop()
        """;

    [Fact]
    public void TheWorldThreadsTopArcaneCoreFrames_AreReadFromAStackReport_NotFromItsHeader()
    {
        IReadOnlyList<string> frames = HangStackCapture.WorldThreadFrames(StackReport, 4);

        Assert.Equal(["SpellSystem.UpdateAuras", "SpellSystem.UpdateState", "SpellSystem.Update", "SpellFeature.Update"], frames);
        Assert.Empty(HangStackCapture.WorldThreadFrames("Thread (0x1):\n  ArcaneCore.Game!ArcaneCore.Game.Maps.MapUpdateScheduler.WorkerLoop()\n", 4));
        Assert.Empty(HangStackCapture.WorldThreadFrames(string.Empty, 4));
    }

    private sealed class ReportRunner(string output) : IHangStackRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout) => Task.FromResult((0, output));
    }

    [Fact]
    public async Task AFinishedHangCapture_BecomesAWorldHangSignal_FingerprintedByTheFramesNotTheTickLength()
    {
        var options = new TickMonitorOptions { HangMs = 2000, StackCaptureTool = "tool", StackCaptureDirectory = _directory, StackCaptureIntervalSeconds = 10 };
        var capture = new HangStackCapture(options, NullLogger.Instance, new ReportRunner(StackReport), fileExists: _ => true, processId: 1);
        var reports = new List<HangStackReport>();
        capture.Captured = reports.Add;

        Assert.True(capture.TryCapture(0, "hang", 2336));
        await capture.Last;
        Assert.True(capture.TryCapture(20_000_000, "hang", 9000));
        await capture.Last;

        Assert.Equal(2, reports.Count);
        Assert.Equal(2336, reports[0].RunningMs);
        QaSignal one = PlayerbotQaFeature.WorldHangSignal(reports[0].RunningMs, reports[0].WorldThreadFrames, QaData.T0);
        QaSignal two = PlayerbotQaFeature.WorldHangSignal(reports[1].RunningMs, reports[1].WorldThreadFrames, QaData.T0);
        Assert.Equal(QaSignalKind.ServerFault, one.Kind);
        Assert.Equal("server", one.Goal);
        Assert.Equal("world-hang", one.ExceptionType);
        Assert.Contains("2336 ms", one.Diag, StringComparison.Ordinal);
        Assert.Contains("SpellSystem.UpdateAuras", one.Detail, StringComparison.Ordinal);
        Assert.Equal(PlayerbotQaFingerprint.Of(one), PlayerbotQaFingerprint.Of(two));
        QaSignal elsewhere = PlayerbotQaFeature.WorldHangSignal(2336, ["MapUpdateScheduler.WorkerLoop"], QaData.T0);
        Assert.NotEqual(PlayerbotQaFingerprint.Of(one), PlayerbotQaFingerprint.Of(elsewhere));

        var aggregator = new PlayerbotQaAggregator(100, 5);
        aggregator.Ingest(one, new FakeFacts(), QaData.T0);
        aggregator.Ingest(two, new FakeFacts(), QaData.T0);
        QaFindingView view = Assert.Single(aggregator.Snapshot());
        Assert.Equal(2, view.Count);
        Assert.Equal("server: world-hang", view.Subject);
        Assert.Equal(QaCause.ServerFault, view.Cause);
    }

    // ----- the ledger path and the publisher -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRetryAttemptsOfAWrite_StayInTheLedger_ButOnlyTheWritesOwnFaultIsFiled()
    {
        var ledger = new FaultLedger();
        Exception busy = Thrower("database is locked");
        ledger.Record("writequeue.keyed-attempt", busy);
        ledger.Record("writequeue.keyed-attempt", busy);
        ledger.Record("writequeue.keyed", busy);

        Assert.Equal(2, ledger.Distinct);
        QaSignal signal = Assert.Single(new PlayerbotQaFaultFeed(ledger).Pull());
        Assert.Equal("writequeue.keyed", signal.Goal);
    }

    private PlayerbotQaPublisher NewPublisher(PlayerbotQaOptions options, IQaPublicationState state, FakeGitHub github, ManualTime time)
        => new(options, _directory, state, new PlayerbotQaRedactor(), new ListLogger(),
            name => name == TokenVariable ? QaData.FakeToken : null,
            t => new PlayerbotQaGitHubClient(github.Http, QaData.Repo, t, options.GitHub, (_, _) => Task.CompletedTask, null, () => time.GetUtcNow()),
            time, "test-build", null, (_, _) => Task.CompletedTask);

    private static List<string> CreatedBodies(FakeGitHub github)
        => [.. github.Http.Requests
            .Where(r => r.Method == HttpMethod.Post && r.PathAndQuery.Split('?')[0].EndsWith("/issues", StringComparison.Ordinal))
            .Select(r => (string)JsonNode.Parse(r.Body)!["body"]!)];

    [Fact]
    public async Task WithTheBotCapFull_AServerFaultIsStillFiled_AheadOfTheBotFindings()
    {
        const string stall = "aaaaaaaaaaaaaaaa";
        const string fault = "bbbbbbbbbbbbbbbb";
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, Publish = true, MinOccurrences = 3, MinDistinctBots = 2, MaxIssuesPerHour = 1, MaxServerFaultIssuesPerHour = 1 } };
        var time = new ManualTime(QaData.T0);
        var github = new FakeGitHub();
        var state = new FakeQaState(
            QaData.View(stall, severity: 300, count: 50, bots: 20),
            QaData.View(fault, severity: 60, count: 1, bots: 0, kind: QaSignalKind.ServerFault));
        PlayerbotQaPublisher publisher = NewPublisher(options, state, github, time);
        publisher.Ledger.RecordCreate(time.GetUtcNow().UtcDateTime); // the bot cap is already used up this hour

        await publisher.RunAsync(state.All, CancellationToken.None);

        string body = Assert.Single(CreatedBodies(github));
        Assert.Contains(fault, body, StringComparison.Ordinal);
        Assert.Equal(QaPublicationState.Published, state.Get(fault)!.Publication.State);
        Assert.Equal(QaPublicationState.Pending, state.Get(stall)!.Publication.State);
    }

    [Fact]
    public async Task ServerFaults_AreFiledFirst_WhenBothLanesHaveRoom_AndHaveTheirOwnHourlyCap()
    {
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, Publish = true, MinOccurrences = 3, MinDistinctBots = 2, MaxIssuesPerHour = 1, MaxServerFaultIssuesPerHour = 1 } };
        var time = new ManualTime(QaData.T0);
        var github = new FakeGitHub();
        var state = new FakeQaState(
            QaData.View("aaaaaaaaaaaaaaaa", severity: 300, count: 50, bots: 20),
            QaData.View("bbbbbbbbbbbbbbbb", severity: 60, count: 1, bots: 0, kind: QaSignalKind.ServerFault),
            QaData.View("cccccccccccccccc", severity: 61, count: 1, bots: 0, kind: QaSignalKind.ServerFault));
        PlayerbotQaPublisher publisher = NewPublisher(options, state, github, time);

        await publisher.RunAsync(state.All, CancellationToken.None);

        List<string> bodies = CreatedBodies(github);
        Assert.Equal(2, bodies.Count); // one per lane
        Assert.Contains("cccccccccccccccc", bodies[0], StringComparison.Ordinal); // the server lane first, highest severity first
        Assert.Contains("aaaaaaaaaaaaaaaa", bodies[1], StringComparison.Ordinal);
        Assert.Equal(QaPublicationState.Pending, state.Get("bbbbbbbbbbbbbbbb")!.Publication.State); // the server cap (1) is spent

        options.GitHub.MaxServerFaultIssuesPerHour = 0;
        var zero = new FakeQaState(QaData.View("dddddddddddddddd", count: 1, bots: 0, kind: QaSignalKind.ServerFault));
        var github2 = new FakeGitHub();
        await NewPublisher(options, zero, github2, time).RunAsync(zero.All, CancellationToken.None);
        Assert.Empty(CreatedBodies(github2));
    }

    [Fact]
    public async Task ALedgerRecordedFailLevelFault_ReachesAFinding_AndIsFiledEvenWhenBotFindingsFillTheCap()
    {
        // The live failure: KeyedStoreWriteQueue / PetsFeature / CreatureRespawnQueue faults were ledger-recorded and became findings, then sat Pending for days behind
        // bot findings (severity 190-360 against 107-125) that used the 5 issues an hour.
        var ledger = new FaultLedger();
        Exception sqlite = Thrower("database is locked (in-process write gate)");
        ledger.Record("writequeue.keyed", sqlite);
        ledger.Record("pet.persistence", OtherThrower());
        var aggregator = new PlayerbotQaAggregator(100, 5);
        foreach (QaSignal s in new PlayerbotQaFaultFeed(ledger).Pull()) aggregator.Ingest(s, new FakeFacts(), QaData.T0);
        QaFindingView[] faults = [.. aggregator.Snapshot()];
        Assert.Equal(2, faults.Length);

        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, Publish = true, MinOccurrences = 3, MinDistinctBots = 2, MaxIssuesPerHour = 5 } };
        var time = new ManualTime(QaData.T0);
        var github = new FakeGitHub();
        QaFindingView[] bots = [.. Enumerable.Range(0, 8).Select(i => QaData.View(i.ToString("x16"), severity: 300 + i, count: 500, bots: 20))];
        var state = new FakeQaState([.. bots, .. faults]);
        PlayerbotQaPublisher publisher = NewPublisher(options, state, github, time);
        for (int i = 0; i < 5; i++) publisher.Ledger.RecordCreate(time.GetUtcNow().UtcDateTime);

        await publisher.RunAsync(state.All, CancellationToken.None);

        Assert.Equal(2, CreatedBodies(github).Count);
        Assert.All(faults, f => Assert.Equal(QaPublicationState.Published, state.Get(f.Id)!.Publication.State));
        Assert.All(bots, b => Assert.Equal(QaPublicationState.Pending, state.Get(b.Id)!.Publication.State));
    }

    [Fact]
    public void ThePendingFindingsAndBothCapLedgers_SurviveARestart()
    {
        var store = new PlayerbotQaStore(_directory, new ListLogger());
        var pending = QaData.View("bbbbbbbbbbbbbbbb", count: 4, bots: 0, kind: QaSignalKind.ServerFault);
        var ledger = new QaLedgerState([QaData.T0], [], null, null, [QaData.T0.AddMinutes(1)]);
        Assert.True(store.Save(new QaStoreFile { SaltBase64 = Convert.ToBase64String([1, 2, 3, 4]), Findings = [pending], Bots = new Dictionary<string, ulong[]>(), Ledger = ledger }));

        QaStoreFile loaded = new PlayerbotQaStore(_directory, new ListLogger()).Load()!;

        Assert.Equal(QaPublicationState.Pending, Assert.Single(loaded.Findings).Publication.State);
        Assert.Equal([QaData.T0.AddMinutes(1)], loaded.Ledger.ServerCreatesUtc);
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, MaxServerFaultIssuesPerHour = 1 } };
        PlayerbotQaPublisher publisher = NewPublisher(options, new FakeQaState(), new FakeGitHub(), new ManualTime(QaData.T0.AddMinutes(2)));
        publisher.Ledger.Restore(loaded.Ledger);
        Assert.False(publisher.Ledger.CanCreateServer(QaData.T0.AddMinutes(2), 1)); // the restart did not hand back the hour's server budget
        Assert.True(publisher.Ledger.CanCreate(QaData.T0.AddMinutes(2), 5));
    }

    // ----- options ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFaultOptions_DefaultOn_Validate_AndAConfiguredDenylistReplacesTheDefault()
    {
        var options = new PlayerbotQaOptions();
        Assert.True(options.Faults.LogExceptions);
        Assert.True(options.Faults.FirstChanceEnabled);
        Assert.True(options.Faults.WorldHangEnabled);
        Assert.Equal(5, options.Faults.FirstChanceMinRepeats);
        Assert.Equal(600, options.Faults.FirstChanceWindowSeconds);
        Assert.Equal(5, options.GitHub.MaxServerFaultIssuesPerHour);
        Assert.Contains("System.OperationCanceledException", options.Faults.FirstChanceDenylist);
        Assert.Contains("System.Net.Sockets.SocketException", options.Faults.FirstChanceDenylist);
        Assert.Empty(options.Validate());

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Playerbots:Qa:Faults:FirstChanceDenylist:0"] = "System.TimeoutException",
            ["World:Playerbots:Qa:Faults:FirstChanceMinRepeats"] = "9",
        }).Build();
        var bound = new PlayerbotQaOptions();
        PlayerbotQaOptions.ApplyConfiguration(bound, configuration);
        Assert.Equal(["System.TimeoutException"], bound.Faults.FirstChanceDenylist);
        Assert.Equal(9, bound.Faults.FirstChanceMinRepeats);

        options.Faults.FirstChanceMinRepeats = 1;
        options.GitHub.MaxServerFaultIssuesPerHour = -1;
        Assert.Equal(2, options.Validate().Count);
    }
}

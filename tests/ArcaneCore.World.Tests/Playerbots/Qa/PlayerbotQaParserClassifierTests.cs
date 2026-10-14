using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Honor;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// Verifies that the <c>; diag: ...</c> diagnostic snapshot suffix is emitted by <see cref="PlayerbotTraceLog"/> and
/// <see cref="ManagedPlayerbotFeature"/>, extracted cleanly by <see cref="PlayerbotQaSignalParser"/> without polluting
/// <see cref="QaSignal.Goal"/>, <see cref="QaSignal.Detail"/>, or <see cref="QaSignal.Message"/>, propagated through
/// <see cref="PlayerbotQaAggregator"/>, and rendered under <c>## Evidence</c> by <see cref="PlayerbotQaIssueComposer"/>.
/// </summary>
public sealed class PlayerbotQaParserClassifierTests
{
    private const string SampleQaContext =
        " qa[level=12,race=Human,team=Alliance,class=Warrior,goal=Quest,quest=1656,graveyard=0,distance=unknown,source=None,attackerFacts=none]";

    private const string SampleDiag =
        "phase=quest-destinations pos=(-5608.8,-439.4,401.2) map=0 zone=1 moveFlags=None motion=moving:no,wp:0/0,dest:none,loops:1 home=none trace=[-14s:goal=Quest/6747/1656 -> -2s:loop-consumed]";

    [Fact]
    public void Stall_WithAndWithoutDiag_ExtractsDiagWithoutPollutingGoalOrMessage()
    {
        var log = new CapturingLogger();
        const string stallReport = "stalled 120s: goal=Quest target=6747 quest=1656 at -5608.8,-439.4,401.2 map 0";

        // Without diag
        ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Ironwander", 1, 0, stallReport,
            outsideHome: false, everyone: null, SampleQaContext, diag: null);
        QaSignal withoutDiag = QaData.Parse(Assert.Single(log.Lines));
        Assert.Equal(QaSignalKind.Stall, withoutDiag.Kind);
        Assert.Equal("Quest", withoutDiag.Goal);
        Assert.Equal(6747u, withoutDiag.Target);
        Assert.Equal(1656u, withoutDiag.Quest);
        Assert.False(withoutDiag.OutsideHome);
        Assert.Null(withoutDiag.Diag);

        // With diag and return-trip
        log.Lines.Clear();
        ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Ironwander", 2, 1, stallReport,
            outsideHome: false, everyone: null, PlayerbotQaSignalParser.ReturnTripToken + SampleQaContext, diag: SampleDiag);
        QaSignal withDiag = QaData.Parse(Assert.Single(log.Lines));
        Assert.Equal(QaSignalKind.Stall, withDiag.Kind);
        Assert.Equal("Quest", withDiag.Goal);
        Assert.Equal(6747u, withDiag.Target);
        Assert.Equal(1656u, withDiag.Quest);
        Assert.False(withDiag.OutsideHome);
        Assert.True(withDiag.ReturnTrip);
        Assert.NotNull(withDiag.Bot);
        Assert.Equal(SampleDiag, withDiag.Diag);
        Assert.DoesNotContain("; diag:", withDiag.Message, StringComparison.Ordinal);
        Assert.EndsWith("; it gives up that goal", withDiag.Message, StringComparison.Ordinal);

        // Outside home with diag: OutsideHomeSuffix detection still works after stripping ; diag:
        log.Lines.Clear();
        ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Ironwander", 3, 2, stallReport,
            outsideHome: true, everyone: null, SampleQaContext, diag: SampleDiag);
        QaSignal outsideWithDiag = QaData.Parse(Assert.Single(log.Lines));
        Assert.Equal(QaSignalKind.Stall, outsideWithDiag.Kind);
        Assert.True(outsideWithDiag.OutsideHome);
        Assert.Equal(SampleDiag, outsideWithDiag.Diag);
        Assert.EndsWith(PlayerbotQaSignalParser.OutsideHomeSuffix, outsideWithDiag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeathLoop_And_UnreachableGoal_WithAndWithoutDiag_ExtractDiagCleanly()
    {
        var log = new CapturingLogger();
        const string deathReport = "death loop: died 3 times in 99s within 60 yd of -5605.6,155.8,453.3 map 0 attackers=16422/2000";
        ManagedPlayerbotFeature.LogStall(log, QaData.Bot2, "Dawnrover", 1, 0, deathReport,
            outsideHome: false, everyone: null, SampleQaContext, diag: SampleDiag);

        QaSignal loop = QaData.Parse(Assert.Single(log.Lines));
        Assert.Equal(QaSignalKind.DeathLoop, loop.Kind);
        Assert.Equal(3, loop.Count);
        Assert.Equal(99, loop.Seconds);
        Assert.Equal(new uint[] { 2000, 16422 }, loop.Attackers);
        Assert.Equal("Quest", loop.Goal);
        Assert.Equal(SampleDiag, loop.Diag);
        Assert.DoesNotContain("; diag:", loop.Message, StringComparison.Ordinal);

        log.Lines.Clear();
        const string unreachableReport =
            "goal unreachable on foot: goal=Quest target=257 quest=114 at -9462.5,64.2,56.0 map 0 components=1/4";
        ManagedPlayerbotFeature.LogUnreachable(log, QaData.Bot1, "Wfbotcg", unreachableReport, SampleQaContext, SampleDiag);

        QaSignal unreachable = QaData.Parse(Assert.Single(log.Lines));
        Assert.Equal(QaSignalKind.UnreachableGoal, unreachable.Kind);
        Assert.Equal("Quest", unreachable.Goal);
        Assert.Equal(257u, unreachable.Target);
        Assert.Equal(114u, unreachable.Quest);
        Assert.Equal("components 1/4", unreachable.Detail);
        Assert.Equal(SampleDiag, unreachable.Diag);
        Assert.DoesNotContain("; diag:", unreachable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionFault_WithAndWithoutDiag_ExtractsDiagAndPreservesDetailAndFaultCode()
    {
        const string faultDiag =
            "phase=recovery-update pos=(10.0,20.0,30.0) map=0 zone=12 moveFlags=None stack=[at PlayerbotRecovery.Update(PlayerbotRecovery.cs:88)]";

        // Format 1: diag embedded inside {Message} (as logged by ManagedPlayerbotFeature.Fault)
        string lineInner =
            $"Playerbot {QaData.Bot1} action failed (InvalidOperationException: playerbot-corpse-reclaim-distance: body is 61.2 yd away; diag: {faultDiag})";
        QaSignal s1 = QaData.Parse(lineInner);
        Assert.Equal(QaSignalKind.ActionFault, s1.Kind);
        Assert.Equal("action", s1.Goal);
        Assert.Equal("InvalidOperationException", s1.ExceptionType);
        Assert.Equal("playerbot-corpse-reclaim-distance", s1.FaultCode);
        Assert.Equal("playerbot-corpse-reclaim-distance: body is 61.2 yd away", s1.Detail);
        Assert.Equal(faultDiag, s1.Diag);
        Assert.DoesNotContain("; diag:", s1.Message, StringComparison.Ordinal);

        // Format 2: diag embedded inside {Message} with "(3 more since)" suffix
        string lineSuppressed =
            $"Playerbot {QaData.Bot1} action failed (InvalidOperationException: playerbot-corpse-reclaim-distance: body is 61.2 yd away; diag: {faultDiag}) (3 more since)";
        QaSignal s2 = QaData.Parse(lineSuppressed);
        Assert.Equal(QaSignalKind.ActionFault, s2.Kind);
        Assert.Equal("playerbot-corpse-reclaim-distance", s2.FaultCode);
        Assert.Equal("playerbot-corpse-reclaim-distance: body is 61.2 yd away", s2.Detail);
        Assert.Equal(faultDiag, s2.Diag);
        Assert.EndsWith("(3 more since)", s2.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("; diag:", s2.Message, StringComparison.Ordinal);

        // Format 3: diag appended at the very end of the line after ')' (as logged by ManagedPlayerbotFeature.Fault)
        string lineTrailing =
            $"Playerbot {QaData.Bot1} action failed (InvalidOperationException: playerbot-corpse-reclaim-distance: body is 61.2 yd away); diag: {faultDiag}";
        QaSignal s3 = QaData.Parse(lineTrailing);
        Assert.Equal(QaSignalKind.ActionFault, s3.Kind);
        Assert.Equal("playerbot-corpse-reclaim-distance", s3.FaultCode);
        Assert.Equal("playerbot-corpse-reclaim-distance: body is 61.2 yd away", s3.Detail);
        Assert.Equal(faultDiag, s3.Diag);
        Assert.DoesNotContain("; diag:", s3.Message, StringComparison.Ordinal);

        // Format 4: diag appended at the very end of the line after '(3 more since)'
        string lineTrailingSuppressed =
            $"Playerbot {QaData.Bot1} action failed (InvalidOperationException: playerbot-corpse-reclaim-distance: body is 61.2 yd away) (3 more since); diag: {faultDiag}";
        QaSignal s4 = QaData.Parse(lineTrailingSuppressed);
        Assert.Equal(QaSignalKind.ActionFault, s4.Kind);
        Assert.Equal("playerbot-corpse-reclaim-distance", s4.FaultCode);
        Assert.Equal("playerbot-corpse-reclaim-distance: body is 61.2 yd away", s4.Detail);
        Assert.Equal(faultDiag, s4.Diag);
        Assert.EndsWith("(3 more since)", s4.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("; diag:", s4.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionFault_LongDiagWithTraceAndStack_IsNotTruncatedAtMaxMessageChars()
    {
        string traceEntries = string.Join(
            " -> ",
            Enumerable.Range(1, 16).Select(i => $"-{16 - i}s:step-{i:00}@(-89{i:00}.5,-12{i:00}.2,82.1)"));
        const string stackSuffix = "stack=[at PlayerbotBrain.UpdateCore(PlayerbotBrain.cs:712) <- at ManagedPlayerbotFeature.Tick(ManagedPlayerbotFeature.cs:1410)]";
        string longDiag =
            $"phase=combat-loot pos=(-8912.3,-140.2,82.1) map=0 zone=12 moveFlags=None motion=moving:no,wp:0/0,dest:none,loops:0 home=0:(-8949.9,-132.5,83.5)/900 trace=[{traceEntries}] {stackSuffix}";
        Assert.True(longDiag.Length > PlayerbotQaSignalParser.MaxMessageChars);
        Assert.True(longDiag.Length <= PlayerbotQaSignalParser.MaxDiagChars);

        string line =
            $"Playerbot {QaData.Bot1} action failed (InvalidOperationException: playerbot-corpse-reclaim-distance: body is 61.2 yd away); diag: {longDiag}";

        QaSignal signal = QaData.Parse(line);

        Assert.Equal(QaSignalKind.ActionFault, signal.Kind);
        Assert.Equal(QaData.Bot1, signal.BotKey);
        Assert.Equal("playerbot-corpse-reclaim-distance", signal.FaultCode);
        Assert.NotNull(signal.Diag);
        Assert.True(signal.Diag!.Length > PlayerbotQaSignalParser.MaxMessageChars);
        Assert.Equal(longDiag, signal.Diag);
        Assert.EndsWith(stackSuffix, signal.Diag, StringComparison.Ordinal);
        Assert.DoesNotContain("; diag:", signal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregator_And_IssueComposer_PropagateAndRenderDiagnostic()
    {
        var aggregator = new PlayerbotQaAggregator(maxFindings: 32, maxSamples: 5, build: "test-build");
        string line =
            $"Playerbot {QaData.Bot1} (Wfbotcg) stalled 120s: goal=Quest target=6747 quest=1656 at -5608.8,-439.4,401.2 map 0{SampleQaContext}; it gives up that goal; diag: {SampleDiag}";
        QaSignal signal = QaData.Parse(line);

        string? id = aggregator.Ingest(signal, NoQaContentFacts.Instance, QaData.T0);
        Assert.NotNull(id);

        QaFindingView? view = aggregator.Get(id!);
        Assert.NotNull(view);
        Assert.Equal(SampleDiag, view!.LastDiag);
        QaSample sample = Assert.Single(view.Samples);
        Assert.Equal(SampleDiag, sample.Diag);

        var redactor = new PlayerbotQaRedactor();
        QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(view, ["bot-qa"], "test-build", redactor);
        Assert.Contains($"- Diagnostic: `{SampleDiag}`", draft.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayerbotTraceLog_RecordsTransitionsAndFormatsSingleLineSnapshotAndStack()
    {
        var trace = new PlayerbotTraceLog();
        for (uint i = 0; i < 12; i++)
        {
            trace.Record(i * 1000u, $"step-{i}");
        }

        Assert.Equal(PlayerbotTraceLog.Capacity, trace.Count);

        await using WorldTestHost host = WorldTestHost.Start();
        var session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            var brain = new PlayerbotBrain(session, new PlayerbotOptions());
            brain.Trace.Record(0, "goal=Quest/240/35");
            brain.Trace.Record(2_000, "loop-consumed");

            string snapshot = brain.FormatDiagnosticSnapshot(player);
            Assert.DoesNotContain("\n", snapshot, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", snapshot, StringComparison.Ordinal);
            Assert.Contains("phase=", snapshot, StringComparison.Ordinal);
            Assert.Contains("motion=moving:no,wp:0/0,dest:none,loops:0", snapshot, StringComparison.Ordinal);
            Assert.Contains("trace=[", snapshot, StringComparison.Ordinal);
            Assert.Contains("goal=Quest/240/35", snapshot, StringComparison.Ordinal);
            Assert.Contains("loop-consumed", snapshot, StringComparison.Ordinal);

            Exception caught;
            try
            {
                ThrowSampleException();
                throw new InvalidOperationException("unreachable");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            string exDiag = PlayerbotTraceLog.FormatExceptionDiag(caught, player, brain);
            Assert.DoesNotContain("\n", exDiag, StringComparison.Ordinal);
            Assert.Contains("stack=[at PlayerbotQaParserClassifierTests.ThrowSampleException", exDiag, StringComparison.Ordinal);
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static void ThrowSampleException()
        => throw new InvalidOperationException("playerbot-test-fault");

    private sealed class CapturingLogger : ILogger
    {
        internal List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Lines.Add(formatter(state, exception));
        }
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>creature-ai/script-action-unsupported is reported (call only) when a creature's EventAI rows use an unsupported event or action.</summary>
public sealed class ScriptActionUnsupportedAnomalyTests
{
    private const string Key = "creature-ai/script-action-unsupported";
    private const uint Entry = 48_123; // unique to this class: the ledger is process-wide and other test classes use the wolf

    private static CreatureContent Content(params CreatureAiEvent[] events)
        => new(
            [Template(Entry, t => t.AIName = CreatureAiFactory.EventAIName)],
            [Spawn(48_123, Entry, 5, 0)], [], [], [],
            new CreatureAiContent(events, []));

    private static CreatureAiEvent Row(uint id, byte eventType, CreatureAiAction action = default)
        => new() { Id = id, CreatureId = Entry, EventType = eventType, Chance = 100, Action1 = action };

    private static AnomalyCapture CaptureOurs() => AnomalyLedger.Shared.Capture(n => n.Key == Key && n.Subject.StartsWith($"creature entry {Entry} ", StringComparison.Ordinal));

    [Fact]
    public void UnsupportedRows_ReportScriptActionUnsupported_ForTheCreature()
    {
        CreatureContent content = Content(Row(1, (byte)EventAiEventType.Aggro, new CreatureAiAction(99, 0, 0, 0)), Row(2, 77));
        using AnomalyCapture capture = CaptureOurs();
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal(CreatureAnomalies.Subject(wolf), notice.Subject);
        Assert.Contains("action type 99", notice.Detail);
    }

    [Fact]
    public void SupportedRows_ReportNothing()
    {
        CreatureContent content = Content(Row(1, (byte)EventAiEventType.Aggro, new CreatureAiAction((byte)EventAiActionType.Cast, 42, 0, 0)));
        using AnomalyCapture capture = CaptureOurs();
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Assert.Single(system.Creatures);

        Assert.False(capture.Any(Key), capture.Describe());
    }

    private sealed class GapRecorder(bool reporting) : Diagnostics.IGameQaObserver
    {
        public List<(uint Entry, string Gap, string Detail)> Gaps { get; } = [];

        public bool Reporting => reporting;

        public void OnCreatureAiGap(Creature? creature, uint entry, string gap, uint value, string detail) => Gaps.Add((entry, gap, detail));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneEventAiGap_RaisesOneSignal_TheObserversWhenQaReports_ElseTheAnomaly(bool reporting)
    {
        CreatureContent content = Content(Row(1, (byte)EventAiEventType.Aggro, new CreatureAiAction(99, 0, 0, 0)));
        using AnomalyCapture capture = CaptureOurs();
        var recorder = new GapRecorder(reporting);
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0, 0);
        var system = new CreatureMapSystem(map, content, null, random: new Random(1), aiServices: new CreatureAiServices(), qa: recorder);
        map.AddUpdater(system);
        AddPlayer(world, 1, 0, 0);
        Assert.Single(system.Creatures);

        Assert.Equal(reporting ? 1 : 0, recorder.Gaps.Count(g => g.Entry == Entry && g.Gap == "event-ai"));
        Assert.Equal(reporting ? 0 : 1, capture.Notices.Count);
    }
}

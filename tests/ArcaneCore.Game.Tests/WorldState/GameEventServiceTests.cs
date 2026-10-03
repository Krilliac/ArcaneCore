using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>
/// The game-event state machine (vmangos / mangos-classic <c>GameEventMgr</c> Update, StartEvent, StopEvent, EnableEvent,
/// Initialize), driven by a settable clock and recording fakes: no wall-clock wait anywhere.
/// </summary>
public sealed class GameEventServiceTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private sealed class Recorder : IGameEventEffects, IGameEventListener, IGameEventStatusSink, IGameEventAnnouncer
    {
        public List<string> Calls { get; } = [];

        public List<IReadOnlyCollection<ushort>> StatusWrites { get; } = [];

        public List<string> Announcements { get; } = [];

        public void SpawnEvent(int signedEventId) => Calls.Add($"spawn {signedEventId}");

        public void UnspawnEvent(int signedEventId) => Calls.Add($"unspawn {signedEventId}");

        public void UpdateCreatureData(ushort eventId, bool activate) => Calls.Add($"data {eventId} {activate}");

        public void UpdateEventQuests(ushort eventId, bool activate) => Calls.Add($"quests {eventId} {activate}");

        public void OnEventChanged(ushort eventId, bool active, bool resume) => Calls.Add($"changed {eventId} {active} resume={resume}");

        public void Changed(IReadOnlyCollection<ushort> activeEvents) => StatusWrites.Add(activeEvents);

        public void Announce(string description) => Announcements.Add(description);
    }

    private sealed class Clock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
    }

    private sealed class Handler(ushort id) : IWorldEventHandler
    {
        public ushort EventId { get; } = id;

        public uint NextUpdateDelaySeconds { get; set; } = 500;

        public List<string> Calls { get; } = [];

        public void Update() => Calls.Add("update");

        public void Enable() => Calls.Add("enable");

        public void Disable() => Calls.Add("disable");
    }

    private static GameEventRecord Event(uint id, int type, uint occurence, uint length, uint holiday = 0, uint linkedTo = 0, string? description = null)
        => new(id, type, occurence, length, holiday, linkedTo, description ?? $"event {id}");

    private static GameEventContent Content(IEnumerable<GameEventRecord> events, IEnumerable<GameEventTimeRecord>? times = null)
        => new([.. events], [.. times ?? []], [], [], [], [], []);

    private static (GameEventService Service, Recorder Recorder, Clock Clock) Make(
        GameEventContent content, DateTimeOffset now, GameEventOptions? options = null, Recorder? recorder = null, TimeZoneInfo? zone = null, Action<ushort, bool>? persistDisabled = null)
    {
        options ??= new GameEventOptions();
        var clock = new Clock(now);
        recorder ??= new Recorder();
        GameEventLoadResult load = GameEventLoader.Load(content, options, now, zone ?? TimeZoneInfo.Utc);
        var service = new GameEventService(load, options, () => clock.Now, zone ?? TimeZoneInfo.Utc, NullLogger.Instance, recorder, recorder, persistDisabled);
        service.AddEffects(recorder);
        service.AddListener(recorder);
        return (service, recorder, clock);
    }

    // classic-db shaped: a date event with a time row. Event 1 runs 2026-10-03 12:00 to 2026-10-03 14:00 once (occurence 1 day, length 2 hours).
    private static GameEventContent OneEventAt(DateTimeOffset start)
        => Content(
            [Event(1, 1, 1440, 120)],
            [new GameEventTimeRecord(1, start.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), "2030-12-31 22:59:59")]);

    [Fact]
    public void AnEvent_StartsWhenItsWindowOpens_AndStopsWhenItCloses_WithTheVmangosEffectOrder()
    {
        (GameEventService service, Recorder rec, Clock clock) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 11));
        service.Initialize(new HashSet<ushort>());
        Assert.False(service.IsActiveEvent(1));
        rec.Calls.Clear();

        clock.Now = Utc(2026, 10, 3, 12, 0, 1);
        service.Update();

        Assert.True(service.IsActiveEvent(1));
        Assert.Equal(
            ["spawn 1", "unspawn -1", "data 1 True", "quests 1 True", "changed 1 True resume=False"],
            rec.Calls);
        rec.Calls.Clear();

        clock.Now = Utc(2026, 10, 3, 14, 0, 1);
        service.Update();

        Assert.False(service.IsActiveEvent(1));
        Assert.Equal(
            ["unspawn 1", "spawn -1", "data 1 False", "quests 1 False", "changed 1 False resume=False"],
            rec.Calls);
    }

    [Fact]
    public void Update_ReturnsTheSmallestNextCheckPlusOneSecond_InMilliseconds()
    {
        (GameEventService service, _, Clock clock) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 11));

        Assert.Equal((3600u + 1) * 1000, service.Update());               // never started: the delay before the start
        clock.Now = Utc(2026, 10, 3, 12, 30);
        service.Update();
        Assert.Equal((5400u + 1) * 1000, service.Update());               // running: the delay before it ends (90 minutes)
        clock.Now = Utc(2026, 10, 3, 14, 30);
        service.Update();
        Assert.Equal((77400u + 1) * 1000, service.Update());              // between occurences: the next start, 21.5 hours away
    }

    [Fact]
    public void AnInactiveEvent_SpawnsItsNegativeObjectsOnTheFirstPassOnly()
    {
        (GameEventService service, Recorder rec, _) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 11));

        service.Initialize(new HashSet<ushort>());
        Assert.Equal(["spawn -1"], rec.Calls);

        rec.Calls.Clear();
        service.Update();
        Assert.Empty(rec.Calls);
    }

    [Fact]
    public void ALinkedEvent_StartsOnlyWhileItsParentIsActive()
    {
        // events 24 and 25 are linked to 12 in classic-db; here 24 -> 12 with both windows open from the start
        GameEventContent content = Content(
            [Event(12, 1, 1440, 600), Event(24, 1, 1440, 600, linkedTo: 12)],
            [
                new GameEventTimeRecord(12, "2026-10-03 13:00:00", "2030-12-31 22:59:59"),
                new GameEventTimeRecord(24, "2026-10-03 12:00:00", "2030-12-31 22:59:59"),
            ]);
        (GameEventService service, _, Clock clock) = Make(content, Utc(2026, 10, 3, 11));
        service.Initialize(new HashSet<ushort>());

        clock.Now = Utc(2026, 10, 3, 12, 30); // 24's window is open, its parent's is not
        service.Update();
        Assert.False(service.IsActiveEvent(24));

        clock.Now = Utc(2026, 10, 3, 13, 30); // the parent starts; the child follows in the same pass (ascending ids: 12 before 24)
        service.Update();
        Assert.True(service.IsActiveEvent(12));
        Assert.True(service.IsActiveEvent(24));
    }

    [Fact]
    public void AManualStart_IgnoresTheLink_AndOverwriteRewritesTheSchedule()
    {
        GameEventContent content = Content(
            [Event(12, 1, 1440, 600), Event(24, 1, 1440, 90, linkedTo: 12)],
            [new GameEventTimeRecord(12, "2026-10-03 13:00:00", "2030-12-31 22:59:59"), new GameEventTimeRecord(24, "2020-01-01 00:00:00", "2020-01-02 00:00:00")]);
        (GameEventService service, _, Clock clock) = Make(content, Utc(2026, 10, 3, 12));
        service.Initialize(new HashSet<ushort>());

        Assert.True(service.StartEvent(24, overwrite: true)); // its table end (2020) has passed

        GameEventDefinition def = service.Find(24)!;
        Assert.True(service.IsActiveEvent(24));
        // mangos-classic is exclusive at the start second, so a manual start is dated one second back: the update that may run in the
        // very same second must not stop the event it just started
        Assert.Equal(clock.Now.AddSeconds(-1), def.Start);
        Assert.Equal(clock.Now.AddSeconds(-1).AddMinutes(90), def.End); // end <= start: start plus the length, in minutes (the references add seconds)

        // the schedule agrees with the manual start: the next pass keeps it running, and stops it when its length is over
        service.Update();
        Assert.True(service.IsActiveEvent(24));
        clock.Now = Utc(2026, 10, 3, 13, 31);
        service.Update();
        Assert.False(service.IsActiveEvent(24));
    }

    [Fact]
    public void AManualStop_WithOverwrite_BackdatesTheStart_SoTheScheduleDoesNotRestartIt()
    {
        (GameEventService service, _, Clock clock) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 12, 30));
        service.Initialize(new HashSet<ushort>());
        Assert.True(service.IsActiveEvent(1));

        Assert.True(service.StopEvent(1, overwrite: true));
        Assert.False(service.IsActiveEvent(1));
        Assert.Equal(clock.Now.AddMinutes(-120), service.Find(1)!.Start);

        service.Update();
        Assert.False(service.IsActiveEvent(1)); // would be active by the table
    }

    [Fact]
    public void UnknownOrInvalidEvents_CannotBeStartedStoppedOrEnabled()
    {
        (GameEventService service, Recorder rec, _) = Make(Content([Event(5, 1, 100, 0)]), Utc(2026, 10, 3));

        Assert.False(service.StartEvent(5)); // length 0: invalid
        Assert.False(service.StartEvent(99));
        Assert.False(service.StopEvent(99));
        Assert.False(service.EnableEvent(99, enable: false));
        Assert.Empty(rec.Calls);
        Assert.Empty(service.ActiveEvents);
    }

    [Fact]
    public void Disable_StopsARunningEvent_PersistsTheFlag_AndADisabledEventNeverStartsFromUpdate()
    {
        var persisted = new List<(ushort, bool)>();
        (GameEventService service, _, Clock clock) = Make(
            OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 12, 30), persistDisabled: (id, disabled) => persisted.Add((id, disabled)));
        service.Initialize(new HashSet<ushort>());
        Assert.True(service.IsActiveEvent(1));

        Assert.True(service.EnableEvent(1, enable: false));

        Assert.False(service.IsActiveEvent(1));
        Assert.True(service.Find(1)!.Disabled);
        Assert.Equal<(ushort, bool)>([(1, true)], persisted);
        service.Update();
        Assert.False(service.IsActiveEvent(1));

        Assert.True(service.EnableEvent(1, enable: false)); // already in that state: nothing to do
        Assert.Single(persisted);

        // enabling takes no action by itself: the next update starts it if its window is open ... which StopEvent(overwrite) closed
        Assert.True(service.EnableEvent(1, enable: true));
        Assert.Equal<(ushort, bool)>([(1, true), (1, false)], persisted);
        Assert.False(service.IsActiveEvent(1));
        clock.Now = Utc(2026, 10, 4, 11, 0); // the stop back-dated the start to 10:30, so the next window is 10:30 to 12:30 tomorrow
        service.Update();
        Assert.True(service.IsActiveEvent(1));
    }

    [Fact]
    public void EventsRunningAtShutdown_ResumeAtTheFirstUpdate_AndTheStatusIsTruncatedFirst()
    {
        (GameEventService service, Recorder rec, _) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 12, 30));

        service.Initialize(new HashSet<ushort> { 1 });

        Assert.Contains("changed 1 True resume=True", rec.Calls);
        Assert.Empty(rec.StatusWrites[0]);                       // game_event_status was truncated after it was read
        Assert.Equal<ushort>([1], rec.StatusWrites[^1]);          // and the resumed event is recorded again
    }

    [Fact]
    public void AnEventNotRunningAtShutdown_StartsWithoutResume()
    {
        (GameEventService service, Recorder rec, _) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 12, 30));

        service.Initialize(new HashSet<ushort>());

        Assert.Contains("changed 1 True resume=False", rec.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServersideEvents_AreRestoredAfterARestartOnlyWhenTheOptionSaysSo(bool restore)
    {
        // classic-db schedule type 0: Scourge Invasion style, started by scripts, far-future start
        GameEventContent content = Content([Event(17, 0, 525600, 1)]);
        var options = new GameEventOptions { RestoreServersideEvents = restore };
        (GameEventService service, Recorder rec, _) = Make(content, Utc(2026, 10, 3, 12), options);

        service.Initialize(new HashSet<ushort> { 17 });

        Assert.Equal(restore, service.IsActiveEvent(17));
        Assert.Equal(restore, rec.Calls.Contains("changed 17 True resume=True"));
    }

    [Fact]
    public void ServersideEvents_NeverActivateOnTheirOwn_AndAreSkippedAfterTheFirstPass()
    {
        (GameEventService service, Recorder rec, Clock clock) = Make(Content([Event(17, 0, 0, 0)]), Utc(2026, 10, 3, 12));
        service.Initialize(new HashSet<ushort>());
        Assert.Equal(["spawn -17"], rec.Calls); // first pass: negatives of an inactive event

        clock.Now = Utc(2099, 1, 1);
        rec.Calls.Clear();
        service.Update();
        Assert.Empty(rec.Calls);
        Assert.False(service.IsActiveEvent(17));

        // started by a script / command, a serverside event stays until stopped: the schedule does not touch it
        Assert.True(service.StartEvent(17));
        service.Update();
        Assert.True(service.IsActiveEvent(17));
    }

    [Fact]
    public void Announce_SendsTheDescription_OnlyWhenTheOptionIsOn()
    {
        (GameEventService quiet, Recorder quietRec, _) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 12, 30));
        quiet.Initialize(new HashSet<ushort>());
        Assert.Empty(quietRec.Announcements);

        (GameEventService loud, Recorder loudRec, _) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 12, 30), new GameEventOptions { Announce = true });
        loud.Initialize(new HashSet<ushort>());
        Assert.Equal(["event 1"], loudRec.Announcements);
    }

    [Fact]
    public void AThrowingEffectOrListener_DoesNotStopTheOthers_NorTheStart()
    {
        var rec = new Recorder();
        (GameEventService service, _, _) = Make(OneEventAt(Utc(2026, 10, 3, 12)), Utc(2026, 10, 3, 12, 30), recorder: rec);
        service.AddEffects(new ThrowingEffects());
        service.AddListener(new ThrowingListener());
        var later = new Recorder();
        service.AddEffects(later);
        service.AddListener(later);

        service.Initialize(new HashSet<ushort>());

        Assert.True(service.IsActiveEvent(1));
        Assert.Contains("spawn 1", later.Calls);
        Assert.Contains("changed 1 True resume=False", later.Calls);
    }

    private sealed class ThrowingEffects : IGameEventEffects
    {
        public void SpawnEvent(int signedEventId) => throw new InvalidOperationException("effect failed");
    }

    private sealed class ThrowingListener : IGameEventListener
    {
        public void OnEventChanged(ushort eventId, bool active, bool resume) => throw new InvalidOperationException("listener failed");
    }

    [Fact]
    public void Holidays_AreActiveWhileTheirEventRuns_AndHolidayZeroNever()
    {
        // classic-db Call to Arms weekends: holiday ids 283-285 on events 18-20
        GameEventContent content = Content(
            [Event(18, 1, 20160, 4320, holiday: 283), Event(19, 1, 20160, 4320, holiday: 284), Event(1, 1, 1440, 120)],
            [
                new GameEventTimeRecord(18, "2026-10-02 00:00:00", "2030-12-31 22:59:59"),
                new GameEventTimeRecord(19, "2026-10-16 00:00:00", "2030-12-31 22:59:59"),
                new GameEventTimeRecord(1, "2026-10-03 00:00:00", "2030-12-31 22:59:59"),
            ]);
        (GameEventService service, _, _) = Make(content, Utc(2026, 10, 3, 1));
        service.Initialize(new HashSet<ushort>());

        Assert.True(service.IsActiveEvent(18));
        Assert.True(service.IsActiveHoliday(283));
        Assert.False(service.IsActiveHoliday(284)); // its weekend is two weeks away
        Assert.False(service.IsActiveHoliday(0));   // an active event with holiday 0 does not make holiday 0 active
        Assert.True(service.IsActiveEvent(1));
    }

    [Fact]
    public void ComputedSchedules_AreRecomputedWhenTheCalendarDayChanges()
    {
        // Easter, type 13, 2026 window: April 5th to April 12th (5 days of it open)
        GameEventContent content = Content([Event(9, 13, 524160, 7200)]);
        (GameEventService service, _, Clock clock) = Make(content, Utc(2026, 3, 1));
        service.Initialize(new HashSet<ushort>());
        Assert.Equal(Utc(2026, 4, 5), service.Find(9)!.Start);

        clock.Now = Utc(2026, 4, 6);
        service.Update();
        Assert.True(service.IsActiveEvent(9));

        clock.Now = Utc(2026, 4, 11);
        service.Update();
        Assert.False(service.IsActiveEvent(9)); // 5 days after the start

        clock.Now = Utc(2027, 3, 29); // next year, the day after the new Easter's Sunday (March 28th)
        service.Update();
        Assert.Equal(Utc(2027, 3, 28), service.Find(9)!.Start);
        Assert.True(service.IsActiveEvent(9));
    }

    [Fact]
    public void AHolidayThatCrossesNewYear_KeepsRunning_ThroughTheYearChange()
    {
        // Feast of Winter Veil: type 11, Dec 16 23:00, 19 days, table end Dec 31 22:59:59 (classic-db)
        GameEventContent content = Content(
            [Event(2, 11, 525600, 27360, holiday: 141)],
            [new GameEventTimeRecord(2, "2020-12-16 23:00:00", "2030-12-31 22:59:59")]);
        (GameEventService service, _, Clock clock) = Make(content, Utc(2026, 12, 30));
        service.Initialize(new HashSet<ushort>());
        Assert.True(service.IsActiveEvent(2));

        clock.Now = Utc(2026, 12, 31, 23, 30); // past the table's end date
        service.Update();
        Assert.True(service.IsActiveEvent(2));

        clock.Now = Utc(2027, 1, 2, 12);
        service.Update();
        Assert.True(service.IsActiveEvent(2));
        Assert.True(service.IsActiveHoliday(141));

        clock.Now = Utc(2027, 1, 5);
        service.Update();
        Assert.False(service.IsActiveEvent(2));
    }

    [Fact]
    public void AHolidayThatCrossesNewYear_IsCutAtTheYearEnd_InMangosLiteralMode()
    {
        GameEventContent content = Content(
            [Event(2, 11, 525600, 27360, holiday: 141)],
            [new GameEventTimeRecord(2, "2020-12-16 23:00:00", "2030-12-31 22:59:59")]);
        (GameEventService service, _, Clock clock) = Make(content, Utc(2026, 12, 30), new GameEventOptions { YearlyRebase = YearlyRebaseMode.MangosLiteral });
        service.Initialize(new HashSet<ushort>());
        Assert.True(service.IsActiveEvent(2));

        clock.Now = Utc(2027, 1, 2, 12);
        service.Update();
        Assert.False(service.IsActiveEvent(2));
    }

    [Fact]
    public void VmangosTable_HardcodedEventsAreRunByTheirHandler_AndDisabledOnesAreSkipped()
    {
        // vmangos dialect: dates on the row, hardcoded and disabled flags
        var content = new GameEventContent(
            [
                new GameEventRecord(1, 1, 1440, 120, 0, 0, "plain", "2026-10-03 12:00:00", "2030-12-31 22:59:59"),
                new GameEventRecord(2, 1, 1440, 120, 0, 0, "hardcoded", "2026-10-03 12:00:00", "2030-12-31 22:59:59", Hardcoded: true),
                new GameEventRecord(3, 1, 1440, 120, 0, 0, "disabled", "2026-10-03 12:00:00", "2030-12-31 22:59:59", Disabled: true),
            ],
            [], [], [], [], [], []);
        (GameEventService service, _, Clock clock) = Make(content, Utc(2026, 10, 3, 11));
        var handler = new Handler(2) { NextUpdateDelaySeconds = 300 };
        service.AddWorldEventHandler(handler);
        Assert.Equal(GameEventDialect.VMangos, service.Dialect);
        Assert.Equal(GameEventStartBoundary.Inclusive, service.Boundary);

        uint delay = service.Initialize(new HashSet<ushort>());

        Assert.Equal(["update"], handler.Calls);
        Assert.Equal((300u + 1) * 1000, delay); // the handler's delay is the smallest
        clock.Now = Utc(2026, 10, 3, 12); // exactly at the start: vmangos is inclusive
        service.Update();
        Assert.True(service.IsActiveEvent(1));
        Assert.False(service.IsActiveEvent(2)); // the schedule never starts a hardcoded event
        Assert.False(service.IsActiveEvent(3));

        // the handler is enabled when its event is started by hand, and a disabled flag stops driving it
        Assert.True(service.StartEvent(2));
        Assert.Equal(["update", "update", "enable"], handler.Calls);
        Assert.True(service.EnableEvent(2, enable: false));
        Assert.Equal("disable", handler.Calls[^1]);
        Assert.True(service.IsActiveEvent(2)); // a hardcoded event is not stopped by the schedule's EnableEvent: its handler winds it down
        handler.Calls.Clear();
        service.Update();
        Assert.Empty(handler.Calls); // a disabled event's handler is no longer updated
    }

    [Fact]
    public void TheStartBoundary_FollowsTheDialect_UnlessTheOptionOverridesIt()
    {
        // mangos-classic: start < now; vmangos: start <= now
        GameEventContent cmangos = OneEventAt(Utc(2026, 10, 3, 12));
        (GameEventService exclusive, _, _) = Make(cmangos, Utc(2026, 10, 3, 12));
        exclusive.Initialize(new HashSet<ushort>());
        Assert.False(exclusive.IsActiveEvent(1));
        (GameEventService forced, _, _) = Make(cmangos, Utc(2026, 10, 3, 12), new GameEventOptions { StartBoundary = GameEventStartBoundary.Inclusive });
        forced.Initialize(new HashSet<ushort>());
        Assert.True(forced.IsActiveEvent(1));
    }

    [Fact]
    public void LoadIssues_ReportRejectedRows_AndRowsForMissingEventsAreDropped()
    {
        var content = new GameEventContent(
            [Event(1, 1, 1440, 120), Event(0, 1, 1440, 120)],
            [],
            [new GameEventSpawnRecord(100, 1), new GameEventSpawnRecord(101, -1), new GameEventSpawnRecord(102, 9), new GameEventSpawnRecord(103, 0)],
            [new GameEventSpawnRecord(5000, -2)],
            [new GameEventCreatureDataRecord(100, 1, 0, 0, 0, 0, 0), new GameEventCreatureDataRecord(100, 7, 0, 0, 0, 0, 0)],
            [new GameEventQuestRecord(7001, 1), new GameEventQuestRecord(7002, 8)],
            []);
        (GameEventService service, _, _) = Make(content, Utc(2026, 10, 3));

        Assert.Equal([100u], service.Rows.Creatures[1]);
        Assert.Equal([101u], service.Rows.Creatures[-1]);
        Assert.False(service.Rows.Creatures.ContainsKey(9));
        Assert.Empty(service.Rows.GameObjects);
        Assert.Equal([(ushort)1], service.Rows.CreatureData.Keys.ToArray());
        Assert.Equal([7001u], service.Rows.Quests[1]);
        Assert.Contains(service.LoadIssues, i => i.Contains("game_event id 0", StringComparison.Ordinal));
        Assert.Contains(service.LoadIssues, i => i.Contains("game_event_creature: game event id 9 does not exist", StringComparison.Ordinal));
        Assert.Contains(service.LoadIssues, i => i.Contains("game_event_creature: game event id 0 is not allowed", StringComparison.Ordinal));
        Assert.Contains(service.LoadIssues, i => i.Contains("game_event_quest: game event id 8", StringComparison.Ordinal));
    }

    [Fact]
    public void ADatabaseDateThatDoesNotParse_IsReported_AndLeavesTheEventNeverRunning()
    {
        GameEventContent content = Content([Event(1, 1, 1440, 120)], [new GameEventTimeRecord(1, "0000-00-00 00:00:00", "2030-12-31 22:59:59")]);
        (GameEventService service, _, _) = Make(content, Utc(2026, 10, 3, 12));

        service.Initialize(new HashSet<ushort>());

        Assert.False(service.IsActiveEvent(1));
        Assert.Contains(service.LoadIssues, i => i.Contains("start_time '0000-00-00 00:00:00' is not", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalDates_AreReadInTheGameZone_WallOrStandardTime()
    {
        // event 400 shape: 07:00 local. In summer (+2) the wall reading starts 05:00 UTC, the standard-time reading 06:00 UTC.
        GameEventContent content = Content([Event(400, 1, 1440, 780)], [new GameEventTimeRecord(400, "2026-07-01 07:00:00", "2030-12-31 20:00:00")]);
        (GameEventService wall, _, _) = Make(content, Utc(2026, 7, 2, 5, 0, 5), zone: GameEventCalendarTests.DstZone);
        (GameEventService standard, _, _) = Make(content, Utc(2026, 7, 2, 5, 0, 5), new GameEventOptions { DateTimeInterpretation = GameEventDateTimeInterpretation.StandardTime }, zone: GameEventCalendarTests.DstZone);
        wall.Initialize(new HashSet<ushort>());
        standard.Initialize(new HashSet<ushort>());

        Assert.True(wall.IsActiveEvent(400));
        Assert.False(standard.IsActiveEvent(400)); // 05:00:05 UTC is before its 06:00 UTC start
    }
}

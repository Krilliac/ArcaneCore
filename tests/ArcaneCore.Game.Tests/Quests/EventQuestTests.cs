using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.QuestFlowKit;

namespace ArcaneCore.Game.Tests.Quests;

/// <summary>
/// Event quests: quests listed in <c>game_event_quest</c> are inactive from load and active only while their event runs (vmangos
/// GameEventMgr.cpp:578 and :1023-1036; QuestDef.h:278). Driven through the real quest offer/accept path with a settable clock.
/// </summary>
public sealed class EventQuestTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        public Rig(QuestFlowKit kit, params (uint Quest, int Event)[] listed)
        {
            Kit = kit;
            var events = new GameEventContent(
                [new GameEventRecord(2, 1, 1440, 120, 0, 0, "Winter Veil"), new GameEventRecord(3, 1, 1440, 120, 0, 0, "Second")],
                [new GameEventTimeRecord(2, "2026-10-03 12:00:00", "2030-12-31 22:59:59"), new GameEventTimeRecord(3, "2026-10-03 13:00:00", "2030-12-31 22:59:59")],
                [], [],
                [],
                [.. listed.Select(l => new GameEventQuestRecord(l.Quest, l.Event))],
                []);
            var options = new GameEventOptions();
            GameEventLoadResult load = GameEventLoader.Load(events, options, Clock.Now, TimeZoneInfo.Utc);
            Service = new GameEventService(load, options, () => Clock.Now, TimeZoneInfo.Utc, NullLogger.Instance);
            Quests = new GameEventQuests(Service, Service.Rows, () => Kit.Services.Quests);
            Service.AddEffects(Quests);
            Quests.Resync(); // what the quest feature does when it wires the service: every listed quest is inactive from load
        }

        public QuestFlowKit Kit { get; }

        public Clock Clock { get; } = new(Utc(2026, 10, 3, 10));

        public GameEventService Service { get; }

        public GameEventQuests Quests { get; }

        public void At(int hour, int minute = 0)
        {
            Clock.Now = Utc(2026, 10, 3, hour, minute);
            Service.Update();
        }

        public void Dispose() => Kit.Dispose();
    }

    private sealed class Clock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
    }

    [Fact]
    public void AQuestListedUnderAnEvent_IsNotOffered_UntilTheEventStarts_AndNotAfterItStops()
    {
        using var rig = new Rig(new QuestFlowKit([Task(10), Task(11)], starters: [10, 11]), (10, 2));
        rig.Service.Initialize(new HashSet<ushort>());
        Quest listed = rig.Kit.Services.Quests.Get(10)!;
        Quest plain = rig.Kit.Services.Quests.Get(11)!;

        Assert.False(listed.IsActive);                        // inactive from load
        Assert.True(plain.IsActive);                           // unlisted quests are untouched
        Assert.False(rig.Kit.Accept(10));
        Assert.True(rig.Kit.Services.Quests.Get(11)!.IsActive);

        rig.At(12, 30);                                        // event 2 runs 12:00-14:00
        Assert.True(listed.IsActive);
        Assert.True(rig.Kit.Accept(10));                       // acceptable now

        rig.At(14, 30);
        Assert.False(listed.IsActive);
        Assert.True(plain.IsActive);
    }

    [Fact]
    public void ARunningEventAtStartup_ActivatesItsQuestsAtInitialisation()
    {
        using var rig = new Rig(new QuestFlowKit([Task(10)], starters: [10]), (10, 2));
        rig.Clock.Now = Utc(2026, 10, 3, 12, 30);

        rig.Service.Initialize(new HashSet<ushort>());

        Assert.True(rig.Kit.Services.Quests.Get(10)!.IsActive);
        Assert.True(rig.Kit.Accept(10));
    }

    [Fact]
    public void AQuestListedUnderTwoEvents_IsActiveWhileEitherRuns()
    {
        using var rig = new Rig(new QuestFlowKit([Task(10)], starters: [10]), (10, 2), (10, 3));
        rig.Service.Initialize(new HashSet<ushort>());
        Quest quest = rig.Kit.Services.Quests.Get(10)!;
        Assert.False(quest.IsActive);

        rig.At(12, 30);          // event 2 only
        Assert.True(quest.IsActive);
        rig.At(13, 30);          // both
        Assert.True(quest.IsActive);
        rig.At(14, 30);          // event 2 is over, event 3 (13:00-15:00) still runs
        Assert.True(quest.IsActive);
        rig.At(15, 30);
        Assert.False(quest.IsActive);
    }

    [Fact]
    public void ADisabledQuestListedUnderAnEvent_IsActiveWhileTheEventRuns_AsInVmangos()
    {
        // vmangos SetQuestActiveState(true) overrides the Method disabled bit for event quests (QuestDef.cpp:143-151 vs :1034)
        using var rig = new Rig(new QuestFlowKit([Task(10, method: 2 | QuestConstants.MethodDisabled)], starters: [10]), (10, 2));
        rig.Service.Initialize(new HashSet<ushort>());
        Quest quest = rig.Kit.Services.Quests.Get(10)!;
        Assert.False(quest.IsActive);

        rig.At(12, 30);
        Assert.True(quest.IsActive);
        rig.At(14, 30);
        Assert.False(quest.IsActive);
    }

    [Fact]
    public void AQuestThatIsNotListed_KeepsItsOwnMethodState()
    {
        using var kit = new QuestFlowKit([Task(10), Task(11, method: 2 | QuestConstants.MethodDisabled)], starters: [10, 11]);
        Assert.True(kit.Services.Quests.Get(10)!.IsActive);
        Assert.False(kit.Services.Quests.Get(11)!.IsActive);
    }

    [Fact]
    public void AListedQuestWithoutATemplate_IsReported_NotFatal()
    {
        using var rig = new Rig(new QuestFlowKit([Task(10)], starters: [10]), (10, 2), (777, 2));
        Assert.False(rig.Kit.Services.Quests.Get(10)!.IsActive);

        // the construction already handled this store: a second look has nothing to do; the missing template shows on a fresh gate
        Assert.Empty(rig.Quests.Resync());
        var fresh = new GameEventQuests(rig.Service, rig.Service.Rows, () => rig.Kit.Services.Quests);
        Assert.Equal([777u], fresh.Resync());
    }

    [Fact]
    public void Release_GivesTheQuestsBackToTheirMethod()
    {
        using var rig = new Rig(new QuestFlowKit([Task(10)], starters: [10]), (10, 2));
        Quest quest = rig.Kit.Services.Quests.Get(10)!;
        Assert.False(quest.IsActive);

        rig.Quests.Release();

        Assert.True(quest.IsActive);
    }

    [Fact]
    public void AQuestInTheLog_KeepsItsState_WhenTheEventEnds()
    {
        using var rig = new Rig(new QuestFlowKit([Task(10)], starters: [10]), (10, 2));
        rig.Service.Initialize(new HashSet<ushort>());
        rig.At(12, 30);
        Assert.True(rig.Kit.Accept(10));
        rig.At(14, 30);

        Assert.False(rig.Kit.Services.Quests.Get(10)!.IsActive); // no new offers
        Assert.Equal(QuestStatus.Incomplete, rig.Kit.State.Quests.GetStatus(10)); // still in the log
    }
}

using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// The invitation timers of the battleground manager (vmangos BgQueueInviteEvent / BGQueueRemoveEvent, BattleGroundMgr.cpp:897-970): the
/// "you can still enter" status a minute after the invitation, the removal at 80 s, their (due time, insertion) order, and what a quiet
/// update costs. The ordering tests pin behaviour that must not change when the event list is reorganised.
/// </summary>
public sealed class BattlegroundManagerInviteEventTests
{
    private const uint Remind = BattlegroundConstants.InvitationRemindTimeMs;
    private const uint Remove = BattlegroundConstants.InviteAcceptWaitTimeMs;

    private static (BattlegroundManager Mgr, RecordingManagerHost Host) NewManager(uint maxPerTeam = 10)
    {
        var host = new RecordingManagerHost();
        var ports = new RecordingPorts();
        var mgr = new BattlegroundManager(new BattlegroundOptions(), host, ports.ToPorts(new RecordingHost()));
        mgr.RegisterTemplate(WsgTemplate(2, maxPerTeam));
        return (mgr, host);
    }

    private static void Solo(BattlegroundManager mgr, ObjectGuid guid, Team team)
        => mgr.JoinQueue(new BattlegroundJoinRequest(new BattlegroundMember(guid, team, 60, true, false), BattlegroundType.WarsongGulch, 0, AsGroup: false, QueuedAtPortal: false, []));

    private static void QueueFour(BattlegroundManager mgr)
    {
        Solo(mgr, Alliance[0], Team.Alliance);
        Solo(mgr, Alliance[1], Team.Alliance);
        Solo(mgr, Horde[0], Team.Horde);
        Solo(mgr, Horde[1], Team.Horde);
    }

    /// <summary>The players of the invitation statuses (WaitJoin with the full 80 s) recorded after <paramref name="from"/>, in send order.</summary>
    private static ObjectGuid[] InvitedSince(RecordingManagerHost host, int from)
        => [.. host.Statuses.Skip(from).Where(s => s.Status == BattlegroundStatus.WaitJoin && s.Time1 == Remove).Select(s => s.Player)];

    private static ObjectGuid[] RemindedSince(RecordingManagerHost host, int from)
        => [.. host.Statuses.Skip(from).Where(s => s.Status == BattlegroundStatus.WaitJoin && s.Time1 == Remove - Remind).Select(s => s.Player)];

    private static ObjectGuid[] RemovedSince(RecordingManagerHost host, int from)
        => [.. host.Statuses.Skip(from).Where(s => s.Status == BattlegroundStatus.None).Select(s => s.Player)];

    [Fact]
    public void RemindAndRemove_FireOnTheirTick_AndNotBefore_InInvitationOrder()
    {
        var (mgr, host) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);
        ObjectGuid[] invited = InvitedSince(host, 0);
        Assert.Equal(4, invited.Length);
        int mark = host.Statuses.Count;

        mgr.Update(Remind - 1);   // 60 999 ms: one millisecond short of the reminder (invited at 1000)
        Assert.Empty(RemindedSince(host, mark));

        mgr.Update(1);            // 61 000 ms: all four reminders are due, same due time: insertion order
        Assert.Equal(invited, RemindedSince(host, mark));
        Assert.Empty(RemovedSince(host, mark));
        mark = host.Statuses.Count;

        mgr.Update(Remove - Remind - 1);   // 80 999 ms
        Assert.Empty(RemovedSince(host, mark));
        Assert.Empty(RemindedSince(host, mark));

        mgr.Update(1);            // 81 000 ms: the invitations lapse, same due time again
        Assert.Equal(invited, RemovedSince(host, mark));
        Assert.Empty(RemindedSince(host, mark));
    }

    [Fact]
    public void EventsOfTwoInvitationRounds_FireOrderedByDueTime_ThenInsertion_WhenOneUpdateCoversThemAll()
    {
        var (mgr, host) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);                      // first round invited at 1000: remind 61 000, remove 81 000
        ObjectGuid[] first = InvitedSince(host, 0);
        int mark = host.Statuses.Count;

        for (int i = 2; i < 4; i++)
        {
            Solo(mgr, Alliance[i], Team.Alliance);
            Solo(mgr, Horde[i], Team.Horde);
        }

        mgr.ScheduleQueueUpdate(BattlegroundType.WarsongGulch, 5);
        mgr.Update(1);                         // second round invited at 1001: remind 61 001, remove 81 001
        ObjectGuid[] second = InvitedSince(host, mark);
        Assert.Equal(4, second.Length);
        Assert.Empty(first.Intersect(second));
        mark = host.Statuses.Count;

        mgr.Update(200_000);                   // everything is due at once

        ObjectGuid[] order = [.. host.Statuses.Skip(mark)
            .Where(s => s.Status == BattlegroundStatus.None || (s.Status == BattlegroundStatus.WaitJoin && s.Time1 == Remove - Remind))
            .Select(s => s.Player)];
        Assert.Equal([.. first, .. second, .. first, .. second], order);
        Assert.Equal(8, host.Statuses.Skip(mark).Count(s => s.Status == BattlegroundStatus.None));
        Assert.Equal(8, host.Statuses.Skip(mark).Count(s => s.Status == BattlegroundStatus.WaitJoin && s.Time1 == Remove - Remind));
    }

    [Fact]
    public void AnEventScheduledByAHandlerThatIsAlreadyDue_FiresInTheSameUpdate()
    {
        var (mgr, host) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);                      // invited at 1000: the removals are due at 81 000
        ObjectGuid[] invited = InvitedSince(host, 0);
        Assert.Equal(4, invited.Length);

        // The first invitee logs out (its own removal event then finds it offline and does nothing).
        host.Offline.Add(invited[0]);
        mgr.PlayerLoggedOut(invited[0]);
        mgr.Update(Remove - 1);                // 80 999 ms

        // While the second invitee's removal runs, the first logs back in: PlayerLoggedIn schedules a removal at
        // NowMs + (RemoveInviteTimeMs - NowMs) = NowMs (81 000), i.e. one that is due as soon as it is added.
        host.OnStatus = (player, status) =>
        {
            if (player == invited[1] && status == BattlegroundStatus.None)
            {
                host.OnStatus = null;
                host.Offline.Remove(invited[0]);
                mgr.PlayerLoggedIn(invited[0], 60);
            }
        };
        int mark = host.Statuses.Count;

        mgr.Update(1);                         // 81 000 ms

        Assert.Equal([invited[1], invited[2], invited[3], invited[0]], RemovedSince(host, mark));
    }

    [Fact]
    public void AnEventThatIsNotYetDue_StaysQueuedUntilItsTick_EvenAfterOtherEventsFired()
    {
        var (mgr, host) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);
        ObjectGuid[] invited = InvitedSince(host, 0);
        mgr.Update(Remind);                    // 61 000: reminders fired, removals pending
        int mark = host.Statuses.Count;

        for (int i = 0; i < 10; i++)
        {
            mgr.Update(1000);                  // 62 000 .. 71 000
        }

        Assert.Empty(RemovedSince(host, mark));
        Assert.Empty(RemindedSince(host, mark));
        mgr.Update(10_000);                    // 81 000
        Assert.Equal(invited, RemovedSince(host, mark));
    }

    [Fact]
    public void Update_WithFiftyPendingInviteEventsNoneDue_AllocatesNothing()
    {
        var (mgr, host) = NewManager(maxPerTeam: 25);
        (WarsongGulch bg, _, _) = NewWsg();
        for (uint i = 0; i < 25; i++)
        {
            var group = new QueuedGroup(BattlegroundType.WarsongGulch, Team.Alliance, 0, 0) { InvitedToInstanceId = bg.InstanceId, RemoveInviteTimeMs = Remove };
            mgr.OnInvited(ObjectGuid.Player(1000 + i), bg, group);   // two events each: a reminder and a removal
        }

        Assert.Equal(25, host.Statuses.Count);
        Assert.Empty(mgr.RunningBattlegrounds);

        AllocationProbe.AssertEveryCleanWindowAtMost(() => { for (int i = 0; i < 100; i++) mgr.Update(0); }, 0, "100 BattlegroundManager.Update(0) with 50 pending invite events, none due");

        Assert.Equal(25, host.Statuses.Count);   // nothing fired
    }
}

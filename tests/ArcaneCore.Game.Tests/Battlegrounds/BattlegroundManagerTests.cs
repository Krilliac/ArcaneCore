using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

internal sealed class RecordingManagerHost : IBattlegroundManagerHost
{
    public readonly HashSet<ObjectGuid> Offline = [];
    public readonly Dictionary<uint, RecordingHost> MatchHosts = [];
    public readonly List<(ObjectGuid Player, uint Slot, BattlegroundStatusSubject Subject, BattlegroundStatus Status, uint Time1, uint Time2)> Statuses = [];
    public readonly List<(ObjectGuid Player, uint Result)> GroupJoined = [];
    public readonly List<(ObjectGuid Player, BattlegroundJoinError Error)> JoinErrors = [];
    public readonly List<(ObjectGuid Player, uint Limit)> LimitNotices = [];
    public readonly List<(ObjectGuid Member, ObjectGuid Leader, bool Portal)> EntryPoints = [];
    public readonly List<ObjectGuid> Prepared = [];
    public readonly List<(ObjectGuid Player, Battleground Bg, Team Team)> Sent = [];
    public readonly List<Battleground> Created = [];
    public readonly List<Battleground> Deleted = [];
    public uint NextInstance = 101;

    public bool IsOnline(ObjectGuid player) => !Offline.Contains(player);

    public IBattlegroundHost CreateMatchHost(BattlegroundType type, uint instanceId)
    {
        var host = new RecordingHost();
        MatchHosts[instanceId] = host;
        return host;
    }

    public uint AllocateInstanceId() => NextInstance++;

    public void SendStatus(ObjectGuid player, uint queueSlot, BattlegroundStatusSubject subject, BattlegroundStatus status, uint time1, uint time2)
        => Statuses.Add((player, queueSlot, subject, status, time1, time2));

    public void SendGroupJoined(ObjectGuid player, uint result) => GroupJoined.Add((player, result));

    public void SendJoinError(ObjectGuid player, BattlegroundJoinError error) => JoinErrors.Add((player, error));

    public void GroupQueueLimitNotice(ObjectGuid player, uint limit) => LimitNotices.Add((player, limit));

    public void StoreEntryPoint(ObjectGuid member, ObjectGuid leader, bool queuedAtPortal) => EntryPoints.Add((member, leader, queuedAtPortal));

    public void PrepareToPortIn(ObjectGuid player) => Prepared.Add(player);

    public void SendToBattleground(ObjectGuid player, Battleground battleground, Team team) => Sent.Add((player, battleground, team));

    public void BattlegroundCreated(Battleground battleground) => Created.Add(battleground);

    public void BattlegroundDeleted(Battleground battleground) => Deleted.Add(battleground);
}

/// <summary>The queue, the invitations and the player-facing flows of the battleground manager (vmangos BattleGroundMgr.cpp and BattleGroundHandler.cpp).</summary>
public sealed class BattlegroundManagerTests
{
    private const BattlegroundQueueType WsQueue = BattlegroundQueueType.WarsongGulch;

    private static (BattlegroundManager Mgr, RecordingManagerHost Host, RecordingPorts Ports) NewManager(BattlegroundOptions? options = null, uint minPerTeam = 2, uint maxPerTeam = 10)
    {
        var host = new RecordingManagerHost();
        var ports = new RecordingPorts();
        var mgr = new BattlegroundManager(options ?? new BattlegroundOptions(), host, ports.ToPorts(new RecordingHost()));
        mgr.RegisterTemplate(WsgTemplate(minPerTeam, maxPerTeam));
        return (mgr, host, ports);
    }

    private static BattlegroundMember M(ObjectGuid guid, Team team, uint level = 60, bool inWorld = true, bool deserter = false) => new(guid, team, level, inWorld, deserter);

    private static BattlegroundJoinResult Solo(BattlegroundManager mgr, ObjectGuid guid, Team team, uint level = 60, bool deserter = false, uint instance = 0, BattlegroundType type = BattlegroundType.WarsongGulch)
        => mgr.JoinQueue(new BattlegroundJoinRequest(M(guid, team, level, deserter: deserter), type, instance, AsGroup: false, QueuedAtPortal: false, []));

    private static BattlegroundJoinResult Party(BattlegroundManager mgr, ObjectGuid leader, Team team, params BattlegroundMember[] members)
        => mgr.JoinQueue(new BattlegroundJoinRequest(members.FirstOrDefault(m => m.Guid == leader) ?? M(leader, team), BattlegroundType.WarsongGulch, 0, AsGroup: true, QueuedAtPortal: false, members));

    private static void QueueFour(BattlegroundManager mgr)
    {
        Solo(mgr, Alliance[0], Team.Alliance);
        Solo(mgr, Alliance[1], Team.Alliance);
        Solo(mgr, Horde[0], Team.Horde);
        Solo(mgr, Horde[1], Team.Horde);
    }

    // ---------------------------------------------------------------- types and templates

    [Theory]
    [InlineData(BattlegroundType.AlteracValley, 30u)]
    [InlineData(BattlegroundType.WarsongGulch, 489u)]
    [InlineData(BattlegroundType.ArathiBasin, 529u)]
    [InlineData(BattlegroundType.None, 0u)]
    public void TypesAndMapsMapBothWays(BattlegroundType type, uint map)
    {
        Assert.Equal(map, BattlegroundManager.MapOfType(type));
        Assert.Equal(type, BattlegroundManager.TypeOfMap(map));
    }

    [Fact]
    public void ATemplateOnTheWrongMapIsRefused()
    {
        var (mgr, _, _) = NewManager();
        Assert.Throws<ArgumentException>(() => mgr.RegisterTemplate(WsgTemplate() with { MapId = 30 }));
    }

    [Theory]
    [InlineData(BattlegroundType.WarsongGulch, 0, 10u, 20u)]
    [InlineData(BattlegroundType.WarsongGulch, 4, 50u, 60u)]
    [InlineData(BattlegroundType.WarsongGulch, 5, 60u, 61u)]
    [InlineData(BattlegroundType.ArathiBasin, 0, 20u, 30u)]
    [InlineData(BattlegroundType.AlteracValley, 0, 51u, 61u)]
    public void BracketLevelRangesFollowVmangos(BattlegroundType type, int bracket, uint min, uint maxExclusive)
    {
        uint templateMin = type == BattlegroundType.ArathiBasin ? 20u : 10u;
        Assert.Equal(min, BattlegroundConstants.MinLevelOfBracket(type, bracket, templateMin));
        Assert.Equal(maxExclusive, BattlegroundConstants.MaxLevelOfBracket(type, bracket, templateMin));
    }

    // ---------------------------------------------------------------- joining alone

    [Fact]
    public void ASoloPlayerIsQueuedWithAWaitStatusForTheTemplate()
    {
        var (mgr, host, _) = NewManager();

        BattlegroundJoinResult result = mgr.JoinQueue(new BattlegroundJoinRequest(M(Alliance[0], Team.Alliance, 45), BattlegroundType.WarsongGulch, 0, false, QueuedAtPortal: true, []));

        Assert.Equal(BattlegroundJoinOutcome.Queued, result.Outcome);
        Assert.Equal([Alliance[0]], result.Queued);
        Assert.Equal(0, mgr.StateOf(Alliance[0]).SlotOf(WsQueue));
        Assert.Equal((Alliance[0], 0u, new BattlegroundStatusSubject(489, 255, 0), BattlegroundStatus.WaitQueue, 0u, 0u), host.Statuses.Single());
        Assert.Equal((Alliance[0], Alliance[0], true), host.EntryPoints.Single());
        Assert.NotNull(mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch));
    }

    [Fact]
    public void AJoinThatIsInvalidIsIgnoredAndChangesNothing()
    {
        var (mgr, host, _) = NewManager();

        Assert.Equal(BattlegroundJoinOutcome.Ignored, Solo(mgr, Alliance[0], Team.Alliance, type: BattlegroundType.None).Outcome);
        Assert.Equal(BattlegroundJoinOutcome.Ignored, Solo(mgr, Alliance[0], Team.Alliance, type: BattlegroundType.ArathiBasin).Outcome);   // no template
        Assert.Equal(BattlegroundJoinOutcome.Ignored, Solo(mgr, Alliance[0], Team.Alliance, level: 9).Outcome);
        Assert.Empty(host.Statuses);
        Assert.False(mgr.StateOf(Alliance[0]).InAnyQueue);
    }

    [Fact]
    public void ADeserterIsAnsweredWithTheDeserterResult()
    {
        var (mgr, host, _) = NewManager();

        BattlegroundJoinResult result = Solo(mgr, Alliance[0], Team.Alliance, deserter: true);

        Assert.Equal(BattlegroundJoinOutcome.Deserter, result.Outcome);
        Assert.Equal((Alliance[0], 0xFFFFFFFEu), host.GroupJoined.Single());
        Assert.False(mgr.StateOf(Alliance[0]).InAnyQueue);
    }

    [Fact]
    public void QueuingTwiceForTheSameBattlegroundIsIgnored()
    {
        var (mgr, host, _) = NewManager();
        Solo(mgr, Alliance[0], Team.Alliance);
        host.Statuses.Clear();

        Assert.Equal(BattlegroundJoinOutcome.Ignored, Solo(mgr, Alliance[0], Team.Alliance).Outcome);
        Assert.Empty(host.Statuses);
    }

    [Fact]
    public void ThePlayerNeedsAFreeQueueSlot()
    {
        var (mgr, _, _) = NewManager(new BattlegroundOptions { QueuesCount = 1 });
        mgr.RegisterTemplate(WsgTemplate() with { Type = BattlegroundType.ArathiBasin, MapId = 529, Name = "Arathi Basin" });
        Assert.Equal(BattlegroundJoinOutcome.Queued, Solo(mgr, Alliance[0], Team.Alliance, type: BattlegroundType.ArathiBasin).Outcome);

        Assert.Equal(BattlegroundJoinOutcome.Ignored, Solo(mgr, Alliance[0], Team.Alliance).Outcome);
        Assert.Equal(-1, mgr.StateOf(Alliance[0]).SlotOf(WsQueue));
    }

    [Fact]
    public void APlayerAlreadyInABattlegroundCannotQueue()
    {
        var (mgr, _, _) = NewManager();
        mgr.StateOf(Alliance[0]).InstanceId = 5;

        Assert.Equal(BattlegroundJoinOutcome.Ignored, Solo(mgr, Alliance[0], Team.Alliance).Outcome);
    }

    [Fact]
    public void AGroupMayNotQueueForAlterac()
    {
        var (mgr, _, _) = NewManager();
        BattlegroundJoinResult result = mgr.JoinQueue(new BattlegroundJoinRequest(M(Alliance[0], Team.Alliance), BattlegroundType.AlteracValley, 0, true, false, [M(Alliance[0], Team.Alliance)]));
        Assert.Equal(BattlegroundJoinOutcome.GroupForAlterac, result.Outcome);
    }

    // ---------------------------------------------------------------- joining as a group

    [Fact]
    public void AGroupQueuesTogetherAndEveryMemberIsToldTheMap()
    {
        var (mgr, host, _) = NewManager();

        BattlegroundJoinResult result = Party(mgr, Alliance[0], Team.Alliance, M(Alliance[0], Team.Alliance), M(Alliance[1], Team.Alliance), M(Alliance[2], Team.Alliance));

        Assert.Equal(BattlegroundJoinOutcome.Queued, result.Outcome);
        Assert.Equal(3, result.Queued.Count);
        Assert.Equal(3, host.Statuses.Count);
        Assert.All(host.Statuses, s => Assert.Equal(BattlegroundStatus.WaitQueue, s.Status));
        Assert.Equal([(Alliance[0], 489u), (Alliance[1], 489u), (Alliance[2], 489u)], host.GroupJoined);
        Assert.All(host.EntryPoints, e => Assert.Equal(Alliance[0], e.Leader));
        QueuedGroup group = mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch)!;
        Assert.Same(group, mgr.QueuedGroupOf(Alliance[2], BattlegroundType.WarsongGulch));
        Assert.Equal(3, group.Size);
    }

    [Theory]
    [MemberData(nameof(GroupErrors))]
    public void AGroupThatBreaksARuleIsRefusedWithItsError(string name, BattlegroundMember[] members, BattlegroundJoinError expected)
    {
        var (mgr, host, _) = NewManager(new BattlegroundOptions { TagInBattlegrounds = name != "tagged" });
        Solo(mgr, Alliance[9], Team.Alliance);                      // a member already in the queue
        mgr.StateOf(Alliance[8]).InstanceId = 7;                    // a member inside a battleground
        host.Statuses.Clear();

        BattlegroundJoinResult result = Party(mgr, Alliance[0], Team.Alliance, members);

        Assert.Equal(BattlegroundJoinOutcome.GroupError, result.Outcome);
        Assert.Equal(expected, result.GroupError);
        Assert.Equal((Alliance[0], expected), host.JoinErrors.Single());
        Assert.Empty(result.Queued);
        Assert.Empty(host.Statuses);
        Assert.False(mgr.StateOf(Alliance[0]).InAnyQueue);
    }

    public static TheoryData<string, BattlegroundMember[], BattlegroundJoinError> GroupErrors()
    {
        BattlegroundMember a(int i, uint level = 60, bool inWorld = true, bool deserter = false) => M(Alliance[i], Team.Alliance, level, inWorld, deserter);
        return new TheoryData<string, BattlegroundMember[], BattlegroundJoinError>
        {
            { "too many", [.. Enumerable.Range(0, 11).Select(i => M(ObjectGuid.Player((uint)(500 + i)), Team.Alliance))], BattlegroundJoinError.GroupTooMany },
            { "offline", [a(0), a(1, inWorld: false)], BattlegroundJoinError.OfflineMember },
            { "faction", [a(0), M(Horde[0], Team.Horde)], BattlegroundJoinError.MixedFaction },
            { "queued", [a(0), a(9)], BattlegroundJoinError.GroupMemberAlreadyInQueue },
            { "deserter", [a(0), a(1, deserter: true)], BattlegroundJoinError.GroupDeserter },
            { "tagged", [a(0), a(8)], BattlegroundJoinError.OfflineMember },
        };
    }

    [Fact]
    public void ADeserterInAGroupAlsoGetsTheDeserterResult()
    {
        var (mgr, host, _) = NewManager();

        Party(mgr, Alliance[0], Team.Alliance, M(Alliance[0], Team.Alliance), M(Alliance[1], Team.Alliance, deserter: true));

        Assert.Equal((Alliance[0], 0xFFFFFFFEu), host.GroupJoined.Single());
    }

    [Fact]
    public void AMemberInAnotherBracketIsLeftOutAndTheRestQueue()
    {
        var (mgr, host, _) = NewManager();

        BattlegroundJoinResult result = Party(mgr, Alliance[0], Team.Alliance,
            M(Alliance[0], Team.Alliance, 25), M(Alliance[1], Team.Alliance, 29), M(Alliance[2], Team.Alliance, 45));

        Assert.Equal(BattlegroundJoinOutcome.Queued, result.Outcome);
        Assert.Equal([Alliance[0], Alliance[1]], result.Queued);
        Assert.Contains((Alliance[2], 0xFFFFFFFFu), host.GroupJoined);
        Assert.False(mgr.StateOf(Alliance[2]).InAnyQueue);
        Assert.Equal(2, mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch)!.Size);
    }

    [Fact]
    public void AGroupLargerThanTheLimitIsQueuedAsIndividuals()
    {
        var (mgr, host, _) = NewManager(new BattlegroundOptions { GroupQueueLimit = 2 });

        BattlegroundJoinResult result = Party(mgr, Alliance[0], Team.Alliance, M(Alliance[0], Team.Alliance), M(Alliance[1], Team.Alliance), M(Alliance[2], Team.Alliance));

        Assert.Equal(BattlegroundJoinOutcome.Queued, result.Outcome);
        Assert.Equal(3, host.LimitNotices.Count);
        Assert.All(host.LimitNotices, n => Assert.Equal(2u, n.Limit));
        Assert.Equal(1, mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch)!.Size);
        Assert.NotSame(mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch), mgr.QueuedGroupOf(Alliance[1], BattlegroundType.WarsongGulch));
    }

    // ---------------------------------------------------------------- creating a match

    [Fact]
    public void TheMinimumOnEachSideStartsAMatchAndInvitesEveryone()
    {
        var (mgr, host, _) = NewManager();
        QueueFour(mgr);
        host.Statuses.Clear();

        mgr.Update(1000);

        Battleground bg = Assert.Single(host.Created);
        Assert.Equal(101u, bg.InstanceId);
        Assert.Equal(1u, bg.ClientInstanceId);
        Assert.Equal(5, bg.Bracket);
        Assert.Equal((60u, 60u), (bg.MinLevel, bg.MaxLevel));
        Assert.Same(bg, mgr.GetBattleground(101, BattlegroundType.WarsongGulch));
        Assert.Same(bg, mgr.GetBattlegroundThroughClientInstance(1, BattlegroundType.WarsongGulch));
        Assert.Equal([1u], mgr.ClientInstanceIds(BattlegroundType.WarsongGulch, 5));
        Assert.Equal(2u, bg.InvitedCount(Team.Alliance));
        Assert.Equal(2u, bg.InvitedCount(Team.Horde));
        Assert.True(bg.InFreeSlotQueue);

        Assert.Equal(4, host.Statuses.Count);
        Assert.All(host.Statuses, s =>
        {
            Assert.Equal(BattlegroundStatus.WaitJoin, s.Status);
            Assert.Equal(80_000u, s.Time1);
            Assert.Equal(new BattlegroundStatusSubject(489, 5, 1), s.Subject);
            Assert.Equal(0u, s.Slot);
        });
        Assert.Equal(101u, mgr.StateOf(Alliance[0]).InvitedInstance(WsQueue));
    }

    [Fact]
    public void OneSideBelowTheMinimumStartsNothing()
    {
        var (mgr, host, _) = NewManager();
        Solo(mgr, Alliance[0], Team.Alliance);
        Solo(mgr, Alliance[1], Team.Alliance);
        Solo(mgr, Horde[0], Team.Horde);

        mgr.Update(1000);

        Assert.Empty(host.Created);
        Assert.Equal(0u, mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch)!.InvitedToInstanceId);
    }

    [Fact]
    public void ADifferentBracketNeverMeetsAnother()
    {
        var (mgr, host, _) = NewManager();
        Solo(mgr, Alliance[0], Team.Alliance, 15);
        Solo(mgr, Alliance[1], Team.Alliance, 15);
        Solo(mgr, Horde[0], Team.Horde, 55);
        Solo(mgr, Horde[1], Team.Horde, 55);

        mgr.Update(1000);

        Assert.Empty(host.Created);
    }

    [Fact]
    public void ABracketsMatchGetsItsLevelRange()
    {
        var (mgr, host, _) = NewManager();
        foreach (ObjectGuid g in Alliance.Take(2))
        {
            Solo(mgr, g, Team.Alliance, 23);
        }

        foreach (ObjectGuid g in Horde.Take(2))
        {
            Solo(mgr, g, Team.Horde, 28);
        }

        mgr.Update(1000);

        Battleground bg = Assert.Single(host.Created);
        Assert.Equal(1, bg.Bracket);
        Assert.Equal((20u, 29u), (bg.MinLevel, bg.MaxLevel));
    }

    [Fact]
    public void AFullMatchLeavesTheRestForASecondMatchOnTheNextQueueRun()
    {
        var (mgr, host, _) = NewManager(minPerTeam: 2, maxPerTeam: 2);
        for (int i = 0; i < 4; i++)
        {
            Solo(mgr, Alliance[i], Team.Alliance);
            Solo(mgr, Horde[i], Team.Horde);
        }

        // One match per queue run (BattleGroundMgr.cpp:884-889): the first match takes two a side, the other four wait.
        mgr.Update(1000);
        Assert.Single(host.Created);
        Assert.Equal(0u, mgr.QueuedGroupOf(Alliance[2], BattlegroundType.WarsongGulch)!.InvitedToInstanceId);

        mgr.ScheduleQueueUpdate(BattlegroundType.WarsongGulch, 5);
        mgr.Update(1);

        Assert.Equal(2, host.Created.Count);
        Assert.Equal([1u, 2u], host.Created.Select(b => b.ClientInstanceId).ToArray());
        Assert.Equal([101u, 102u], host.Created.Select(b => b.InstanceId).ToArray());
        Assert.Equal([1u, 2u], mgr.ClientInstanceIds(BattlegroundType.WarsongGulch, 5));
    }

    [Fact]
    public void AFreedClientInstanceIdIsReusedBeforeANewOne()
    {
        var (mgr, host, _) = NewManager(minPerTeam: 2, maxPerTeam: 2);
        for (int i = 0; i < 4; i++)
        {
            Solo(mgr, Alliance[i], Team.Alliance);
            Solo(mgr, Horde[i], Team.Horde);
        }

        mgr.Update(1000);
        mgr.ScheduleQueueUpdate(BattlegroundType.WarsongGulch, 5);
        mgr.Update(1);
        Assert.Equal([1u, 2u], mgr.ClientInstanceIds(BattlegroundType.WarsongGulch, 5));

        // The first match's four players never answer: it lapses (the second one lapses a tick later) and is deleted, id 1 is free again.
        mgr.Update(79_999);
        Assert.Contains(host.Created[0], host.Deleted);
        Assert.Equal([2u], mgr.ClientInstanceIds(BattlegroundType.WarsongGulch, 5));
    }

    [Fact]
    public void AnEmptyMatchWithNobodyInvitedIsDeletedAndItsClientIdFreed()
    {
        var (mgr, host, _) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);
        Battleground bg = host.Created.Single();

        // Nobody accepts: the invitations lapse after 80 seconds, then the empty match goes.
        mgr.Update(80_000);

        Assert.Contains(bg, host.Deleted);
        Assert.Null(mgr.GetBattleground(101, BattlegroundType.WarsongGulch));
        Assert.Empty(mgr.ClientInstanceIds(BattlegroundType.WarsongGulch, 5));
        Assert.False(bg.InFreeSlotQueue);
        Assert.Empty(mgr.FreeSlotBattlegrounds(BattlegroundType.WarsongGulch));
    }

    // ---------------------------------------------------------------- balance

    [Fact]
    public void BalancedInvitationsKeepTheSidesWithinTwoPlayers()
    {
        var (mgr, host, _) = NewManager(new BattlegroundOptions { InvitationType = 1 });
        for (int i = 0; i < 8; i++)
        {
            Solo(mgr, Alliance[i], Team.Alliance);
        }

        Solo(mgr, Horde[0], Team.Horde);
        Solo(mgr, Horde[1], Team.Horde);

        mgr.Update(1000);

        Battleground bg = host.Created.Single();
        Assert.True(bg.InvitedCount(Team.Alliance) <= bg.InvitedCount(Team.Horde) + 2, $"{bg.InvitedCount(Team.Alliance)} v {bg.InvitedCount(Team.Horde)}");
        Assert.True(bg.InvitedCount(Team.Horde) >= 2);
    }

    [Fact]
    public void ABalancedMatchIsRefusedWhenTheSidesWouldDifferByMoreThanTwoPlayers()
    {
        var (mgr, host, _) = NewManager();
        BattlegroundMember[] party = [.. Alliance.Take(6).Select(g => M(g, Team.Alliance))];
        Party(mgr, Alliance[0], Team.Alliance, party);
        Solo(mgr, Horde[0], Team.Horde);
        Solo(mgr, Horde[1], Team.Horde);

        // Six against two and, after topping the Horde up, six against three: more than two apart (BattleGroundMgr.cpp:618-620).
        mgr.Update(1000);
        Assert.Empty(host.Created);
        Solo(mgr, Horde[2], Team.Horde);
        mgr.Update(1000);
        Assert.Empty(host.Created);

        // Six against four is within two.
        Solo(mgr, Horde[3], Team.Horde);
        mgr.Update(1000);
        Assert.Single(host.Created);
    }

    [Fact]
    public void WithoutBalancingTheSmallSideIsEnough()
    {
        var (mgr, host, _) = NewManager(new BattlegroundOptions { InvitationType = 0 });
        Party(mgr, Alliance[0], Team.Alliance, [.. Alliance.Take(6).Select(g => M(g, Team.Alliance))]);
        Solo(mgr, Horde[0], Team.Horde);
        Solo(mgr, Horde[1], Team.Horde);

        mgr.Update(1000);

        Assert.Single(host.Created);
    }
    [Fact]
    public void InvitationTypeZeroFillsTheMatchInQueueOrder()
    {
        var (mgr, host, _) = NewManager(new BattlegroundOptions { InvitationType = 0 });
        for (int i = 0; i < 8; i++)
        {
            Solo(mgr, Alliance[i], Team.Alliance);
        }

        Solo(mgr, Horde[0], Team.Horde);
        Solo(mgr, Horde[1], Team.Horde);

        mgr.Update(1000);

        Battleground bg = host.Created.Single();
        Assert.Equal(8u, bg.InvitedCount(Team.Alliance));
        Assert.Equal(2u, bg.InvitedCount(Team.Horde));
    }

    [Fact]
    public void LateJoinersFillARunningMatchWithFreeSlots()
    {
        var (mgr, host, _) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);
        Battleground bg = host.Created.Single();

        Solo(mgr, Alliance[2], Team.Alliance);
        Solo(mgr, Horde[2], Team.Horde);
        mgr.Update(1000);

        Assert.Single(host.Created);
        Assert.Equal(3u, bg.InvitedCount(Team.Alliance));
        Assert.Equal(3u, bg.InvitedCount(Team.Horde));
        Assert.Equal(101u, mgr.StateOf(Alliance[2]).InvitedInstance(WsQueue));
    }

    [Fact]
    public void AQueuedPlayerWhoAskedForAnInstanceIsOnlyInvitedToIt()
    {
        var (mgr, host, _) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);
        Solo(mgr, Alliance[2], Team.Alliance, instance: 7);
        Solo(mgr, Horde[2], Team.Horde);
        mgr.Update(1000);

        Assert.Equal(0u, mgr.StateOf(Alliance[2]).InvitedInstance(WsQueue));
        Assert.Equal(101u, mgr.StateOf(Horde[2]).InvitedInstance(WsQueue));
    }

    [Fact]
    public void TheAverageWaitIsTheMeanOfTheLastTenInvitedPlayers()
    {
        var (mgr, host, _) = NewManager();
        for (int i = 0; i < 10; i++)
        {
            Solo(mgr, Alliance[i], Team.Alliance);
            Solo(mgr, Horde[i], Team.Horde);
        }

        mgr.Update(30_000);
        Assert.Equal(20, host.Statuses.Count(s => s.Status == BattlegroundStatus.WaitJoin));

        // Ten alliance players waited 30 s each: a new alliance player is told 30 s.
        host.Statuses.Clear();
        Solo(mgr, Alliance[10], Team.Alliance);
        Assert.Equal(30_000u, host.Statuses.Single().Time1);
        Assert.Equal(30_000u, mgr.AverageWaitOf(Alliance[10], BattlegroundType.WarsongGulch, 60));
    }

    [Fact]
    public void WithFewerThanTenInvitedTheAverageIsZero()
    {
        var (mgr, host, _) = NewManager();
        QueueFour(mgr);
        mgr.Update(30_000);
        host.Statuses.Clear();

        Solo(mgr, Alliance[5], Team.Alliance);

        Assert.Equal(0u, host.Statuses.Single().Time1);
    }

    // ---------------------------------------------------------------- invitations in time

    [Fact]
    public void AReminderComesAMinuteAfterTheInvitationAndTheInvitationLapsesAfterEighty()
    {
        var (mgr, host, _) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);
        host.Statuses.Clear();

        mgr.Update(59_999);
        Assert.Empty(host.Statuses);
        mgr.Update(1);

        Assert.Equal(4, host.Statuses.Count);
        Assert.All(host.Statuses, s => Assert.Equal((BattlegroundStatus.WaitJoin, 20_000u), (s.Status, s.Time1)));

        host.Statuses.Clear();
        mgr.Update(19_999);
        Assert.False(mgr.StateOf(Alliance[0]).SlotOf(WsQueue) < 0);
        mgr.Update(1);

        Assert.Equal(4, host.Statuses.Count);
        Assert.All(host.Statuses, s => Assert.Equal(BattlegroundStatus.None, s.Status));
        Assert.All(new[] { Alliance[0], Alliance[1], Horde[0], Horde[1] }, g =>
        {
            Assert.Equal(-1, mgr.StateOf(g).SlotOf(WsQueue));
            Assert.Null(mgr.QueuedGroupOf(g, BattlegroundType.WarsongGulch));
        });
        Assert.Equal(0u, host.Created[0].InvitedCount(Team.Alliance));
    }

    [Fact]
    public void AnOfflinePlayersInvitationLapsesWithoutAPacket()
    {
        var (mgr, host, _) = NewManager();
        QueueFour(mgr);
        host.Offline.Add(Alliance[1]);
        mgr.Update(1000);

        Assert.DoesNotContain(host.Statuses, s => s.Player == Alliance[1] && s.Status == BattlegroundStatus.WaitJoin);
        // vmangos still counts the offline player as invited (BattleGroundMgr.cpp:416).
        Assert.Equal(2u, host.Created[0].InvitedCount(Team.Alliance));
    }

    // ---------------------------------------------------------------- the port

    private static (BattlegroundManager Mgr, RecordingManagerHost Host, Battleground Bg) WithInvitations()
    {
        var (mgr, host, _) = NewManager();
        QueueFour(mgr);
        mgr.Update(1000);
        host.Statuses.Clear();
        return (mgr, host, host.Created.Single());
    }

    [Fact]
    public void AcceptingAnInvitationSendsThePlayerToTheMatchAndTheAckAddsIt()
    {
        var (mgr, host, bg) = WithInvitations();

        BattlegroundPortOutcome outcome = mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 60, false));

        Assert.Equal(BattlegroundPortOutcome.Entering, outcome);
        Assert.Equal([Alliance[0]], host.Prepared);
        Assert.Equal((Alliance[0], bg, Team.Alliance), host.Sent.Single());
        Assert.Equal((Alliance[0], 0u, BattlegroundStatusSubject.Of(bg), BattlegroundStatus.InProgress, 0u, bg.StartTimeMs), host.Statuses.Single());
        BattlegroundPlayerState state = mgr.StateOf(Alliance[0]);
        Assert.Equal((101u, BattlegroundType.WarsongGulch, (Team?)Team.Alliance), (state.InstanceId, state.Type, state.Team));
        Assert.Null(mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch));
        Assert.Equal(0, bg.PlayerCount);

        Assert.True(mgr.EnterBattleground(Alliance[0]));
        Assert.Equal(Team.Alliance, bg.PlayerTeam(Alliance[0]));
        Assert.False(mgr.EnterBattleground(Alliance[1]));     // not bound to a match
    }

    [Fact]
    public void MatchStatusOf_IsTheBoundMatchsStatus_AndNullOutsideAnyMatch()
    {
        var (mgr, _, bg) = WithInvitations();
        Assert.Null(mgr.MatchStatusOf(Alliance[0])); // invited, not bound yet

        mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 60, false));

        Assert.Equal(BattlegroundStatus.WaitJoin, bg.Status); // the doors are still closed
        Assert.Equal(BattlegroundStatus.WaitJoin, mgr.MatchStatusOf(Alliance[0]));
        Assert.Null(mgr.MatchStatusOf(Alliance[1]));
    }

    [Fact]
    public void ThePlayerWhoAcceptedIsNotRemovedWhenTheInvitationWouldHaveLapsed()
    {
        var (mgr, host, bg) = WithInvitations();
        mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 60, false));
        mgr.EnterBattleground(Alliance[0]);

        mgr.Update(80_000);

        Assert.Equal(101u, mgr.StateOf(Alliance[0]).InstanceId);
        Assert.Equal(0, mgr.StateOf(Alliance[0]).SlotOf(WsQueue));   // the slot stays until the player leaves
        Assert.Equal(1, bg.PlayerCount);
        Assert.DoesNotContain(host.Deleted, b => b == bg);
        // The others did not answer.
        Assert.Equal(-1, mgr.StateOf(Alliance[1]).SlotOf(WsQueue));
    }

    [Fact]
    public void LeavingTheQueueFreesTheSlotAndTellsThePlayer()
    {
        var (mgr, host, bg) = WithInvitations();

        BattlegroundPortOutcome outcome = mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 0, 60, false));

        Assert.Equal(BattlegroundPortOutcome.LeftQueue, outcome);
        Assert.Equal((Alliance[0], 0u, BattlegroundStatusSubject.Of(bg), BattlegroundStatus.None, 0u, 0u), host.Statuses.Single());
        Assert.Equal(-1, mgr.StateOf(Alliance[0]).SlotOf(WsQueue));
        Assert.Null(mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch));
        // The invitation is given back (RemovePlayer(guid, true)).
        Assert.Equal(1u, bg.InvitedCount(Team.Alliance));
    }

    [Fact]
    public void LeavingAQueueBeforeAnyInvitationUsesTheTemplate()
    {
        var (mgr, host, _) = NewManager();
        Solo(mgr, Alliance[0], Team.Alliance);
        host.Statuses.Clear();

        Assert.Equal(BattlegroundPortOutcome.LeftQueue, mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 0, 60, false)));

        Assert.Equal(BattlegroundStatusSubject.OfTemplate(WsgTemplate()), host.Statuses.Single().Subject);
    }

    [Fact]
    public void ADeserterWhoAcceptsIsRemovedFromTheQueueWithTheMessage()
    {
        var (mgr, host, _) = WithInvitations();

        BattlegroundPortOutcome outcome = mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 60, Deserter: true));

        Assert.Equal(BattlegroundPortOutcome.LeftQueue, outcome);
        Assert.Equal((Alliance[0], 0xFFFFFFFEu), host.GroupJoined.Single());
        Assert.Empty(host.Sent);
    }

    [Fact]
    public void APlayerWhoLevelledPastTheMaximumIsNotPorted()
    {
        var (mgr, host, _) = WithInvitations();

        Assert.Equal(BattlegroundPortOutcome.LeftQueue, mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 61, false)));
        Assert.Empty(host.Sent);
    }

    [Fact]
    public void ABattlegroundThatAlreadyEndedCannotBeEntered()
    {
        var (mgr, host, bg) = WithInvitations();
        bg.AddPlayer(Horde[0], Team.Horde);
        bg.EndBattleground(Team.Horde);

        Assert.Equal(BattlegroundPortOutcome.LeftQueue, mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 60, false)));
        Assert.Empty(host.Sent);
    }

    [Theory]
    [InlineData(0u, 1)]      // not a battleground map
    [InlineData(1u, 1)]
    [InlineData(489u, 2)]    // unknown action
    public void NonsenseRequestsAreIgnored(uint map, int action)
    {
        var (mgr, host, _) = WithInvitations();

        Assert.Equal(BattlegroundPortOutcome.Ignored, mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], map, (byte)action, 60, false)));
        Assert.Empty(host.Sent);
        Assert.NotNull(mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch));
    }

    [Fact]
    public void ARequestFromAPlayerNotInAQueueOrNotYetInvitedIsIgnored()
    {
        var (mgr, host, _) = NewManager();
        Assert.Equal(BattlegroundPortOutcome.Ignored, mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 60, false)));

        Solo(mgr, Alliance[0], Team.Alliance);
        Assert.Equal(BattlegroundPortOutcome.Ignored, mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 60, false)));
        Assert.Empty(host.Sent);
    }

    // ---------------------------------------------------------------- logout and login

    [Fact]
    public void ALoggedOutPlayerKeepsItsPlaceForAMinute()
    {
        var (mgr, host, _) = NewManager();
        Solo(mgr, Alliance[0], Team.Alliance);

        mgr.PlayerLoggedOut(Alliance[0]);
        Assert.Equal(-1, mgr.StateOf(Alliance[0]).SlotOf(WsQueue));
        Assert.NotNull(mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch));

        mgr.ScheduleQueueUpdate(BattlegroundType.WarsongGulch, 5);
        mgr.Update(60_000);
        Assert.NotNull(mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch));

        mgr.ScheduleQueueUpdate(BattlegroundType.WarsongGulch, 5);
        mgr.Update(1);
        Assert.Null(mgr.QueuedGroupOf(Alliance[0], BattlegroundType.WarsongGulch));
        Assert.NotNull(host);
    }

    [Fact]
    public void LoggingBackInRestoresTheSlotAndShowsTheWaitTime()
    {
        var (mgr, host, _) = NewManager();
        Solo(mgr, Alliance[0], Team.Alliance);
        mgr.Update(5000);
        mgr.PlayerLoggedOut(Alliance[0]);
        host.Statuses.Clear();

        mgr.Update(10_000);
        mgr.PlayerLoggedIn(Alliance[0], 60);

        Assert.Equal(0, mgr.StateOf(Alliance[0]).SlotOf(WsQueue));
        Assert.Equal((Alliance[0], 0u, BattlegroundStatusSubject.OfTemplate(WsgTemplate()), BattlegroundStatus.WaitQueue, 0u, 15_000u), host.Statuses.Single());
    }

    [Fact]
    public void LoggingBackInWhileInvitedRestartsTheLapseTimer()
    {
        var (mgr, host, bg) = WithInvitations();
        mgr.PlayerLoggedOut(Alliance[0]);
        mgr.Update(10_000);
        mgr.PlayerLoggedIn(Alliance[0], 60);
        Assert.Equal(101u, mgr.StateOf(Alliance[0]).InvitedInstance(WsQueue));

        host.Statuses.Clear();
        mgr.Update(70_000);

        Assert.Contains(host.Statuses, s => s.Player == Alliance[0] && s.Status == BattlegroundStatus.None);
        Assert.NotNull(bg);
    }

    // ---------------------------------------------------------------- the status poll

    [Fact]
    public void TheStatusPollShowsWaitingInvitedAndInside()
    {
        var (mgr, host, bg) = NewManager().WithQueued();
        Assert.Empty(mgr.StatusReports(Alliance[5], 60));

        // Level 25 is another bracket, so this player keeps waiting.
        Solo(mgr, Alliance[5], Team.Alliance, 25);
        mgr.Update(7000);
        BattlegroundStatusReport waiting = mgr.StatusReports(Alliance[5], 25).Single();
        Assert.Equal((0u, BattlegroundStatus.WaitQueue, 0u, 7000u), (waiting.QueueSlot, waiting.Status, waiting.Time1, waiting.Time2));

        BattlegroundStatusReport invited = mgr.StatusReports(Alliance[0], 60).Single();
        Assert.Equal((BattlegroundStatus.WaitJoin, 73_000u), (invited.Status, invited.Time1));      // the 80 s deadline set at t=1000, now t=8000
        Assert.Equal(BattlegroundStatusSubject.Of(bg), invited.Subject);

        mgr.HandlePort(new BattlegroundPortRequest(Alliance[0], 489, 1, 60, false));
        mgr.EnterBattleground(Alliance[0]);
        BattlegroundStatusReport inside = mgr.StatusReports(Alliance[0], 60).Single();
        Assert.Equal((BattlegroundStatus.InProgress, 0u), (inside.Status, inside.Time1));
        Assert.Equal(bg.StartTimeMs, inside.Time2);

        host.Statuses.Clear();
        mgr.SendStatusReports(Alliance[0], 60);
        Assert.Single(host.Statuses);
    }

    // ---------------------------------------------------------------- selection pool

    private static QueuedGroup Group(int size, uint desired = 0, uint invited = 0)
    {
        var group = new QueuedGroup(BattlegroundType.WarsongGulch, Team.Alliance, 0, desired) { InvitedToInstanceId = invited };
        for (int i = 0; i < size; i++)
        {
            group.PlayerList.Add(ObjectGuid.Player((uint)(1000 + i)));
        }

        return group;
    }

    [Fact]
    public void ThePoolTakesGroupsThatFitAndSaysWhenToStop()
    {
        var pool = new SelectionPool();

        Assert.True(pool.AddGroup(Group(4), 10, 0));
        Assert.True(pool.AddGroup(Group(5), 10, 0));
        Assert.Equal(9u, pool.PlayerCount);
        // A group of 3 does not fit, but there is still room for one: keep offering.
        Assert.True(pool.AddGroup(Group(3), 10, 0));
        Assert.Equal(9u, pool.PlayerCount);
        Assert.True(pool.AddGroup(Group(1), 10, 0));
        Assert.Equal(10u, pool.PlayerCount);
        // Full: stop.
        Assert.False(pool.AddGroup(Group(1), 10, 0));
    }

    [Fact]
    public void ThePoolIgnoresInvitedGroupsAndGroupsForAnotherInstance()
    {
        var pool = new SelectionPool();

        pool.AddGroup(Group(2, invited: 9), 10, 0);
        pool.AddGroup(Group(2, desired: 3), 10, 1);
        pool.AddGroup(Group(2, desired: 3), 10, 3);

        Assert.Equal(2u, pool.PlayerCount);
        Assert.Single(pool.SelectedGroups);
    }

    [Fact]
    public void KickingRemovesTheLastGroupWithinOneOfTheSizeOtherwiseTheLargest()
    {
        var pool = new SelectionPool();
        QueuedGroup three = Group(3);
        QueuedGroup five = Group(5);
        QueuedGroup four = Group(4);
        pool.AddGroup(three, 20, 0);
        pool.AddGroup(five, 20, 0);
        pool.AddGroup(four, 20, 0);

        // Wanted size 4: groups of 3, 5 and 4 are all within one; the last of them (4) goes and the pool has enough (returns false).
        Assert.False(pool.KickGroup(4));
        Assert.DoesNotContain(four, pool.SelectedGroups);
        Assert.Equal(8u, pool.PlayerCount);

        // Wanted size 1: nothing is within one, the largest (5) goes, and since it was bigger than size + 1 the caller should add more.
        Assert.True(pool.KickGroup(1));
        Assert.DoesNotContain(five, pool.SelectedGroups);
        Assert.Equal(3u, pool.PlayerCount);
    }

    [Fact]
    public void KickingAnEmptyPoolDoesNothingAndAsksForMore()
    {
        var pool = new SelectionPool();
        Assert.True(pool.KickGroup(3));
        Assert.Equal(0u, pool.PlayerCount);
    }
}

internal static class ManagerTestExtensions
{
    /// <summary>A manager with Alliance 0 and 1 and Horde 0 and 1 invited to a match (the usual starting point).</summary>
    public static (BattlegroundManager Mgr, RecordingManagerHost Host, Battleground Bg) WithQueued(this (BattlegroundManager Mgr, RecordingManagerHost Host, RecordingPorts Ports) setup)
    {
        foreach (ObjectGuid g in Alliance.Take(2))
        {
            setup.Mgr.JoinQueue(new BattlegroundJoinRequest(new BattlegroundMember(g, Team.Alliance, 60, true, false), BattlegroundType.WarsongGulch, 0, false, false, []));
        }

        foreach (ObjectGuid g in Horde.Take(2))
        {
            setup.Mgr.JoinQueue(new BattlegroundJoinRequest(new BattlegroundMember(g, Team.Horde, 60, true, false), BattlegroundType.WarsongGulch, 0, false, false, []));
        }

        setup.Mgr.Update(1000);
        return (setup.Mgr, setup.Host, setup.Host.Created.Single());
    }
}
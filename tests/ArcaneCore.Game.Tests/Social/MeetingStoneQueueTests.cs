using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Lfg;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// The meeting stone queue (vmangos LFGMgr / LFGQueue, LFG/LFGQueue.cpp, and the Group hooks, Group.cpp:418-605) over the real
/// <see cref="GroupManager"/>.
/// </summary>
public sealed class MeetingStoneQueueTests : IDisposable
{
    private const uint Area = 719; // Blackfathom Deeps meeting stone 178828 (classic-db gameobject_template data2)

    private readonly SocialFixture _f = new();
    private readonly LfgQueue _queue;

    public MeetingStoneQueueTests()
    {
        GroupManager groups = _f.Context.Groups;
        _queue = new LfgQueue(new Groups(groups, _f));
        groups.MemberAdded += (group, _) => _queue.OnMemberJoined(group);
        groups.MemberLeft += _queue.OnMemberRemoved;
        groups.Disbanding += (group, _) => _queue.OnDisbanding(group);
    }

    public void Dispose() => _f.Dispose();

    private sealed class Groups(GroupManager groups, SocialFixture f) : ILfgGroups
    {
        public Group? GroupOf(ObjectGuid player) => groups.GetGroup(player);

        public Group? CreateGroup(Player leader, Player member) => groups.CreateLfgGroup(leader, member);

        public bool AddMember(Group group, Player member) => groups.AddLfgMember(group, member);

        public void Broadcast(Group group, WorldOpcode opcode, byte[] payload) => groups.Broadcast(group, opcode, payload);

        public Player? FindOnlinePlayer(ObjectGuid guid) => f.World.FindOnlinePlayer(guid);

        public Class ClassOf(ObjectGuid guid) => f.World.FindOnlinePlayer(guid)?.Class ?? 0;
    }

    private Player Add(uint guid, Class playerClass)
    {
        Player player = _f.AddPlayer(guid, x: guid * 300);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)playerClass);
        return player;
    }

    private static (uint Area, MeetingStoneStatus Status) SetQueue(byte[] payload)
        => (BitConverter.ToUInt32(payload, 0), (MeetingStoneStatus)payload[4]);

    private (uint, MeetingStoneStatus)[] SetQueues(Player player)
        => [.. _f.Sent(player, WorldOpcode.SmsgMeetingstoneSetqueue).Select(SetQueue)];

    private void Pass() => _queue.Update(LfgQueue.UpdateIntervalMs);

    private Group Party(Player leader, params Player[] members)
    {
        foreach (Player member in members)
        {
            _f.Context.Groups.Invite(leader, member.Name);
            _f.Context.Groups.Accept(member);
        }

        return _f.Context.Groups.GetGroup(leader.Guid)!;
    }

    [Fact]
    public void SoloPlayer_JoinsInfoAndLeaves()
    {
        Player mage = Add(1, Class.Mage);

        _queue.Join(mage, Area);
        Assert.Equal([(Area, MeetingStoneStatus.JoinedQueue)], SetQueues(mage));
        _f.ClearAll();
        _queue.Info(mage);
        Assert.Equal([(Area, MeetingStoneStatus.JoinedQueue)], SetQueues(mage));

        _f.ClearAll();
        _queue.Leave(mage);
        Assert.Equal([(0u, MeetingStoneStatus.LeaveQueue)], SetQueues(mage));
        Assert.False(_queue.IsPlayerQueued(mage.Guid));
        _f.ClearAll();
        _queue.Info(mage);
        Assert.Equal([(0u, MeetingStoneStatus.None)], SetQueues(mage));
    }

    [Fact]
    public void QueuedParty_IsFilledByRole_ThenCompletes()
    {
        Player warrior = Add(1, Class.Warrior);
        Player mage = Add(2, Class.Mage);
        Group party = Party(warrior, mage);
        _f.ClearAll();

        _queue.Join(warrior, Area);
        Assert.Equal([(Area, MeetingStoneStatus.JoinedQueue)], SetQueues(warrior));
        Assert.Equal([(Area, MeetingStoneStatus.JoinedQueue)], SetQueues(mage));
        Assert.True(_queue.IsGroupQueued(party));

        Player priest = Add(3, Class.Priest);
        Player rogue = Add(4, Class.Rogue);
        Player hunter = Add(5, Class.Hunter);
        Player otherArea = Add(6, Class.Paladin);
        _queue.Join(priest, Area);
        _queue.Join(rogue, Area);
        _queue.Join(hunter, Area);
        _queue.Join(otherArea, Area + 1);

        _f.ClearAll();
        Pass();

        // The warrior tanks, the mage, rogue and hunter fill the three damage slots, the priest heals: five, complete.
        Assert.Equal(5, party.MemberCount);
        Assert.True(party.IsMember(priest.Guid) && party.IsMember(rogue.Guid) && party.IsMember(hunter.Guid));
        Assert.False(party.IsMember(otherArea.Guid));
        Assert.False(_queue.IsGroupQueued(party));
        Assert.Equal(0u, _queue.GroupAreaOf(party));
        Assert.Single(_f.Sent(warrior, WorldOpcode.SmsgMeetingstoneComplete));
        Assert.Contains((0u, MeetingStoneStatus.None), SetQueues(warrior));
        Assert.Equal(3, _f.Sent(warrior, WorldOpcode.SmsgMeetingstoneMemberAdded).Count);
        Assert.True(_queue.IsPlayerQueued(otherArea.Guid));
    }

    [Fact]
    public void QueuedParty_TakesOnlyRolesItStillLacks()
    {
        Player warrior = Add(1, Class.Warrior);
        Player priest = Add(2, Class.Priest);
        Group party = Party(warrior, priest);
        _queue.Join(warrior, Area);

        // A second priest can only heal or deal damage; healing is taken, so he comes as damage. A second warrior can tank or deal damage: damage.
        Player secondWarrior = Add(3, Class.Warrior);
        _queue.Join(secondWarrior, Area);
        Pass();
        Assert.True(party.IsMember(secondWarrior.Guid));
        Assert.Equal(3, party.MemberCount);
        Assert.True(_queue.IsGroupQueued(party));
    }

    [Fact]
    public void FiveSoloPlayersOfOneArea_FormAPartyThatIsQueuedAndFilled()
    {
        Player[] players = [Add(1, Class.Warrior), Add(2, Class.Priest), Add(3, Class.Mage), Add(4, Class.Rogue), Add(5, Class.Warlock)];
        foreach (Player player in players)
        {
            _queue.Join(player, Area);
        }

        _f.ClearAll();
        Pass();

        Group? party = _f.Context.Groups.GetGroup(players[0].Guid);
        Assert.NotNull(party);
        Assert.Equal(players[0].Guid, party!.LeaderGuid);
        Assert.True(party.IsMember(players[1].Guid));
        Assert.Equal(players[1].Guid.Value, BitConverter.ToUInt64(Assert.Single(_f.Sent(players[0], WorldOpcode.SmsgMeetingstoneMemberAdded))));
        Assert.True(_queue.IsGroupQueued(party));

        Pass();
        Assert.Equal(5, party.MemberCount);
        Assert.False(_queue.IsGroupQueued(party));
    }

    [Fact]
    public void FewerThanFiveSoloPlayers_WaitForMore()
    {
        foreach (Player player in new[] { Add(1, Class.Warrior), Add(2, Class.Priest), Add(3, Class.Mage), Add(4, Class.Rogue) })
        {
            _queue.Join(player, Area);
        }

        Pass();
        Pass();
        Assert.Null(_f.Context.Groups.GetGroup(ObjectGuid.Player(1)));
    }

    [Fact]
    public void KickedMember_TakesThePartyOutOfTheQueue_AndIsQueuedAlone()
    {
        Player warrior = Add(1, Class.Warrior);
        Player mage = Add(2, Class.Mage);
        Player rogue = Add(3, Class.Rogue);
        Group party = Party(warrior, mage, rogue);
        _queue.Join(warrior, Area);
        _f.ClearAll();

        _f.Context.Groups.UninviteByGuid(warrior, rogue.Guid);

        Assert.False(_queue.IsGroupQueued(party));
        Assert.Equal([(0u, MeetingStoneStatus.PartyMemberRemovedPartyRemoved), (0u, MeetingStoneStatus.LeaveQueue)], SetQueues(mage));
        Assert.Equal([(Area, MeetingStoneStatus.LookingForNewPartyInQueue), (Area, MeetingStoneStatus.JoinedQueue)], SetQueues(rogue));
        Assert.True(_queue.IsPlayerQueued(rogue.Guid));
    }

    [Fact]
    public void MemberLeaving_IsToldNone_ThePartyHearsMemberLeft_AndStaysQueued()
    {
        Player warrior = Add(1, Class.Warrior);
        Player mage = Add(2, Class.Mage);
        Player rogue = Add(3, Class.Rogue);
        Group party = Party(warrior, mage, rogue);
        _queue.Join(warrior, Area);
        _f.ClearAll();

        _f.Context.Groups.Leave(rogue);

        Assert.Equal([(0u, MeetingStoneStatus.None)], SetQueues(rogue));
        Assert.Equal([(Area, MeetingStoneStatus.PartyMemberLeftLfg)], SetQueues(mage));
        Assert.True(_queue.IsGroupQueued(party));
        Assert.False(_queue.IsPlayerQueued(rogue.Guid));
    }

    [Fact]
    public void LeaderLeaving_TakesThePartyOutOfTheQueue()
    {
        Player warrior = Add(1, Class.Warrior);
        Player mage = Add(2, Class.Mage);
        Player rogue = Add(3, Class.Rogue);
        Group party = Party(warrior, mage, rogue);
        _queue.Join(warrior, Area);
        _f.ClearAll();

        _f.Context.Groups.Leave(warrior);

        Assert.False(_queue.IsGroupQueued(party));
        Assert.Equal([(0u, MeetingStoneStatus.LeaveQueue)], SetQueues(mage));
    }

    [Fact]
    public void Disband_TellsEveryMemberNone()
    {
        Player warrior = Add(1, Class.Warrior);
        Player mage = Add(2, Class.Mage);
        Group party = Party(warrior, mage);
        _queue.Join(warrior, Area);
        _f.ClearAll();

        _f.Context.Groups.Leave(mage); // two members: the party is disbanded

        Assert.False(_queue.IsGroupQueued(party));
        Assert.Contains((0u, MeetingStoneStatus.None), SetQueues(warrior));
    }

    [Fact]
    public void LeaderLeavesTheQueue_NonLeaderOnlyHearsNone()
    {
        Player warrior = Add(1, Class.Warrior);
        Player mage = Add(2, Class.Mage);
        Group party = Party(warrior, mage);
        _queue.Join(warrior, Area);
        _f.ClearAll();

        _queue.Leave(mage);
        Assert.Equal([(0u, MeetingStoneStatus.None)], SetQueues(mage));
        Assert.True(_queue.IsGroupQueued(party));

        _f.ClearAll();
        _queue.Leave(warrior);
        Assert.False(_queue.IsGroupQueued(party));
        Assert.Equal([(0u, MeetingStoneStatus.LeaveQueue)], SetQueues(mage));
    }

    [Fact]
    public void LoggingOut_LeavesTheSoloQueueSilently()
    {
        Player mage = Add(1, Class.Mage);
        _queue.Join(mage, Area);
        _f.ClearAll();

        _queue.OnLoggingOut(mage);
        Assert.False(_queue.IsPlayerQueued(mage.Guid));
        Assert.Empty(SetQueues(mage));
    }

    [Fact]
    public void LongerWaitingPlayer_TakesTheRoleFirst_EvenWhenALowerGuidComesFirst()
    {
        Player warrior = Add(1, Class.Warrior);
        Player mage = Add(2, Class.Mage);
        Player rogue = Add(5, Class.Rogue);
        Player hunter = Add(6, Class.Hunter);
        Group party = Party(warrior, mage, rogue, hunter); // tank and three damage: only the healer is open

        Player early = Add(4, Class.Priest);
        _queue.Join(early, Area);
        Pass(); // the early priest has waited a second

        _queue.Join(warrior, Area);
        Player late = Add(3, Class.Priest); // a lower guid: looked at first
        _queue.Join(late, Area);
        Pass();

        Assert.True(party.IsMember(early.Guid));
        Assert.False(party.IsMember(late.Guid));
        Assert.True(_queue.IsPlayerQueued(late.Guid));
        Assert.False(_queue.IsGroupQueued(party)); // full
    }

    [Fact]
    public void WaitingParty_HearsInProgressEveryFiveMinutes()
    {
        Player warrior = Add(1, Class.Warrior);
        Player mage = Add(2, Class.Mage);
        Party(warrior, mage);
        _queue.Join(warrior, Area);
        _f.ClearAll();

        for (int i = 0; i < 299; i++)
        {
            Pass();
        }

        Assert.Empty(_f.Sent(warrior, WorldOpcode.SmsgMeetingstoneInProgress));
        Pass();
        Assert.Single(_f.Sent(warrior, WorldOpcode.SmsgMeetingstoneInProgress));
        Assert.Single(_f.Sent(mage, WorldOpcode.SmsgMeetingstoneInProgress));
    }
}

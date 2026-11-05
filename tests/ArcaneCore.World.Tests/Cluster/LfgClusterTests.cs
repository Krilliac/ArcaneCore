using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Lfg;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// The meeting stone queue and the world state broadcast across workers (S3e): players queued from two workers are matched by the one queue on the
/// holder and each hears its packets through the sender of its own worker; a world state change on the holder reaches the sessions of another worker.
/// Everything is driven by hand: links are in-memory recorders, the queue clock is <c>Update(diff)</c>.
/// </summary>
public sealed class LfgClusterTests
{
    private const uint Area = 719;

    private sealed class HolderGroups : ILfgGroups
    {
        private readonly Dictionary<ObjectGuid, Group> _byMember = [];
        private readonly Dictionary<ObjectGuid, Class> _classes = [];
        private uint _nextId = 1;

        public int Broadcasts { get; private set; }

        public void SetClass(ObjectGuid guid, Class playerClass) => _classes[guid] = playerClass;

        public Group? GroupOf(ObjectGuid player) => _byMember.GetValueOrDefault(player);

        public Group? CreateGroup(Player leader, Player member) => null;

        public bool AddMember(Group group, Player member) => false;

        public void Broadcast(Group group, WorldOpcode opcode, byte[] payload) => Broadcasts++;

        public Player? FindOnlinePlayer(ObjectGuid guid) => null;

        public Class ClassOf(ObjectGuid guid) => _classes.GetValueOrDefault(guid);

        public bool AddRemoteMember(Group group, ObjectGuid member)
        {
            if (group.IsFull || _byMember.ContainsKey(member) || !group.AddMemberSlot(member, "m" + member.Value))
            {
                return false;
            }

            _byMember[member] = group;
            return true;
        }

        public Group? CreateRemoteGroup(ObjectGuid leader, ObjectGuid member)
        {
            var group = new Group(_nextId++) { LeaderGuid = leader, IsCreated = true };
            group.LooterGuid = leader;
            if (!AddRemoteMember(group, leader) || !AddRemoteMember(group, member))
            {
                return null;
            }

            return group;
        }
    }

    private readonly HolderGroups _groups = new();
    private readonly LfgQueue _queue;
    private readonly List<(ObjectGuid Player, WorldOpcode Opcode, byte[] Payload)> _sent = [];

    public LfgClusterTests() => _queue = new LfgQueue(_groups);

    private static readonly (uint Id, Class Class, string Worker)[] Roster =
    [
        (1, Class.Warrior, "worker-a"), (2, Class.Priest, "worker-b"), (3, Class.Mage, "worker-a"),
        (4, Class.Rogue, "worker-b"), (5, Class.Warlock, "worker-a"),
    ];

    private LfgRemoteSend SenderOf(ObjectGuid guid) => (opcode, payload) => _sent.Add((guid, opcode, payload));

    private void JoinRemote(uint id, Class playerClass)
    {
        ObjectGuid guid = ObjectGuid.Player(id);
        _groups.SetClass(guid, playerClass);
        _queue.JoinRemote(guid, Team.Alliance, playerClass, Area, SenderOf(guid));
    }

    private IEnumerable<(ObjectGuid Player, byte[] Payload)> Sent(WorldOpcode opcode)
        => _sent.Where(s => s.Opcode == opcode).Select(s => (s.Player, s.Payload));

    [Fact]
    public void PlayersOnTwoWorkers_AreMatchedByOneQueue()
    {
        foreach ((uint id, Class playerClass, _) in Roster)
        {
            JoinRemote(id, playerClass);
        }

        Assert.Equal(5, Roster.Length);
        Assert.All(Roster, r => Assert.True(_queue.IsPlayerQueued(ObjectGuid.Player(r.Id))));
        Assert.Equal(5, Sent(WorldOpcode.SmsgMeetingstoneSetqueue).Count());

        // Pass 1: five wait, so the first two (warrior of worker A, priest of worker B) form a party and the leader is told.
        _queue.Update(LfgQueue.UpdateIntervalMs);
        Group? party = _groups.GroupOf(ObjectGuid.Player(1));
        Assert.NotNull(party);
        Assert.True(party!.IsMember(ObjectGuid.Player(2)));
        Assert.True(_queue.IsGroupQueued(party));
        (ObjectGuid memberAddedTo, byte[] memberAdded) = Assert.Single(Sent(WorldOpcode.SmsgMeetingstoneMemberAdded));
        Assert.Equal(ObjectGuid.Player(1), memberAddedTo);
        Assert.Equal(ObjectGuid.Player(2).Value, BitConverter.ToUInt64(memberAdded));

        // Pass 2: the three damage dealers of both workers fill the party, which completes.
        _queue.Update(LfgQueue.UpdateIntervalMs);
        Assert.Equal(5, party.MemberCount);
        Assert.False(_queue.IsGroupQueued(party));
        foreach ((uint id, _, _) in Roster)
        {
            Assert.Contains(Sent(WorldOpcode.SmsgMeetingstoneComplete), s => s.Player == ObjectGuid.Player(id));
        }
    }

    [Fact]
    public void RemotePlayer_LeavesInfoAndLogsOut_ThroughItsOwnSender()
    {
        ObjectGuid guid = ObjectGuid.Player(7);
        JoinRemote(7, Class.Mage);
        _sent.Clear();

        _queue.InfoRemote(guid);
        (_, byte[] info) = Assert.Single(Sent(WorldOpcode.SmsgMeetingstoneSetqueue));
        Assert.Equal(Area, BitConverter.ToUInt32(info, 0));
        Assert.Equal((byte)MeetingStoneStatus.JoinedQueue, info[4]);

        _sent.Clear();
        _queue.LeaveRemote(guid);
        (_, byte[] left) = Assert.Single(Sent(WorldOpcode.SmsgMeetingstoneSetqueue));
        Assert.Equal((byte)MeetingStoneStatus.LeaveQueue, left[4]);
        Assert.False(_queue.IsPlayerQueued(guid));

        JoinRemote(7, Class.Mage);
        _queue.OnRemoteLoggingOut(guid);
        Assert.False(_queue.IsPlayerQueued(guid));
        _sent.Clear();
        _queue.LeaveRemote(guid); // the seat is gone: nothing is sent to a worker that no longer serves him
        Assert.Empty(_sent);
    }

    [Fact]
    public void FewerThanFiveRemotePlayers_Wait()
    {
        foreach ((uint id, Class playerClass, _) in Roster.Take(4))
        {
            JoinRemote(id, playerClass);
        }

        _queue.Update(LfgQueue.UpdateIntervalMs);
        Assert.Null(_groups.GroupOf(ObjectGuid.Player(1)));
    }

    private sealed class Sink : IWorldStateSessionSink
    {
        public readonly List<(uint Zone, WorldOpcode Opcode, byte[] Payload)> Sent = [];

        public void SendToZone(uint zoneId, WorldOpcode opcode, byte[] payload) => Sent.Add((zoneId, opcode, payload));
    }

    private sealed class Link : IWorldStateWorkerLink
    {
        public readonly Dictionary<string, WorldStateClusterBroadcast> Workers = [];
        public readonly List<(string Worker, WorldStateChange Change)> Delivered = [];

        public void Deliver(string workerId, WorldStateChange change)
        {
            Delivered.Add((workerId, change));
            Workers[workerId].Receive(change);
        }
    }

    [Fact]
    public void WorldStateChangeOnTheHolder_ReachesSessionsOnAnotherWorker()
    {
        var link = new Link();
        var holderSink = new Sink();
        var workerSink = new Sink();
        var holder = new WorldStateClusterBroadcast(holderSink, link);
        var worker = new WorldStateClusterBroadcast(workerSink);
        link.Workers["worker-b"] = worker;
        holder.RegisterWorker("worker-b");

        holder.Publish(new WorldStateChange(3277, 1581, 7));

        Assert.Single(holderSink.Sent);
        Assert.Empty(workerSink.Sent); // queued until the worker's world thread pumps
        Assert.Equal(1, worker.Pump());
        (uint zone, WorldOpcode opcode, byte[] payload) = Assert.Single(workerSink.Sent);
        Assert.Equal(3277u, zone);
        Assert.Equal(WorldOpcode.SmsgUpdateWorldState, opcode);
        Assert.Equal(1581u, BitConverter.ToUInt32(payload, 0));
        Assert.Equal(7u, BitConverter.ToUInt32(payload, 4));
        Assert.Equal(7u, holder.CurrentValue(3277, 1581));
    }

    [Fact]
    public void UnchangedValue_IsNotSentAgain_AndALateWorkerIsReplayedTheCurrentValues()
    {
        var link = new Link();
        var holder = new WorldStateClusterBroadcast(new Sink(), link);
        holder.Publish(new WorldStateChange(0, 10, 1));
        holder.Publish(new WorldStateChange(0, 10, 1));
        holder.Publish(new WorldStateChange(0, 10, 2));
        holder.Publish(new WorldStateChange(0, 11, 5));

        var lateSink = new Sink();
        var late = new WorldStateClusterBroadcast(lateSink);
        link.Workers["late"] = late;
        holder.RegisterWorker("late");
        late.Pump();

        Assert.Equal([(0u, 10u, 2u), (0u, 11u, 5u)], link.Delivered.Select(d => (d.Change.ZoneId, d.Change.State, d.Change.Value)).ToArray());
        Assert.Equal(2, lateSink.Sent.Count);

        Assert.True(holder.UnregisterWorker("late"));
        holder.Publish(new WorldStateChange(0, 10, 3));
        Assert.Equal(2, link.Delivered.Count);
    }
}

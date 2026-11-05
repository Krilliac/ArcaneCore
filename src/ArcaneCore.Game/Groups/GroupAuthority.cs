using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Groups;

/// <summary>What a <see cref="GroupCommand"/> asks the group authority to do. Values are wire values: append only.</summary>
public enum GroupCommandKind : byte
{
    Invite = 1,
    Accept = 2,
    Decline = 3,
    Leave = 4,

    /// <summary>Uninvite by name (<see cref="GroupCommand.TargetName"/>) or by guid (<see cref="GroupCommand.TargetGuid"/>, when the name is empty).</summary>
    Kick = 5,

    SetLeader = 6,

    /// <summary><see cref="GroupCommand.TargetGuid"/> becomes or stops being an assistant (<see cref="GroupCommand.Value"/> 1 or 0).</summary>
    SetAssistant = 7,

    /// <summary>Loot method (<see cref="GroupCommand.Value"/>), master looter (<see cref="GroupCommand.Master"/>), threshold (<see cref="GroupCommand.Value2"/>).</summary>
    SetLootMethod = 8,

    ConvertToRaid = 9,

    /// <summary>Move <see cref="GroupCommand.TargetName"/> to sub-group <see cref="GroupCommand.Value"/>.</summary>
    ChangeSubGroup = 10,

    /// <summary>Swap the sub-groups of <see cref="GroupCommand.TargetName"/> and <see cref="GroupCommand.OtherName"/>.</summary>
    SwapSubGroup = 11,

    /// <summary><see cref="GroupCommand.Value"/> 0 starts a check; 1 plus the answer byte answers it.</summary>
    ReadyCheck = 12,

    /// <summary>Icon <see cref="GroupCommand.Value"/> on <see cref="GroupCommand.TargetGuid"/> (empty clears it).</summary>
    TargetIcon = 13,

    /// <summary>The actor logged in on the sending worker: members are told it is online and it gets its group list.</summary>
    Login = 14,

    /// <summary>The actor is leaving the world on the sending worker: pending invites go, members see it offline.</summary>
    Logout = 15,

    /// <summary>The invitee ignores the inviter (<see cref="GroupCommand.TargetGuid"/>): the invite is dropped and the inviter is told.</summary>
    RefuseIgnored = 16,

    /// <summary>A packet of the actor for the group members on other workers: <see cref="GroupCommand.Value"/> is the opcode, <see cref="GroupCommand.Payload"/> the body. Only stats, minimap pings and rolls are relayed.</summary>
    Relay = 17,
}

/// <summary>
/// One group operation of a character, forwarded by the worker that holds its session to the Social lease holder. The actor facts (name, team, game master) are the
/// forwarding worker's; the target is named by guid or name and resolved against realm presence at the holder.
/// </summary>
public sealed record GroupCommand(
    GroupCommandKind Kind,
    ulong Actor,
    string ActorName,
    byte ActorTeam,
    bool ActorIsGameMaster,
    ulong TargetGuid = 0,
    string TargetName = "",
    string OtherName = "",
    uint Value = 0,
    uint Value2 = 0,
    ulong Master = 0,
    byte[]? Payload = null);

/// <summary>One server packet inside a <see cref="GroupEvent"/>, built by the holder and sent to the recipient's session unchanged.</summary>
public sealed record GroupPacket(WorldOpcode Opcode, byte[] Payload);

/// <summary>One member of a <see cref="GroupReplica"/>.</summary>
public sealed record GroupReplicaMember(ulong Guid, string Name, byte SubGroup, bool Assistant);

/// <summary>The whole state of a created group as the holder has it: what a worker installs so that its local checks (chat, loot, instance binds, presence) see the same group.</summary>
public sealed record GroupReplica(
    uint GroupId,
    bool IsRaid,
    ulong Leader,
    string LeaderName,
    byte LootMethod,
    ulong Looter,
    byte LootThreshold,
    IReadOnlyList<ulong> TargetIcons,
    IReadOnlyList<GroupReplicaMember> Members);

/// <summary>
/// What the holder tells the worker of one online character after a command: the group state to install (<see cref="Replica"/>), or that the character left
/// <see cref="DepartedGroupId"/> (<see cref="Disbanded"/> when the whole group ended), the inviter when this is an invite notice (the worker applies the character's ignore list
/// before showing it), and the packets for the character's session. <see cref="InviteCancelled"/> says the character's pending invite ended (accepted, declined, withdrawn or
/// the inviter's group ended), so the worker drops the invite it mirrors for <see cref="GroupManager.GetInvite"/>.
/// </summary>
public sealed record GroupEvent(
    ulong RecipientGuid,
    GroupReplica? Replica,
    uint DepartedGroupId,
    bool Disbanded,
    ulong InviterGuid,
    IReadOnlyList<GroupPacket> Packets,
    bool InviteCancelled = false);

/// <summary>The path from a worker's <see cref="GroupManager"/> to the group authority (in-process at the holder, over the peer link elsewhere). Never blocks the caller.</summary>
public interface IGroupAuthorityLink
{
    /// <summary>Queue <paramref name="command"/> for the authority; the outcome arrives later as <see cref="GroupEvent"/>s through <see cref="GroupManager.ApplyAuthorityEvent"/>.</summary>
    void Forward(GroupCommand command);
}

/// <summary>
/// The authoritative group table of a clustered realm, run by the node that holds the Social lease (vmangos Group.cpp / GroupHandler.cpp rules, applied to realm presence instead of
/// local sessions). Every group operation of every worker reaches <see cref="Execute"/>; the result is a set of <see cref="GroupEvent"/>s queued per worker (<see cref="Drain"/>):
/// the group list and party packets of each online member, wherever it is, and the group state each member's worker mirrors (<see cref="GroupReplica"/>).
/// <para>
/// Not modelled here (the worker keeps them local): battleground raids, the ignore list of an invitee (the invitee's worker checks it and answers with
/// <see cref="GroupCommandKind.RefuseIgnored"/>; so the inviter gets PARTY_RESULT Ok and then IgnoringYou, where vmangos answers IgnoringYou first), the pvp/dead/afk bits of the group
/// list status (the holder only knows online and offline; the stats pass carries the rest), and the meeting stone queue (off in cluster roles: it forms and fills parties
/// synchronously). These rules restate the vmangos group rules over realm presence instead of sharing <see cref="GroupManager"/>'s; a rule changed in one must be changed in the other.
/// </para>
/// <para>
/// Thread-safety: one lock guards all state; <see cref="Execute"/> and <see cref="Drain"/> may be called from any thread. Memory: the groups, plus at most
/// <see cref="MaxQueuedEventsPerWorker"/> events per worker (the oldest are dropped) and <see cref="MaxCommandsHeldForReload"/> commands during a reload.
/// </para>
/// </summary>
public sealed class GroupAuthority(IRealmPresenceDirectory directory)
{
    /// <summary>Most undelivered events kept per worker; a worker that never drains loses its oldest.</summary>
    public const int MaxQueuedEventsPerWorker = 8192;

    /// <summary>Most commands kept while the table is reloaded (<see cref="HoldForReload"/>).</summary>
    public const int MaxCommandsHeldForReload = 4096;

    /// <summary>Encoded size past which <see cref="Drain"/> stops adding events to one reply.</summary>
    public const int MaxDrainBytes = 48 * 1024;

    /// <summary>How long a worker's own command counts as proof that its character is online when realm presence has not caught up yet.</summary>
    private const long OnlineHintMs = 30_000;

    private readonly IRealmPresenceDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly Lock _gate = new();
    private readonly Dictionary<ObjectGuid, Group> _memberOf = [];
    private readonly Dictionary<ObjectGuid, Group> _invitedTo = [];
    private readonly Dictionary<ObjectGuid, (string Worker, long AtMs)> _hint = [];
    private readonly Dictionary<string, Queue<GroupEvent>> _queues = new(StringComparer.Ordinal);
    private readonly Dictionary<ObjectGuid, long> _readyAnswerAt = [];
    private readonly Dictionary<ObjectGuid, Draft> _drafts = [];
    private uint _nextId = 1;
    private List<(string WorkerId, GroupCommand Command)>? _heldForReload;

    private sealed class Draft
    {
        public GroupReplica? Replica;
        public uint DepartedGroupId;
        public bool Disbanded;
        public ulong Inviter;
        public bool InviteCancelled;
        public readonly List<GroupPacket> Packets = [];
    }

    /// <summary>Realm rules (<see cref="SocialOptions.AllowTwoSideGroup"/>, <see cref="SocialOptions.ReadyCheckAnswerIntervalMs"/>); the social feature binds the live instance.</summary>
    public SocialOptions Options { get; set; } = new();

    /// <summary>The durable group id block allocator, when cluster ID range reservation is on (thread-safe); null uses a counter above every restored id.</summary>
    public Func<DurableIdBlockAllocator?>? IdBlockAllocatorSource { get; set; }

    /// <summary>Whether (Worker mode) an unprimed allocator must throw instead of falling back to the counter; null means false.</summary>
    public Func<bool>? RequireDurableIdsSource { get; set; }

    /// <summary>World time in Unix seconds (test seam).</summary>
    internal Func<long> UnixSecondsClock { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Monotonic milliseconds (test seam).</summary>
    internal Func<long> MillisecondsClock { get; set; } = Kernel.Logging.Clock.Milliseconds;

    /// <summary>Every created group, lowest id first; a copy, taken under the lock.</summary>
    public IReadOnlyList<Group> Groups
    {
        get
        {
            lock (_gate)
            {
                return [.. _memberOf.Values.Where(g => g.IsCreated).Distinct().OrderBy(g => g.Id)];
            }
        }
    }

    /// <summary>The stored form of every created group, taken under the lock (the members of a group cannot change while it is read).</summary>
    public IReadOnlyList<GroupRecord> Snapshots()
    {
        lock (_gate)
        {
            return [.. _memberOf.Values.Where(g => g.IsCreated).Distinct().OrderBy(g => g.Id).Select(GroupManager.Snapshot)];
        }
    }

    /// <summary>The group of <paramref name="guid"/>, or null (a live object: read it only for tests and under no concurrent command).</summary>
    public Group? GetGroup(ObjectGuid guid)
    {
        lock (_gate)
        {
            return _memberOf.GetValueOrDefault(guid);
        }
    }

    /// <summary>Forget every group, invite and queued event (before a restore at a lease gain).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _memberOf.Clear();
            _invitedTo.Clear();
            _hint.Clear();
            _queues.Clear();
            _readyAnswerAt.Clear();
        }
    }

    /// <summary>
    /// The table is about to be replaced by a read that is still running: from now on <see cref="Execute"/> keeps each command, in order, instead of applying it, because <see cref="Reset"/> would throw its result away. At most
    /// <see cref="MaxCommandsHeldForReload"/> are kept (the oldest goes first).
    /// </summary>
    public void HoldForReload()
    {
        lock (_gate)
        {
            _heldForReload ??= [];
        }
    }

    /// <summary>The reload is over: the held commands run, in order, on the new table, ahead of any command still to come. Returns how many failed (the rest still ran).</summary>
    public int ReleaseHeldCommands()
    {
        lock (_gate)
        {
            List<(string WorkerId, GroupCommand Command)>? held = _heldForReload;
            _heldForReload = null;
            if (held is null)
            {
                return 0;
            }

            int failed = 0;
            foreach ((string workerId, GroupCommand command) in held)
            {
                try
                {
                    Execute(workerId, command);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed++;
                }
            }

            return failed;
        }
    }

    /// <summary>The reload will not finish here (the lease went, or the node is stopping): the held commands are dropped and commands apply again.</summary>
    public void DropHeldCommands()
    {
        lock (_gate)
        {
            _heldForReload = null;
        }
    }

    /// <summary>
    /// Install stored groups (same rules as <see cref="GroupManager.Restore"/>: a member whose character is gone, a duplicate, or one in a missing or full sub-group is skipped; a
    /// group whose leader is gone or with fewer than two members left is dropped and returned for its rows to be deleted). New ids continue above the highest stored one.
    /// </summary>
    public GroupRestoreResult Restore(IEnumerable<GroupRecord> records, ICharacterLookup characters)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(characters);
        var restored = new List<uint>();
        var dropped = new List<uint>();
        lock (_gate)
        {
            long now = UnixSecondsClock();
            foreach (GroupRecord record in records.OrderBy(r => r.Id))
            {
                _nextId = Math.Max(_nextId, record.Id + 1);
                CharacterInfo? leaderInfo = record.LeaderId > 0 ? characters.Find((uint)record.LeaderId) : null;
                if (leaderInfo is null || _memberOf.Values.Any(g => g.Id == record.Id))
                {
                    dropped.Add(record.Id);
                    continue;
                }

                var group = new Group(record.Id)
                {
                    IsCreated = true,
                    Type = record.IsRaid ? GroupType.Raid : GroupType.Normal,
                    LootMethod = record.LootMethod <= (byte)LootMethod.NeedBeforeGreed ? (LootMethod)record.LootMethod : LootMethod.GroupLoot,
                    LootThreshold = record.LootThreshold is >= Group.MinLootThreshold and <= Group.MaxLootThreshold ? record.LootThreshold : Group.DefaultLootThreshold,
                };
                foreach (GroupMemberRecord member in record.Members)
                {
                    if (member.CharacterId > 0 && !_memberOf.ContainsKey(ObjectGuid.Player((uint)member.CharacterId))
                        && characters.Find((uint)member.CharacterId) is { } info)
                    {
                        group.RestoreMemberSlot(ObjectGuid.Player(info.Id), info.Name, member.SubGroup, member.Assistant);
                    }
                }

                if (group.MemberCount < Group.MinMemberCount)
                {
                    dropped.Add(record.Id);
                    continue;
                }

                GroupMemberSlot leader = group.Find(ObjectGuid.Player(leaderInfo.Id)) ?? group.Members[0];
                group.LeaderGuid = leader.Guid;
                group.LeaderName = leader.Name;
                group.LeaderLastOnlineUnixSeconds = now;
                ObjectGuid looter = record.LooterId > 0 ? ObjectGuid.Player((uint)record.LooterId) : ObjectGuid.Empty;
                group.LooterGuid = group.IsMember(looter) ? looter : group.LeaderGuid;
                for (int i = 0; i < Group.TargetIconCount && i < record.TargetIcons.Count; i++)
                {
                    group.TargetIcons[i] = new ObjectGuid(record.TargetIcons[i]);
                }

                foreach (GroupMemberSlot slot in group.Members)
                {
                    _memberOf[slot.Guid] = group;
                }

                restored.Add(record.Id);
            }
        }

        return new GroupRestoreResult(restored, dropped);
    }

    /// <summary>
    /// The events queued for <paramref name="workerId"/>, oldest first, at most <see cref="MaxDrainBytes"/> of encoded size (an estimate: packet and name bytes); the rest stays queued
    /// for the next call. Each event is delivered once.
    /// </summary>
    public IReadOnlyList<GroupEvent> Drain(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return [];
        }

        lock (_gate)
        {
            if (!_queues.TryGetValue(workerId, out Queue<GroupEvent>? queue) || queue.Count == 0)
            {
                return [];
            }

            var batch = new List<GroupEvent>();
            int bytes = 0;
            while (queue.Count > 0)
            {
                int size = EstimateSize(queue.Peek());
                if (batch.Count > 0 && bytes + size > MaxDrainBytes)
                {
                    break;
                }

                bytes += size;
                batch.Add(queue.Dequeue());
            }

            return batch;
        }
    }

    /// <summary>The events waiting for <paramref name="workerId"/> (tests and health).</summary>
    public int PendingFor(string workerId)
    {
        lock (_gate)
        {
            return _queues.TryGetValue(workerId, out Queue<GroupEvent>? queue) ? queue.Count : 0;
        }
    }

    internal static int EstimateSize(GroupEvent e)
    {
        int size = 48;
        foreach (GroupPacket packet in e.Packets)
        {
            size += 8 + packet.Payload.Length;
        }

        if (e.Replica is { } replica)
        {
            size += 64 + (replica.TargetIcons.Count * 8) + replica.LeaderName.Length;
            foreach (GroupReplicaMember member in replica.Members)
            {
                size += 16 + (member.Name.Length * 3);
            }
        }

        return size;
    }

    /// <summary>Apply one command of the character on <paramref name="workerId"/> and queue the resulting events.</summary>
    public void Execute(string workerId, GroupCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (_heldForReload is { } held)
            {
                if (held.Count >= MaxCommandsHeldForReload)
                {
                    held.RemoveAt(0);
                }

                held.Add((workerId, command));
                return;
            }

            _drafts.Clear();
            var actor = new ObjectGuid(command.Actor);
            if (command.Kind != GroupCommandKind.Logout)
            {
                _hint[actor] = (workerId, MillisecondsClock());
            }

            try
            {
                Dispatch(command, actor);
            }
            finally
            {
                if (command.Kind == GroupCommandKind.Logout)
                {
                    _hint.Remove(actor);
                }

                Flush();
            }
        }
    }

    // ---- dispatch ------------------------------------------------------------------------------------------------------------------------------

    private void Dispatch(GroupCommand c, ObjectGuid actor)
    {
        switch (c.Kind)
        {
            case GroupCommandKind.Invite: Invite(c, actor); break;
            case GroupCommandKind.Accept: Accept(c, actor); break;
            case GroupCommandKind.Decline: Decline(c, actor); break;
            case GroupCommandKind.Leave: Leave(c, actor); break;
            case GroupCommandKind.Kick: Kick(c, actor); break;
            case GroupCommandKind.SetLeader: SetLeader(c, actor); break;
            case GroupCommandKind.SetAssistant: SetAssistant(c, actor); break;
            case GroupCommandKind.SetLootMethod: SetLootMethod(c, actor); break;
            case GroupCommandKind.ConvertToRaid: ConvertToRaid(actor); break;
            case GroupCommandKind.ChangeSubGroup: ChangeSubGroup(c, actor); break;
            case GroupCommandKind.SwapSubGroup: SwapSubGroup(c, actor); break;
            case GroupCommandKind.ReadyCheck: ReadyCheck(c, actor); break;
            case GroupCommandKind.TargetIcon: TargetIcon(c, actor); break;
            case GroupCommandKind.Login: Login(c, actor); break;
            case GroupCommandKind.Logout: Logout(actor); break;
            case GroupCommandKind.RefuseIgnored: RefuseIgnored(c, actor); break;
            case GroupCommandKind.Relay: Relay(c, actor); break;
            default: break;
        }
    }

    /// <summary>vmangos HandleGroupInviteOpcode.</summary>
    private void Invite(GroupCommand c, ObjectGuid actor)
    {
        RealmPresenceEntry? target = c.TargetName.Length == 0 ? null : _directory.FindByName(c.TargetName);
        if (target is null)
        {
            Result(actor, PartyOperation.Invite, c.TargetName, PartyResult.BadPlayerName);
            return;
        }

        var targetGuid = new ObjectGuid(target.CharacterGuid);
        Group? inviterGroup = _memberOf.GetValueOrDefault(actor);
        Group? targetGroup = _memberOf.GetValueOrDefault(targetGuid);
        if (targetGuid == actor && inviterGroup is null && !_invitedTo.ContainsKey(actor))
        {
            return; // vmangos GroupHandler.cpp:120-143: the self-invite is discarded without an answer
        }

        if (!c.ActorIsGameMaster && !Options.AllowTwoSideGroup && c.ActorTeam != (byte)TeamRules.TeamOf((Race)target.Race))
        {
            Result(actor, PartyOperation.Invite, c.TargetName, PartyResult.WrongFaction);
            return;
        }

        if (targetGroup is not null || _invitedTo.ContainsKey(targetGuid))
        {
            Result(actor, PartyOperation.Invite, c.TargetName, PartyResult.AlreadyInGroup);
            return;
        }

        Group? group = inviterGroup;
        if (group is not null)
        {
            if (!group.IsLeaderOrAssistant(actor))
            {
                Result(actor, PartyOperation.Invite, string.Empty, PartyResult.NotLeader);
                return;
            }

            if (group.IsFull)
            {
                Result(actor, PartyOperation.Invite, string.Empty, PartyResult.GroupFull);
                return;
            }
        }
        else
        {
            if (_invitedTo.ContainsKey(actor))
            {
                return; // vmangos AddLeaderInvite fails silently while the inviter has a pending invite
            }

            group = new Group(AllocateId())
            {
                LeaderGuid = actor,
                LeaderName = c.ActorName,
                LeaderLastOnlineUnixSeconds = UnixSecondsClock(),
            };
            AddInvite(group, actor);
        }

        AddInvite(group, targetGuid);
        Draft invitee = DraftOf(targetGuid);
        invitee.Inviter = c.Actor;
        invitee.Packets.Add(new GroupPacket(WorldOpcode.SmsgGroupInvite, GroupPackets.BuildName(c.ActorName)));
        Result(actor, PartyOperation.Invite, c.TargetName, PartyResult.Ok);
    }

    /// <summary>vmangos HandleGroupAcceptOpcode.</summary>
    private void Accept(GroupCommand c, ObjectGuid actor)
    {
        if (!_invitedTo.TryGetValue(actor, out Group? group) || group.IsLeader(actor))
        {
            return;
        }

        RemoveInvite(group, actor);
        if (group.IsFull)
        {
            Result(actor, PartyOperation.Invite, string.Empty, PartyResult.GroupFull);
            return;
        }

        if (!group.IsCreated)
        {
            RemoveInvite(group, group.LeaderGuid);
            group.IsCreated = true;
            group.LooterGuid = group.LeaderGuid;
            if (!AddMember(group, group.LeaderGuid, group.LeaderName))
            {
                return;
            }
        }

        AddMember(group, actor, c.ActorName);
    }

    /// <summary>vmangos HandleGroupDeclineOpcode.</summary>
    private void Decline(GroupCommand c, ObjectGuid actor)
    {
        if (!_invitedTo.TryGetValue(actor, out Group? group))
        {
            return;
        }

        ObjectGuid leader = group.LeaderGuid;
        UninviteFromGroup(actor);
        if (IsOnline(leader))
        {
            DraftOf(leader).Packets.Add(new GroupPacket(WorldOpcode.SmsgGroupDecline, GroupPackets.BuildName(c.ActorName)));
        }
    }

    private void RefuseIgnored(GroupCommand c, ObjectGuid actor)
    {
        var inviter = new ObjectGuid(c.TargetGuid);
        if (!_invitedTo.TryGetValue(actor, out Group? group) || !group.IsLeaderOrAssistant(inviter))
        {
            return;
        }

        UninviteFromGroup(actor);
        Result(inviter, PartyOperation.Invite, c.ActorName, PartyResult.IgnoringYou);
    }

    /// <summary>vmangos HandleGroupDisbandOpcode.</summary>
    private void Leave(GroupCommand c, ObjectGuid actor)
    {
        if (!_memberOf.TryGetValue(actor, out Group? group))
        {
            return;
        }

        Result(actor, PartyOperation.Leave, c.ActorName, PartyResult.Ok);
        RemoveFromGroup(group, actor, kicked: false);
    }

    /// <summary>vmangos HandleGroupUninviteOpcode and HandleGroupUninviteGuidOpcode.</summary>
    private void Kick(GroupCommand c, ObjectGuid actor)
    {
        Group? group = _memberOf.GetValueOrDefault(actor);
        if (c.TargetName.Length > 0)
        {
            if (c.TargetName == c.ActorName || group is null)
            {
                return;
            }

            if (group.FindByName(c.TargetName) is { } slot)
            {
                PartyResult res = CanUninvite(group, actor, slot.Guid);
                if (res != PartyResult.Ok)
                {
                    Result(actor, PartyOperation.Leave, string.Empty, res);
                    return;
                }

                RemoveFromGroup(group, slot.Guid, kicked: true);
                return;
            }

            RealmPresenceEntry? invited = _directory.FindByName(c.TargetName);
            if (invited is not null && group.IsInvited(new ObjectGuid(invited.CharacterGuid)))
            {
                PartyResult res = CanUninvite(group, actor, new ObjectGuid(invited.CharacterGuid));
                if (res != PartyResult.Ok)
                {
                    Result(actor, PartyOperation.Leave, string.Empty, res);
                    return;
                }

                UninviteFromGroup(new ObjectGuid(invited.CharacterGuid));
                return;
            }

            Result(actor, PartyOperation.Leave, c.TargetName, PartyResult.TargetNotInGroup);
            return;
        }

        var target = new ObjectGuid(c.TargetGuid);
        if (target == actor)
        {
            return;
        }

        PartyResult result = CanUninvite(group, actor, target);
        if (result != PartyResult.Ok)
        {
            Result(actor, PartyOperation.Leave, string.Empty, result);
            return;
        }

        if (group!.IsMember(target))
        {
            RemoveFromGroup(group, target, kicked: true);
        }
        else if (group.IsInvited(target))
        {
            UninviteFromGroup(target);
        }
        else
        {
            Result(actor, PartyOperation.Leave, string.Empty, PartyResult.TargetNotInGroup);
        }
    }

    /// <summary>vmangos HandleGroupSetLeaderOpcode.</summary>
    private void SetLeader(GroupCommand c, ObjectGuid actor)
    {
        var target = new ObjectGuid(c.TargetGuid);
        if (!_memberOf.TryGetValue(actor, out Group? group) || target == actor || !group.IsLeader(actor)
            || !IsOnline(target) || group.Find(target) is not { } slot)
        {
            return;
        }

        ChangeLeader(group, slot);
        Broadcast(group, WorldOpcode.SmsgGroupSetLeader, GroupPackets.BuildName(slot.Name));
        SendUpdate(group);
    }

    /// <summary>vmangos HandleGroupAssistantLeaderOpcode (raids only).</summary>
    private void SetAssistant(GroupCommand c, ObjectGuid actor)
    {
        var target = new ObjectGuid(c.TargetGuid);
        if (!_memberOf.TryGetValue(actor, out Group? group) || !group.IsRaid || target == actor || !group.IsLeader(actor)
            || !IsOnline(target) || group.Find(target) is not { } slot)
        {
            return;
        }

        slot.Assistant = c.Value != 0;
        SendUpdate(group);
    }

    /// <summary>vmangos HandleLootMethodOpcode, with the threshold bounds of <see cref="GroupManager.SetLootMethod"/>.</summary>
    private void SetLootMethod(GroupCommand c, ObjectGuid actor)
    {
        if (c.Value > (uint)LootMethod.NeedBeforeGreed || c.Value2 is < Group.MinLootThreshold or > Group.MaxLootThreshold
            || !_memberOf.TryGetValue(actor, out Group? group) || !group.IsLeader(actor))
        {
            return;
        }

        var master = new ObjectGuid(c.Master);
        if ((LootMethod)c.Value == LootMethod.MasterLoot)
        {
            if (!group.IsMember(master))
            {
                return;
            }
        }
        else
        {
            master = ObjectGuid.Empty;
        }

        group.LootMethod = (LootMethod)c.Value;
        group.LooterGuid = master;
        group.LootThreshold = (byte)c.Value2;
        SendUpdate(group);
    }

    /// <summary>vmangos HandleGroupRaidConvertOpcode.</summary>
    private void ConvertToRaid(ObjectGuid actor)
    {
        if (!_memberOf.TryGetValue(actor, out Group? group) || group.IsRaid || !group.IsLeader(actor) || group.MemberCount < Group.MinMemberCount)
        {
            return;
        }

        Result(actor, PartyOperation.Invite, string.Empty, PartyResult.Ok);
        group.ConvertToRaid();
        SendUpdate(group);
    }

    private void ChangeSubGroup(GroupCommand c, ObjectGuid actor)
    {
        if (c.Value >= Group.MaxRaidSubGroups || !_memberOf.TryGetValue(actor, out Group? group) || !group.IsRaid
            || !group.IsLeaderOrAssistant(actor) || !group.HasFreeSlotSubGroup((byte)c.Value)
            || group.FindByName(c.TargetName) is not { } slot || slot.SubGroup == c.Value)
        {
            return;
        }

        group.MoveToSubGroup(slot, (byte)c.Value);
        SendUpdate(group);
    }

    private void SwapSubGroup(GroupCommand c, ObjectGuid actor)
    {
        if (!_memberOf.TryGetValue(actor, out Group? group) || !group.IsRaid || !group.IsLeaderOrAssistant(actor)
            || group.FindByName(c.TargetName) is not { } a || group.FindByName(c.OtherName) is not { } b || a.SubGroup == b.SubGroup)
        {
            return;
        }

        byte first = a.SubGroup;
        group.MoveToSubGroup(a, b.SubGroup);
        group.MoveToSubGroup(b, first);
        SendUpdate(group);
    }

    /// <summary>vmangos HandleRaidReadyCheckOpcode: a request from the leader or an assistant goes to the whole group, an answer to the leader.</summary>
    private void ReadyCheck(GroupCommand c, ObjectGuid actor)
    {
        if (!_memberOf.TryGetValue(actor, out Group? group))
        {
            return;
        }

        if (c.Value == 0)
        {
            if (group.IsLeaderOrAssistant(actor))
            {
                foreach (GroupMemberSlot member in group.Members)
                {
                    _readyAnswerAt.Remove(member.Guid);
                }

                Broadcast(group, WorldOpcode.MsgRaidReadyCheck, []);
            }

            return;
        }

        if (AdmitReadyAnswer(actor) && IsOnline(group.LeaderGuid))
        {
            DraftOf(group.LeaderGuid).Packets.Add(new GroupPacket(WorldOpcode.MsgRaidReadyCheck, GroupPackets.BuildReadyCheckResponse(actor, (byte)(c.Value - 1))));
        }
    }

    /// <summary>The answer rate limit of <see cref="GroupManager.ReadyCheck"/> (World:Social:ReadyCheckAnswerIntervalMs).</summary>
    private bool AdmitReadyAnswer(ObjectGuid member)
    {
        int interval = Options.ReadyCheckAnswerIntervalMs;
        if (interval <= 0)
        {
            return true;
        }

        long now = MillisecondsClock();
        if (_readyAnswerAt.TryGetValue(member, out long last) && now - last < interval)
        {
            return false;
        }

        if (_readyAnswerAt.Count >= 4096)
        {
            foreach (ObjectGuid stale in _readyAnswerAt.Where(e => now - e.Value >= interval).Select(e => e.Key).ToList())
            {
                _readyAnswerAt.Remove(stale);
            }
        }

        _readyAnswerAt[member] = now;
        return true;
    }

    /// <summary>vmangos HandleRaidTargetUpdateOpcode (setting; the list request is answered by the worker).</summary>
    private void TargetIcon(GroupCommand c, ObjectGuid actor)
    {
        if (!_memberOf.TryGetValue(actor, out Group? group) || !group.IsLeaderOrAssistant(actor) || c.Value >= Group.TargetIconCount)
        {
            return;
        }

        SetTargetIcon(group, (byte)c.Value, new ObjectGuid(c.TargetGuid));
    }

    /// <summary>vmangos Group::UpdatePlayerOnlineStatus.</summary>
    private void Login(GroupCommand c, ObjectGuid actor)
    {
        if (!_memberOf.TryGetValue(actor, out Group? group))
        {
            return;
        }

        if (group.Find(actor) is { } slot)
        {
            slot.Name = c.ActorName;
        }

        if (group.IsLeader(actor))
        {
            group.LeaderName = c.ActorName;
            group.LeaderLastOnlineUnixSeconds = UnixSecondsClock();
        }

        SendUpdate(group);
    }

    /// <summary>vmangos WorldSession::LogoutPlayer: pending invites go and the group sees the member offline; membership stays.</summary>
    private void Logout(ObjectGuid actor)
    {
        UninviteFromGroup(actor);
        if (_memberOf.TryGetValue(actor, out Group? group))
        {
            if (group.IsLeader(actor))
            {
                group.LeaderLastOnlineUnixSeconds = UnixSecondsClock();
            }

            SendUpdate(group, offline: actor);
        }
    }

    /// <summary>A packet the actor's worker built for the members on other workers (stats, minimap ping, roll): forwarded to those workers' members only.</summary>
    private void Relay(GroupCommand c, ObjectGuid actor)
    {
        var opcode = (WorldOpcode)c.Value;
        if (opcode is not (WorldOpcode.SmsgPartyMemberStats or WorldOpcode.MsgMinimapPing or WorldOpcode.MsgRandomRoll)
            || c.Payload is not { Length: > 0 } payload || payload.Length > 1024
            || !_memberOf.TryGetValue(actor, out Group? group))
        {
            return;
        }

        string? origin = WorkerOf(actor);
        foreach (GroupMemberSlot member in group.Members)
        {
            if (member.Guid == actor || WorkerOf(member.Guid) is not { } worker || string.Equals(worker, origin, StringComparison.Ordinal))
            {
                continue;
            }

            DraftOf(member.Guid).Packets.Add(new GroupPacket(opcode, payload));
        }
    }

    /// <summary>
    /// vmangos@0e3ff01 src/game/World.cpp:2063-2072 and Group/Group.cpp:1448-1471 (as <see cref="GroupManager.UpdateOfflineLeaders"/>): a group whose leader has been offline for
    /// <see cref="SocialOptions.OfflineLeaderDelaySeconds"/> gets an online assistant first, then another online member, as its leader; with nobody online the offline leader stays.
    /// Run by the holder about once a second; the workers never do this for their mirrors.
    /// </summary>
    public void UpdateOfflineLeaders()
    {
        int delay = Options.OfflineLeaderDelaySeconds;
        if (delay <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _drafts.Clear();
            long now = UnixSecondsClock();
            try
            {
                foreach (Group group in _memberOf.Values.Where(g => g.IsCreated).Distinct().ToArray())
                {
                    if (IsOnline(group.LeaderGuid))
                    {
                        group.LeaderLastOnlineUnixSeconds = now;
                        continue;
                    }

                    if (now - group.LeaderLastOnlineUnixSeconds < delay || !ChooseLeader(group, onlineOnly: true))
                    {
                        continue;
                    }

                    Broadcast(group, WorldOpcode.SmsgGroupSetLeader, GroupPackets.BuildName(group.LeaderName));
                    SendUpdate(group);
                }
            }
            finally
            {
                Flush();
            }
        }
    }

    // ---- group mechanics (vmangos Group.cpp) -----------------------------------------------------------------------------------------------------

    /// <summary>vmangos Player::CanUninviteFromGroup.</summary>
    private static PartyResult CanUninvite(Group? group, ObjectGuid actor, ObjectGuid target)
    {
        if (group is null)
        {
            return PartyResult.NotInGroup;
        }

        return group.LeaderGuid == target || !group.IsLeaderOrAssistant(actor) ? PartyResult.NotLeader : PartyResult.Ok;
    }

    /// <summary>vmangos Group::AddMember: slot, then the group list for every online member.</summary>
    private bool AddMember(Group group, ObjectGuid guid, string name)
    {
        if (!group.AddMemberSlot(guid, name))
        {
            return false;
        }

        _memberOf[guid] = group;
        SendUpdate(group);
        return true;
    }

    /// <summary>vmangos Player::UninviteFromGroup.</summary>
    private void UninviteFromGroup(ObjectGuid guid)
    {
        if (!_invitedTo.TryGetValue(guid, out Group? group))
        {
            return;
        }

        RemoveInvite(group, guid);
        if (group.MemberCount <= 1)
        {
            if (group.IsCreated)
            {
                Disband(group, hideDestroy: true);
            }
            else
            {
                foreach (ObjectGuid invitee in group.Invitees.ToArray())
                {
                    RemoveInvite(group, invitee);
                }
            }
        }
    }

    /// <summary>vmangos Group::RemoveMember: above the minimum size the member leaves; at the minimum the group ends silently.</summary>
    private void RemoveFromGroup(Group group, ObjectGuid guid, bool kicked)
    {
        if (group.MemberCount <= Group.MinMemberCount)
        {
            Disband(group, hideDestroy: true);
            return;
        }

        GroupMemberSlot slot = group.Find(guid)!;
        group.RemoveMemberSlot(slot);
        _memberOf.Remove(guid);
        _readyAnswerAt.Remove(guid);
        bool leaderChanged = group.LeaderGuid == guid;
        if (leaderChanged)
        {
            ChooseLeader(group);
        }

        if (IsOnline(guid))
        {
            Draft left = DraftOf(guid);
            left.Replica = null;
            left.DepartedGroupId = group.Id;
            if (kicked)
            {
                left.Packets.Add(new GroupPacket(WorldOpcode.SmsgGroupUninvite, []));
            }

            left.Packets.Add(new GroupPacket(WorldOpcode.SmsgGroupList, GroupPackets.BuildEmptyGroupList()));
        }

        if (leaderChanged)
        {
            Broadcast(group, WorldOpcode.SmsgGroupSetLeader, GroupPackets.BuildName(group.LeaderName));
        }

        SendUpdate(group);
    }

    /// <summary>vmangos Group::Disband.</summary>
    private void Disband(Group group, bool hideDestroy)
    {
        ObjectGuid[] members = [.. group.Members.Select(m => m.Guid)];
        foreach (ObjectGuid invitee in group.Invitees.ToArray())
        {
            RemoveInvite(group, invitee);
        }

        group.Clear();
        foreach (ObjectGuid guid in members)
        {
            if (_memberOf.GetValueOrDefault(guid) == group)
            {
                _memberOf.Remove(guid);
            }

            _readyAnswerAt.Remove(guid);
            if (!IsOnline(guid))
            {
                continue;
            }

            Draft left = DraftOf(guid);
            left.Replica = null;
            left.DepartedGroupId = group.Id;
            left.Disbanded = true;
            if (!hideDestroy)
            {
                left.Packets.Add(new GroupPacket(WorldOpcode.SmsgGroupDestroyed, []));
            }

            left.Packets.Add(new GroupPacket(WorldOpcode.SmsgGroupList, GroupPackets.BuildEmptyGroupList()));
        }
    }

    /// <summary>vmangos Group::_chooseLeader: an online member, in raids an assistant first; otherwise the first member.</summary>
    private bool ChooseLeader(Group group, bool onlineOnly = false)
    {
        GroupMemberSlot? first = null;
        GroupMemberSlot? chosen = null;
        foreach (GroupMemberSlot member in group.Members)
        {
            if (!IsOnline(member.Guid))
            {
                continue;
            }

            if (group.IsRaid && !member.Assistant)
            {
                first ??= member;
                continue;
            }

            chosen = member;
            break;
        }

        chosen ??= first ?? (onlineOnly ? null : group.Members.FirstOrDefault());
        if (chosen is null)
        {
            return false;
        }

        ChangeLeader(group, chosen);
        return true;
    }

    private void ChangeLeader(Group group, GroupMemberSlot slot)
    {
        group.LeaderGuid = slot.Guid;
        group.LeaderName = slot.Name;
        group.LeaderLastOnlineUnixSeconds = UnixSecondsClock();
    }

    /// <summary>vmangos Group::SetTargetIcon.</summary>
    private void SetTargetIcon(Group group, byte icon, ObjectGuid target)
    {
        if (icon >= Group.TargetIconCount)
        {
            return;
        }

        if (!target.IsEmpty)
        {
            for (byte i = 0; i < Group.TargetIconCount; i++)
            {
                if (group.TargetIcons[i] == target)
                {
                    SetTargetIcon(group, i, ObjectGuid.Empty);
                }
            }
        }

        group.TargetIcons[icon] = target;
        GroupReplica replica = ReplicaOf(group);
        byte[] delta = GroupPackets.BuildTargetIconDelta(icon, target);
        foreach (GroupMemberSlot member in group.Members)
        {
            if (!IsOnline(member.Guid))
            {
                continue;
            }

            Draft d = DraftOf(member.Guid);
            d.Replica = replica;
            d.Packets.Add(new GroupPacket(WorldOpcode.MsgRaidTargetUpdate, delta));
        }
    }

    private void AddInvite(Group group, ObjectGuid guid)
    {
        group.AddInvite(guid);
        _invitedTo[guid] = group;
    }

    private void RemoveInvite(Group group, ObjectGuid guid)
    {
        group.RemoveInvite(guid);
        if (_invitedTo.GetValueOrDefault(guid) == group)
        {
            _invitedTo.Remove(guid);
            if (IsOnline(guid))
            {
                DraftOf(guid).InviteCancelled = true;
            }
        }
    }

    private uint AllocateId()
    {
        if (IdBlockAllocatorSource?.Invoke() is { } allocator)
        {
            uint allocated = allocator.Next();
            if (allocated >= _nextId && allocated < uint.MaxValue)
            {
                _nextId = allocated + 1;
            }

            return allocated;
        }

        if (RequireDurableIdsSource?.Invoke() == true)
        {
            throw new IdRangeExhaustedException(
                IdRangeScopes.Group,
                $"Group id allocator for scope '{IdRangeScopes.Group}' is not primed with a durable block (fail-closed in Worker mode).");
        }

        if (_nextId == 0)
        {
            throw new IdRangeExhaustedException(IdRangeScopes.Group, "group ID space exhausted");
        }

        return _nextId++;
    }

    // ---- events --------------------------------------------------------------------------------------------------------------------------------

    private string? WorkerOf(ObjectGuid guid)
    {
        if (_directory.FindByGuid(guid.Value) is { } entry)
        {
            return entry.WorkerId;
        }

        return _hint.TryGetValue(guid, out (string Worker, long AtMs) hint) && MillisecondsClock() - hint.AtMs < OnlineHintMs ? hint.Worker : null;
    }

    private bool IsOnline(ObjectGuid guid) => WorkerOf(guid) is not null;

    private Draft DraftOf(ObjectGuid guid)
    {
        if (!_drafts.TryGetValue(guid, out Draft? draft))
        {
            draft = new Draft();
            _drafts[guid] = draft;
        }

        return draft;
    }

    private void Result(ObjectGuid recipient, PartyOperation operation, string name, PartyResult result)
    {
        if (IsOnline(recipient))
        {
            DraftOf(recipient).Packets.Add(new GroupPacket(WorldOpcode.SmsgPartyCommandResult, GroupPackets.BuildPartyCommandResult(operation, name, result)));
        }
    }

    private void Broadcast(Group group, WorldOpcode opcode, byte[] payload)
    {
        foreach (GroupMemberSlot member in group.Members)
        {
            if (IsOnline(member.Guid))
            {
                DraftOf(member.Guid).Packets.Add(new GroupPacket(opcode, payload));
            }
        }
    }

    /// <summary>SMSG_GROUP_LIST to every online member (vmangos Group::SendUpdate), each with the group state its worker mirrors.</summary>
    private void SendUpdate(Group group, ObjectGuid offline = default)
    {
        byte[]? icons = !group.IsRaid && group.TargetIcons.Any(i => !i.IsEmpty)
            ? GroupPackets.BuildTargetIconList(group.TargetIcons)
            : null;
        GroupMemberStatus StatusOf(ObjectGuid guid) => guid == offline || !IsOnline(guid) ? GroupMemberStatus.Offline : GroupMemberStatus.Online;
        GroupReplica replica = ReplicaOf(group);
        foreach (GroupMemberSlot member in group.Members)
        {
            if (member.Guid == offline || !IsOnline(member.Guid))
            {
                continue;
            }

            Draft d = DraftOf(member.Guid);
            d.Replica = replica;
            d.Packets.Add(new GroupPacket(WorldOpcode.SmsgGroupList, GroupPackets.BuildGroupList(group, member, StatusOf)));
            if (icons is not null)
            {
                d.Packets.Add(new GroupPacket(WorldOpcode.MsgRaidTargetUpdate, icons));
            }
        }
    }

    private static GroupReplica ReplicaOf(Group group)
        => new(
            group.Id, group.IsRaid, group.LeaderGuid.Value, group.LeaderName, (byte)group.LootMethod, group.LooterGuid.Value, group.LootThreshold,
            [.. group.TargetIcons.Select(i => i.Value)],
            [.. group.Members.Select(m => new GroupReplicaMember(m.Guid.Value, m.Name, m.SubGroup, m.Assistant))]);

    /// <summary>Move the command's drafts into the queue of each recipient's worker.</summary>
    private void Flush()
    {
        foreach ((ObjectGuid guid, Draft draft) in _drafts)
        {
            if (WorkerOf(guid) is not { } worker)
            {
                continue;
            }

            if (!_queues.TryGetValue(worker, out Queue<GroupEvent>? queue))
            {
                queue = new Queue<GroupEvent>();
                _queues[worker] = queue;
            }

            queue.Enqueue(new GroupEvent(guid.Value, draft.Replica, draft.DepartedGroupId, draft.Disbanded, draft.Inviter, draft.Packets.ToArray(), draft.InviteCancelled));
            while (queue.Count > MaxQueuedEventsPerWorker)
            {
                queue.Dequeue();
            }
        }

        _drafts.Clear();
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Groups;

/// <summary>
/// Cluster mode of the group manager: with an <see cref="Authority"/> link every group operation of a player is forwarded to the Social lease holder
/// (<see cref="GroupAuthority"/>) instead of being decided here, and the manager only mirrors what comes back (<see cref="ApplyAuthorityEvent"/>): the group state its
/// local players belong to, and the packets for their sessions. Without a link (single process, Combined role) nothing here runs and the manager is authoritative as before.
/// A player inside a battleground raid keeps the local path (battleground raids are local to the worker that runs the battleground). World thread.
/// </summary>
public sealed partial class GroupManager
{
    private readonly Dictionary<uint, Group> _replicaById = [];

    /// <summary>The path to the group authority; null when this manager is the authority (set once at start by the social feature).</summary>
    public IGroupAuthorityLink? Authority { get; set; }

    private bool Forwarding(Player player) => Authority is not null && GetGroup(player.Guid) is not { IsBattlegroundGroup: true };

    private GroupCommand CommandOf(
        Player actor, GroupCommandKind kind, ulong targetGuid = 0, string targetName = "", string otherName = "", uint value = 0, uint value2 = 0, ulong master = 0, byte[]? payload = null)
        => new(kind, actor.Guid.Value, actor.Name, (byte)actor.Team, actor.IsGameMaster, targetGuid, targetName, otherName, value, value2, master, payload);

    /// <summary>Forwards <paramref name="command"/> when <paramref name="actor"/> is served by the authority; false when the caller must decide locally.</summary>
    private bool TryForward(Player actor, GroupCommand command)
    {
        if (!Forwarding(actor))
        {
            return false;
        }

        if (command.Kind is GroupCommandKind.Accept or GroupCommandKind.Decline)
        {
            // The answer ends the invite this worker mirrors; the holder's events confirm it.
            _invitedTo.Remove(actor.Guid);
        }

        Authority!.Forward(command);
        return true;
    }

    /// <summary>
    /// Relays a packet of <paramref name="player"/> to the group members on other workers (stats, minimap ping, roll); members on this worker were served by the caller.
    /// </summary>
    private void RelayToOtherWorkers(Player player, WorldOpcode opcode, byte[] payload)
    {
        if (Authority is not null && GetGroup(player.Guid) is { IsBattlegroundGroup: false })
        {
            Authority.Forward(CommandOf(player, GroupCommandKind.Relay, value: (uint)opcode, payload: payload));
        }
    }

    /// <summary>
    /// Applies one event of the authority for a character of this worker: installs the group state, or drops the membership, then sends the packets to the session (world
    /// thread). An invite notice for a character that ignores the inviter is answered with <see cref="GroupCommandKind.RefuseIgnored"/> and not shown.
    /// </summary>
    public void ApplyAuthorityEvent(GroupEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var guid = new ObjectGuid(ev.RecipientGuid);
        Player? player = context.World.FindOnlinePlayer(guid);
        if (ev.InviterGuid != 0 && player is not null && context.IsIgnoring(player, new ObjectGuid(ev.InviterGuid)))
        {
            Authority?.Forward(CommandOf(player, GroupCommandKind.RefuseIgnored, targetGuid: ev.InviterGuid));
            return;
        }

        if (ev.InviteCancelled)
        {
            _invitedTo.Remove(guid);
        }

        if (ev.InviterGuid != 0 && player is not null)
        {
            MirrorInvite(player, ev);
        }

        if (ev.DepartedGroupId != 0)
        {
            ApplyDeparture(guid, ev.DepartedGroupId, ev.Disbanded);
        }
        else if (ev.Replica is { } replica)
        {
            ApplyReplica(replica);
        }

        if (player is null)
        {
            if (ev.DepartedGroupId == 0 && ev.Replica is { } mirrored && _replicaById.TryGetValue(mirrored.GroupId, out Group? lingering))
            {
                DropReplicaIfNoLocalMember(lingering);
            }

            return;
        }

        UpdateLeaderFlag(player);
        foreach (GroupPacket packet in ev.Packets)
        {
            player.Session.Send(packet.Opcode, packet.Payload);
        }
    }

    /// <summary>
    /// Keeps the pending invite of a character of this worker visible to <see cref="GetInvite"/> (the playerbots answer invites from it): a not yet created group led by the inviter with
    /// the character as its invitee. Ended by an <see cref="GroupEvent.InviteCancelled"/> event or by the character's own accept or decline.
    /// </summary>
    private void MirrorInvite(Player invitee, GroupEvent ev)
    {
        var inviter = new ObjectGuid(ev.InviterGuid);
        string name = context.World.FindOnlinePlayer(inviter)?.Name ?? string.Empty;
        if (name.Length == 0)
        {
            GroupPacket? notice = ev.Packets.FirstOrDefault(p => p.Opcode == WorldOpcode.SmsgGroupInvite);
            if (notice is not null)
            {
                int end = Array.IndexOf(notice.Payload, (byte)0);
                name = System.Text.Encoding.UTF8.GetString(notice.Payload, 0, end < 0 ? notice.Payload.Length : end);
            }
        }

        var pending = new Group(0) { LeaderGuid = inviter, LeaderName = name };
        pending.AddInvite(invitee.Guid);
        _invitedTo[invitee.Guid] = pending;
    }

    /// <summary>Install the holder's state of a group; raises the member and leader events for what differs from the previous mirror.</summary>
    private void ApplyReplica(GroupReplica r)
    {
        bool isNew = !_replicaById.TryGetValue(r.GroupId, out Group? group);
        if (isNew)
        {
            group = new Group(r.GroupId);
            _replicaById[r.GroupId] = group;
        }

        group = group!;
        var before = new HashSet<ObjectGuid>(group.Members.Select(m => m.Guid));
        ObjectGuid oldLeader = group.LeaderGuid;
        group.IsCreated = true;
        group.Type = r.IsRaid ? GroupType.Raid : GroupType.Normal;
        group.LootMethod = r.LootMethod <= (byte)LootMethod.NeedBeforeGreed ? (LootMethod)r.LootMethod : LootMethod.GroupLoot;
        group.LootThreshold = r.LootThreshold is >= Group.MinLootThreshold and <= Group.MaxLootThreshold ? r.LootThreshold : Group.DefaultLootThreshold;
        group.LooterGuid = new ObjectGuid(r.Looter);
        group.LeaderGuid = new ObjectGuid(r.Leader);
        group.LeaderName = r.LeaderName;
        group.LeaderLastOnlineUnixSeconds = UnixSecondsClock();
        for (int i = 0; i < Group.TargetIconCount; i++)
        {
            group.TargetIcons[i] = i < r.TargetIcons.Count ? new ObjectGuid(r.TargetIcons[i]) : ObjectGuid.Empty;
        }

        group.Clear();
        foreach (GroupReplicaMember m in r.Members)
        {
            group.RestoreMemberSlot(new ObjectGuid(m.Guid), m.Name, m.SubGroup, m.Assistant);
        }

        var now = new HashSet<ObjectGuid>(group.Members.Select(m => m.Guid));
        foreach (ObjectGuid gone in before)
        {
            if (!now.Contains(gone))
            {
                ForgetMembership(gone, group);
                MemberLeft?.Invoke(group, gone, false, false);
                MemberRemoved?.Invoke(group, gone);
            }
        }

        foreach (GroupMemberSlot slot in group.Members)
        {
            // A player inside a battleground raid keeps that raid; the replica is then its original group (as Group::_addMember).
            if (_memberOf.TryGetValue(slot.Guid, out Group? current) && current != group && current.IsBattlegroundGroup)
            {
                _originalOf[slot.Guid] = group;
            }
            else
            {
                _memberOf[slot.Guid] = group;
            }

            if (!before.Contains(slot.Guid))
            {
                MemberAdded?.Invoke(group, slot.Guid);
            }
        }

        if (!isNew && oldLeader != group.LeaderGuid && !oldLeader.IsEmpty)
        {
            LeaderChanged?.Invoke(group, oldLeader);
        }
    }

    /// <summary>The character left <paramref name="groupId"/> (or the group ended): drop the mirrored membership.</summary>
    private void ApplyDeparture(ObjectGuid guid, uint groupId, bool disbanded)
    {
        if (!_replicaById.TryGetValue(groupId, out Group? group))
        {
            if (_memberOf.TryGetValue(guid, out Group? stale) && stale.Id == groupId && !stale.IsBattlegroundGroup)
            {
                _memberOf.Remove(guid);
            }

            return;
        }

        if (disbanded)
        {
            Disbanding?.Invoke(group, ObjectGuid.Empty);
            DropReplica(group);
            return;
        }

        if (group.Find(guid) is { } slot)
        {
            group.RemoveMemberSlot(slot);
            ForgetMembership(guid, group);
            MemberLeft?.Invoke(group, guid, false, false);
            MemberRemoved?.Invoke(group, guid);
        }

        DropReplicaIfNoLocalMember(group);
    }

    /// <summary>Forget a mirrored group and every membership that pointed at it (no events).</summary>
    private void DropReplica(Group group)
    {
        foreach (ObjectGuid member in group.Members.Select(m => m.Guid).ToArray())
        {
            ForgetMembership(member, group);
        }

        group.Clear();
        _replicaById.Remove(group.Id);
    }

    /// <summary>A mirrored group with no member online on this worker is dropped; the next login or event brings it back.</summary>
    private void DropReplicaIfNoLocalMember(Group group)
    {
        if (!group.Members.Any(m => context.World.FindOnlinePlayer(m.Guid) is not null))
        {
            DropReplica(group);
        }
    }
}

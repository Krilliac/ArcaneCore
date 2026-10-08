using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Groups;

// The group operations of the meeting stone queue (vmangos LFGQueue creating a party and adding the members it found,
// LFGQueue.cpp:180-206 and 370-380, and the RemoveMember details its hooks need, Group.cpp:428-526).
public sealed partial class GroupManager
{
    /// <summary>
    /// A member left or was removed without disbanding the group, with how (world thread): <c>kicked</c> by the leader (GROUP_KICK) or left
    /// (GROUP_LEAVE), and whether the leader changed because of it. Raised just before <see cref="MemberRemoved"/>.
    /// </summary>
    public event Action<Group, ObjectGuid, bool, bool>? MemberLeft;

    /// <summary>
    /// The party the meeting stone queue forms from two queued players (LFGQueue.cpp:185-206): created with <paramref name="leader"/> as leader
    /// and looter (vmangos Group::Create), then <paramref name="member"/> added. Null when either already belongs to a group or has an invite.
    /// </summary>
    public Group? CreateLfgGroup(Player leader, Player member)
    {
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(member);
        if (leader.Guid == member.Guid || GetGroup(leader.Guid) is not null || GetGroup(member.Guid) is not null
            || GetInvite(leader.Guid) is not null || GetInvite(member.Guid) is not null)
        {
            return null;
        }

        var group = new Group(_nextId++)
        {
            LeaderGuid = leader.Guid,
            LeaderName = leader.Name,
            LeaderLastOnlineUnixSeconds = UnixSecondsClock(),
            IsCreated = true,
        };
        group.LooterGuid = group.LeaderGuid;
        if (!AddMember(group, leader.Guid, leader.Name))
        {
            return null;
        }

        UpdateLeaderFlag(leader);
        AddMember(group, member.Guid, member.Name);
        return group;
    }

    /// <summary>vmangos Group::AddMember(guid, name, GROUP_LFG) for a player the queue found (LFGQueue.cpp:370-380). False when full or grouped.</summary>
    public bool AddLfgMember(Group group, Player member)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(member);
        return group.IsCreated && !group.IsFull && GetGroup(member.Guid) is null && GetInvite(member.Guid) is null
            && AddMember(group, member.Guid, member.Name);
    }
}

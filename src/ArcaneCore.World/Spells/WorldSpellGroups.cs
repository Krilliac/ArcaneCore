using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Spells;

/// <summary>
/// <see cref="ISpellGroupResolver"/> over the social area's <see cref="GroupManager"/> (vmangos
/// Player::GetGroup / Group::SameSubGroup). The manager is resolved lazily on the world thread
/// because the social feature may attach after this one; without it a unit is alone.
/// </summary>
public sealed class WorldSpellGroups(Func<GroupManager?> groups) : ISpellGroupResolver
{
    private GroupManager? Manager()
    {
        try
        {
            return groups();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public bool IsMember(Unit unit, ObjectGuid other, bool raid)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (Manager()?.GetGroup(unit.Guid) is not { } group)
        {
            return unit.Guid == other;
        }

        // Manual scans (no Find lambda): this runs on map threads and must not allocate.
        IReadOnlyList<GroupMemberSlot> members = group.Members;
        GroupMemberSlot? self = null;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i].Guid == unit.Guid)
            {
                self = members[i];
                break;
            }
        }

        if (self is null)
        {
            return unit.Guid == other;
        }

        bool anySubGroup = raid || !group.IsRaid;
        for (int i = 0; i < members.Count; i++)
        {
            GroupMemberSlot m = members[i];
            if (m.Guid == other && (anySubGroup || m.SubGroup == self.SubGroup))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
    {
        ArgumentNullException.ThrowIfNull(unit);
        GroupManager? manager = Manager();

        if (manager?.GetGroup(unit.Guid) is not { } group || group.Find(unit.Guid) is not { } self)
        {
            return [unit.Guid];
        }

        // A raid's "party" is the caster's sub-group (vmangos Group::SameSubGroup).
        return [.. group.Members
            .Where(m => raid || !group.IsRaid || m.SubGroup == self.SubGroup)
            .Select(m => m.Guid)];
    }
}

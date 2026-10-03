namespace ArcaneCore.Game.Groups;

/// <summary>The outcome of <see cref="GroupLooterSelection.Next"/>: the looter afterwards and whether it differs from before.</summary>
public readonly record struct LooterSelection(ObjectGuid Looter, bool Changed);

/// <summary>
/// The group's round-robin looter pointer, a pure port of vmangos Group::UpdateLooterGuid
/// (D:\refs\vmangos\src\game\Group\Group.cpp:2474-2543) and of the master looter fallback in
/// Unit::Kill (D:\refs\vmangos\src\game\Objects\Unit.cpp:1041-1063).
/// Retail calls the selection twice per creature kill (Unit.cpp:1037 and 1078): first with
/// <c>ifNeeded</c> to settle who loots THIS kill (the current looter keeps the turn while still in
/// reach), then without it to advance the pointer for the NEXT kill.
/// </summary>
public static class GroupLooterSelection
{
    /// <summary>
    /// Group::UpdateLooterGuid. Master loot and free-for-all never move the pointer. Otherwise the
    /// current looter is kept when <paramref name="ifNeeded"/> and still <paramref name="eligible"/>;
    /// else the next eligible member after the current one (group order, wrapping, the current looter
    /// last) is chosen, and when nobody is eligible the looter is cleared. A current looter that is
    /// not a member searches from the start of the member list.
    /// </summary>
    public static LooterSelection Next(Group group, ObjectGuid current, bool ifNeeded, Func<ObjectGuid, bool> eligible)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(eligible);
        if (group.LootMethod is LootMethod.MasterLoot or LootMethod.FreeForAll)
        {
            return new LooterSelection(current, false);
        }

        IReadOnlyList<GroupMemberSlot> members = group.Members;
        int start = 0;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i].Guid != current)
            {
                continue;
            }

            if (ifNeeded && eligible(current))
            {
                return new LooterSelection(current, false);
            }

            start = i + 1;
            break;
        }

        for (int n = 0; n < members.Count; n++)
        {
            ObjectGuid candidate = members[(start + n) % members.Count].Guid;
            if (eligible(candidate))
            {
                return new LooterSelection(candidate, candidate != current);
            }
        }

        return new LooterSelection(default, !current.IsEmpty);
    }

    /// <summary>
    /// Unit::Kill's replacement for an unavailable master looter: the first member other than the
    /// master who is the group leader or an assistant and is <paramref name="isOnline"/>, in member
    /// order; null when there is none (the caller then switches the group to group loot, threshold
    /// uncommon).
    /// </summary>
    public static ObjectGuid? MasterLooterFallback(Group group, Func<ObjectGuid, bool> isOnline)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(isOnline);
        foreach (GroupMemberSlot member in group.Members)
        {
            if (member.Guid != group.LooterGuid && (member.Guid == group.LeaderGuid || member.Assistant) && isOnline(member.Guid))
            {
                return member.Guid;
            }
        }

        return null;
    }
}

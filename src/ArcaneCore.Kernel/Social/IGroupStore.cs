namespace ArcaneCore.Kernel.Social;

/// <summary>One member of a stored group (vmangos <c>group_member</c>: member_guid, assistant, subgroup), in slot order.</summary>
public sealed record GroupMemberRecord(int CharacterId, byte SubGroup, bool Assistant);

/// <summary>
/// A stored party or raid (vmangos <c>groups</c>: group_id, leader_guid, loot_method, looter_guid, loot_threshold,
/// icon1..icon8, is_raid). Character ids are the low parts of the player guids; <see cref="TargetIcons"/> holds the eight
/// raw target guids (0 when unset). vmangos' main tank and main assistant columns have no counterpart here: the 1.12
/// group list carries no such fields and nothing in this server sets them.
/// </summary>
public sealed record GroupRecord(
    uint Id,
    int LeaderId,
    byte LootMethod,
    int LooterId,
    byte LootThreshold,
    bool IsRaid,
    IReadOnlyList<ulong> TargetIcons,
    IReadOnlyList<GroupMemberRecord> Members)
{
    /// <summary>Value equality over every field, the lists included (a record compares its list references only).</summary>
    public bool SameAs(GroupRecord? other)
        => other is not null
            && Id == other.Id && LeaderId == other.LeaderId && LootMethod == other.LootMethod && LooterId == other.LooterId
            && LootThreshold == other.LootThreshold && IsRaid == other.IsRaid
            && TargetIcons.SequenceEqual(other.TargetIcons) && Members.SequenceEqual(other.Members);
}

/// <summary>
/// Persistence of parties and raids across a world restart (vmangos Group::Create / SaveToDB, the member and leader
/// updates of Group.cpp, ObjectMgr::LoadGroups). The world keeps the working set in memory and writes whole snapshots.
/// </summary>
public interface IGroupStore
{
    /// <summary>Every stored group with its members (in slot order), lowest id first.</summary>
    Task<IReadOnlyList<GroupRecord>> LoadGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert or replace the group and its member rows, in one transaction. A character listed here leaves any other
    /// stored group (a character is in at most one group, vmangos <c>group_member</c> is keyed by the member).
    /// </summary>
    Task SaveGroupAsync(GroupRecord group, CancellationToken cancellationToken = default);

    /// <summary>Remove the group and its member rows (no-op when there is none).</summary>
    Task DeleteGroupAsync(uint groupId, CancellationToken cancellationToken = default);
}

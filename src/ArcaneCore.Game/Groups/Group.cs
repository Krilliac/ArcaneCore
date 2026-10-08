namespace ArcaneCore.Game.Groups;

/// <summary>One member slot (vmangos Group::MemberSlot).</summary>
public sealed class GroupMemberSlot(ObjectGuid guid, string name, byte subGroup)
{
    public ObjectGuid Guid { get; } = guid;

    public string Name { get; internal set; } = name;

    public byte SubGroup { get; internal set; } = subGroup;

    public bool Assistant { get; internal set; }
}

/// <summary>
/// A party or raid (vmangos Group). State only; <see cref="GroupManager"/> runs the rules and
/// sends the packets. Groups live in memory for the lifetime of the world daemon (vmangos also
/// stores them in the groups / group_member tables; see docs/areas/social.md). World thread.
/// </summary>
public sealed class Group
{
    /// <summary>vmangos MAX_GROUP_SIZE.</summary>
    public const int MaxGroupSize = 5;

    /// <summary>vmangos MAX_RAID_SIZE.</summary>
    public const int MaxRaidSize = 40;

    /// <summary>vmangos MAX_RAID_SUBGROUPS.</summary>
    public const int MaxRaidSubGroups = MaxRaidSize / MaxGroupSize;

    /// <summary>vmangos TARGET_ICON_COUNT.</summary>
    public const int TargetIconCount = 8;

    /// <summary>vmangos ITEM_QUALITY_UNCOMMON, the default loot threshold (Group::Create).</summary>
    public const byte DefaultLootThreshold = 2;

    /// <summary>The lowest loot threshold a leader may set: ITEM_QUALITY_UNCOMMON.</summary>
    public const byte MinLootThreshold = 2;

    /// <summary>The highest loot threshold a leader may set: ITEM_QUALITY_ARTIFACT, the top 1.12 item quality.</summary>
    public const byte MaxLootThreshold = 6;

    private readonly List<GroupMemberSlot> _members = [];
    private readonly HashSet<ObjectGuid> _invitees = [];
    private readonly byte[] _subGroupCounts = new byte[MaxRaidSubGroups];

    internal Group(uint id) => Id = id;

    public uint Id { get; }

    /// <summary>False until the first invite is accepted (vmangos IsCreated).</summary>
    public bool IsCreated { get; internal set; }

    public GroupType Type { get; internal set; } = GroupType.Normal;

    public bool IsRaid => Type == GroupType.Raid;

    public ObjectGuid LeaderGuid { get; internal set; }

    public string LeaderName { get; internal set; } = string.Empty;

    /// <summary>Last online time of the leader (D:\refs\vmangos\src\game\Group\Group.cpp:1448-1471).</summary>
    internal long LeaderLastOnlineUnixSeconds { get; set; }

    public LootMethod LootMethod { get; internal set; } = LootMethod.GroupLoot;

    public byte LootThreshold { get; internal set; } = DefaultLootThreshold;

    public ObjectGuid LooterGuid { get; internal set; }

    public ObjectGuid[] TargetIcons { get; } = new ObjectGuid[TargetIconCount];

    public IReadOnlyList<GroupMemberSlot> Members => _members;

    public IReadOnlyCollection<ObjectGuid> Invitees => _invitees;

    public int MemberCount => _members.Count;

    /// <summary>vmangos GetMembersMinCount: 2 outside battlegrounds.</summary>
    public static int MinMemberCount => 2;

    public bool IsFull => _members.Count >= (IsRaid ? MaxRaidSize : MaxGroupSize);

    public bool IsLeader(ObjectGuid guid) => LeaderGuid == guid;

    public bool IsAssistant(ObjectGuid guid) => Find(guid)?.Assistant == true;

    public bool IsLeaderOrAssistant(ObjectGuid guid) => IsLeader(guid) || IsAssistant(guid);

    public bool IsMember(ObjectGuid guid) => Find(guid) is not null;

    public GroupMemberSlot? Find(ObjectGuid guid) => _members.Find(m => m.Guid == guid);

    public GroupMemberSlot? FindByName(string name) => _members.Find(m => m.Name == name);

    public bool IsInvited(ObjectGuid guid) => _invitees.Contains(guid);

    public byte SubGroupCount(byte subGroup) => _subGroupCounts[subGroup];

    /// <summary>vmangos HasFreeSlotSubGroup.</summary>
    public bool HasFreeSlotSubGroup(byte subGroup) => subGroup < MaxRaidSubGroups && _subGroupCounts[subGroup] < MaxGroupSize;

    internal void AddInvite(ObjectGuid guid) => _invitees.Add(guid);

    internal bool RemoveInvite(ObjectGuid guid) => _invitees.Remove(guid);

    internal void ClearInvites() => _invitees.Clear();

    /// <summary>vmangos Group::_addMember: the first subgroup with room (raids), otherwise 0.</summary>
    internal bool AddMemberSlot(ObjectGuid guid, string name)
    {
        byte subGroup = 0;
        if (IsRaid)
        {
            while (subGroup < MaxRaidSubGroups && _subGroupCounts[subGroup] >= MaxGroupSize)
            {
                subGroup++;
            }

            if (subGroup == MaxRaidSubGroups)
            {
                return false;
            }
        }

        _members.Add(new GroupMemberSlot(guid, name, subGroup));
        _subGroupCounts[subGroup]++;
        return true;
    }

    /// <summary>vmangos Group::LoadMemberFromDB: a stored slot with its own subgroup and assistant flag; a duplicate or a full subgroup is refused.</summary>
    internal bool RestoreMemberSlot(ObjectGuid guid, string name, byte subGroup, bool assistant)
    {
        if (Find(guid) is not null || subGroup >= MaxRaidSubGroups || (!IsRaid && subGroup != 0) || _subGroupCounts[subGroup] >= MaxGroupSize
            || _members.Count >= (IsRaid ? MaxRaidSize : MaxGroupSize))
        {
            return false;
        }

        _members.Add(new GroupMemberSlot(guid, name, subGroup) { Assistant = assistant && IsRaid });
        _subGroupCounts[subGroup]++;
        return true;
    }

    internal void RemoveMemberSlot(GroupMemberSlot slot)
    {
        _members.Remove(slot);
        if (_subGroupCounts[slot.SubGroup] > 0)
        {
            _subGroupCounts[slot.SubGroup]--;
        }
    }

    internal void MoveToSubGroup(GroupMemberSlot slot, byte subGroup)
    {
        if (_subGroupCounts[slot.SubGroup] > 0)
        {
            _subGroupCounts[slot.SubGroup]--;
        }

        slot.SubGroup = subGroup;
        _subGroupCounts[subGroup]++;
    }

    /// <summary>vmangos ConvertToRaid → _initRaidSubGroupsCounter: everyone counts in their current subgroup (0).</summary>
    internal void ConvertToRaid()
    {
        Type = GroupType.Raid;
        Array.Clear(_subGroupCounts);
        foreach (GroupMemberSlot member in _members)
        {
            _subGroupCounts[member.SubGroup]++;
        }
    }

    internal void Clear()
    {
        _members.Clear();
        _invitees.Clear();
        Array.Clear(_subGroupCounts);
    }
}

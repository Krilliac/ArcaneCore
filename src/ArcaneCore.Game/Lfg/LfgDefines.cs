using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Lfg;

// Re-implemented from vmangos LFG/LFGDefines.h, LFG/LFGMgr.cpp and Server/Packets/Misc.cpp (behaviour and wire layouts only, no code copied).

/// <summary>vmangos LfgRoles (LFGDefines.h:22-28).</summary>
[Flags]
public enum LfgRoles : uint
{
    None = 0x00,
    Tank = 0x01,
    Healer = 0x02,
    Damage = 0x04,
}

/// <summary>vmangos LfgRolePriority (LFGDefines.h:30-36).</summary>
public enum LfgRolePriority
{
    None = 0,
    Low = 1,
    Normal = 2,
    High = 3,
}

/// <summary>vmangos MeetingstoneQueueStatus (LFGDefines.h:50-58), the status byte of SMSG_MEETINGSTONE_SETQUEUE.</summary>
public enum MeetingStoneStatus : byte
{
    LeaveQueue = 0,
    JoinedQueue = 1,
    PartyMemberLeftLfg = 2,
    PartyMemberRemovedPartyRemoved = 3,
    LookingForNewPartyInQueue = 4,
    None = 5,
}

/// <summary>vmangos MeetingstoneFailedStatus (LFGDefines.h:60-66), the reason byte of SMSG_MEETINGSTONE_JOINFAILED.</summary>
public enum MeetingStoneFailure : byte
{
    PartyLeader = 1,
    FullGroup = 2,
    RaidGroup = 3,
}

/// <summary>The class tables of vmangos LFGMgr (CalculateRoles, GetPriority, GetMaximumDPSSlots; LFGMgr.cpp:84-160, LFGMgr.h:50).</summary>
public static class LfgRules
{
    /// <summary>LFGQueue::m_groupSize: a dungeon party.</summary>
    public const int GroupSize = 5;

    /// <summary>LFGMgr::GetMaximumDPSSlots.</summary>
    public const uint MaxDpsSlots = 3;

    /// <summary>The roles in the order the queue tries them (LFGQueue.cpp:30-35 PotentialRoles).</summary>
    public static IReadOnlyList<LfgRoles> PotentialRoles { get; } = [LfgRoles.Tank, LfgRoles.Healer, LfgRoles.Damage];

    /// <summary>LFGMgr::CalculateRoles: the roles a class can fill.</summary>
    public static LfgRoles RolesOf(Class playerClass) => playerClass switch
    {
        Class.Druid => LfgRoles.Tank | LfgRoles.Damage | LfgRoles.Healer,
        Class.Hunter => LfgRoles.Damage,
        Class.Mage => LfgRoles.Damage,
        Class.Paladin => LfgRoles.Tank | LfgRoles.Damage | LfgRoles.Healer,
        Class.Priest => LfgRoles.Damage | LfgRoles.Healer,
        Class.Rogue => LfgRoles.Damage,
        Class.Shaman => LfgRoles.Damage | LfgRoles.Healer,
        Class.Warlock => LfgRoles.Damage,
        Class.Warrior => LfgRoles.Tank | LfgRoles.Damage,
        _ => LfgRoles.None,
    };

    /// <summary>LFGMgr::GetPriority: how much a class is wanted in a role.</summary>
    public static LfgRolePriority PriorityOf(Class playerClass, LfgRoles role) => role switch
    {
        LfgRoles.Tank => playerClass switch
        {
            Class.Druid or Class.Paladin => LfgRolePriority.Normal,
            Class.Warrior => LfgRolePriority.High,
            _ => LfgRolePriority.None,
        },
        LfgRoles.Healer => playerClass switch
        {
            Class.Druid or Class.Paladin or Class.Priest or Class.Shaman => LfgRolePriority.High,
            _ => LfgRolePriority.None,
        },
        LfgRoles.Damage => playerClass switch
        {
            Class.Druid or Class.Paladin or Class.Shaman or Class.Warrior => LfgRolePriority.Normal,
            Class.Hunter or Class.Mage or Class.Rogue or Class.Warlock => LfgRolePriority.High,
            Class.Priest => LfgRolePriority.Low,
            _ => LfgRolePriority.None,
        },
        _ => LfgRolePriority.None,
    };
}

/// <summary>The meeting stone packets (vmangos Server/Packets/Misc.cpp:441-500; gtker wow_messages meetingstone/*.wowm).</summary>
public static class MeetingStonePackets
{
    /// <summary>SMSG_MEETINGSTONE_SETQUEUE: u32 area id, u8 status.</summary>
    public static byte[] SetQueue(uint areaId, MeetingStoneStatus status)
    {
        var writer = new PacketWriter(5);
        writer.WriteUInt32(areaId);
        writer.WriteByte((byte)status);
        return writer.ToArray();
    }

    /// <summary>SMSG_MEETINGSTONE_JOINFAILED: u8 reason.</summary>
    public static byte[] JoinFailed(MeetingStoneFailure reason) => [(byte)reason];

    /// <summary>SMSG_MEETINGSTONE_MEMBER_ADDED: u64 player guid.</summary>
    public static byte[] MemberAdded(ObjectGuid player)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(player.Value);
        return writer.ToArray();
    }
}

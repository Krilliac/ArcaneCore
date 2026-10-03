namespace ArcaneCore.Game.Groups;

/// <summary>vmangos Group.h GroupType.</summary>
public enum GroupType : byte
{
    Normal = 0,
    Raid = 1,
}

/// <summary>vmangos LootMgr.h LootMethod.</summary>
public enum LootMethod : byte
{
    FreeForAll = 0,
    RoundRobin = 1,
    MasterLoot = 2,
    GroupLoot = 3,
    NeedBeforeGreed = 4,
}

/// <summary>Member status bits in SMSG_GROUP_LIST / SMSG_PARTY_MEMBER_STATS (vmangos Group.h GroupMemberOnlineStatus).</summary>
[Flags]
public enum GroupMemberStatus : byte
{
    Offline = 0x00,
    Online = 0x01,
    Pvp = 0x02,
    Dead = 0x04,
    Ghost = 0x08,
    PvpFfa = 0x10,
    Afk = 0x40,
    Dnd = 0x80,
}

/// <summary>SMSG_PARTY_COMMAND_RESULT operation (vmangos SharedDefines.h PartyOperation).</summary>
public enum PartyOperation : uint
{
    Invite = 0,
    Leave = 2,
}

/// <summary>SMSG_PARTY_COMMAND_RESULT result (vmangos SharedDefines.h PartyResult, 1.12).</summary>
public enum PartyResult : uint
{
    Ok = 0,
    BadPlayerName = 1,
    TargetNotInGroup = 2,
    GroupFull = 3,
    AlreadyInGroup = 4,
    NotInGroup = 5,
    NotLeader = 6,
    WrongFaction = 7,
    IgnoringYou = 8,
}

/// <summary>SMSG_PARTY_MEMBER_STATS field mask (vmangos Group.h GroupUpdateFlags).</summary>
[Flags]
public enum GroupUpdateFlags : uint
{
    None = 0,
    Status = 0x00000001,
    CurrentHp = 0x00000002,
    MaxHp = 0x00000004,
    PowerType = 0x00000008,
    CurrentPower = 0x00000010,
    MaxPower = 0x00000020,
    Level = 0x00000040,
    Zone = 0x00000080,
    Position = 0x00000100,
    Auras = 0x00000200,
    AurasNegative = 0x00000400,
    PetGuid = 0x00000800,
    PetName = 0x00001000,
    PetModelId = 0x00002000,
    PetCurrentHp = 0x00004000,
    PetMaxHp = 0x00008000,
    PetPowerType = 0x00010000,
    PetCurrentPower = 0x00020000,
    PetMaxPower = 0x00040000,
    PetAuras = 0x00080000,
    PetAurasNegative = 0x00100000,
    Full = 0x001FFFFF,
}

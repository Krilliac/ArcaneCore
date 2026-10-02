namespace ArcaneCore.Protocol;

/// <summary>
/// Chat message types for builds 1.7–1.12. gtker social_common.wowm (versions 1.7–1.12) and
/// vmangos SharedDefines.h ChatMsg agree on every value listed.
/// </summary>
public enum ChatType : byte
{
    Say = 0x00,
    Party = 0x01,
    Raid = 0x02,
    Guild = 0x03,
    Officer = 0x04,
    Yell = 0x05,
    Whisper = 0x06,
    WhisperInform = 0x07,
    Emote = 0x08,
    TextEmote = 0x09,
    System = 0x0A,
    MonsterSay = 0x0B,
    MonsterYell = 0x0C,
    MonsterEmote = 0x0D,
    Channel = 0x0E,
    ChannelJoin = 0x0F,
    ChannelLeave = 0x10,
    ChannelList = 0x11,
    ChannelNotice = 0x12,
    ChannelNoticeUser = 0x13,
    Afk = 0x14,
    Dnd = 0x15,
    Ignored = 0x16,
    Skill = 0x17,
    Loot = 0x18,
    MonsterWhisper = 0x1A,
    BgSystemNeutral = 0x52,
    BgSystemAlliance = 0x53,
    BgSystemHorde = 0x54,
    RaidLeader = 0x57,
    RaidWarning = 0x58,
    RaidBossWhisper = 0x59,
    RaidBossEmote = 0x5A,
    Battleground = 0x5C,
    BattlegroundLeader = 0x5D,
}

/// <summary>Chat languages (gtker Language enum, vmangos SharedDefines.h Language).</summary>
public enum Language : uint
{
    Universal = 0,
    Orcish = 1,
    Darnassian = 2,
    Taurahe = 3,
    Dwarvish = 6,
    Common = 7,
    Demonic = 8,
    Titan = 9,
    Thalassian = 10,
    Draconic = 11,
    Kalimag = 12,
    Gnomish = 13,
    Troll = 14,
    Gutterspeak = 33,
    Addon = 0xFFFFFFFF,
}

/// <summary>The tag shown next to a speaker's name (gtker PlayerChatTag, vmangos CHAT_TAG_*).</summary>
public enum ChatTag : byte
{
    None = 0,
    Afk = 1,
    Dnd = 2,
    Gm = 3,
}

/// <summary>SMSG_LOGOUT_RESPONSE result (gtker LogoutResult; vmangos HandleLogoutRequestOpcode).</summary>
public enum LogoutResult : uint
{
    Success = 0,
    InCombat = 1,
    FrozenByGm = 2,
    JumpingOrFalling = 3,
}

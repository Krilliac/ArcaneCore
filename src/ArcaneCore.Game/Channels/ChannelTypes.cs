namespace ArcaneCore.Game.Channels;

/// <summary>SMSG_CHANNEL_NOTIFY type (vmangos Channel.h ChatNotify; gtker chat_notify).</summary>
public enum ChatNotify : byte
{
    Joined = 0x00,
    Left = 0x01,
    YouJoined = 0x02,
    YouLeft = 0x03,
    WrongPassword = 0x04,
    NotMember = 0x05,
    NotModerator = 0x06,
    PasswordChanged = 0x07,
    OwnerChanged = 0x08,
    PlayerNotFound = 0x09,
    NotOwner = 0x0A,
    ChannelOwner = 0x0B,
    ModeChange = 0x0C,
    AnnouncementsOn = 0x0D,
    AnnouncementsOff = 0x0E,
    ModerationOn = 0x0F,
    ModerationOff = 0x10,
    Muted = 0x11,
    PlayerKicked = 0x12,
    Banned = 0x13,
    PlayerBanned = 0x14,
    PlayerUnbanned = 0x15,
    PlayerNotBanned = 0x16,
    PlayerAlreadyMember = 0x17,
    Invite = 0x18,
    InviteWrongFaction = 0x19,
    WrongFaction = 0x1A,
    InvalidName = 0x1B,
    NotModerated = 0x1C,
    PlayerInvited = 0x1D,
    PlayerInviteBanned = 0x1E,
    Throttled = 0x1F,
}

/// <summary>Channel flags sent in YOU_JOINED and SMSG_CHANNEL_LIST (vmangos Channel.h ChannelFlags).</summary>
[Flags]
public enum ChannelFlags : byte
{
    None = 0x00,
    Custom = 0x01,
    Trade = 0x04,
    NotLfg = 0x08,
    General = 0x10,
    City = 0x20,
    Lfg = 0x40,
}

/// <summary>Per-member flags (vmangos Channel.h ChannelMemberFlags).</summary>
[Flags]
public enum ChannelMemberFlags : byte
{
    None = 0x00,
    Owner = 0x01,
    Moderator = 0x02,
    Voiced = 0x04,
    Muted = 0x08,
}

/// <summary>A built-in channel: its ChatChannels.dbc id, name pattern and resulting flags.</summary>
public sealed record BuiltInChannel(uint Id, string Pattern, ChannelFlags Flags);

/// <summary>
/// The built-in channels. vmangos recognises them through ChatChannels.dbc (GetChannelEntryFor:
/// the channel name contains the pattern with "%s" removed) and derives the flags in
/// Channel::Channel. This repository has no ChatChannels.dbc loader, so the rows are written out
/// from the vmangos Channel.h ChannelId values and its CHANNEL_DBC_FLAG comments (which channels
/// carry which DBC flags); they have not been checked against the 1.12.1 DBC file itself.
/// </summary>
public static class BuiltInChannels
{
    /// <summary>vmangos Channel.h ChannelId.</summary>
    public const uint GeneralId = 1;

    public const uint TradeId = 2;

    public const uint LocalDefenseId = 22;

    public const uint WorldDefenseId = 23;

    public const uint GuildRecruitmentId = 25;

    public const uint LookingForGroupId = 26;

    /// <summary>The honor rank needed to speak in WorldDefense (vmangos Channel::Say).</summary>
    public const byte WorldDefenseSpeakRank = 15;

    // DBC flags → channel flags (vmangos Channel::Channel): every built-in gets GENERAL; TRADE
    // for the trade flag; CITY for CITY_ONLY2; LFG for the LFG flag, NOT_LFG otherwise.
    public static readonly IReadOnlyList<BuiltInChannel> All =
    [
        new(GeneralId, "General - ", ChannelFlags.General | ChannelFlags.NotLfg),
        new(TradeId, "Trade - ", ChannelFlags.General | ChannelFlags.Trade | ChannelFlags.City | ChannelFlags.NotLfg),
        new(LocalDefenseId, "LocalDefense - ", ChannelFlags.General | ChannelFlags.NotLfg),
        new(WorldDefenseId, "WorldDefense", ChannelFlags.General | ChannelFlags.NotLfg),
        new(GuildRecruitmentId, "GuildRecruitment - ", ChannelFlags.General | ChannelFlags.City | ChannelFlags.NotLfg),
        new(LookingForGroupId, "LookingForGroup", ChannelFlags.General | ChannelFlags.Lfg),
    ];

    /// <summary>The flags of GuildRecruitment, which guilded players may not join (vmangos Channel::Join).</summary>
    public const ChannelFlags GuildRecruitmentFlags = ChannelFlags.General | ChannelFlags.City | ChannelFlags.NotLfg;

    /// <summary>vmangos GetChannelEntryFor(name): the first row whose pattern occurs in the name.</summary>
    public static BuiltInChannel? Find(string name) => All.FirstOrDefault(c => name.Contains(c.Pattern, StringComparison.Ordinal));
}

namespace ArcaneCore.Game.Guilds;

/// <summary>Rank rights (vmangos Guild.h GuildRankRights). A right is held when <c>(rights &amp; right) != Empty</c>.</summary>
public static class GuildRights
{
    public const uint Empty = 0x00000040;
    public const uint GuildChatListen = 0x00000041;
    public const uint GuildChatSpeak = 0x00000042;
    public const uint OfficerChatListen = 0x00000044;
    public const uint OfficerChatSpeak = 0x00000048;
    public const uint Promote = 0x000000C0;
    public const uint Demote = 0x00000140;
    public const uint Invite = 0x00000050;
    public const uint Remove = 0x00000060;
    public const uint SetMotd = 0x00001040;
    public const uint EditPublicNote = 0x00002040;
    public const uint ViewOfficerNote = 0x00004040;
    public const uint EditOfficerNote = 0x00008040;
    public const uint ModifyGuildInfo = 0x00010040;
    public const uint All = 0x000FF1FF;

    /// <summary>vmangos Guild::HasRankRight.</summary>
    public static bool Has(uint rights, uint right) => (rights & right) != Empty;
}

/// <summary>SMSG_GUILD_COMMAND_RESULT command (vmangos Guild.h Typecommand).</summary>
public enum GuildCommand : uint
{
    Create = 0x00,
    Invite = 0x01,
    Quit = 0x03,
    Founder = 0x0E,
}

/// <summary>SMSG_GUILD_COMMAND_RESULT result (vmangos Guild.h CommandErrors).</summary>
public enum GuildCommandError : uint
{
    PlayerNoMoreInGuild = 0x00,
    Internal = 0x01,
    AlreadyInGuild = 0x02,
    AlreadyInGuildS = 0x03,
    InvitedToGuild = 0x04,
    AlreadyInvitedToGuildS = 0x05,
    NameInvalid = 0x06,
    NameExistsS = 0x07,

    /// <summary>ERR_GUILD_LEADER_LEAVE for Quit, ERR_GUILD_PERMISSIONS otherwise (both 0x08).</summary>
    Permissions = 0x08,
    PlayerNotInGuild = 0x09,
    PlayerNotInGuildS = 0x0A,
    PlayerNotFoundS = 0x0B,
    NotAllied = 0x0C,
    RankTooHighS = 0x0D,
    RankTooLowS = 0x0E,
    RanksLocked = 0x11,
    RankInUse = 0x12,
    IgnoringYouS = 0x13,
}

/// <summary>SMSG_GUILD_EVENT event (vmangos Guild.h GuildEvents).</summary>
public enum GuildEvent : byte
{
    Promotion = 0x00,
    Demotion = 0x01,
    Motd = 0x02,
    Joined = 0x03,
    Left = 0x04,
    Removed = 0x05,
    LeaderIs = 0x06,
    LeaderChanged = 0x07,
    Disbanded = 0x08,
    TabardChange = 0x09,
    RankUpdated = 0x0A,
    RosterUpdate = 0x0B,
    SignedOn = 0x0C,
    SignedOff = 0x0D,
}

/// <summary>Roster presence flags (vmangos Guild.h GuildRosterFlags).</summary>
[Flags]
public enum GuildRosterFlags : byte
{
    Offline = 0x00,
    Online = 0x01,
    Afk = 0x02,
    Dnd = 0x04,
}

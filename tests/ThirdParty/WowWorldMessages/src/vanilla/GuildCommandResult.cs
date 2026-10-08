namespace WowWorldMessages.Vanilla;

public enum GuildCommandResult : byte {
    PlayerNoMoreInGuild = 0,
    GuildInternal = 1,
    AlreadyInGuild = 2,
    AlreadyInGuildS = 3,
    InvitedToGuild = 4,
    AlreadyInvitedToGuildS = 5,
    GuildNameInvalid = 6,
    GuildNameExistsS = 7,
    GuildPermissionsOrLeader = 8,
    GuildPlayerNotInGuild = 9,
    GuildPlayerNotInGuildS = 10,
    GuildPlayerNotFoundS = 11,
    GuildNotAllied = 12,
    GuildRankTooHighS = 13,
    GuildRankTooLowS = 14,
}


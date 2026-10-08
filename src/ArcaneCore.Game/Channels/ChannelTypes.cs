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

/// <summary>
/// A built-in channel: its ChatChannels.dbc id, its English name pattern (<c>%s</c> removed) and the channel flags vmangos
/// derives from the DBC flags. <see cref="Patterns"/> holds every locale's pattern a channel name is matched against
/// (just <see cref="Pattern"/> for the transcribed rows); <see cref="DbcFlags"/> are the raw DBC flags.
/// </summary>
public sealed record BuiltInChannel(uint Id, string Pattern, ChannelFlags Flags)
{
    public IReadOnlyList<string> Patterns { get; init; } = [Pattern];

    public uint DbcFlags { get; init; }
}

/// <summary>
/// The built-in channels: which channel names are constant channels, with which ids and flags (vmangos ChatChannels.dbc,
/// GetChannelEntryFor and Channel::Channel). <see cref="FromDbc"/> builds it from the client's own ChatChannels.dbc
/// (World:Chat:ChatChannelsDbcPath), every locale's pattern included; without one the transcribed <see cref="Builtin"/>
/// rows of the 1.12.1 client file are used, English patterns only.
/// </summary>
public sealed class ChatChannelCatalog
{
    /// <summary>ChatChannels.dbc flags (vmangos Channel.h ChannelDBCFlags).</summary>
    public const uint DbcTrade = 0x00008;

    public const uint DbcCityOnly2 = 0x00020;

    public const uint DbcLfg = 0x40000;

    private ChatChannelCatalog(IReadOnlyList<BuiltInChannel> channels, bool fromClientData)
    {
        Channels = channels;
        FromClientData = fromClientData;
    }

    /// <summary>
    /// The six rows of the 1.12.1 client's ChatChannels.dbc (build 5875, 21 fields), in file order, transcribed from the
    /// developer's own client file: ids 1, 2, 22, 23, 24, 25 with DBC flags 0x3, 0x3B, 0x10003, 0x10004, 0x0, 0x20032.
    /// vmangos' Channel.h comments and its CHANNEL_ID_LOOKING_FOR_GROUP = 26 describe the 2.x file (37 fields, where
    /// LookingForGroup is id 26 with the LFG flag); on a 1.12 client file vmangos itself computes id 24 and General|NotLfg.
    /// </summary>
    public static ChatChannelCatalog Builtin { get; } = new(
    [
        Row(BuiltInChannels.GeneralId, "General - %s", 0x00003),
        Row(BuiltInChannels.TradeId, "Trade - %s", 0x0003B),
        Row(BuiltInChannels.LocalDefenseId, "LocalDefense - %s", 0x10003),
        Row(BuiltInChannels.WorldDefenseId, "WorldDefense", 0x10004),
        Row(BuiltInChannels.LookingForGroupId, "LookingForGroup", 0x00000),
        Row(BuiltInChannels.GuildRecruitmentId, "GuildRecruitment - %s", 0x20032),
    ], fromClientData: false);

    /// <summary>The channels, in DBC file order (the order patterns are tried in).</summary>
    public IReadOnlyList<BuiltInChannel> Channels { get; }

    /// <summary>True when built from a client ChatChannels.dbc.</summary>
    public bool FromClientData { get; }

    /// <summary>A catalog from the rows of a client ChatChannels.dbc (file order kept).</summary>
    public static ChatChannelCatalog FromDbc(IEnumerable<ArcaneCore.Kernel.Social.ChatChannelRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var channels = new List<BuiltInChannel>();
        foreach (ArcaneCore.Kernel.Social.ChatChannelRow row in rows)
        {
            string[] patterns = [.. row.Patterns.Where(p => p.Length > 0).Select(StripZone).Where(p => p.Length > 0)];
            if (patterns.Length == 0)
            {
                continue; // vmangos skips a locale that is not loaded; a row without any pattern can never match
            }

            channels.Add(new BuiltInChannel(row.Id, patterns[0], FlagsOf(row.Flags)) { Patterns = patterns, DbcFlags = row.Flags });
        }

        return new ChatChannelCatalog(channels, fromClientData: true);
    }

    /// <summary>
    /// vmangos GetChannelEntryFor(name) (DBCStores.cpp:531-552): the first row, in file order, one of whose locale patterns
    /// (with "%s" removed) occurs in the name.
    /// </summary>
    public BuiltInChannel? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (BuiltInChannel channel in Channels)
        {
            foreach (string pattern in channel.Patterns)
            {
                if (name.Contains(pattern, StringComparison.Ordinal))
                {
                    return channel;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// vmangos Channel::Channel (Channel.cpp:33-55): every built-in is GENERAL; TRADE for the DBC trade flag; CITY for
    /// CITY_ONLY2; LFG for the DBC LFG flag, NOT_LFG otherwise.
    /// </summary>
    public static ChannelFlags FlagsOf(uint dbcFlags)
    {
        ChannelFlags flags = ChannelFlags.General;
        if ((dbcFlags & DbcTrade) != 0)
        {
            flags |= ChannelFlags.Trade;
        }

        if ((dbcFlags & DbcCityOnly2) != 0)
        {
            flags |= ChannelFlags.City;
        }

        flags |= (dbcFlags & DbcLfg) != 0 ? ChannelFlags.Lfg : ChannelFlags.NotLfg;
        return flags;
    }

    private static BuiltInChannel Row(uint id, string pattern, uint dbcFlags)
    {
        string stripped = StripZone(pattern);
        return new BuiltInChannel(id, stripped, FlagsOf(dbcFlags)) { DbcFlags = dbcFlags };
    }

    /// <summary>vmangos: "need to remove %s from entryName if it exists before we match" (the first occurrence).</summary>
    private static string StripZone(string pattern)
    {
        int at = pattern.IndexOf("%s", StringComparison.Ordinal);
        return at < 0 ? pattern : pattern.Remove(at, 2);
    }
}

/// <summary>The built-in channel ids and rules (vmangos Channel.h ChannelId, Channel::Join, Channel::Say).</summary>
public static class BuiltInChannels
{
    /// <summary>vmangos Channel.h ChannelId (ids of the 1.12.1 ChatChannels.dbc).</summary>
    public const uint GeneralId = 1;

    public const uint TradeId = 2;

    public const uint LocalDefenseId = 22;

    public const uint WorldDefenseId = 23;

    /// <summary>LookingForGroup is row 24 of the 1.12.1 ChatChannels.dbc (vmangos' 26 is the 2.x row).</summary>
    public const uint LookingForGroupId = 24;

    public const uint GuildRecruitmentId = 25;

    /// <summary>The honor rank needed to speak in WorldDefense (vmangos Channel::Say).</summary>
    public const byte WorldDefenseSpeakRank = 15;

    /// <summary>The transcribed rows (<see cref="ChatChannelCatalog.Builtin"/>).</summary>
    public static IReadOnlyList<BuiltInChannel> All => ChatChannelCatalog.Builtin.Channels;

    /// <summary>The flags of GuildRecruitment, which guilded players may not join (vmangos Channel::Join, <c>GetFlags() == 0x38</c>).</summary>
    public const ChannelFlags GuildRecruitmentFlags = ChannelFlags.General | ChannelFlags.City | ChannelFlags.NotLfg;

    /// <summary>vmangos GetChannelEntryFor(name) over the transcribed rows.</summary>
    public static BuiltInChannel? Find(string name) => ChatChannelCatalog.Builtin.Find(name);
}

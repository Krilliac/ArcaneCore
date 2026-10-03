using System.Text;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Guilds;

/// <summary>One roster line (vmangos GuildRosterMember).</summary>
public readonly record struct GuildRosterEntry(
    ObjectGuid Guid, GuildRosterFlags Flags, string Name, uint Rank, byte Level, byte Class, uint Zone, float DaysOffline,
    string PublicNote, string OfficerNote);

/// <summary>Guild packets (vmangos Server/Packets/Guild.cpp; gtker guild/*.wowm).</summary>
public static class GuildPackets
{
    /// <summary>vmangos GUILD_ROSTER_MAX_LENGTH: the largest packet the client accepts, minus the header.</summary>
    public const int RosterMaxLength = 0x8000 - 4;

    /// <summary>SMSG_GUILD_COMMAND_RESULT: u32 command, CString, u32 result (vmangos GuildCommandResult).</summary>
    public static byte[] BuildCommandResult(GuildCommand command, string text, GuildCommandError result)
    {
        var writer = new PacketWriter(9 + text.Length);
        writer.WriteUInt32((uint)command);
        writer.WriteCString(text);
        writer.WriteUInt32((uint)result);
        return writer.ToArray();
    }

    /// <summary>SMSG_GUILD_EVENT: u8 event, u8 string count, CStrings, then the u64 guid when set (vmangos GuildEvent::AppendBodyTo).</summary>
    public static byte[] BuildEvent(GuildEvent guildEvent, ObjectGuid guid, params string[] strings)
    {
        var writer = new PacketWriter(16 + strings.Sum(s => s.Length + 1));
        writer.WriteByte((byte)guildEvent);
        writer.WriteByte((byte)strings.Length);
        foreach (string s in strings)
        {
            writer.WriteCString(s);
        }

        if (!guid.IsEmpty)
        {
            writer.WriteUInt64(guid.Value);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_GUILD_INVITE: CString inviter, CString guild (vmangos GuildInviteNotification).</summary>
    public static byte[] BuildInvite(string inviter, string guild)
    {
        var writer = new PacketWriter(inviter.Length + guild.Length + 2);
        writer.WriteCString(inviter);
        writer.WriteCString(guild);
        return writer.ToArray();
    }

    /// <summary>SMSG_GUILD_DECLINE: CString name (vmangos GuildDeclineNotification).</summary>
    public static byte[] BuildDecline(string name)
    {
        var writer = new PacketWriter(name.Length + 1);
        writer.WriteCString(name);
        return writer.ToArray();
    }

    /// <summary>SMSG_GUILD_INFO: CString name, u32 day, month, year, members, accounts (vmangos GuildInfo::AppendBodyTo).</summary>
    public static byte[] BuildInfo(Guild guild, int accounts)
    {
        // vmangos Guild::Create: tm_mday, tm_mon + 1, tm_year + 1900 of the creation time.
        DateTime created = DateTimeOffset.FromUnixTimeSeconds(guild.CreatedAt).UtcDateTime;
        var writer = new PacketWriter(guild.Name.Length + 21);
        writer.WriteCString(guild.Name);
        writer.WriteUInt32((uint)created.Day);
        writer.WriteUInt32((uint)created.Month);
        writer.WriteUInt32((uint)created.Year);
        writer.WriteUInt32((uint)guild.MemberCount);
        writer.WriteUInt32((uint)accounts);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_GUILD_QUERY_RESPONSE: u32 id, CString name, always 10 CString rank names (missing
    /// ranks empty), i32 emblem style, emblem color, border style, border color, background
    /// (vmangos Guild::SendQueryResponse / GuildQueryResponse::AppendBodyTo).
    /// </summary>
    public static byte[] BuildQueryResponse(Guild guild)
    {
        var writer = new PacketWriter(64 + guild.Name.Length + guild.Ranks.Sum(r => r.Name.Length));
        writer.WriteUInt32((uint)guild.Id);
        writer.WriteCString(guild.Name);
        for (int i = 0; i < Guild.MaxRanks; i++)
        {
            writer.WriteCString(i < guild.Ranks.Count ? guild.Ranks[i].Name : string.Empty);
        }

        writer.WriteInt32(guild.EmblemStyle);
        writer.WriteInt32(guild.EmblemColor);
        writer.WriteInt32(guild.BorderStyle);
        writer.WriteInt32(guild.BorderColor);
        writer.WriteInt32(guild.BackgroundColor);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_GUILD_ROSTER: u32 member count, CString MOTD, CString info, u32 rank count, u32
    /// rights per rank; per member u64 guid, u8 flags, CString name, u32 rank, u8 level, u8
    /// class, u32 zone, f32 days offline (offline members only), CString public note, CString
    /// officer note. Members that would push the packet past the client limit are left out
    /// (vmangos Guild::SendGuildRoster / GuildRoster::AppendBodyTo).
    /// </summary>
    public static byte[] BuildRoster(Guild guild, IEnumerable<GuildRosterEntry> entries)
    {
        int spaceLeft = RosterMaxLength - (4 + Utf8(guild.Motd) + 1) - (Utf8(guild.Info) + 1) - (4 + (guild.Ranks.Count * 4));
        var included = new List<GuildRosterEntry>();
        foreach (GuildRosterEntry entry in entries)
        {
            int size = 8 + 1 + Utf8(entry.Name) + 1 + 4 + 1 + 1 + 4 + (entry.Flags == GuildRosterFlags.Offline ? 4 : 0)
                + Utf8(entry.PublicNote) + 1 + Utf8(entry.OfficerNote) + 1;
            if (spaceLeft < size)
            {
                break;
            }

            spaceLeft -= size;
            included.Add(entry);
        }

        var writer = new PacketWriter(256 + (included.Count * 48));
        writer.WriteUInt32((uint)included.Count);
        writer.WriteCString(guild.Motd);
        writer.WriteCString(guild.Info);
        writer.WriteUInt32((uint)guild.Ranks.Count);
        foreach (GuildRank rank in guild.Ranks)
        {
            writer.WriteUInt32(rank.Rights);
        }

        foreach (GuildRosterEntry e in included)
        {
            writer.WriteUInt64(e.Guid.Value);
            writer.WriteByte((byte)e.Flags);
            writer.WriteCString(e.Name);
            writer.WriteUInt32(e.Rank);
            writer.WriteByte(e.Level);
            writer.WriteByte(e.Class);
            writer.WriteUInt32(e.Zone);
            if (e.Flags == GuildRosterFlags.Offline)
            {
                writer.WriteSingle(e.DaysOffline);
            }

            writer.WriteCString(e.PublicNote);
            writer.WriteCString(e.OfficerNote);
        }

        return writer.ToArray();
    }

    private static int Utf8(string s) => Encoding.UTF8.GetByteCount(s);
}

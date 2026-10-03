using ArcaneCore.Kernel.Social;

namespace ArcaneCore.Game.Guilds;

/// <summary>A guild rank (vmangos RankInfo).</summary>
public sealed class GuildRank(string name, uint rights)
{
    public string Name { get; internal set; } = name;

    public uint Rights { get; internal set; } = rights;
}

/// <summary>A guild member (vmangos MemberSlot); level and zone are refreshed from the player while online.</summary>
public sealed class GuildMember(uint characterId, byte rank)
{
    public uint CharacterId { get; } = characterId;

    public ObjectGuid Guid => ObjectGuid.Player(CharacterId);

    public byte Rank { get; internal set; } = rank;

    public string PublicNote { get; internal set; } = string.Empty;

    public string OfficerNote { get; internal set; } = string.Empty;

    public byte Level { get; internal set; } = 1;

    public uint ZoneId { get; internal set; }

    /// <summary>Unix seconds of the last logout (vmangos MemberSlot::LogoutTime).</summary>
    public long LogoutTime { get; internal set; }
}

/// <summary>A guild (vmangos Guild): ranks, members, MOTD, info text and emblem. World thread.</summary>
public sealed class Guild
{
    /// <summary>vmangos GUILD_RANKS_MIN_COUNT / GUILD_RANKS_MAX_COUNT.</summary>
    public const int MinRanks = 5;

    public const int MaxRanks = 10;

    /// <summary>vmangos Guild.h length limits (client limits).</summary>
    public const int MaxNameLength = 24;

    public const int MaxRankNameLength = 15;

    public const int MaxNoteLength = 31;

    public const int MaxInfoLength = 500;

    public const int MaxMotdLength = 128;

    /// <summary>vmangos GR_GUILDMASTER / GR_OFFICER.</summary>
    public const byte GuildMasterRank = 0;

    public const byte OfficerRank = 1;

    /// <summary>vmangos Guild::Create: the MOTD of a new guild.</summary>
    public const string DefaultMotd = "No message set.";

    private readonly List<GuildRank> _ranks = [];
    private readonly Dictionary<uint, GuildMember> _members = [];

    internal Guild(int id, string name, uint leaderId, long createdAt)
    {
        Id = id;
        Name = name;
        LeaderId = leaderId;
        CreatedAt = createdAt;
    }

    public int Id { get; }

    public string Name { get; }

    public uint LeaderId { get; internal set; }

    public ObjectGuid LeaderGuid => ObjectGuid.Player(LeaderId);

    public string Motd { get; internal set; } = DefaultMotd;

    public string Info { get; internal set; } = string.Empty;

    /// <summary>Unix seconds of creation (SMSG_GUILD_INFO reports the day, month and year).</summary>
    public long CreatedAt { get; }

    public int EmblemStyle { get; internal set; } = -1;

    public int EmblemColor { get; internal set; } = -1;

    public int BorderStyle { get; internal set; } = -1;

    public int BorderColor { get; internal set; } = -1;

    public int BackgroundColor { get; internal set; } = -1;

    public IReadOnlyList<GuildRank> Ranks => _ranks;

    public IReadOnlyCollection<GuildMember> Members => _members.Values;

    public int MemberCount => _members.Count;

    public byte LowestRank => (byte)(_ranks.Count - 1);

    public GuildMember? Find(uint characterId) => _members.GetValueOrDefault(characterId);

    public uint RankRights(byte rank) => rank < _ranks.Count ? _ranks[rank].Rights : GuildRights.Empty;

    public string RankName(byte rank) => rank < _ranks.Count ? _ranks[rank].Name : string.Empty;

    public bool HasRight(byte rank, uint right) => GuildRights.Has(RankRights(rank), right);

    /// <summary>vmangos Guild::CreateDefaultGuildRanks (LANG_GUILD_MASTER … LANG_GUILD_INITIATE).</summary>
    internal void CreateDefaultRanks()
    {
        _ranks.Clear();
        _ranks.Add(new GuildRank("Guild Master", GuildRights.All));
        _ranks.Add(new GuildRank("Officer", GuildRights.All));
        _ranks.Add(new GuildRank("Veteran", GuildRights.GuildChatListen | GuildRights.GuildChatSpeak));
        _ranks.Add(new GuildRank("Member", GuildRights.GuildChatListen | GuildRights.GuildChatSpeak));
        _ranks.Add(new GuildRank("Initiate", GuildRights.GuildChatListen | GuildRights.GuildChatSpeak));
    }

    internal void AddRank(string name, uint rights) => _ranks.Add(new GuildRank(name, rights));

    internal void RemoveLowestRank() => _ranks.RemoveAt(_ranks.Count - 1);

    internal GuildMember AddMember(uint characterId, byte rank)
    {
        var member = new GuildMember(characterId, rank);
        _members[characterId] = member;
        return member;
    }

    internal bool RemoveMember(uint characterId) => _members.Remove(characterId);

    internal void ClearMembers() => _members.Clear();

    /// <summary>A copy for persistence.</summary>
    public GuildData ToData() => new(
        Id, Name, (int)LeaderId, Motd, Info, CreatedAt, EmblemStyle, EmblemColor, BorderStyle, BorderColor, BackgroundColor,
        [.. _ranks.Select((r, i) => new GuildRankData((byte)i, r.Name, r.Rights))],
        [.. _members.Values.Select(m => new GuildMemberData((int)m.CharacterId, m.Rank, m.PublicNote, m.OfficerNote, m.Level, m.ZoneId, m.LogoutTime))]);

    /// <summary>Rebuild a guild from storage.</summary>
    internal static Guild FromData(GuildData data)
    {
        var guild = new Guild(data.Id, data.Name, (uint)data.LeaderId, data.CreatedAt)
        {
            Motd = data.Motd,
            Info = data.Info,
            EmblemStyle = data.EmblemStyle,
            EmblemColor = data.EmblemColor,
            BorderStyle = data.BorderStyle,
            BorderColor = data.BorderColor,
            BackgroundColor = data.BackgroundColor,
        };
        foreach (GuildRankData rank in data.Ranks.OrderBy(r => r.RankId))
        {
            guild._ranks.Add(new GuildRank(rank.Name, rank.Rights));
        }

        foreach (GuildMemberData m in data.Members)
        {
            GuildMember member = guild.AddMember((uint)m.CharacterId, m.Rank);
            member.PublicNote = m.PublicNote;
            member.OfficerNote = m.OfficerNote;
            member.Level = m.Level;
            member.ZoneId = m.ZoneId;
            member.LogoutTime = m.LogoutTime;
        }

        return guild;
    }
}

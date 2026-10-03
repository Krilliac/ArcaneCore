namespace ArcaneCore.Kernel.Social;

/// <summary>Social-list flags of one entry (vmangos SocialMgr.h SocialFlag).</summary>
[Flags]
public enum SocialFlags : byte
{
    None = 0x00,
    Friend = 0x01,
    Ignored = 0x02,
}

/// <summary>One row of a character's friend/ignore list (vmangos character_social: guid, friend, flags).</summary>
public readonly record struct SocialEntry(int OtherId, SocialFlags Flags);

/// <summary>One guild rank (vmangos guild_rank: rid, rname, rights).</summary>
public sealed record GuildRankData(byte RankId, string Name, uint Rights);

/// <summary>
/// One guild member (vmangos guild_member: guid, rank, pnote, offnote). Level, zone and logout
/// time are cached here for offline members' roster lines (vmangos reads them from the
/// characters table, which this module does not own).
/// </summary>
public sealed record GuildMemberData(
    int CharacterId, byte Rank, string PublicNote, string OfficerNote, byte Level, uint ZoneId, long LogoutTime);

/// <summary>A guild with its ranks and members (vmangos guild + guild_rank + guild_member).</summary>
public sealed record GuildData(
    int Id,
    string Name,
    int LeaderId,
    string Motd,
    string Info,
    long CreatedAt,
    int EmblemStyle,
    int EmblemColor,
    int BorderStyle,
    int BorderColor,
    int BackgroundColor,
    IReadOnlyList<GuildRankData> Ranks,
    IReadOnlyList<GuildMemberData> Members);

/// <summary>Friend/ignore lists and guilds in the characters database.</summary>
public interface ISocialStore
{
    /// <summary>A character's friend and ignore entries.</summary>
    Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Set the flags of one entry; <see cref="SocialFlags.None"/> deletes it.</summary>
    Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default);

    /// <summary>Every guild with ranks and members (startup).</summary>
    Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert or replace a guild with exactly these ranks and members.</summary>
    Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default);

    /// <summary>Delete a guild, its ranks and its members.</summary>
    Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A deleted character's leftovers: friend/ignore entries it owns or that point at it, and its
    /// guild membership. Queued after earlier social writes so none of them can bring a row back.
    /// Applies only while the id has no character row, so a character recreated with the same id keeps its own.
    /// </summary>
    Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

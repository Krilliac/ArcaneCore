using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Social;

/// <summary>EF Core implementation of <see cref="ISocialStore"/> over the characters database.</summary>
public sealed class EfSocialStore(CharacterDbContext db) : ISocialStore
{
    public async Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
    {
        List<CharacterSocialRow> rows = await db.Set<CharacterSocialRow>()
            .AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.OtherId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new SocialEntry(r.OtherId, (SocialFlags)r.Flags))];
    }

    public async Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
    {
        DbSet<CharacterSocialRow> set = db.Set<CharacterSocialRow>();
        CharacterSocialRow? row = await set
            .FirstOrDefaultAsync(r => r.CharacterId == characterId && r.OtherId == otherId, cancellationToken)
            .ConfigureAwait(false);
        if (flags == SocialFlags.None)
        {
            if (row is not null)
            {
                set.Remove(row);
            }
        }
        else if (row is null)
        {
            set.Add(new CharacterSocialRow { CharacterId = characterId, OtherId = otherId, Flags = (byte)flags });
        }
        else
        {
            row.Flags = (byte)flags;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
    {
        List<GuildRow> guilds = await db.Set<GuildRow>().AsNoTracking().OrderBy(g => g.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GuildRankRow> ranks = await db.Set<GuildRankRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GuildMemberRow> members = await db.Set<GuildMemberRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        ILookup<int, GuildRankRow> ranksByGuild = ranks.ToLookup(r => r.GuildId);
        ILookup<int, GuildMemberRow> membersByGuild = members.ToLookup(m => m.GuildId);
        return
        [
            .. guilds.Select(g => new GuildData(
                g.Id, g.Name, g.LeaderId, g.Motd, g.Info, g.CreatedAt,
                g.EmblemStyle, g.EmblemColor, g.BorderStyle, g.BorderColor, g.BackgroundColor,
                [.. ranksByGuild[g.Id].OrderBy(r => r.RankId).Select(r => new GuildRankData(r.RankId, r.Name, r.Rights))],
                [.. membersByGuild[g.Id].OrderBy(m => m.CharacterId).Select(m => new GuildMemberData(
                    m.CharacterId, m.Rank, m.PublicNote, m.OfficerNote, m.Level, m.ZoneId, m.LogoutTime))])),
        ];
    }

    /// <summary>Replace the stored guild with <paramref name="guild"/> in one SaveChanges (one transaction).</summary>
    public async Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default)
    {
        GuildRow? row = await db.Set<GuildRow>().FirstOrDefaultAsync(g => g.Id == guild.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new GuildRow { Id = guild.Id };
            db.Set<GuildRow>().Add(row);
        }

        row.Name = guild.Name;
        row.LeaderId = guild.LeaderId;
        row.Motd = guild.Motd;
        row.Info = guild.Info;
        row.CreatedAt = guild.CreatedAt;
        row.EmblemStyle = guild.EmblemStyle;
        row.EmblemColor = guild.EmblemColor;
        row.BorderStyle = guild.BorderStyle;
        row.BorderColor = guild.BorderColor;
        row.BackgroundColor = guild.BackgroundColor;

        Dictionary<byte, GuildRankRow> ranks = await db.Set<GuildRankRow>()
            .Where(r => r.GuildId == guild.Id)
            .ToDictionaryAsync(r => r.RankId, cancellationToken)
            .ConfigureAwait(false);
        foreach (GuildRankData rank in guild.Ranks)
        {
            if (!ranks.Remove(rank.RankId, out GuildRankRow? rankRow))
            {
                rankRow = new GuildRankRow { GuildId = guild.Id, RankId = rank.RankId };
                db.Set<GuildRankRow>().Add(rankRow);
            }

            rankRow.Name = rank.Name;
            rankRow.Rights = rank.Rights;
        }

        db.Set<GuildRankRow>().RemoveRange(ranks.Values);

        Dictionary<int, GuildMemberRow> members = await db.Set<GuildMemberRow>()
            .Where(m => m.GuildId == guild.Id)
            .ToDictionaryAsync(m => m.CharacterId, cancellationToken)
            .ConfigureAwait(false);
        db.Set<GuildMemberRow>().RemoveRange(members.Values.Where(m => guild.Members.All(n => n.CharacterId != m.CharacterId)));

        foreach (GuildMemberData member in guild.Members)
        {
            if (!members.TryGetValue(member.CharacterId, out GuildMemberRow? memberRow))
            {
                memberRow = new GuildMemberRow { GuildId = guild.Id, CharacterId = member.CharacterId };
                db.Set<GuildMemberRow>().Add(memberRow);
            }

            memberRow.Rank = member.Rank;
            memberRow.PublicNote = member.PublicNote;
            memberRow.OfficerNote = member.OfficerNote;
            memberRow.Level = member.Level;
            memberRow.ZoneId = member.ZoneId;
            memberRow.LogoutTime = member.LogoutTime;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default)
    {
        db.Set<GuildMemberRow>().RemoveRange(await db.Set<GuildMemberRow>().Where(m => m.GuildId == guildId).ToListAsync(cancellationToken).ConfigureAwait(false));
        db.Set<GuildRankRow>().RemoveRange(await db.Set<GuildRankRow>().Where(r => r.GuildId == guildId).ToListAsync(cancellationToken).ConfigureAwait(false));
        if (await db.Set<GuildRow>().FirstOrDefaultAsync(g => g.Id == guildId, cancellationToken).ConfigureAwait(false) is { } row)
        {
            db.Set<GuildRow>().Remove(row);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        => SocialDataModule.DeleteReferencesAsync(db, characterId, cancellationToken);
}

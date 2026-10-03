using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Social;

/// <summary>One friend/ignore entry (vmangos character_social: guid, friend, flags).</summary>
public sealed class CharacterSocialRow
{
    public int CharacterId { get; set; }
    public int OtherId { get; set; }
    public byte Flags { get; set; }
}

/// <summary>A guild (vmangos guild).</summary>
public sealed class GuildRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int LeaderId { get; set; }
    public string Motd { get; set; } = string.Empty;
    public string Info { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
    public int EmblemStyle { get; set; }
    public int EmblemColor { get; set; }
    public int BorderStyle { get; set; }
    public int BorderColor { get; set; }
    public int BackgroundColor { get; set; }
}

/// <summary>A guild rank (vmangos guild_rank).</summary>
public sealed class GuildRankRow
{
    public int GuildId { get; set; }
    public byte RankId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint Rights { get; set; }
}

/// <summary>A guild member (vmangos guild_member) with the cached roster fields of offline members.</summary>
public sealed class GuildMemberRow
{
    public int GuildId { get; set; }
    public int CharacterId { get; set; }
    public byte Rank { get; set; }
    public string PublicNote { get; set; } = string.Empty;
    public string OfficerNote { get; set; } = string.Empty;
    public byte Level { get; set; }
    public uint ZoneId { get; set; }
    public long LogoutTime { get; set; }
}

/// <summary>
/// Social tables of the characters database: friend/ignore lists and guilds. Characters schema
/// v6 (allocated in docs/integration/social.md). The tables are new; nothing existing changes.
/// </summary>
public sealed class SocialDataModule : IDataModule, ICharacterDataCleanup
{
    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => 6;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("character_social"),
        new CreateTableChange("guild"),
        new CreateTableChange("guild_rank"),
        new CreateTableChange("guild_member"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterSocialRow>(entity =>
        {
            entity.ToTable("character_social");
            entity.HasKey(r => new { r.CharacterId, r.OtherId });
        });

        modelBuilder.Entity<GuildRow>(entity =>
        {
            entity.ToTable("guild");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(24).IsRequired();
            entity.Property(r => r.Motd).HasMaxLength(128).IsRequired();
            entity.Property(r => r.Info).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<GuildRankRow>(entity =>
        {
            entity.ToTable("guild_rank");
            entity.HasKey(r => new { r.GuildId, r.RankId });
            entity.Property(r => r.Name).HasMaxLength(15).IsRequired();
        });

        modelBuilder.Entity<GuildMemberRow>(entity =>
        {
            entity.ToTable("guild_member");
            entity.HasKey(r => new { r.GuildId, r.CharacterId });
            entity.HasIndex(r => r.CharacterId).IsUnique();
            entity.Property(r => r.PublicNote).HasMaxLength(31).IsRequired();
            entity.Property(r => r.OfficerNote).HasMaxLength(31).IsRequired();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ISocialStore, EfSocialStore>();

    /// <summary>
    /// A guild leader cannot be deleted (vmangos HandleCharDeleteOpcode: GetGuildByLeader →
    /// CHAR_DELETE_FAILED); otherwise the character leaves its guild and every friend/ignore
    /// entry it owns or that points at it goes (vmangos DeleteFromDB: character_social guid/friend,
    /// guild_member).
    /// </summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.Set<GuildRow>().AnyAsync(g => g.LeaderId == characterId, cancellationToken).ConfigureAwait(false))
        {
            throw new CharacterDeletionRefusedException($"character {characterId} leads a guild");
        }

        await DeleteReferencesAsync(db, characterId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Friend/ignore entries owned by or pointing at the character, and its guild membership.</summary>
    internal static async Task DeleteReferencesAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        await db.Set<CharacterSocialRow>().Where(r => r.CharacterId == characterId || r.OtherId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<GuildMemberRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same removal for a character id that has no <c>characters</c> row, in one conditional
    /// statement per table: the queued purge after a deletion must not wipe the friends or guild
    /// membership of a character recreated with the same id (docs/integration/character-delete.md).
    /// </summary>
    internal static async Task DeleteReferencesOfDeletedCharacterAsync(
        CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        await db.Set<CharacterSocialRow>()
            .Where(r => (r.CharacterId == characterId || r.OtherId == characterId) && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<GuildMemberRow>()
            .Where(r => r.CharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

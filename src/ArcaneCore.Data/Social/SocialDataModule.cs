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
public sealed class SocialDataModule : IDataModule
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
}

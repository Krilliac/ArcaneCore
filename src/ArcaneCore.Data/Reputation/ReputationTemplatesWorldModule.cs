using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Reputation;

/// <summary>
/// <c>reputation_spillover_template</c> (vmangos ObjectMgr::LoadReputationSpilloverTemplate, ObjectMgr.cpp:8971-9070;
/// cmangos classic-db column names). Up to four spillover factions, each with the share of the main gain it receives
/// (<c>rate_N</c>) and the highest rank the player may have with it (<c>rank_N</c>); a faction of 0 is an unused slot.
/// </summary>
public sealed class ReputationSpilloverTemplateRow
{
    public uint Faction { get; set; }

    public uint Faction1 { get; set; }

    public float Rate1 { get; set; }

    public byte Rank1 { get; set; }

    public uint Faction2 { get; set; }

    public float Rate2 { get; set; }

    public byte Rank2 { get; set; }

    public uint Faction3 { get; set; }

    public float Rate3 { get; set; }

    public byte Rank3 { get; set; }

    public uint Faction4 { get; set; }

    public float Rate4 { get; set; }

    public byte Rank4 { get; set; }
}

/// <summary><c>reputation_reward_rate</c> (vmangos ObjectMgr::LoadReputationRewardRate, ObjectMgr.cpp:8827-8892): per-faction multipliers per source.</summary>
public sealed class ReputationRewardRateRow
{
    public uint Faction { get; set; }

    public float QuestRate { get; set; } = 1f;

    public float CreatureRate { get; set; } = 1f;

    public float SpellRate { get; set; } = 1f;
}

/// <summary>
/// World schema step for the two reputation template tables, each exactly one <see cref="CreateTableChange"/> and no inline
/// data step: MariaDB DDL implicitly commits and is not transactional, so an inline step could leave a half-applied
/// version behind (PostgreSQL DDL is transactional, SQLite too). Allocated as the next free world version (21 at the
/// wave-4 base; the integrator renumbers this one constant, tests refer to <see cref="Version"/>).
/// Registers <see cref="EfReputationContentSource"/>. No cleanup registration: the world schema holds no per-character rows.
/// </summary>
public sealed class ReputationTemplatesWorldModule : IDataModule
{
    public const int Version = 21;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("reputation_spillover_template"),
        new CreateTableChange("reputation_reward_rate"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReputationSpilloverTemplateRow>(entity =>
        {
            entity.ToTable("reputation_spillover_template");
            entity.HasKey(r => r.Faction);
            entity.Property(r => r.Faction).ValueGeneratedNever();
        });
        modelBuilder.Entity<ReputationRewardRateRow>(entity =>
        {
            entity.ToTable("reputation_reward_rate");
            entity.HasKey(r => r.Faction);
            entity.Property(r => r.Faction).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IReputationContentSource, EfReputationContentSource>();
}

/// <summary>Reads both reputation template tables from the world database (at startup and on reload).</summary>
public sealed class EfReputationContentSource(WorldDbContext db) : IReputationContentSource
{
    public async Task<ReputationContentRows> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<ReputationSpilloverTemplateRow> spillovers = await db.Set<ReputationSpilloverTemplateRow>().AsNoTracking()
            .OrderBy(r => r.Faction).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ReputationRewardRateRow> rates = await db.Set<ReputationRewardRateRow>().AsNoTracking()
            .OrderBy(r => r.Faction).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ReputationContentRows(
            [.. spillovers.Select(r => new ReputationSpilloverTemplate(r.Faction,
            [
                new ReputationSpillover(r.Faction1, r.Rate1, r.Rank1),
                new ReputationSpillover(r.Faction2, r.Rate2, r.Rank2),
                new ReputationSpillover(r.Faction3, r.Rate3, r.Rank3),
                new ReputationSpillover(r.Faction4, r.Rate4, r.Rank4),
            ]))],
            [.. rates.Select(r => new ReputationRewardRate(r.Faction, r.QuestRate, r.CreatureRate, r.SpellRate))]);
    }
}

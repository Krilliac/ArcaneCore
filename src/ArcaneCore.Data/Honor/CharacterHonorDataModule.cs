using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Honor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Honor;

/// <summary>Per-character honor state (the vmangos characters.honor_* columns, kept in this module's own table).</summary>
public sealed class CharacterHonorRow
{
    public int CharacterId { get; set; }
    public float RankPoints { get; set; }
    public byte HighestRank { get; set; }
    public uint Standing { get; set; }
    public uint LastWeekHk { get; set; }
    public float LastWeekCp { get; set; }
    public int StoredHk { get; set; }
    public int StoredDk { get; set; }
    public byte PvpFlags { get; set; }
    public bool CityProtector { get; set; }
}

/// <summary>One contribution point row (vmangos character_honor_cp).</summary>
public sealed class HonorCpRow
{
    public long Id { get; set; }
    public int CharacterId { get; set; }
    public byte VictimType { get; set; }
    public uint VictimId { get; set; }
    public float Cp { get; set; }
    public uint Date { get; set; }
    public byte Type { get; set; }
}

/// <summary>The single weekly maintenance bookkeeping row (vmangos saved_variables honor_* columns).</summary>
public sealed class HonorMaintenanceRow
{
    /// <summary>The only row's key.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; }
    public uint LastDay { get; set; }
    public uint NextDay { get; set; }
    public bool Marker { get; set; }
}

/// <summary>
/// Characters schema (honor, docs/areas/honor.md): <c>character_honor</c>, <c>character_honor_cp</c> and
/// <c>honor_maintenance</c>. All three tables are new; nothing existing changes. The step is additive and
/// re-runnable: MariaDB DDL commits implicitly, so a half-applied upgrade is a real state and the bootstrapper
/// adopts a table that already has exactly the model's columns.
/// </summary>
public sealed class CharacterHonorDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>
    /// The characters schema version of this module: the next free number in this tree. The one constant the
    /// integrator renumbers (docs/integration/seams.md "Schema versions"); tests refer to it.
    /// </summary>
    public const int Version = 23; // the lane allocated 21; renumbered at wave-4 integration (bank 21, taxi flight 22)

    public const string StateTable = "character_honor";
    public const string CpTable = "character_honor_cp";
    public const string MaintenanceTable = "honor_maintenance";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(StateTable),
        new CreateTableChange(CpTable),
        new CreateTableChange(MaintenanceTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterHonorRow>(entity =>
        {
            entity.ToTable(StateTable);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).HasColumnName("character_id").ValueGeneratedNever();
            entity.Property(r => r.RankPoints).HasColumnName("rank_points");
            entity.Property(r => r.HighestRank).HasColumnName("highest_rank");
            entity.Property(r => r.Standing).HasColumnName("standing");
            entity.Property(r => r.LastWeekHk).HasColumnName("last_week_hk");
            entity.Property(r => r.LastWeekCp).HasColumnName("last_week_cp");
            entity.Property(r => r.StoredHk).HasColumnName("stored_hk");
            entity.Property(r => r.StoredDk).HasColumnName("stored_dk");
            entity.Property(r => r.PvpFlags).HasColumnName("pvp_flags");
            entity.Property(r => r.CityProtector).HasColumnName("city_protector");
        });

        modelBuilder.Entity<HonorCpRow>(entity =>
        {
            entity.ToTable(CpTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(r => r.CharacterId).HasColumnName("character_id");
            entity.Property(r => r.VictimType).HasColumnName("victim_type");
            entity.Property(r => r.VictimId).HasColumnName("victim_id");
            entity.Property(r => r.Cp).HasColumnName("cp");
            entity.Property(r => r.Date).HasColumnName("date");
            entity.Property(r => r.Type).HasColumnName("type");
            entity.HasIndex(r => new { r.CharacterId, r.Date });
            entity.HasIndex(r => r.Date);
        });

        modelBuilder.Entity<HonorMaintenanceRow>(entity =>
        {
            entity.ToTable(MaintenanceTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.LastDay).HasColumnName("last_day");
            entity.Property(r => r.NextDay).HasColumnName("next_day");
            entity.Property(r => r.Marker).HasColumnName("marker");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IHonorStore, EfHonorStore>();

    /// <summary>vmangos Player::DeleteFromDB: character_honor_cp (and the honor columns, which die with the characters row).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<HonorCpRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterHonorRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

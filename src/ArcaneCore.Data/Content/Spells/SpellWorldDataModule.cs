using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Spells;

/// <summary>
/// The spell tables of the world database (world schema version 2; see
/// docs/integration/spells.md for the version claim). Table and column names follow
/// cmangos-classic so the M8 content importer maps classic-db rows by name; the four
/// DBC-derived tables are filled by <c>tools/spell-import</c>. Nothing is seeded: spell data is
/// game content and never committed (docs/areas/spells.md).
/// </summary>
public sealed class SpellWorldDataModule : IDataModule
{
    public const string SpellTemplateTable = "spell_template";
    public const string CastTimesTable = "spell_cast_times";
    public const string DurationTable = "spell_duration";
    public const string RangeTable = "spell_range";
    public const string RadiusTable = "spell_radius";
    public const string CreateSpellTable = "playercreateinfo_spell";
    public const string TargetPositionTable = "spell_target_position";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => 2;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(SpellTemplateTable),
        new CreateTableChange(CastTimesTable),
        new CreateTableChange(DurationTable),
        new CreateTableChange(RangeTable),
        new CreateTableChange(RadiusTable),
        new CreateTableChange(CreateSpellTable),
        new CreateTableChange(TargetPositionTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SpellTemplateRow>(entity =>
        {
            entity.ToTable(SpellTemplateTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<SpellCastTimeRow>(entity =>
        {
            entity.ToTable(CastTimesTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<SpellDurationRow>(entity =>
        {
            entity.ToTable(DurationTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<SpellRangeRow>(entity =>
        {
            entity.ToTable(RangeTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<SpellRadiusRow>(entity =>
        {
            entity.ToTable(RadiusTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });

        // cmangos-classic: playercreateinfo_spell (race, class, Spell, Note), PK (race, class, Spell).
        modelBuilder.Entity<PlayerCreateSpellRow>(entity =>
        {
            entity.ToTable(CreateSpellTable);
            entity.HasKey(r => new { r.Race, r.Class, r.Spell });
            entity.Property(r => r.Race).HasColumnName("race");
            entity.Property(r => r.Class).HasColumnName("class");
            entity.Property(r => r.Spell).HasColumnName("Spell");
            entity.Property(r => r.Note).HasColumnName("Note").HasMaxLength(255);
        });

        // cmangos-classic: spell_target_position (id, target_map, target_position_x/y/z, target_orientation).
        modelBuilder.Entity<SpellTargetPositionRow>(entity =>
        {
            entity.ToTable(TargetPositionTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.TargetMap).HasColumnName("target_map");
            entity.Property(r => r.TargetPositionX).HasColumnName("target_position_x");
            entity.Property(r => r.TargetPositionY).HasColumnName("target_position_y");
            entity.Property(r => r.TargetPositionZ).HasColumnName("target_position_z");
            entity.Property(r => r.TargetOrientation).HasColumnName("target_orientation");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ISpellContentStore, EfSpellContentStore>();
}

/// <summary>EF Core implementation of <see cref="ISpellContentStore"/>.</summary>
public sealed class EfSpellContentStore(WorldDbContext db) : ISpellContentStore
{
    /// <summary>Rows per SaveChanges while importing (keeps the change tracker small).</summary>
    private const int ImportBatchSize = 1000;

    public async Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => new(
        await db.Set<SpellTemplateRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
        await db.Set<SpellCastTimeRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
        await db.Set<SpellDurationRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
        await db.Set<SpellRangeRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
        await db.Set<SpellRadiusRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
        await db.Set<PlayerCreateSpellRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
        await db.Set<SpellTargetPositionRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));

    public async Task ReplaceDbcTablesAsync(SpellDbcContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        bool tracking = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ReplaceAsync(content.Spells, cancellationToken).ConfigureAwait(false);
            await ReplaceAsync(content.CastTimes, cancellationToken).ConfigureAwait(false);
            await ReplaceAsync(content.Durations, cancellationToken).ConfigureAwait(false);
            await ReplaceAsync(content.Ranges, cancellationToken).ConfigureAwait(false);
            await ReplaceAsync(content.Radii, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = tracking;
        }
    }

    private async Task ReplaceAsync<TRow>(IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
        where TRow : class
    {
        DbSet<TRow> set = db.Set<TRow>();
        await set.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        for (int start = 0; start < rows.Count; start += ImportBatchSize)
        {
            set.AddRange(rows.Skip(start).Take(ImportBatchSize));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }
}

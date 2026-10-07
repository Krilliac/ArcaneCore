using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Procs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Procs;

/// <summary>
/// <c>spell_proc_event</c> row. The columns are vmangos' and cmangos-classic's (<c>entry, SchoolMask, SpellFamilyName, SpellFamilyMask0..2, procFlags,
/// procEx, ppmRate, CustomChance, Cooldown</c>; vmangos adds <c>build_min, build_max</c>, Spells/SpellMgr.cpp:321). The family masks are 64-bit
/// (vmangos <c>uint64 spellFamilyMask[3]</c>) and stored as their two's complement in a signed 64-bit column. <see cref="Cooldown"/> is milliseconds.
/// </summary>
public sealed class SpellProcEventRow
{
    public uint Entry { get; set; }

    public uint SchoolMask { get; set; }

    public uint SpellFamilyName { get; set; }

    public long SpellFamilyMask0 { get; set; }

    public long SpellFamilyMask1 { get; set; }

    public long SpellFamilyMask2 { get; set; }

    public uint ProcFlags { get; set; }

    public uint ProcEx { get; set; }

    public float PpmRate { get; set; }

    public float CustomChance { get; set; }

    public uint Cooldown { get; set; }

    public uint BuildMin { get; set; }

    public uint BuildMax { get; set; } = 9999;
}

/// <summary>
/// The proc condition table of the world database (docs/areas/procs.md): <c>spell_proc_event</c>. The data is imported from the operator's vmangos
/// or classic-db content (<see cref="SpellProcEventDumpImporter"/>); nothing is bundled. Without rows every proc aura runs on its Spell.dbc procFlags
/// and procChance alone (vmangos with an empty table). <see cref="Version"/> is the world number reserved for the proc-engine lane.
/// </summary>
public sealed class SpellProcEventDataModule : IDataModule
{
    /// <summary>The world schema version of <c>spell_proc_event</c> (reserved for the wave-2 proc-engine lane: world 41-42).</summary>
    public const int Version = 41;

    public const string Table = "spell_proc_event";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpellProcEventRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).HasColumnName("entry").ValueGeneratedNever();
            entity.Property(r => r.SchoolMask).HasColumnName("SchoolMask");
            entity.Property(r => r.SpellFamilyName).HasColumnName("SpellFamilyName");
            entity.Property(r => r.SpellFamilyMask0).HasColumnName("SpellFamilyMask0");
            entity.Property(r => r.SpellFamilyMask1).HasColumnName("SpellFamilyMask1");
            entity.Property(r => r.SpellFamilyMask2).HasColumnName("SpellFamilyMask2");
            entity.Property(r => r.ProcFlags).HasColumnName("procFlags");
            entity.Property(r => r.ProcEx).HasColumnName("procEx");
            entity.Property(r => r.PpmRate).HasColumnName("ppmRate");
            entity.Property(r => r.CustomChance).HasColumnName("CustomChance");
            entity.Property(r => r.Cooldown).HasColumnName("Cooldown");
            entity.Property(r => r.BuildMin).HasColumnName("build_min");
            entity.Property(r => r.BuildMax).HasColumnName("build_max");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ISpellProcEventDataStore, EfSpellProcEventStore>();

    internal static SpellProcEventRecord ToRecord(SpellProcEventRow r) => new(
        r.Entry, r.SchoolMask, r.SpellFamilyName,
        unchecked((ulong)r.SpellFamilyMask0), unchecked((ulong)r.SpellFamilyMask1), unchecked((ulong)r.SpellFamilyMask2),
        r.ProcFlags, r.ProcEx, r.PpmRate, r.CustomChance, r.Cooldown);

    internal static SpellProcEventRow ToRow(SpellProcEventRecord r) => new()
    {
        Entry = r.Entry,
        SchoolMask = r.SchoolMask,
        SpellFamilyName = r.SpellFamilyName,
        SpellFamilyMask0 = unchecked((long)r.SpellFamilyMask0),
        SpellFamilyMask1 = unchecked((long)r.SpellFamilyMask1),
        SpellFamilyMask2 = unchecked((long)r.SpellFamilyMask2),
        ProcFlags = r.ProcFlags,
        ProcEx = r.ProcEx,
        PpmRate = r.PpmRate,
        CustomChance = r.CustomChance,
        Cooldown = r.Cooldown,
        BuildMin = 0,
        BuildMax = 9999,
    };
}

/// <summary>EF Core implementation of <see cref="ISpellProcEventDataStore"/>.</summary>
public sealed class EfSpellProcEventStore(WorldDbContext db) : ISpellProcEventDataStore
{
    public async Task<SpellProcEventContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<SpellProcEventRow> rows = await db.Set<SpellProcEventRow>().AsNoTracking().OrderBy(r => r.Entry).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new SpellProcEventContent(rows.Select(SpellProcEventDataModule.ToRecord));
    }
}

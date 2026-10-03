using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Threat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Threat;

/// <summary>
/// <c>spell_threat</c> row. The columns are vmangos': <c>entry, threat, multiplier, inverse_effect_mask</c> plus the build range
/// <c>build_min, build_max</c> its loader filters on (<c>WHERE 5875 BETWEEN build_min AND build_max</c>, Spells/SpellMgr.cpp:839);
/// the importer keeps only rows whose range holds the supported build, so a stored row is always one for build 5875.
/// <see cref="ApBonus"/> is cmangos' attack-power threat bonus; it must be zero (no reader applies it).
/// </summary>
public sealed class SpellThreatRow
{
    public uint Entry { get; set; }

    public int Threat { get; set; }

    public float Multiplier { get; set; } = 1f;

    public float ApBonus { get; set; }

    public byte InverseEffectMask { get; set; }

    public uint BuildMin { get; set; }

    public uint BuildMax { get; set; } = 9999;
}

/// <summary>
/// The threat table of the world database (docs/areas/threat.md): <c>spell_threat</c>. The data is imported from the operator's vmangos
/// or classic-db content (<see cref="SpellThreatDumpImporter"/>); nothing is bundled. <see cref="Version"/> is a single constant
/// the integrator renumbers.
/// </summary>
public sealed class SpellThreatDataModule : IDataModule
{
    /// <summary>The world schema version of the <c>spell_threat</c> table (the next free number on the wave-4 lane base; the integrator renumbers).</summary>
    public const int Version = 21;

    public const string Table = "spell_threat";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpellThreatRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ISpellThreatDataStore, EfSpellThreatStore>();
}

/// <summary>EF Core implementation of <see cref="ISpellThreatDataStore"/>.</summary>
public sealed class EfSpellThreatStore(WorldDbContext db) : ISpellThreatDataStore
{
    public async Task<SpellThreatContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<SpellThreatRow> rows = await db.Set<SpellThreatRow>().AsNoTracking().OrderBy(r => r.Entry).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new SpellThreatContent(rows.Select(r => new SpellThreatRecord(
            r.Entry,
            r.Threat is >= 0 and <= ushort.MaxValue ? (ushort)r.Threat : throw new InvalidDataException($"spell_threat spell {r.Entry}: threat {r.Threat} is outside 0..65535"),
            r.Multiplier,
            r.InverseEffectMask)));
    }
}

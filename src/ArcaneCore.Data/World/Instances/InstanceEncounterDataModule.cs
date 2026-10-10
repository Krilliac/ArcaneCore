using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Instances;

/// <summary>Read access to the world database's <c>instance_encounter</c> table (docs/areas/playbots-groups.md, dungeon and raid runs).</summary>
public interface IInstanceEncounterStore
{
    /// <summary>Every encounter, ordered by map then encounter index.</summary>
    Task<IReadOnlyList<InstanceEncounter>> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>instance_encounter</c> row: an encounter of an instance map (cmangos classic-db <c>instance_dungeon_encounters</c> joined to
/// <c>instance_encounters</c>, difficulty 0).
/// </summary>
public sealed class InstanceEncounterRow
{
    public uint EncounterId { get; set; }

    public uint Map { get; set; }

    public uint EncounterIndex { get; set; }

    public byte CreditType { get; set; }

    public uint CreditEntry { get; set; }

    public bool LastEncounter { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The <c>instance_encounter</c> table of the world database: which creature kills complete which encounter of an instance, in the
/// dungeon's order, and where its segments (wings) end. The bots' dungeon and raid runs read it to find the bosses of a map
/// (<c>PlayerbotRunCatalogue</c>). The world schema version is the next free one after the smart script condition table (51); the integrator
/// renumbers this one constant when other world steps merge first (tests refer to it, never to a literal).
/// </summary>
public sealed class InstanceEncounterDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 52;

    public const string Table = "instance_encounter";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<InstanceEncounterRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.EncounterId);
            entity.Property(r => r.EncounterId).HasColumnName("encounter_id").ValueGeneratedNever();
            entity.Property(r => r.Map).HasColumnName("map");
            entity.Property(r => r.EncounterIndex).HasColumnName("encounter_index");
            entity.Property(r => r.CreditType).HasColumnName("credit_type");
            entity.Property(r => r.CreditEntry).HasColumnName("credit_entry");
            entity.Property(r => r.LastEncounter).HasColumnName("last_encounter");
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(255);
            entity.HasIndex(r => r.Map);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IInstanceEncounterStore, EfInstanceEncounterStore>();
}

/// <summary>EF Core implementation of <see cref="IInstanceEncounterStore"/>.</summary>
public sealed class EfInstanceEncounterStore(WorldDbContext db) : IInstanceEncounterStore
{
    public async Task<IReadOnlyList<InstanceEncounter>> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<InstanceEncounterRow> rows = await db.Set<InstanceEncounterRow>().AsNoTracking()
            .OrderBy(r => r.Map).ThenBy(r => r.EncounterIndex).ThenBy(r => r.EncounterId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new InstanceEncounter(r.Map, r.EncounterIndex, r.EncounterId, r.CreditType, r.CreditEntry, r.LastEncounter, r.Name))];
    }
}

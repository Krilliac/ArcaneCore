using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Instances;

/// <summary>A dungeon/raid instance (vmangos <c>instance</c>: id, map, resettime).</summary>
public sealed class InstanceRow
{
    public int Id { get; set; }
    public int MapId { get; set; }
    public long ResetTime { get; set; }
}

/// <summary>A character's instance bind (vmangos <c>character_instance</c>: guid, instance, permanent).</summary>
public sealed class CharacterInstanceRow
{
    public int CharacterId { get; set; }
    public int InstanceId { get; set; }
    public bool Permanent { get; set; }
}

/// <summary>A raid map's next global reset (vmangos <c>instance_reset</c>: mapid, resettime).</summary>
public sealed class InstanceResetRow
{
    public int MapId { get; set; }
    public long ResetTime { get; set; }
}

/// <summary>The dungeon instance a character last entered (vmangos <c>characters.instance_id</c>, kept in its own table here).</summary>
public sealed class CharacterLastInstanceRow
{
    public int CharacterId { get; set; }
    public int MapId { get; set; }
    public int InstanceId { get; set; }
}

/// <summary>
/// Instance tables of the characters database (docs/integration/instances.md). The fleet plan
/// reserved characters schema v9 for this module (<see cref="ReservedSchemaVersion"/>), but the
/// schema composer refuses version gaps, so it takes the next version in merge order: v8,
/// directly after reputation (v7). The tables are new; nothing existing changes.
/// <para>Character deletion (<see cref="ICharacterDataCleanup"/>) removes the character's binds and
/// last-instance row; an instance nobody is bound to any more is dropped at the next load.</para>
/// </summary>
public sealed class InstanceDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The characters schema version reserved for instances in the fleet plan.</summary>
    public const int ReservedSchemaVersion = 9;

    /// <summary>The version this module takes in merge order: v8, after reputation (v7).</summary>
    public const int BranchSchemaVersion = 8;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => BranchSchemaVersion;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("instance"),
        new CreateTableChange("character_instance"),
        new CreateTableChange("instance_reset"),
        new CreateTableChange("character_last_instance"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InstanceRow>(entity =>
        {
            entity.ToTable("instance");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.HasIndex(r => r.MapId);
        });

        modelBuilder.Entity<CharacterInstanceRow>(entity =>
        {
            entity.ToTable("character_instance");
            entity.HasKey(r => new { r.CharacterId, r.InstanceId });
            entity.HasIndex(r => r.InstanceId);
        });

        modelBuilder.Entity<InstanceResetRow>(entity =>
        {
            entity.ToTable("instance_reset");
            entity.HasKey(r => r.MapId);
            entity.Property(r => r.MapId).ValueGeneratedNever();
        });

        modelBuilder.Entity<CharacterLastInstanceRow>(entity =>
        {
            entity.ToTable("character_last_instance");
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IInstanceStore, EfInstanceStore>();

    /// <summary>The character's binds and last instance (vmangos DeleteFromDB: character_instance).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterInstanceRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterLastInstanceRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

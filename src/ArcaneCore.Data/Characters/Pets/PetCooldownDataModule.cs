using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Pets;

/// <summary>Current-pet spell/category cooldowns, keyed by the durable pet number.</summary>
public sealed class PersistentPetCooldownRow
{
    public int CharacterId { get; set; }
    public uint PetNumber { get; set; }
    public byte Kind { get; set; }
    public uint SpellId { get; set; }
    public uint Category { get; set; }
    public long EndsAtUnixMs { get; set; }
}

public sealed class PetCooldownDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 31; // allocated as 23 on the Codex line; renumbered at the 2026-10-07 integration
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("character_pet_cooldown")];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<PersistentPetCooldownRow>(entity =>
        {
            entity.ToTable("character_pet_cooldown");
            entity.HasKey(r => new { r.CharacterId, r.PetNumber, r.Kind, r.SpellId, r.Category });
        });

    public void AddServices(IServiceCollection services) { }

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<PersistentPetCooldownRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
}

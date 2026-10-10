using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Pets;

/// <summary>
/// Hunter pet loyalty level, loyalty points and training points (vmangos character_pet.loyalty, loyaltypoints, trainpoint;
/// Pet.cpp:283-290, 551-553). Existing rows read as a Rebellious pet with 1000 points, as a fresh tame.
/// </summary>
public sealed class PetLoyaltyDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>Characters schema version (renumbered by the integrator when other characters steps merge first).</summary>
    public const int Version = 51;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("character_pet", nameof(PersistentPetRow.Loyalty)),
        new AddColumnChange("character_pet", nameof(PersistentPetRow.LoyaltyPoints)),
        new AddColumnChange("character_pet", nameof(PersistentPetRow.TrainingPoints)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder) { }

    public void AddServices(IServiceCollection services) { }

    // These columns belong to the pet row deleted by PersistentPetDataModule.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

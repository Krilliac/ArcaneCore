using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Pets;

/// <summary>
/// Characters schema 76: <c>character_pet.CreatedBySpell</c> (the summoning spell of a saved warlock demon; vmangos character_pet.created_by_spell,
/// Pet.cpp:580), <c>ResetTalentsCost</c> and <c>ResetTalentsTime</c> (the last pet untraining price in copper and its unix seconds; vmangos
/// reset_talents_cost and reset_talents_time, Pet.cpp:582-583). Existing rows read 0 in all three: a hunter pet's next untraining costs 10 silver,
/// as it did when these values were not stored. The columns are properties of <see cref="PersistentPetRow"/> (its table is configured by
/// <see cref="PersistentPetDataModule"/>); the step only adds them to a database at characters version 75. A warlock's saved demons need no table of their own: they are
/// <see cref="PetStableRow"/> rows from <see cref="PetStableRow.DemonSlotBase"/> up, one per creature entry.
/// </summary>
public sealed class PetUntrainAndDemonDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>Characters schema version (76; the honor contribution operation id step follows at 77).</summary>
    public const int Version = 76;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("character_pet", nameof(PersistentPetRow.CreatedBySpell)),
        new AddColumnChange("character_pet", nameof(PersistentPetRow.ResetTalentsCost)),
        new AddColumnChange("character_pet", nameof(PersistentPetRow.ResetTalentsTime)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder) { }

    public void AddServices(IServiceCollection services) { }

    // These columns belong to the pet row deleted by PersistentPetDataModule.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

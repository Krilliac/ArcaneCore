using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Pets;

/// <summary>Pet name and rename-permission columns; characters version 24 was allocated by the integration coordinator.</summary>
public sealed class PetNamingDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 24;
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("character_pet", nameof(PersistentPetRow.Name)),
        new AddColumnChange("character_pet", nameof(PersistentPetRow.NameTimestamp)),
        new AddColumnChange("character_pet", nameof(PersistentPetRow.RenameAllowed)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder) { }
    public void AddServices(IServiceCollection services) { }

    // These columns belong to the pet row deleted by PersistentPetDataModule.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

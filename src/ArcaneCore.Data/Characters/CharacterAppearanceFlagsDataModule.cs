using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters;

/// <summary>
/// Characters schema 67: the helm and cloak show toggles live with the character, as in vmangos characters.character_flags
/// (Player.cpp:16339-16340 save, :14611 and :14678-14681 load, 0e3ff01). The character row is removed by the core deletion transaction.
/// </summary>
public sealed class CharacterAppearanceFlagsDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 67;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange("characters", "character_flags")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterRecord>().Property(c => c.CharacterFlags).HasColumnName("character_flags").HasDefaultValue(0u);
    }

    public void AddServices(IServiceCollection services)
    {
        // The existing character store saves this column with the rest of the character snapshot.
    }

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        // The core deletion removes the characters row and its character_flags column in the same transaction.
        return Task.CompletedTask;
    }
}

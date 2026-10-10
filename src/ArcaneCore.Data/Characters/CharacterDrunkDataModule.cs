using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters;

/// <summary>
/// The drunk value and the save time it sobers from (vmangos characters.drunk and logout_time; Player.cpp:14666, 14887-14895, 16377).
/// Both live on the character row, which the core deletion removes.
/// </summary>
public sealed class CharacterDrunkDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>Characters schema version (renumbered by the integrator when other characters steps merge first).</summary>
    public const int Version = 50;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("characters", nameof(CharacterRecord.Drunk)),
        new AddColumnChange("characters", nameof(CharacterRecord.LogoutTime)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder) { }

    public void AddServices(IServiceCollection services) { }

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

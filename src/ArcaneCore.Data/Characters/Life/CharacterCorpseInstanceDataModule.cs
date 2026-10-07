using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Life;

/// <summary>
/// The map instance of a released body (vmangos <c>corpse.instance</c>): a ghost that logs out after dying in a dungeon gets its
/// body back in that dungeon instance, not in a shared copy of the map. Adds <c>character_corpse.InstanceId</c> (0 for rows
/// written before it, which is what they meant: those bodies were always restored to instance 0).
/// </summary>
public sealed class CharacterCorpseInstanceDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single place the schema version is set (allocated as the next free characters version, 34, on the instances/death lane).</summary>
    public const int Version = 34;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange(CharacterLifeDataModule.CorpseTable, nameof(CharacterCorpseRow.InstanceId))];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The column is part of CharacterCorpseRow, which CharacterLifeDataModule maps.
    }

    public void AddServices(IServiceCollection services)
    {
        // EfCharacterLifeStore reads and writes the column with the rest of the corpse row.
    }

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        // The column lives in the corpse row, which CharacterLifeDataModule deletes.
        return Task.CompletedTask;
    }
}

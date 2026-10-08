using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Instances;

/// <summary>
/// Characters v41 belongs to another lane. This placeholder yields when its real module is merged;
/// production bootstrap and upgrade refuse to cross it until then.
/// </summary>
public sealed class InstanceScriptDataPredecessorGap : ReservedSchemaGap, ICharacterDataCleanup
{
    public override DatabaseComponent Component => DatabaseComponent.Characters;
    public override int SchemaVersion => 41;

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>
/// Characters v42: vmangos InstanceData::SaveToDB writes instance.data, and
/// Map::CreateInstanceData loads it when the map is recreated.
/// </summary>
public sealed class InstanceScriptDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 42;

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new AddColumnChange("instance", "data")];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<InstanceRow>().Property(r => r.Data).HasColumnName("data");

    public void AddServices(IServiceCollection services)
    {
    }

    // The data belongs to the instance row; deleting one character leaves the instance for its other binds.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

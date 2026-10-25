using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Transfers;

/// <summary>
/// Characters schema 65: indexes <c>cluster_transfer_decision.state</c> (declared by <see cref="ClusterTransferDataModule"/>'s model) on
/// databases created at schema 62, so <see cref="EfTransferDecisionStore.CountUnresolvedAsync"/> does not scan a table whose rows are never pruned in normal operation.
/// </summary>
public sealed class ClusterTransferStateIndexDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 65;

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new EnsureIndexesChange(ClusterTransferDataModule.DecisionTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }

    // The index belongs to the table whose rows ClusterTransferDataModule deletes.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Transfers;

/// <summary>
/// Characters schema 74: adds <c>cluster_transfer_decision.target_scope</c>, the ownership scope a transfer's target claims
/// under, so a per-character claim can move from one worker shard to another when the transfer commits (design _wf/writer-lock-DESIGN.md
/// section 1b). Null on a row means the source token's scope (the realm-wide 'world' scope), which is every row written before this step.
/// </summary>
public sealed class ClusterTransferTargetScopeDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 74;

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new AddColumnChange(ClusterTransferDataModule.DecisionTable, "target_scope")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ClusterTransferDecisionRow>(entity =>
            entity.Property(r => r.TargetScope).HasColumnName("target_scope").HasMaxLength(64));
    }

    public void AddServices(IServiceCollection services)
    {
    }

    // The column belongs to the table whose rows ClusterTransferDataModule deletes.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

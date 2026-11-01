using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Transfers;

/// <summary>
/// Characters schema 73: <c>cluster_world_handoff.open_scope</c> and its unique index, so a handoff is open per ownership scope
/// (the realm-wide <c>world</c> row, or one <c>worker:&lt;shard&gt;</c>) and two shards can hand off at the same time. The older <c>open_key</c> unique
/// index stays: only a <c>world</c> handoff sets it, so Realm-mode behaviour (one open handoff) is unchanged. A handoff opened before this schema has no
/// open scope; the store treats an unresolved row with an open key and no open scope as a <c>world</c> handoff. Released modules (v62) are frozen, which is
/// why the column is added here and not in <see cref="ClusterTransferDataModule"/>'s table step. Design: _wf/writer-lock-DESIGN.md step C7.
/// </summary>
public sealed class ClusterHandoffScopeDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 73;

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange(ClusterTransferDataModule.HandoffTable, "open_scope"),
        new EnsureIndexesChange(ClusterTransferDataModule.HandoffTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ClusterWorldHandoffRow>(entity =>
        {
            entity.Property(r => r.OpenScope).HasColumnName("open_scope").HasMaxLength(64);
            entity.HasIndex(r => r.OpenScope).IsUnique();
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }

    // Handoff rows belong to worlds, not characters.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

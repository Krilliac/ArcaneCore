using ArcaneCore.Kernel.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Characters.Ids;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Ownership;

/// <summary>
/// Characters schema 72: per-worker-shard ownership. Adds <c>character_ownership.owned_maps</c> and <c>heartbeat_at_ms</c>
/// (worker rows are inserted when a shard first claims, not seeded), and seeds the id-range rows (<c>pet</c>, <c>gm_ticket</c>, <c>petition</c>)
/// and realm-lease rows (<c>maintenance</c>, <c>worldstate</c>) the later writer-lock steps use. Released modules (v60, v61, v67) are frozen,
/// so the new rows are seeded here and not added to <see cref="IdRangeScopes"/>.All or <see cref="RealmAuthorityLeaseDataModule.Scopes"/>.
/// Design: _wf/writer-lock-DESIGN.md sections 1, 3, 4 and 7. The <c>cluster_world_handoff.open_scope</c> column is owned by the handoff-journal step (it needs
/// the ClusterTransferDataModule model), not by this module.
/// </summary>
public sealed class CharacterOwnershipShardDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 72;

    public const string MaintenanceLeaseScope = "maintenance";
    public const string WorldStateLeaseScope = "worldstate";

    public static readonly IReadOnlyList<string> NewIdRangeScopes = ["pet", "gm_ticket", "petition"];
    public static readonly IReadOnlyList<string> NewLeaseScopes = [MaintenanceLeaseScope, WorldStateLeaseScope];

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange(CharacterOwnershipDataModule.Table, "owned_maps"),
        new AddColumnChange(CharacterOwnershipDataModule.Table, "heartbeat_at_ms"),
        .. NewIdRangeScopes.Select(scope => new EnsureRowChange(CharacterIdRangeDataModule.Table, "scope", scope, new Dictionary<string, object?>
        {
            ["next_id"] = 0L,
        })),
        .. NewLeaseScopes.Select(scope => new EnsureRowChange(RealmAuthorityLeaseDataModule.Table, "scope", scope, new Dictionary<string, object?>
        {
            ["owner_node"] = null,
            ["owner_incarnation"] = null,
            ["fencing_token"] = 0L,
            ["acquired_at_unix_ms"] = 0L,
            ["expires_at_unix_ms"] = 0L,
            ["active"] = false,
        })),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterOwnershipRow>(entity =>
        {
            entity.Property(r => r.OwnedMaps).HasColumnName("owned_maps").HasMaxLength(EfCharacterOwnershipStore.MaxOwnedMapsLength);
            entity.Property(r => r.HeartbeatAtMs).HasColumnName("heartbeat_at_ms");
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }

    // Ownership, id-range and lease rows belong to shards and nodes, not characters.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

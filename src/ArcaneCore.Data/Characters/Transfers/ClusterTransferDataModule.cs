using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Cluster.Transfer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArcaneCore.Data.Characters.Transfers;

/// <summary>One player transfer decision (<see cref="TransferDecisionRecord"/>), durable across a crash of either world or the coordinator.</summary>
public sealed class ClusterTransferDecisionRow
{
    public string TransferId { get; set; } = string.Empty;
    public string? HandoffId { get; set; }
    public long CharacterGuid { get; set; }
    public int AccountId { get; set; }
    public long SessionGeneration { get; set; }
    public string SourceWorker { get; set; } = string.Empty;
    public string TargetWorker { get; set; } = string.Empty;
    public long SourceMapId { get; set; }
    public long SourceInstanceId { get; set; }
    public long TargetMapId { get; set; }
    public long TargetInstanceId { get; set; }
    public int Stage { get; set; }
    public int State { get; set; }
    public string SourceScope { get; set; } = string.Empty;
    public long SourceEpoch { get; set; }
    public string SourceIncarnation { get; set; } = string.Empty;
    public long? TargetEpoch { get; set; }
    public string? TargetIncarnation { get; set; }
    public string? Bundle { get; set; }
    public string? AbortReason { get; set; }
    public long UpdatedAtUtcMs { get; set; }
}

/// <summary>
/// The ownership token a player transfer moves (per character), plus the one-transfer-in-flight claim: <see cref="ActiveTransfer"/> is
/// taken by compare-and-swap when a transfer is reserved and given back when it completes or aborts, so two coordinators can never
/// run a transfer of the same character at once, on any provider.
/// </summary>
public sealed class ClusterTransferOwnerRow
{
    public long CharacterGuid { get; set; }
    public string Scope { get; set; } = string.Empty;
    public long Epoch { get; set; }
    public string Incarnation { get; set; } = string.Empty;
    public string? ActiveTransfer { get; set; }
    public long UpdatedAtUtcMs { get; set; }
}

/// <summary>One whole-World handoff (<see cref="WorldHandoffRecord"/>).</summary>
public sealed class ClusterWorldHandoffRow
{
    public string HandoffId { get; set; } = string.Empty;

    /// <summary><see cref="ClusterTransferDataModule.OpenHandoffKey"/> while the handoff is unresolved, null after; unique, so only one handoff is open at a time.</summary>
    public string? OpenKey { get; set; }

    public string SourceWorker { get; set; } = string.Empty;
    public string TargetWorker { get; set; } = string.Empty;
    public int Stage { get; set; }
    public int State { get; set; }
    public string SourceScope { get; set; } = string.Empty;
    public long SourceEpoch { get; set; }
    public string SourceIncarnation { get; set; } = string.Empty;
    public long? TargetEpoch { get; set; }
    public string? TargetIncarnation { get; set; }
    public string? TargetOwnerNode { get; set; }
    public string? AbortReason { get; set; }
    public long CreatedAtUtcMs { get; set; }
    public long UpdatedAtUtcMs { get; set; }
}

/// <summary>
/// Characters schema 62 (Tier 3b groundwork, docs/design/hot-reload-tiers.md section 5.3): the durable transfer-decision store.
/// <c>cluster_transfer_decision</c> records each stage of each player transfer, <c>cluster_transfer_owner</c> the per-character
/// ownership token and in-flight claim, <c>cluster_world_handoff</c> each stage of a whole-World handoff. Implemented by
/// <see cref="EfTransferDecisionStore"/>, registered as the <see cref="ITransferDecisionStore"/> and <see cref="IWorldHandoffStore"/>.
/// </summary>
public sealed class ClusterTransferDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 62;
    public const string DecisionTable = "cluster_transfer_decision";
    public const string OwnerTable = "cluster_transfer_owner";
    public const string HandoffTable = "cluster_world_handoff";

    /// <summary>The value of <see cref="ClusterWorldHandoffRow.OpenKey"/> while a handoff is unresolved.</summary>
    public const string OpenHandoffKey = "open";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(DecisionTable),
        new CreateTableChange(OwnerTable),
        new CreateTableChange(HandoffTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ClusterTransferDecisionRow>(entity =>
        {
            entity.ToTable(DecisionTable);
            entity.HasKey(r => r.TransferId);
            entity.Property(r => r.TransferId).HasColumnName("transfer_id").HasMaxLength(36).ValueGeneratedNever();
            entity.Property(r => r.HandoffId).HasColumnName("handoff_id").HasMaxLength(36);
            entity.Property(r => r.CharacterGuid).HasColumnName("character_guid");
            entity.Property(r => r.AccountId).HasColumnName("account_id");
            entity.Property(r => r.SessionGeneration).HasColumnName("session_generation");
            entity.Property(r => r.SourceWorker).HasColumnName("source_worker").HasMaxLength(128);
            entity.Property(r => r.TargetWorker).HasColumnName("target_worker").HasMaxLength(128);
            entity.Property(r => r.SourceMapId).HasColumnName("source_map");
            entity.Property(r => r.SourceInstanceId).HasColumnName("source_instance");
            entity.Property(r => r.TargetMapId).HasColumnName("target_map");
            entity.Property(r => r.TargetInstanceId).HasColumnName("target_instance");
            entity.Property(r => r.Stage).HasColumnName("stage");
            entity.Property(r => r.State).HasColumnName("state");
            entity.Property(r => r.SourceScope).HasColumnName("source_scope").HasMaxLength(64);
            entity.Property(r => r.SourceEpoch).HasColumnName("source_epoch");
            entity.Property(r => r.SourceIncarnation).HasColumnName("source_incarnation").HasMaxLength(36);
            entity.Property(r => r.TargetEpoch).HasColumnName("target_epoch");
            entity.Property(r => r.TargetIncarnation).HasColumnName("target_incarnation").HasMaxLength(36);
            entity.Property(r => r.Bundle).HasColumnName("bundle");
            entity.Property(r => r.AbortReason).HasColumnName("abort_reason").HasMaxLength(256);
            entity.Property(r => r.UpdatedAtUtcMs).HasColumnName("updated_at_ms");
            entity.HasIndex(r => r.CharacterGuid);
            entity.HasIndex(r => r.HandoffId);
            entity.HasIndex(r => r.State);
        });

        modelBuilder.Entity<ClusterTransferOwnerRow>(entity =>
        {
            entity.ToTable(OwnerTable);
            entity.HasKey(r => r.CharacterGuid);
            entity.Property(r => r.CharacterGuid).HasColumnName("character_guid").ValueGeneratedNever();
            entity.Property(r => r.Scope).HasColumnName("scope").HasMaxLength(64);
            entity.Property(r => r.Epoch).HasColumnName("epoch");
            entity.Property(r => r.Incarnation).HasColumnName("incarnation").HasMaxLength(36);
            entity.Property(r => r.ActiveTransfer).HasColumnName("active_transfer").HasMaxLength(36);
            entity.Property(r => r.UpdatedAtUtcMs).HasColumnName("updated_at_ms");
        });

        modelBuilder.Entity<ClusterWorldHandoffRow>(entity =>
        {
            entity.ToTable(HandoffTable);
            entity.HasKey(r => r.HandoffId);
            entity.Property(r => r.HandoffId).HasColumnName("handoff_id").HasMaxLength(36).ValueGeneratedNever();
            entity.Property(r => r.OpenKey).HasColumnName("open_key").HasMaxLength(16);
            entity.Property(r => r.SourceWorker).HasColumnName("source_worker").HasMaxLength(128);
            entity.Property(r => r.TargetWorker).HasColumnName("target_worker").HasMaxLength(128);
            entity.Property(r => r.Stage).HasColumnName("stage");
            entity.Property(r => r.State).HasColumnName("state");
            entity.Property(r => r.SourceScope).HasColumnName("source_scope").HasMaxLength(64);
            entity.Property(r => r.SourceEpoch).HasColumnName("source_epoch");
            entity.Property(r => r.SourceIncarnation).HasColumnName("source_incarnation").HasMaxLength(36);
            entity.Property(r => r.TargetEpoch).HasColumnName("target_epoch");
            entity.Property(r => r.TargetIncarnation).HasColumnName("target_incarnation").HasMaxLength(36);
            entity.Property(r => r.TargetOwnerNode).HasColumnName("target_owner_node").HasMaxLength(128);
            entity.Property(r => r.AbortReason).HasColumnName("abort_reason").HasMaxLength(256);
            entity.Property(r => r.CreatedAtUtcMs).HasColumnName("created_at_ms");
            entity.Property(r => r.UpdatedAtUtcMs).HasColumnName("updated_at_ms");
            entity.HasIndex(r => r.OpenKey).IsUnique();
        });
    }

    public void AddServices(IServiceCollection services)
    {
        services.TryAddSingleton<EfTransferDecisionStore>();
        services.TryAddSingleton<ITransferDecisionStore>(sp => sp.GetRequiredService<EfTransferDecisionStore>());
        services.TryAddSingleton<IWorldHandoffStore>(sp => sp.GetRequiredService<EfTransferDecisionStore>());
    }

    /// <summary>
    /// Refuses to delete a character while a transfer of it is unresolved (its <see cref="ClusterTransferOwnerRow.ActiveTransfer"/>
    /// claim is held, or a decision is still pre-commit or committed): the transfer would otherwise resume, roll forward or abort onto a
    /// character that no longer exists. Otherwise removes its ownership token and its (resolved) transfer decisions. A player character's
    /// guid is its id (high part 0), which is how the transfer rows key it.
    /// </summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        long guid = characterId;
        bool unresolved = await db.Set<ClusterTransferOwnerRow>()
                .AnyAsync(r => r.CharacterGuid == guid && r.ActiveTransfer != null, cancellationToken).ConfigureAwait(false)
            || await db.Set<ClusterTransferDecisionRow>()
                .AnyAsync(r => r.CharacterGuid == guid
                    && (r.State == (int)PlayerTransferDecisionState.PreCommit || r.State == (int)PlayerTransferDecisionState.Committed), cancellationToken)
                .ConfigureAwait(false);
        if (unresolved)
            throw new CharacterDeletionRefusedException($"character {characterId} has a cluster transfer in flight");

        await db.Set<ClusterTransferDecisionRow>().Where(r => r.CharacterGuid == guid).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<ClusterTransferOwnerRow>().Where(r => r.CharacterGuid == guid).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

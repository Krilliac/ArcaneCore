using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Ownership;

/// <summary>One worker shard's last heartbeat (characters schema 75); one row per ownership scope, written by the owner and read by the reaper.</summary>
public sealed class CharacterOwnershipHeartbeatRow
{
    public string Scope { get; set; } = string.Empty;

    /// <summary>Unix milliseconds of the owner's last heartbeat.</summary>
    public long HeartbeatAtMs { get; set; }
}

/// <summary>
/// Characters schema 75: moves the worker heartbeat off the <c>character_ownership</c> fence row into <c>character_ownership_heartbeat</c>. A heartbeat UPDATE on the fence row took a row lock
/// that collided with the Serializable and RepeatableRead <c>FOR SHARE</c> locks every fenced transaction begin takes on that row, and nothing retried the loser. The new table is not part of any
/// fence, so a beat never waits for a character write. The old <c>character_ownership.heartbeat_at_ms</c> column stays (a step cannot drop a column) but is no longer written or read; the first
/// beat of a restarted worker fills the new row (a shard with no beat is never reaped, so a shard whose owner died before that stays claimed until it is reclaimed or released).
/// Design: _wf/followups.md (soak diagnosis 2026-11-05).
/// </summary>
public sealed class CharacterOwnershipHeartbeatDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 75;
    public const string Table = "character_ownership_heartbeat";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterOwnershipHeartbeatRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Scope);
            entity.Property(r => r.Scope).HasColumnName("scope").HasMaxLength(64).ValueGeneratedNever();
            entity.Property(r => r.HeartbeatAtMs).HasColumnName("heartbeat_at_ms");
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }

    // Heartbeats belong to shards, not characters.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

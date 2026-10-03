using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters;

/// <summary>A committed character deletion whose runtime finalization is not yet complete (table <c>character_deletion</c>).</summary>
public sealed class CharacterDeletionRow
{
    /// <summary>The operation id (a GUID in "D" format), the primary key.</summary>
    public string OperationId { get; set; } = string.Empty;

    public int CharacterId { get; set; }

    public int AccountId { get; set; }

    public string Name { get; set; } = string.Empty;

    public long CommittedAt { get; set; }
}

/// <summary>Creating a character with an id whose deletion is still pending finalization.</summary>
public sealed class CharacterIdPendingDeletionException(int characterId)
    : InvalidOperationException($"character id {characterId} is still pending deletion finalization");

/// <summary>
/// The character-deletion ledger of the characters database (docs/integration/character-delete.md):
/// <c>character_deletion</c> holds one row per committed deletion until the world confirms that its
/// runtime finalizers ran. <see cref="Version"/> is the single constant the lead renumbers: it is
/// the next contiguous characters version at this branch's base (c3dea16, economy v10); the
/// integrator renumbers it at merge time (docs/integration/seams.md, "Schema versions").
/// </summary>
public sealed class CharacterDeletionDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single version constant the lead renumbers.</summary>
    public const int Version = 12;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("character_deletion")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterDeletionRow>(entity =>
        {
            entity.ToTable("character_deletion");
            entity.HasKey(r => r.OperationId);
            entity.Property(r => r.OperationId).HasColumnName("operation_id").HasMaxLength(36).ValueGeneratedNever();
            entity.Property(r => r.CharacterId).HasColumnName("character_id");
            entity.Property(r => r.AccountId).HasColumnName("account_id");
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(12).IsRequired();
            entity.Property(r => r.CommittedAt).HasColumnName("committed_at");

            // At most one pending deletion per character id.
            entity.HasIndex(r => r.CharacterId).IsUnique();
            entity.HasIndex(r => r.AccountId);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterDeletionStore, EfCharacterStore>();

    /// <summary>
    /// Refuse to delete a live character whose id still has a pending deletion of an earlier
    /// lifetime: that id was recreated outside <see cref="EfCharacterStore.CreateAsync"/>'s fence
    /// (for example a raw import), and finalizing the earlier lifetime would now hit this one.
    /// </summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.Set<CharacterDeletionRow>().AnyAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false))
        {
            throw new CharacterDeletionRefusedException($"character id {characterId} still has a pending earlier deletion");
        }
    }
}

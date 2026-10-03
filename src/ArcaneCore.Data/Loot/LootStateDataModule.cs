using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Loot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Loot;

/// <summary>The durable loot of one chest of a dungeon instance (the header of a <see cref="LootStateRecord"/>).</summary>
public sealed class LootStateRow
{
    public int InstanceId { get; set; }
    public uint SpawnGuid { get; set; }
    public uint SourceEntry { get; set; }
    public int LootOwner { get; set; }
    public uint Generation { get; set; }
    public bool Consumed { get; set; }
    public long RespawnAt { get; set; }
}

/// <summary>One stack of a stored chest.</summary>
public sealed class LootStateItemRow
{
    public int InstanceId { get; set; }
    public uint SpawnGuid { get; set; }
    public byte Slot { get; set; }
    public uint ItemId { get; set; }
    public uint Count { get; set; }
    public bool IsQuest { get; set; }
    public bool IsPerPlayer { get; set; }
    public bool IsLooted { get; set; }
}

/// <summary>
/// One character named by a stored chest: a recipient (slot <see cref="RecipientSlot"/>), a
/// character allowed to take a quest stack, or a character that took its copy of a stack.
/// </summary>
public sealed class LootStatePlayerRow
{
    /// <summary>The slot value of recipient rows (a chest-wide list, not tied to a stack).</summary>
    public const byte RecipientSlot = 255;

    public const byte RoleRecipient = 0;
    public const byte RoleAllowed = 1;
    public const byte RoleLootedBy = 2;

    public int InstanceId { get; set; }
    public uint SpawnGuid { get; set; }
    public byte Slot { get; set; }
    public int CharacterId { get; set; }
    public byte Role { get; set; }
}

/// <summary>The idempotency ledger: one row per committed loot operation.</summary>
public sealed class LootOperationRow
{
    public string Id { get; set; } = string.Empty;
    public long CommittedAt { get; set; }
}

/// <summary>
/// Durable chest loot of dungeon instances (docs/integration/gameobjects-loot.md): what a chest
/// generated, who may take it and what was taken, tied to the logical instance save. Rows go with
/// the instance (<c>EfInstanceStore.DeleteInstanceAsync</c>, a real reset or deletion) and are
/// purged at startup when their instance row is missing.
/// <para>Schema allocation: <see cref="Version"/> is the next contiguous characters version at
/// this branch's base (after economy v10); the lead renumbers it at merge time
/// (docs/integration/seams.md, "Schema versions"). It is the only place the number is written.</para>
/// <para>Character deletion keeps the rows: they name characters only by id, and removing a
/// recipient would turn the chest into one that anyone may loot (an empty recipient list means
/// "anyone"), so a deleted character's marks stay as inert history.</para>
/// </summary>
public sealed class LootStateDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single version constant the lead renumbers.</summary>
    public const int Version = 13;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("loot_state"),
        new CreateTableChange("loot_state_item"),
        new CreateTableChange("loot_state_player"),
        new CreateTableChange("loot_operation"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LootStateRow>(entity =>
        {
            entity.ToTable("loot_state");
            entity.HasKey(r => new { r.InstanceId, r.SpawnGuid });
            entity.Property(r => r.InstanceId).HasColumnName("instance_id").ValueGeneratedNever();
            entity.Property(r => r.SpawnGuid).HasColumnName("spawn_guid").ValueGeneratedNever();
            entity.Property(r => r.SourceEntry).HasColumnName("source_entry");
            entity.Property(r => r.LootOwner).HasColumnName("loot_owner");
            entity.Property(r => r.Generation).HasColumnName("generation");
            entity.Property(r => r.Consumed).HasColumnName("consumed");
            entity.Property(r => r.RespawnAt).HasColumnName("respawn_at");
        });

        modelBuilder.Entity<LootStateItemRow>(entity =>
        {
            entity.ToTable("loot_state_item");
            entity.HasKey(r => new { r.InstanceId, r.SpawnGuid, r.Slot });
            entity.Property(r => r.InstanceId).HasColumnName("instance_id").ValueGeneratedNever();
            entity.Property(r => r.SpawnGuid).HasColumnName("spawn_guid").ValueGeneratedNever();
            entity.Property(r => r.Slot).HasColumnName("slot").ValueGeneratedNever();
            entity.Property(r => r.ItemId).HasColumnName("item_id");
            entity.Property(r => r.Count).HasColumnName("count");
            entity.Property(r => r.IsQuest).HasColumnName("is_quest");
            entity.Property(r => r.IsPerPlayer).HasColumnName("is_per_player");
            entity.Property(r => r.IsLooted).HasColumnName("is_looted");
        });

        modelBuilder.Entity<LootStatePlayerRow>(entity =>
        {
            entity.ToTable("loot_state_player");
            entity.HasKey(r => new { r.InstanceId, r.SpawnGuid, r.Slot, r.CharacterId, r.Role });
            entity.Property(r => r.InstanceId).HasColumnName("instance_id").ValueGeneratedNever();
            entity.Property(r => r.SpawnGuid).HasColumnName("spawn_guid").ValueGeneratedNever();
            entity.Property(r => r.Slot).HasColumnName("slot").ValueGeneratedNever();
            entity.Property(r => r.CharacterId).HasColumnName("character_id").ValueGeneratedNever();
            entity.Property(r => r.Role).HasColumnName("role").ValueGeneratedNever();
        });

        modelBuilder.Entity<LootOperationRow>(entity =>
        {
            entity.ToTable("loot_operation");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();
            entity.Property(r => r.CommittedAt).HasColumnName("committed_at");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ILootStateStore, EfLootStateStore>();

    /// <summary>Nothing to remove: chest rows name characters only as inert history (see the class remarks).</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

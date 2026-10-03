using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Quests;

/// <summary>
/// Characters schema version 5 (quests and NPC services, docs/integration/quests-npc.md):
/// per-character quest progress (vmangos character_queststatus) and known flight paths.
/// </summary>
public sealed class QuestNpcCharactersModule : IDataModule
{
    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => 5;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("character_queststatus"),
        new CreateTableChange("character_taxi"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterQuestStatusRow>(entity =>
        {
            entity.ToTable("character_queststatus");
            entity.HasKey(r => new { r.CharacterId, r.Quest });
            entity.Property(r => r.CharacterId).HasColumnName("guid");
            entity.Property(r => r.Quest).HasColumnName("quest");
            entity.Property(r => r.Status).HasColumnName("status");
            entity.Property(r => r.Rewarded).HasColumnName("rewarded");
            entity.Property(r => r.Explored).HasColumnName("explored");
            entity.Property(r => r.Timer).HasColumnName("timer");
            entity.Property(r => r.MobCount1).HasColumnName("mob_count1");
            entity.Property(r => r.MobCount2).HasColumnName("mob_count2");
            entity.Property(r => r.MobCount3).HasColumnName("mob_count3");
            entity.Property(r => r.MobCount4).HasColumnName("mob_count4");
            entity.Property(r => r.ItemCount1).HasColumnName("item_count1");
            entity.Property(r => r.ItemCount2).HasColumnName("item_count2");
            entity.Property(r => r.ItemCount3).HasColumnName("item_count3");
            entity.Property(r => r.ItemCount4).HasColumnName("item_count4");
            entity.Property(r => r.RewardChoice).HasColumnName("reward_choice");
        });

        modelBuilder.Entity<CharacterTaxiRow>(entity =>
        {
            entity.ToTable("character_taxi");
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Mask0).HasColumnName("mask0");
            entity.Property(r => r.Mask1).HasColumnName("mask1");
            entity.Property(r => r.Mask2).HasColumnName("mask2");
            entity.Property(r => r.Mask3).HasColumnName("mask3");
            entity.Property(r => r.Mask4).HasColumnName("mask4");
            entity.Property(r => r.Mask5).HasColumnName("mask5");
            entity.Property(r => r.Mask6).HasColumnName("mask6");
            entity.Property(r => r.Mask7).HasColumnName("mask7");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterQuestStore, EfCharacterQuestStore>();
}

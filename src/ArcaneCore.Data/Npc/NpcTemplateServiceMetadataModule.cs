using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Npc;

/// <summary>World schema step for direct creature-template NPC service metadata.</summary>
public sealed class NpcTemplateServiceMetadataModule : IDataModule
{
    public const int Version = 26;
    public const string Table = "npc_template_service_metadata";

    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<NpcTemplateServiceMetadata>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).HasColumnName("entry").ValueGeneratedNever();
            entity.Property(r => r.GossipMenuId).HasColumnName("gossip_menu_id");
            entity.Property(r => r.TrainerType).HasColumnName("trainer_type");
            entity.Property(r => r.TrainerClass).HasColumnName("trainer_class");
            entity.Property(r => r.TrainerRace).HasColumnName("trainer_race");
            entity.Property(r => r.TrainerSpell).HasColumnName("trainer_spell");
        });

    public void AddServices(IServiceCollection services)
        => services.AddScoped<INpcTemplateServiceMetadataSource, EfNpcTemplateServiceMetadataSource>();
}

using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Skills;

public sealed class StartingSkillEntity
{
    public uint RaceMask { get; set; }
    public uint ClassMask { get; set; }
    public ushort Skill { get; set; }
    public ushort Step { get; set; }
    public string Note { get; set; } = string.Empty;
}

/// <summary>World schema for classic-db/vmangos playercreateinfo_skills.</summary>
public sealed class StartingSkillWorldDataModule : IDataModule
{
    public const int Version = 25;
    public const string Table = "playercreateinfo_skills";

    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StartingSkillEntity>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => new { r.RaceMask, r.ClassMask, r.Skill });
            entity.Property(r => r.RaceMask).HasColumnName("raceMask");
            entity.Property(r => r.ClassMask).HasColumnName("classMask");
            entity.Property(r => r.Skill).HasColumnName("skill");
            entity.Property(r => r.Step).HasColumnName("step");
            entity.Property(r => r.Note).HasColumnName("note");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IStartingSkillSource, EfStartingSkillSource>();
}

public sealed class EfStartingSkillSource(WorldDbContext db) : IStartingSkillSource
{
    public async Task<IReadOnlyList<StartingSkill>> GetAsync(byte race, byte playerClass, CancellationToken cancellationToken = default)
    {
        if (race is < 1 or > 32 || playerClass is < 1 or > 32)
        {
            return [];
        }

        uint raceBit = 1u << (race - 1);
        uint classBit = 1u << (playerClass - 1);
        return await db.Set<StartingSkillEntity>().AsNoTracking()
            .Where(r => (r.RaceMask == 0 || (r.RaceMask & raceBit) != 0)
                && (r.ClassMask == 0 || (r.ClassMask & classBit) != 0))
            .OrderBy(r => r.Skill)
            .Select(r => new StartingSkill(r.Skill, r.Step, r.Note))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

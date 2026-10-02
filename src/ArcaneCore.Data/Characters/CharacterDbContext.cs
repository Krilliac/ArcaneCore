using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Characters;

/// <summary>EF Core context for the characters database (one per realm).</summary>
public sealed class CharacterDbContext(DbContextOptions<CharacterDbContext> options) : DbContext(options)
{
    /// <summary>Schema history of the characters database.</summary>
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "characters",
        CurrentVersion = 1,
        Version1Tables = ["characters"],
    };

    public DbSet<CharacterRecord> Characters => Set<CharacterRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);

        modelBuilder.Entity<CharacterRecord>(entity =>
        {
            entity.ToTable("characters");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();
            entity.Property(c => c.Name).HasMaxLength(12).IsRequired();
            entity.HasIndex(c => c.Name).IsUnique();
            entity.HasIndex(c => c.AccountId);
        });
    }
}

using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Auth;

/// <summary>
/// EF Core context for the auth database: accounts and the realm list. The Kernel domain
/// types double as the persistence entities.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    /// <summary>Schema history of the auth database.</summary>
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "auth",
        CurrentVersion = 2,
        Version1Tables = ["account", "realmlist"],
        Steps =
        [
            // M6: account GM level.
            new SchemaStep(2, [new AddColumnChange("account", "Security")]),
        ],
    };

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<RealmEntry> Realms => Set<RealmEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("account");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedOnAdd();
            entity.Property(a => a.Username).HasMaxLength(16).IsRequired();
            entity.HasIndex(a => a.Username).IsUnique();
            entity.Property(a => a.Salt).HasMaxLength(32).IsRequired();
            entity.Property(a => a.Verifier).HasMaxLength(32).IsRequired();
            entity.Property(a => a.SessionKey).HasMaxLength(40);
            entity.Property(a => a.Status).HasConversion<int>();
            entity.Property(a => a.Security).HasConversion<byte>();
        });

        modelBuilder.Entity<RealmEntry>(entity =>
        {
            entity.ToTable("realmlist");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedOnAdd();
            entity.Property(r => r.Name).HasMaxLength(64).IsRequired();
            entity.Property(r => r.Address).HasMaxLength(64).IsRequired();
            entity.Property(r => r.Type).HasConversion<uint>();
            entity.Property(r => r.Flags).HasConversion<byte>();
            entity.Property(r => r.Population);
            entity.Property(r => r.Category);
        });
    }
}

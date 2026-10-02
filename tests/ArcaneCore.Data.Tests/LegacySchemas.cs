using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Tests;

/// <summary>Context construction shared by the data tests.</summary>
internal static class TestContexts
{
    public static DbContextOptions<T> Options<T>(DatabaseConnectionOptions cs) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, cs);
        return builder.Options;
    }

    public static T Create<T>(DatabaseConnectionOptions cs) where T : DbContext
        => (T)Activator.CreateInstance(typeof(T), Options<T>(cs))!;

    public static string Quote(DbContext db, string identifier)
        => db.GetService<ISqlGenerationHelper>().DelimitIdentifier(identifier);

    /// <summary>Create the database and the context's tables the way EnsureCreated did before M5.</summary>
    public static async Task CreateTablesAsync(DbContext db)
    {
        IRelationalDatabaseCreator creator = db.GetService<IRelationalDatabaseCreator>();
        await creator.CreateAsync();
        await creator.CreateTablesAsync();
    }
}

/// <summary>A row of the characters table as M3–M5 defined it (schema version 1).</summary>
internal sealed class CharacterV1Row
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Race { get; set; }
    public byte Class { get; set; }
    public byte Gender { get; set; }
    public byte Skin { get; set; }
    public byte Face { get; set; }
    public byte HairStyle { get; set; }
    public byte HairColor { get; set; }
    public byte FacialHair { get; set; }
    public byte Level { get; set; } = 1;
    public uint MapId { get; set; }
    public uint ZoneId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Orientation { get; set; }
    public uint PlayedTime { get; set; }
}

/// <summary>
/// The characters database exactly as commit ee8dd3e (M5) mapped it: characters schema
/// version 1. With <paramref name="withVersionTable"/> false it is what M3/M4's EnsureCreated
/// produced (no characters_schema table).
/// </summary>
internal class CharactersV1Context(DbContextOptions options, bool withVersionTable) : DbContext(options)
{
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "characters",
        CurrentVersion = 1,
        Version1Tables = ["characters"],
    };

    public DbSet<CharacterV1Row> Characters => Set<CharacterV1Row>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (withVersionTable)
        {
            SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);
        }

        modelBuilder.Entity<CharacterV1Row>(entity =>
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

/// <summary>The pre-M5 shape: tables only.</summary>
internal sealed class CharactersPreM5Context(DbContextOptions<CharactersPreM5Context> options)
    : CharactersV1Context(options, withVersionTable: false);

/// <summary>The M5 shape: tables plus characters_schema at version 1.</summary>
internal sealed class CharactersM5Context(DbContextOptions<CharactersM5Context> options)
    : CharactersV1Context(options, withVersionTable: true);

/// <summary>An account row as M1–M5 defined it (auth schema version 1, no Security column).</summary>
internal sealed class AccountV1Row
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public byte[] Salt { get; set; } = [];
    public byte[] Verifier { get; set; } = [];
    public byte[]? SessionKey { get; set; }
    public AccountStatus Status { get; set; }
}

/// <summary>The auth database exactly as commit ee8dd3e (M5) mapped it: auth schema version 1.</summary>
internal sealed class AuthM5Context(DbContextOptions<AuthM5Context> options) : DbContext(options)
{
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "auth",
        CurrentVersion = 1,
        Version1Tables = ["account", "realmlist"],
    };

    public DbSet<AccountV1Row> Accounts => Set<AccountV1Row>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);

        modelBuilder.Entity<AccountV1Row>(entity =>
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

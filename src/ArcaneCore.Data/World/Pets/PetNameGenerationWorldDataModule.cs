using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Pets;

/// <summary>
/// <c>pet_name_generation</c> (vmangos column names <c>id, word, entry, half</c>; ObjectMgr::LoadPetNames, ObjectMgr.cpp:8530): one word of the
/// generated name of a warlock demon of creature <see cref="Entry"/>. <see cref="Half"/> false puts the word in the first list, true in the second;
/// a name is one word of each.
/// </summary>
public sealed class PetNameGenerationRow
{
    public uint Id { get; set; }

    public string Word { get; set; } = string.Empty;

    public uint Entry { get; set; }

    public bool Half { get; set; }
}

/// <summary>
/// World schema 70: the <c>pet_name_generation</c> table, read into <see cref="ArcaneCore.Kernel.WorldData.Pets.PetContent"/> by
/// <see cref="EfPetDataStore"/>. A database upgraded to 70 holds no rows until the dump is imported, so demons keep the creature name.
/// </summary>
public sealed class PetNameGenerationWorldDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 70;

    public const string Table = "pet_name_generation";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<PetNameGenerationRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Word).HasMaxLength(100).IsRequired();
            entity.HasIndex(r => new { r.Entry, r.Half });
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

using System.Text.Json;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Pets;

/// <summary>Durable current hunter pet state, modelled after vmangos character_pet slot 0.</summary>
public sealed class PersistentPetRow
{
    public int CharacterId { get; set; }
    public uint PetNumber { get; set; }
    public uint Entry { get; set; }
    public byte Level { get; set; }
    public uint Experience { get; set; }
    public uint Health { get; set; }
    public uint Mana { get; set; }
    public uint Happiness { get; set; }
    public byte ReactState { get; set; }
    public string ActionBarJson { get; set; } = "[]";
    public string SpellsJson { get; set; } = "[]";
    public bool IsCurrent { get; set; } = true;
    public string Name { get; set; } = "";
    public uint NameTimestamp { get; set; }
    public bool RenameAllowed { get; set; } = true;
}

/// <summary>
/// Characters schema slice reserved for persistent pets (version 21). The coordinator owns
/// reconciliation with the branch's current-version manifest.
/// </summary>
public sealed class PersistentPetDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 21;
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("character_pet")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PersistentPetRow>(entity =>
        {
            entity.ToTable("character_pet");
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
            entity.Property(r => r.ActionBarJson).IsRequired();
            entity.Property(r => r.SpellsJson).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(100).IsRequired().HasDefaultValue("");
            entity.Property(r => r.RenameAllowed).HasDefaultValue(true);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IPersistentPetStore, EfPersistentPetStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<PersistentPetRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
}

public sealed class EfPersistentPetStore(CharacterDbContext db) : IPersistentPetStore
{
    public bool SupportsDetachedState => true;
    public async Task<PersistentPetSnapshot?> LoadCurrentAsync(int characterId, CancellationToken cancellationToken = default)
    {
        PersistentPetRow? row = await db.Set<PersistentPetRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.CharacterId == characterId && r.IsCurrent, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        List<PersistentPetCooldownRow> cooldowns = await db.Set<PersistentPetCooldownRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId && r.PetNumber == row.PetNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(row, cooldowns);
    }

    public async Task<PersistentPetSnapshot?> LoadCallableAsync(int characterId, CancellationToken cancellationToken = default)
    {
        PersistentPetRow? row = await db.Set<PersistentPetRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        List<PersistentPetCooldownRow> cooldowns = await db.Set<PersistentPetCooldownRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId && r.PetNumber == row.PetNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(row, cooldowns);
    }

    public async Task SaveCurrentAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken = default)
        => await SaveCoreAsync(snapshot with { IsCurrent = true }, cancellationToken).ConfigureAwait(false);

    public async Task SaveDetachedAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken = default)
        => await SaveCoreAsync(snapshot with { IsCurrent = false }, cancellationToken).ConfigureAwait(false);

    private async Task SaveCoreAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistentPetRow? row = await db.Set<PersistentPetRow>().SingleOrDefaultAsync(r => r.CharacterId == snapshot.CharacterId, cancellationToken).ConfigureAwait(false);
            row ??= new PersistentPetRow { CharacterId = snapshot.CharacterId };
            if (row.Entry == 0) db.Set<PersistentPetRow>().Add(row);
            row.PetNumber = snapshot.PetNumber; row.Entry = snapshot.Entry; row.Level = snapshot.Level;
            row.Experience = snapshot.Experience; row.Health = snapshot.Health; row.Mana = snapshot.Mana;
            row.Happiness = snapshot.Happiness; row.ReactState = snapshot.ReactState; row.IsCurrent = snapshot.IsCurrent;
            row.ActionBarJson = JsonSerializer.Serialize(snapshot.ActionBar);
            row.SpellsJson = JsonSerializer.Serialize(snapshot.Spells);
            row.Name = snapshot.Name;
            row.NameTimestamp = snapshot.NameTimestamp;
            row.RenameAllowed = snapshot.RenameAllowed;
            await db.Set<PersistentPetCooldownRow>().Where(r => r.CharacterId == snapshot.CharacterId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Cooldowns is { Count: > 0 })
            {
                db.Set<PersistentPetCooldownRow>().AddRange(snapshot.Cooldowns.Select(c => new PersistentPetCooldownRow
                {
                    CharacterId = snapshot.CharacterId, PetNumber = snapshot.PetNumber, Kind = c.Kind,
                    SpellId = c.SpellId, Category = c.Category, EndsAtUnixMs = c.EndsAtUnixMs,
                }));
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            DetachPetRows(snapshot.CharacterId);
        }
    }

    public async Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.Set<PersistentPetCooldownRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<PersistentPetRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            DetachPetRows(characterId);
        }
    }

    private void DetachPetRows(int characterId)
    {
        // ExecuteDelete bypasses the tracker. A retry on this scope must not
        // publish entities retained from a failed or previous replacement.
        foreach (var entry in db.ChangeTracker.Entries<PersistentPetCooldownRow>()
            .Where(e => e.Entity.CharacterId == characterId).ToArray()) entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<PersistentPetRow>()
            .Where(e => e.Entity.CharacterId == characterId).ToArray()) entry.State = EntityState.Detached;
    }

    private static PersistentPetSnapshot ToSnapshot(PersistentPetRow row, IReadOnlyList<PersistentPetCooldownRow> cooldowns)
        => new(row.CharacterId, row.PetNumber, row.Entry, row.Level, row.Experience, row.Health, row.Mana,
            row.Happiness, row.ReactState,
            JsonSerializer.Deserialize<uint[]>(row.ActionBarJson) ?? [],
            JsonSerializer.Deserialize<PersistentPetSpell[]>(row.SpellsJson) ?? [], row.IsCurrent,
            cooldowns.Select(c => new PersistentPetCooldown(c.Kind, c.SpellId, c.Category, c.EndsAtUnixMs)).ToArray(),
            row.Name, row.NameTimestamp, row.RenameAllowed);
}

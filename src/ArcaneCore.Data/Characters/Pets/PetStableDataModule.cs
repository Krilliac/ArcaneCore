using System.Text.Json;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Pets;

/// <summary>
/// One stabled hunter pet (vmangos character_pet rows with slot PET_SAVE_FIRST_STABLE_SLOT..PET_SAVE_LAST_STABLE_SLOT). The list columns are
/// what MSG_LIST_STABLED_PETS shows; the whole pet travels in <see cref="SnapshotJson"/> and comes back as the current pet when unstabled.
/// </summary>
public sealed class PetStableRow
{
    public int CharacterId { get; set; }
    public byte Slot { get; set; }
    public uint PetNumber { get; set; }
    public uint Entry { get; set; }
    public byte Level { get; set; }
    public string Name { get; set; } = "";
    public byte Loyalty { get; set; }
    public uint Health { get; set; }
    public string SnapshotJson { get; set; } = "{}";
}

/// <summary>The stable slots a hunter bought (vmangos characters.stable_slots).</summary>
public sealed class PetStableSlotsRow
{
    public int CharacterId { get; set; }
    public byte Slots { get; set; }
}

/// <summary>Characters schema step for the hunter pet stable (version 63).</summary>
public sealed class PetStableDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>Characters schema version (renumbered by the integrator when other characters steps merge first).</summary>
    public const int Version = 63;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("character_pet_stable"),
        new CreateTableChange("character_pet_stable_slots"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PetStableRow>(entity =>
        {
            entity.ToTable("character_pet_stable");
            entity.HasKey(r => new { r.CharacterId, r.Slot });
            entity.HasIndex(r => new { r.CharacterId, r.PetNumber }).IsUnique();
            entity.Property(r => r.Name).HasMaxLength(100).IsRequired();
            entity.Property(r => r.SnapshotJson).IsRequired();
        });
        modelBuilder.Entity<PetStableSlotsRow>(entity =>
        {
            entity.ToTable("character_pet_stable_slots");
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IPetStableStore, EfPetStableStore>();

    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        await db.Set<PetStableRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<PetStableSlotsRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class EfPetStableStore(CharacterDbContext db) : IPetStableStore
{
    public async Task<PetStableState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        PetStableSlotsRow? slots = await db.Set<PetStableSlotsRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        List<PetStableRow> rows = await db.Set<PetStableRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId).OrderBy(r => r.Slot).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PetStableState(slots?.Slots ?? 0, rows.Select(r => new StabledPet(r.Slot, ToSnapshot(characterId, r))).ToArray());
    }

    public async Task SetPurchasedSlotsAsync(int characterId, byte slots, CancellationToken cancellationToken = default)
    {
        try
        {
            PetStableSlotsRow? row = await db.Set<PetStableSlotsRow>()
                .SingleOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (row is null) db.Set<PetStableSlotsRow>().Add(new PetStableSlotsRow { CharacterId = characterId, Slots = slots });
            else row.Slots = slots;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var entry in db.ChangeTracker.Entries<PetStableSlotsRow>().Where(e => e.Entity.CharacterId == characterId).ToArray())
                entry.State = EntityState.Detached;
        }
    }

    public async Task StableAsync(int characterId, byte slot, PersistentPetSnapshot active, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(active);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await db.Set<PetStableRow>().AnyAsync(r => r.CharacterId == characterId && (r.Slot == slot || r.PetNumber == active.PetNumber), cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"stable slot {slot} of character {characterId} is taken or pet {active.PetNumber} is already stabled");
            }

            await db.Set<PersistentPetCooldownRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<PersistentPetRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.Set<PetStableRow>().Add(ToRow(characterId, slot, active));
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
            Detach(characterId);
        }
    }

    public async Task<PersistentPetSnapshot?> UnstableAsync(int characterId, uint petNumber, PersistentPetSnapshot? active, CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PetStableRow? stabled = await db.Set<PetStableRow>()
                .SingleOrDefaultAsync(r => r.CharacterId == characterId && r.PetNumber == petNumber, cancellationToken).ConfigureAwait(false);
            if (stabled is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            PersistentPetSnapshot taken = ToSnapshot(characterId, stabled) with { IsCurrent = true };
            byte slot = stabled.Slot;
            db.Set<PetStableRow>().Remove(stabled);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (active is not null)
            {
                db.Set<PetStableRow>().Add(ToRow(characterId, slot, active));
            }

            // The taken pet replaces the character_pet row (the active pet, when there was one, now lives in the slot).
            await EfPersistentPetStore.WriteRowsAsync(db, taken, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return taken;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Detach(characterId);
        }
    }

    private void Detach(int characterId)
    {
        foreach (var entry in db.ChangeTracker.Entries<PetStableRow>().Where(e => e.Entity.CharacterId == characterId).ToArray())
            entry.State = EntityState.Detached;
        EfPersistentPetStore.DetachPetRows(db, characterId);
    }

    private static PetStableRow ToRow(int characterId, byte slot, PersistentPetSnapshot pet) => new()
    {
        CharacterId = characterId, Slot = slot, PetNumber = pet.PetNumber, Entry = pet.Entry, Level = pet.Level, Name = pet.Name,
        Loyalty = pet.LoyaltyLevel, Health = pet.Health,
        SnapshotJson = JsonSerializer.Serialize(pet with { CharacterId = characterId, IsCurrent = false }),
    };

    private static PersistentPetSnapshot ToSnapshot(int characterId, PetStableRow row)
    {
        PersistentPetSnapshot? pet = JsonSerializer.Deserialize<PersistentPetSnapshot>(row.SnapshotJson);
        return (pet ?? PersistentPetSnapshot.Empty(characterId, row.PetNumber, row.Entry, row.Level)) with
        {
            CharacterId = characterId, PetNumber = row.PetNumber, Entry = row.Entry, IsCurrent = false,
        };
    }
}
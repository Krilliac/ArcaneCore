using System.Text.Json;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Characters.Pets;

/// <summary>
/// <see cref="IPetAdminStore"/> over character_pet (the current or dismissed pet) and character_pet_stable (the stable slots, whose full pet
/// lives in SnapshotJson as well as in the list columns).
/// </summary>
public sealed class EfPetAdminStore(CharacterDbContext db) : IPetAdminStore
{
    public async Task<StoredPetLocation?> FindAsync(uint petNumber, CancellationToken cancellationToken = default)
    {
        if (await db.Set<PersistentPetRow>().AsNoTracking().Where(r => r.PetNumber == petNumber).OrderBy(r => r.CharacterId)
                .Select(r => new { r.CharacterId, r.Name }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } current)
        {
            return new StoredPetLocation(petNumber, current.CharacterId, current.Name, Stabled: false);
        }

        return await db.Set<PetStableRow>().AsNoTracking().Where(r => r.PetNumber == petNumber).OrderBy(r => r.CharacterId)
            .Select(r => new StoredPetLocation(petNumber, r.CharacterId, r.Name, true)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RenameAsync(uint petNumber, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int changed = await db.Set<PersistentPetRow>().Where(r => r.PetNumber == petNumber)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, name), cancellationToken).ConfigureAwait(false);
            List<PetStableRow> stabled = await db.Set<PetStableRow>().Where(r => r.PetNumber == petNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (PetStableRow row in stabled)
            {
                row.Name = name;
                if (JsonSerializer.Deserialize<PersistentPetSnapshot>(row.SnapshotJson) is { } snapshot)
                {
                    row.SnapshotJson = JsonSerializer.Serialize(snapshot with { Name = name });
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changed + stabled.Count > 0;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Detach(petNumber);
        }
    }

    public async Task<bool> DeleteAsync(uint petNumber, CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.Set<PersistentPetCooldownRow>().Where(r => r.PetNumber == petNumber).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            int removed = await db.Set<PersistentPetRow>().Where(r => r.PetNumber == petNumber).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            removed += await db.Set<PetStableRow>().Where(r => r.PetNumber == petNumber).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return removed > 0;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Detach(petNumber);
        }
    }

    // ExecuteUpdate/ExecuteDelete bypass the tracker; nothing tracked here may outlive the call (same rule as EfPersistentPetStore).
    private void Detach(uint petNumber)
    {
        foreach (var entry in db.ChangeTracker.Entries<PetStableRow>().Where(e => e.Entity.PetNumber == petNumber).ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<PersistentPetRow>().Where(e => e.Entity.PetNumber == petNumber).ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<PersistentPetCooldownRow>().Where(e => e.Entity.PetNumber == petNumber).ToArray())
            entry.State = EntityState.Detached;
    }
}
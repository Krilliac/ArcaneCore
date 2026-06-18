using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>EF Core implementation of <see cref="ICharacterStore"/>.</summary>
public sealed class EfCharacterStore(CharacterDbContext db) : ICharacterStore
{
    public async Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(
        int accountId, CancellationToken cancellationToken = default)
    {
        return await db.Characters
            .AsNoTracking()
            .Where(c => c.AccountId == accountId)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default)
        => await db.Characters.AnyAsync(c => c.Name == name, cancellationToken).ConfigureAwait(false);

    public async Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default)
        => await db.Characters.CountAsync(c => c.AccountId == accountId, cancellationToken).ConfigureAwait(false);

    public async Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default)
    {
        db.Characters.Add(character);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return character;
    }

    public async Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default)
    {
        CharacterRecord? character = await db.Characters
            .FirstOrDefaultAsync(c => c.Id == id && c.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (character is null)
        {
            return false;
        }

        db.Characters.Remove(character);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

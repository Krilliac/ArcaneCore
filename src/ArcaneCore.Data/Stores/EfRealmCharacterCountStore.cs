using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// EF Core implementation of <see cref="IRealmCharacterCountStore"/> over the <c>realmcharacters</c> table (SQLite, MariaDB, PostgreSQL
/// alike: set-based updates and one insert, no provider-specific SQL). Not thread-safe: one scoped <see cref="AuthDbContext"/> per use.
/// </summary>
public sealed class EfRealmCharacterCountStore(AuthDbContext db) : IRealmCharacterCountStore
{
    public async Task<IReadOnlyDictionary<int, int>> GetCountsAsync(int accountId, CancellationToken cancellationToken = default)
        => await db.Set<RealmCharacterCountRow>().AsNoTracking()
            .Where(r => r.AccountId == accountId)
            .ToDictionaryAsync(r => r.RealmId, r => r.NumChars, cancellationToken).ConfigureAwait(false);

    public async Task SetCountAsync(int realmId, int accountId, int count, CancellationToken cancellationToken = default)
    {
        int value = Math.Max(0, count);
        if (await TryUpdateAsync(realmId, accountId, value, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            db.Set<RealmCharacterCountRow>().Add(new RealmCharacterCountRow { RealmId = realmId, AccountId = accountId, NumChars = value });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another writer inserted the row between the check and the insert (two sessions of one account): update theirs.
            db.ChangeTracker.Clear();
            if (!await TryUpdateAsync(realmId, accountId, value, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>True when the row exists (and now holds <paramref name="value"/>); false when there is no row to update.</summary>
    private async Task<bool> TryUpdateAsync(int realmId, int accountId, int value, CancellationToken cancellationToken)
    {
        int changed = await db.Set<RealmCharacterCountRow>()
            .Where(r => r.RealmId == realmId && r.AccountId == accountId && r.NumChars != value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.NumChars, value), cancellationToken).ConfigureAwait(false);
        if (changed > 0)
        {
            return true;
        }

        // Nothing differed: either the row already holds the value, or there is no row.
        return await db.Set<RealmCharacterCountRow>().AsNoTracking()
            .AnyAsync(r => r.RealmId == realmId && r.AccountId == accountId, cancellationToken).ConfigureAwait(false);
    }
}

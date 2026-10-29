using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// EF Core implementation of <see cref="IRealmStatusStore"/> over the <c>realmlist</c> table (SQLite, MariaDB, PostgreSQL alike:
/// a tracked read and a SaveChanges of the changed columns, no provider-specific SQL).
/// </summary>
public sealed class EfRealmStatusStore(AuthDbContext db) : IRealmStatusStore
{
    public Task<bool> SetOnlineAsync(int realmId, CancellationToken cancellationToken = default)
        => UpdateAsync(realmId, static realm =>
        {
            realm.Flags &= ~RealmFlags.Offline;
            realm.Population = 0f;
        }, cancellationToken);

    public Task<bool> SetOfflineAsync(int realmId, CancellationToken cancellationToken = default)
        => UpdateAsync(realmId, static realm => realm.Flags |= RealmFlags.Offline, cancellationToken);

    public Task<bool> SetPopulationAsync(int realmId, float population, CancellationToken cancellationToken = default)
        => UpdateAsync(realmId, realm => realm.Population = population, cancellationToken);

    private async Task<bool> UpdateAsync(int realmId, Action<RealmEntry> change, CancellationToken cancellationToken)
    {
        RealmEntry? realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == realmId, cancellationToken).ConfigureAwait(false);
        if (realm is null)
        {
            return false;
        }

        change(realm);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

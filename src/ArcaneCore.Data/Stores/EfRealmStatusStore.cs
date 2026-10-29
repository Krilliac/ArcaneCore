using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// EF Core implementation of <see cref="IRealmStatusStore"/> over the <c>realmlist</c> table (SQLite, MariaDB, PostgreSQL alike: set-based
/// <c>ExecuteUpdate</c> of exactly the named columns, no provider-specific SQL, and no read-modify-write of the whole row, so a
/// hand edit of another column between two heartbeats is never overwritten). The flags are a bit set the database cannot edit
/// portably, so a flags change reads them, computes the new value and updates only when the row still holds what was read.
/// One scoped <see cref="AuthDbContext"/> per use.
/// </summary>
public sealed class EfRealmStatusStore(AuthDbContext db) : IRealmStatusStore
{
    private const int FlagsAttempts = 4;

    public Task<bool> SetStartingAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
    {
        DateTime beat = Utc(heartbeatUtc);
        return ChangeFlagsAsync(
            realmId,
            static flags => flags | RealmFlags.Offline,
            (rows, flags, ct) => rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Flags, flags)
                .SetProperty(r => r.State, RealmRuntimeState.Starting)
                .SetProperty(r => r.HeartbeatUtc, (DateTime?)beat), ct),
            cancellationToken);
    }

    public Task<bool> SetOnlineAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
    {
        DateTime beat = Utc(heartbeatUtc);
        return ChangeFlagsAsync(
            realmId,
            static flags => flags & ~RealmFlags.Offline,
            (rows, flags, ct) => rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Flags, flags)
                .SetProperty(r => r.Population, 0f)
                .SetProperty(r => r.State, RealmRuntimeState.Online)
                .SetProperty(r => r.HeartbeatUtc, (DateTime?)beat), ct),
            cancellationToken);
    }

    public async Task<bool> SetStateAsync(int realmId, RealmRuntimeState state, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
    {
        DateTime beat = Utc(heartbeatUtc);
        return await db.Realms.Where(r => r.Id == realmId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, state).SetProperty(r => r.HeartbeatUtc, (DateTime?)beat), cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    public async Task<bool> BeatAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
    {
        DateTime beat = Utc(heartbeatUtc);
        return await db.Realms.Where(r => r.Id == realmId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.HeartbeatUtc, (DateTime?)beat), cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    public Task<bool> SetStoppedAsync(int realmId, CancellationToken cancellationToken = default)
        => ChangeFlagsAsync(
            realmId,
            static flags => flags | RealmFlags.Offline,
            (rows, flags, ct) => rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Flags, flags)
                .SetProperty(r => r.State, RealmRuntimeState.Stopped), ct),
            cancellationToken);

    public async Task<bool> SetPopulationAsync(int realmId, float population, CancellationToken cancellationToken = default)
        => await db.Realms.Where(r => r.Id == realmId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Population, population), cancellationToken)
            .ConfigureAwait(false) > 0;

    /// <summary>
    /// Read the flags, compute <paramref name="change"/>, and apply it to the row only while it still holds the flags that were read;
    /// a concurrent flags edit makes the next attempt re-read. False when no row has the id.
    /// </summary>
    private async Task<bool> ChangeFlagsAsync(
        int realmId,
        Func<RealmFlags, RealmFlags> change,
        Func<IQueryable<RealmEntry>, RealmFlags, CancellationToken, Task<int>> apply,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < FlagsAttempts; attempt++)
        {
            List<RealmFlags> found = await db.Realms.AsNoTracking()
                .Where(r => r.Id == realmId).Select(r => r.Flags).Take(1)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (found.Count == 0)
            {
                return false;
            }

            RealmFlags current = found[0];
            int changed = await apply(db.Realms.Where(r => r.Id == realmId && r.Flags == current), change(current), cancellationToken).ConfigureAwait(false);
            if (changed > 0)
            {
                return true;
            }
        }

        throw new InvalidOperationException($"the flags of realm {realmId} kept changing while they were being updated");
    }

    // The columns hold UTC; Npgsql refuses a timestamptz value that is not Kind=Utc.
    private static DateTime Utc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

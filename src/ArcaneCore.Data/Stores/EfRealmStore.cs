using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>EF Core implementation of <see cref="IRealmStore"/>.</summary>
public sealed class EfRealmStore(ArcaneCoreDbContext db) : IRealmStore
{
    public async Task<IReadOnlyList<RealmEntry>> GetRealmsAsync(CancellationToken cancellationToken = default)
    {
        return await db.Realms
            .AsNoTracking()
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

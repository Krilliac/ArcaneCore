using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.WorldData.Pools;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Pools;

/// <summary>Which spawn table a pool catalog is built for.</summary>
public enum PoolSpawnKind
{
    Creature,
    GameObject,
}

/// <summary>Reads the pool tables of the world database into a <see cref="PoolCatalog"/> (the creature and game object stores call it).</summary>
public static class PoolStore
{
    /// <summary>
    /// The pools of one kind, checked against <paramref name="spawns"/> (guid to entry and map) as cmangos PoolManager::LoadFromDB checks them.
    /// </summary>
    public static async Task<PoolCatalog> LoadAsync(
        WorldDbContext db, PoolSpawnKind kind, IReadOnlyDictionary<uint, (uint Entry, uint MapId)> spawns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(spawns);
        List<PoolTemplateRow> templates = await db.Set<PoolTemplateRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (templates.Count == 0
            && !await db.Set<PoolCreatureRow>().AnyAsync(cancellationToken).ConfigureAwait(false)
            && !await db.Set<PoolGameObjectRow>().AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return PoolCatalog.Empty;
        }

        List<PoolPoolRow> links = await db.Set<PoolPoolRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PoolSpawnLink> guidRows;
        List<PoolSpawnLink> entryRows;
        if (kind == PoolSpawnKind.Creature)
        {
            guidRows = await db.Set<PoolCreatureRow>().AsNoTracking().Select(r => new PoolSpawnLink(r.Guid, r.PoolEntry, r.Chance, r.Description))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            entryRows = await db.Set<PoolCreatureTemplateRow>().AsNoTracking().Select(r => new PoolSpawnLink(r.Id, r.PoolEntry, r.Chance, r.Description))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            guidRows = await db.Set<PoolGameObjectRow>().AsNoTracking().Select(r => new PoolSpawnLink(r.Guid, r.PoolEntry, r.Chance, r.Description))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            entryRows = await db.Set<PoolGameObjectTemplateRow>().AsNoTracking().Select(r => new PoolSpawnLink(r.Id, r.PoolEntry, r.Chance, r.Description))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        return PoolCatalog.Build(
            templates.Select(t => new PoolTemplateData(t.Entry, t.MaxLimit, t.Description)),
            guidRows,
            entryRows,
            links.Select(l => new PoolPoolLink(l.PoolId, l.MotherPool, l.Chance, l.Description)),
            spawns);
    }
}

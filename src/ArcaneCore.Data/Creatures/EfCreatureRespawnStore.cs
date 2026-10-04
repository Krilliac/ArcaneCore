using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Instances;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Creatures;

/// <summary>
/// EF Core implementation of <see cref="ICreatureRespawnStore"/> over the characters database. Meant for one writer at a time (the world's
/// respawn queue): an upsert reads the key and updates or inserts it, so two concurrent writers of one key could both insert. Every write is one
/// transaction; nothing here relies on advisory locks, provider-specific SQL or the case of an identifier.
/// </summary>
public sealed class EfCreatureRespawnStore(CharacterDbContext db) : ICreatureRespawnStore
{
    public async Task<IReadOnlyList<CreatureRespawnRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // vmangos keeps only times in the future (t > GetGameTime, MapPersistentStateMgr.cpp:90): the others are deleted, not just skipped.
        await db.Set<CreatureRespawnRow>().Where(r => r.RespawnTime <= nowUnixSeconds).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // A dungeon instance that no longer exists must not hand its timers to a later instance that reuses the id (the same guard
        // EfLootStateStore applies to chest loot).
        await db.Set<CreatureRespawnRow>()
            .Where(r => r.InstanceId != 0 && !db.Set<InstanceRow>().Any(i => i.Id == r.InstanceId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        List<CreatureRespawnRow> rows = await db.Set<CreatureRespawnRow>().AsNoTracking()
            .OrderBy(r => r.InstanceId).ThenBy(r => r.SpawnGuid)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new CreatureRespawnRecord(r.MapId, (uint)r.InstanceId, r.SpawnGuid, r.RespawnTime))];
    }

    public async Task SaveAsync(
        IReadOnlyCollection<CreatureRespawnRecord> upserts, IReadOnlyCollection<CreatureRespawnKey> deletes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);
        foreach (CreatureRespawnRecord record in upserts)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(upserts));
        }

        if (upserts.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        // A later record of the same key wins; a deleted key is not written at all.
        var latest = new Dictionary<(int Instance, uint Guid), CreatureRespawnRecord>();
        foreach (CreatureRespawnRecord record in upserts)
        {
            latest[(checked((int)record.InstanceId), record.SpawnGuid)] = record;
        }

        foreach (CreatureRespawnKey key in deletes)
        {
            latest.Remove((checked((int)key.InstanceId), key.SpawnGuid));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (CreatureRespawnKey key in deletes)
            {
                int instance = checked((int)key.InstanceId);
                await db.Set<CreatureRespawnRow>().Where(r => r.InstanceId == instance && r.SpawnGuid == key.SpawnGuid)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (((int instance, uint guid), CreatureRespawnRecord record) in latest)
            {
                CreatureRespawnRow? row = await db.Set<CreatureRespawnRow>()
                    .FirstOrDefaultAsync(r => r.InstanceId == instance && r.SpawnGuid == guid, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    db.Set<CreatureRespawnRow>().Add(new CreatureRespawnRow { InstanceId = instance, SpawnGuid = guid, MapId = record.MapId, RespawnTime = record.RespawnTime });
                }
                else
                {
                    row.MapId = record.MapId;
                    row.RespawnTime = record.RespawnTime;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
    {
        int id = checked((int)instanceId);
        await db.Set<CreatureRespawnRow>().Where(r => r.InstanceId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

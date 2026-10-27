using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Creatures;

/// <summary>
/// EF Core implementation of <see cref="ICreatureRespawnStore"/> over the characters database. Meant for one writer at a time (the world's
/// respawn queue): an upsert reads the key and updates or inserts it, so two concurrent writers of one key could both insert. Every write is one
/// transaction; nothing here relies on advisory locks or the case of an identifier. On SQLite the upsert is one
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> per row (<see cref="SqliteRowCommand"/>); other providers read, then update or insert.
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

        // All in-memory grouping happens before the transaction opens, so the write lock is held only for the statements themselves.
        Dictionary<int, List<uint>> deleteGuids = [];
        foreach (CreatureRespawnKey key in deletes)
        {
            int instance = checked((int)key.InstanceId);
            if (!deleteGuids.TryGetValue(instance, out List<uint>? guids))
            {
                deleteGuids[instance] = guids = [];
            }

            guids.Add(key.SpawnGuid);
        }

        Dictionary<int, List<CreatureRespawnRecord>> upsertsByInstance = [];
        foreach (((int instance, _), CreatureRespawnRecord record) in latest)
        {
            if (!upsertsByInstance.TryGetValue(instance, out List<CreatureRespawnRecord>? list))
            {
                upsertsByInstance[instance] = list = [];
            }

            list.Add(record);
        }

        // Commit per chunk so SQLite/PostgreSQL write locks and EF change-tracker state stay bounded per chunk.
        try
        {
            foreach ((int instance, List<uint> guids) in deleteGuids)
            {
                foreach (uint[] part in guids.Distinct().Chunk(ChunkSize))
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    List<uint> chunk = [.. part]; // a List: C# 14 would bind an array's Contains to the span overload, which EF Core 9 cannot translate (EF 10 can)
                    await db.Set<CreatureRespawnRow>().Where(r => r.InstanceId == instance && chunk.Contains(r.SpawnGuid))
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            foreach ((int instance, List<CreatureRespawnRecord> records) in upsertsByInstance)
            {
                foreach (CreatureRespawnRecord[] chunk in records.Chunk(ChunkSize))
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    if (db.Database.IsSqlite())
                    {
                        // SQLite: one raw INSERT ... ON CONFLICT DO UPDATE per row instead of a tracked read, update or insert and SaveChanges,
                        // so the write lock covers only the statements (the tracked form held it 2-10 s on a starved live host).
                        using (SqliteRowCommand upsert = SqliteRowCommand.Upsert(db, typeof(CreatureRespawnRow),
                            [nameof(CreatureRespawnRow.InstanceId), nameof(CreatureRespawnRow.SpawnGuid)],
                            [nameof(CreatureRespawnRow.MapId), nameof(CreatureRespawnRow.RespawnTime)]))
                        {
                            foreach (CreatureRespawnRecord record in chunk)
                            {
                                await upsert.ExecuteAsync([instance, record.SpawnGuid, record.MapId, record.RespawnTime], cancellationToken).ConfigureAwait(false);
                            }
                        }

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        List<uint> guids = [.. chunk.Select(c => c.SpawnGuid)];
                        Dictionary<uint, CreatureRespawnRow> existing = (await db.Set<CreatureRespawnRow>()
                            .Where(r => r.InstanceId == instance && guids.Contains(r.SpawnGuid))
                            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.SpawnGuid);
                        foreach (CreatureRespawnRecord record in chunk)
                        {
                            if (existing.TryGetValue(record.SpawnGuid, out CreatureRespawnRow? row))
                            {
                                row.MapId = record.MapId;
                                row.RespawnTime = record.RespawnTime;
                            }
                            else
                            {
                                db.Set<CreatureRespawnRow>().Add(new CreatureRespawnRow { InstanceId = instance, SpawnGuid = record.SpawnGuid, MapId = record.MapId, RespawnTime = record.RespawnTime });
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
            }
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Keys per set-based statement: bounds statement size and per-statement work (SQLite and PostgreSQL pass the list as one parameter; 500 also stays far under every per-key parameter limit).</summary>
    private const int ChunkSize = 500;

    public async Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
    {
        int id = checked((int)instanceId);
        await CharacterWrites.InTransactionAsync(
            db, () => db.Set<CreatureRespawnRow>().Where(r => r.InstanceId == id).ExecuteDeleteAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }
}

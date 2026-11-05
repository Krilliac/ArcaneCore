using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Instances;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>The columns every respawn table has (<c>creature_respawn</c>, <c>gameobject_respawn</c>): a spawn guid within one map instance, its map and the unix second it respawns at.</summary>
public interface IRespawnRow
{
    int InstanceId { get; set; }

    uint SpawnGuid { get; set; }

    uint MapId { get; set; }

    /// <summary>Unix seconds.</summary>
    long RespawnTime { get; set; }
}

/// <summary>
/// EF Core implementation of <see cref="IRespawnStore{TRecord, TKey}"/> over the characters database, shared by the creature and game object tables
/// (<typeparamref name="TRow"/> names the table). Meant for one writer at a time (the world's
/// respawn queue): an upsert reads the key and updates or inserts it, so two concurrent writers of one key could both insert. Every write is one
/// transaction; nothing here relies on advisory locks or the case of an identifier. On SQLite the upsert is one
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> per row (<see cref="SqliteRowCommand"/>); other providers read, then update or insert. Queries address columns
/// through <c>EF.Property</c> by the <see cref="IRespawnRow"/> names, so they translate for any row type of the model.
/// </summary>
public abstract class EfRespawnStore<TRow, TRecord, TKey>(CharacterDbContext db, IRealmLeaseFenceSource? leaseFences)
    : IRespawnStore<TRecord, TKey>
    where TRow : class, IRespawnRow, new()
    where TRecord : class, IRespawnRecord<TRecord>
    where TKey : struct, IRespawnKey<TKey>
{
    public async Task<IReadOnlyList<TRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // vmangos keeps only times in the future (t > GetGameTime: MapPersistentState::SaveGORespawnTime, MapPersistentStateMgr.cpp:112; the creature save, :90; Load*RespawnTimes delete the expired rows, :1070-1100 and :1152): the others are deleted, not just skipped.
        // A worker owns only its maps (Cluster:Worker:OwnedMaps): another worker's rows are its to expire (writer-lock design section 4, row 3), so the delete is limited to the owned maps. Realm and
        // Combined own every map and the delete is unchanged.
        IQueryable<TRow> expired = db.Set<TRow>().Where(r => EF.Property<long>(r, nameof(IRespawnRow.RespawnTime)) <= nowUnixSeconds);
        OwnedMapSet owned = leaseFences?.OwnedMaps ?? OwnedMapSet.All;
        if (!owned.OwnsEverything)
            expired = expired.Where(owned.RowFilter<TRow>(nameof(IRespawnRow.MapId), nameof(IRespawnRow.InstanceId)));
        await expired.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // A dungeon instance that no longer exists must not hand its timers to a later instance that reuses the id (the same guard
        // EfLootStateStore applies to chest loot). In a cluster only the Instances lease holder deletes orphans (writer-lock design section 4, row 3): another
        // worker may have committed an instance row this node has not seen. A non-holder loads read-only; the holder's delete is verified against the lease row.
        if (LeaseFencedWrite.IsHolder(leaseFences, RealmAuthorityScopes.Instances))
        {
            if (leaseFences?.FenceFor(RealmAuthorityScopes.Instances) is { } fence)
            {
                await LeaseFencedWrite.EnsureHeldAsync(db, fence, cancellationToken).ConfigureAwait(false);
            }

            await db.Set<TRow>()
                .Where(r => EF.Property<int>(r, nameof(IRespawnRow.InstanceId)) != 0 && !db.Set<InstanceRow>().Any(i => i.Id == EF.Property<int>(r, nameof(IRespawnRow.InstanceId))))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        List<TRow> rows = await db.Set<TRow>().AsNoTracking()
            .OrderBy(r => EF.Property<int>(r, nameof(IRespawnRow.InstanceId))).ThenBy(r => EF.Property<uint>(r, nameof(IRespawnRow.SpawnGuid)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => TRecord.Create(r.MapId, (uint)r.InstanceId, r.SpawnGuid, r.RespawnTime))];
    }

    public async Task SaveAsync(
        IReadOnlyCollection<TRecord> upserts, IReadOnlyCollection<TKey> deletes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);
        foreach (TRecord record in upserts)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(upserts));
        }

        if (upserts.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        // A later record of the same key wins; a deleted key is not written at all.
        var latest = new Dictionary<(int Instance, uint Guid), TRecord>();
        foreach (TRecord record in upserts)
        {
            latest[(checked((int)record.InstanceId), record.SpawnGuid)] = record;
        }

        foreach (TKey key in deletes)
        {
            latest.Remove((checked((int)key.InstanceId), key.SpawnGuid));
        }

        // All in-memory grouping happens before the transaction opens, so the write lock is held only for the statements themselves.
        Dictionary<int, List<uint>> deleteGuids = [];
        foreach (TKey key in deletes)
        {
            int instance = checked((int)key.InstanceId);
            if (!deleteGuids.TryGetValue(instance, out List<uint>? guids))
            {
                deleteGuids[instance] = guids = [];
            }

            guids.Add(key.SpawnGuid);
        }

        Dictionary<int, List<TRecord>> upsertsByInstance = [];
        foreach (((int instance, _), TRecord record) in latest)
        {
            if (!upsertsByInstance.TryGetValue(instance, out List<TRecord>? list))
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
                    await db.Set<TRow>().Where(r => EF.Property<int>(r, nameof(IRespawnRow.InstanceId)) == instance && chunk.Contains(EF.Property<uint>(r, nameof(IRespawnRow.SpawnGuid))))
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            foreach ((int instance, List<TRecord> records) in upsertsByInstance)
            {
                foreach (TRecord[] chunk in records.Chunk(ChunkSize))
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    if (db.Database.IsSqlite())
                    {
                        // SQLite: one raw INSERT ... ON CONFLICT DO UPDATE per row instead of a tracked read, update or insert and SaveChanges,
                        // so the write lock covers only the statements (the tracked form held it 2-10 s on a starved live host).
                        using (SqliteRowCommand upsert = SqliteRowCommand.Upsert(db, typeof(TRow),
                            [nameof(IRespawnRow.InstanceId), nameof(IRespawnRow.SpawnGuid)],
                            [nameof(IRespawnRow.MapId), nameof(IRespawnRow.RespawnTime)]))
                        {
                            foreach (TRecord record in chunk)
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
                        Dictionary<uint, TRow> existing = (await db.Set<TRow>()
                            .Where(r => EF.Property<int>(r, nameof(IRespawnRow.InstanceId)) == instance && guids.Contains(EF.Property<uint>(r, nameof(IRespawnRow.SpawnGuid))))
                            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.SpawnGuid);
                        foreach (TRecord record in chunk)
                        {
                            if (existing.TryGetValue(record.SpawnGuid, out TRow? row))
                            {
                                row.MapId = record.MapId;
                                row.RespawnTime = record.RespawnTime;
                            }
                            else
                            {
                                db.Set<TRow>().Add(new TRow { InstanceId = instance, SpawnGuid = record.SpawnGuid, MapId = record.MapId, RespawnTime = record.RespawnTime });
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
            db, () => db.Set<TRow>().Where(r => EF.Property<int>(r, nameof(IRespawnRow.InstanceId)) == id).ExecuteDeleteAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }
}

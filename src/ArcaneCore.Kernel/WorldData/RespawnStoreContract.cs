namespace ArcaneCore.Kernel.WorldData;

/// <summary>
/// The shape shared by the durable respawn records of creature and game object spawns: a spawn guid within one map instance and the unix
/// second it respawns at. Lets one queue and one store implementation serve both kinds.
/// </summary>
public interface IRespawnRecord<TSelf> where TSelf : class, IRespawnRecord<TSelf>
{
    uint MapId { get; }

    /// <summary>0 for the shared copy of a map.</summary>
    uint InstanceId { get; }

    uint SpawnGuid { get; }

    /// <summary>Unix seconds.</summary>
    long RespawnTime { get; }

    static abstract TSelf Create(uint mapId, uint instanceId, uint spawnGuid, long respawnTime);
}

/// <summary>The key of an <see cref="IRespawnRecord{TSelf}"/>: a spawn guid within one map instance.</summary>
public interface IRespawnKey<TSelf> where TSelf : struct, IRespawnKey<TSelf>
{
    uint InstanceId { get; }

    uint SpawnGuid { get; }

    static abstract TSelf Create(uint instanceId, uint spawnGuid);
}

/// <summary>
/// Durable respawn times (the characters database). Used by one writer at a time (the world's respawn queue, <c>RespawnWriteQueue</c> in the World project: the place to edit queueing, batching and retries; <c>EfRespawnStore</c> in the Data project is the place to edit the SQL); callers never await it from the
/// world thread. The creature and game object stores add nothing to this contract.
/// </summary>
public interface IRespawnStore<TRecord, TKey>
    where TRecord : class, IRespawnRecord<TRecord>
    where TKey : struct, IRespawnKey<TKey>
{
    /// <summary>
    /// Every stored respawn time that is still in the future at <paramref name="nowUnixSeconds"/>. Rows whose time has passed, and rows of
    /// a dungeon instance that no longer exists, are deleted first (vmangos keeps only future times; the kind's own store cites the source).
    /// </summary>
    Task<IReadOnlyList<TRecord>> LoadAsync(long nowUnixSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply <paramref name="upserts"/> (insert or replace by key) and <paramref name="deletes"/>. A key in both is deleted. The call is NOT one
    /// transaction: <c>EfRespawnStore</c> commits per chunk (500 keys of one instance; the deletes first, then the upserts), so a failure part-way leaves the
    /// earlier chunks committed. That is safe because every entry is an absolute value or a delete: the caller retries the whole call and the repeat changes
    /// nothing already written. The replace semantics are the store's own upsert, not provider SQL (<c>REPLACE INTO</c> does not exist on PostgreSQL).
    /// </summary>
    Task SaveAsync(IReadOnlyCollection<TRecord> upserts, IReadOnlyCollection<TKey> deletes, CancellationToken cancellationToken = default);

    /// <summary>Remove every respawn time of a dungeon instance (it was reset or deleted).</summary>
    Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default);
}

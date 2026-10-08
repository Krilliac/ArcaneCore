namespace ArcaneCore.Kernel.Instances;

/// <summary>
/// One persistent dungeon/raid instance (vmangos characters DB <c>instance</c>: id, map,
/// reset_time, data). <see cref="ResetTime"/> is a Unix time in seconds: the scheduled global reset
/// for a raid, the earliest reset of a normal dungeon (it resets only while empty).
/// </summary>
public sealed record InstanceRecord(uint Id, uint MapId, long ResetTime, string? Data = null);

/// <summary>A character's bind to an instance (vmangos <c>character_instance</c>: guid, instance, permanent).</summary>
public sealed record CharacterInstanceBindRecord(int CharacterId, uint InstanceId, bool Permanent);

/// <summary>
/// A group's bind to an instance, stored under the character id of the group's leader (vmangos <c>group_instance</c>: leader_guid,
/// instance, permanent). Only permanent group binds are stored here: groups themselves are not stored yet, and a temporary bind of a
/// group that no longer exists would only keep its instance alive.
/// </summary>
public sealed record GroupInstanceBindRecord(int LeaderCharacterId, uint InstanceId, bool Permanent);

/// <summary>The next global reset of a raid map (vmangos <c>instance_reset</c>: mapid, resettime), Unix seconds.</summary>
public sealed record InstanceResetRecord(uint MapId, long ResetTime);

/// <summary>
/// The dungeon instance a character last entered (vmangos keeps it on the character row as
/// <c>characters.instance_id</c>). Login compares it with the character's current bind: an
/// instance reset meanwhile sends the character to the entrance.
/// </summary>
public sealed record CharacterLastInstanceRecord(int CharacterId, uint MapId, uint InstanceId);

/// <summary>Everything the instance system loads at startup.</summary>
public sealed record InstanceStoreSnapshot(
    IReadOnlyList<InstanceRecord> Instances,
    IReadOnlyList<CharacterInstanceBindRecord> Binds,
    IReadOnlyList<InstanceResetRecord> ResetTimes,
    IReadOnlyList<CharacterLastInstanceRecord> LastInstances)
{
    public static InstanceStoreSnapshot Empty { get; } = new([], [], [], []);

    /// <summary>The stored group binds (vmangos <c>group_instance</c>); empty when the store has none.</summary>
    public IReadOnlyList<GroupInstanceBindRecord> GroupBinds { get; init; } = [];
}

/// <summary>
/// Storage of instance saves and binds (characters database; docs/integration/instances.md).
/// Every write is idempotent so a retried or replayed write converges.
/// </summary>
public interface IInstanceStore
{
    /// <summary>
    /// Load every instance, bind, raid reset time and last-instance row. Rows of characters that
    /// no longer exist and binds of missing instances are deleted first (vmangos
    /// <c>MapPersistentStateManager::CleanupInstances</c>).
    /// </summary>
    Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert or update an instance row.</summary>
    Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default);

    /// <summary>Update only the script data of an existing instance (vmangos InstanceData::SaveToDB).</summary>
    Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default);

    /// <summary>Delete an instance with all its character binds and last-instance references; a stored body in it keeps its place but its instance becomes 0.</summary>
    Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default);

    /// <summary>Insert or update a character bind (one bind per character and instance).</summary>
    Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default);

    /// <summary>Delete a character bind (a missing bind is a no-op).</summary>
    Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default);

    /// <summary>Insert or update a stored group bind (one per leader and instance).</summary>
    Task SaveGroupBindAsync(GroupInstanceBindRecord bind, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Delete a stored group bind (a missing bind is a no-op).</summary>
    Task DeleteGroupBindAsync(int leaderCharacterId, uint instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Insert or update a raid map's next global reset time.</summary>
    Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default);

    /// <summary>Insert or update the instance a character last entered.</summary>
    Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete every bind and last-instance row of a character id that has no <c>characters</c> row
    /// (the queued removal after a character deletion; a recreated character's rows are kept).
    /// </summary>
    Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}

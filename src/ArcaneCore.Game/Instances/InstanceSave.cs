using ArcaneCore.Kernel.WorldData;


namespace ArcaneCore.Game.Instances;

/// <summary>
/// One persistent dungeon or raid instance (vmangos <c>DungeonPersistentState</c>): its id, map,
/// reset time and who is bound to it. A save outlives its <see cref="Maps.Map"/>: an empty
/// instance map is unloaded and recreated with the same id while binds remain.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class InstanceSave
{
    internal InstanceSave(uint instanceId, MapTemplate template, long resetTime)
    {
        InstanceId = instanceId;
        Template = template;
        ResetTime = resetTime;
    }

    public uint InstanceId { get; }

    public MapTemplate Template { get; }

    public uint MapId => Template.Entry;

    /// <summary>Unix seconds: the scheduled global reset (raid), the earliest reset while empty (normal dungeon).</summary>
    public long ResetTime { get; internal set; }

    /// <summary>
    /// False once anyone is permanently bound (vmangos <c>Player::BindToInstance</c>:
    /// <c>if (permanent) state->SetCanReset(false)</c>); such an instance only resets globally.
    /// </summary>
    public bool CanReset { get; internal set; } = true;

    /// <summary>Characters bound to the instance (online or not).</summary>
    public IReadOnlyCollection<ObjectGuid> BoundPlayers => Players;

    /// <summary>Ids of the groups bound to the instance.</summary>
    public IReadOnlyCollection<uint> BoundGroups => Groups;

    public bool HasBinds => Players.Count > 0 || Groups.Count > 0 || StoredGroupLeaders.Count > 0;

    /// <summary>
    /// The instance script's save string (vmangos <c>instance.data</c>, written by InstanceData::SaveToDB, Maps/InstanceData.cpp:24-40, and read
    /// back by Map::CreateInstanceData, Maps/Map.cpp:2001-2022), or null when the script never saved. It lives with the save, so an instance
    /// map that is unloaded and created again loads it. It goes to <see cref="IInstancePersistence.InstanceDataSaved"/>
    /// and the characters database for startup restoration.
    /// </summary>
    public string? Data { get; internal set; }

    /// <summary>Whether this save has been reset or deleted (its id is never handed out again).</summary>
    public bool IsDeleted { get; internal set; }

    /// <summary>Normal dungeons: the reset is armed (the instance is empty; vmangos <c>SetResetSchedule(true)</c>).</summary>
    internal bool ResetScheduled { get; set; }

    internal HashSet<ObjectGuid> Players { get; } = [];

    internal HashSet<uint> Groups { get; } = [];

    /// <summary>
    /// Leaders (character ids) of a stored permanent group bind to this save that no group has taken back yet (vmangos
    /// <c>group_instance</c> rows; <see cref="InstanceManager.RestoreStoredGroupBinds"/>). They keep the save alive like a bind.
    /// </summary>
    internal HashSet<uint> StoredGroupLeaders { get; } = [];

    public override string ToString() => $"instance {InstanceId} of map {MapId} ({Template.Name})";
}

/// <summary>A bind of a character or group to an instance (vmangos <c>InstancePlayerBind</c> / <c>InstanceGroupBind</c>).</summary>
public readonly record struct InstanceBind(InstanceSave Save, bool Permanent);

/// <summary>
/// Where the instance system's state changes go (the world feature queues them to the characters
/// database). Characters are identified by their GUID counter. Implementations must not touch
/// world state; calls arrive on the world thread in order.
/// </summary>
public interface IInstancePersistence
{
    void InstanceSaved(InstanceSave save);

    void InstanceDeleted(uint instanceId);

    void PlayerBound(uint characterId, uint instanceId, bool permanent);

    void PlayerUnbound(uint characterId, uint instanceId);

    void RaidResetTimeChanged(uint mapId, long resetTime);

    void PlayerEnteredInstance(uint characterId, uint mapId, uint instanceId);

    /// <summary>
    /// The instance script saved its state (<see cref="InstanceSave.Data"/>; vmangos InstanceData::SaveToDB: <c>UPDATE instance SET data</c>).
    /// Implementations with a store persist the data string.
    /// </summary>
    void InstanceDataSaved(InstanceSave save);

    /// <summary>A group became permanently bound; stored under its leader's character id (vmangos Group::BindToInstance → <c>group_instance</c>).</summary>
    void GroupBound(uint leaderCharacterId, uint instanceId, bool permanent)
    {
    }

    /// <summary>A stored group bind ended (vmangos Group::UnbindInstance, the leader change, the disband).</summary>
    void GroupUnbound(uint leaderCharacterId, uint instanceId)
    {
    }
}

/// <summary>Discards every change (no characters database).</summary>
public sealed class NullInstancePersistence : IInstancePersistence
{
    public static NullInstancePersistence Instance { get; } = new();

    public void InstanceSaved(InstanceSave save)
    {
    }

    public void InstanceDataSaved(InstanceSave save)
    {
    }

    public void InstanceDeleted(uint instanceId)
    {
    }

    public void PlayerBound(uint characterId, uint instanceId, bool permanent)
    {
    }

    public void PlayerUnbound(uint characterId, uint instanceId)
    {
    }

    public void RaidResetTimeChanged(uint mapId, long resetTime)
    {
    }

    public void PlayerEnteredInstance(uint characterId, uint mapId, uint instanceId)
    {
    }
}

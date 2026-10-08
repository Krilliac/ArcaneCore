using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>
/// The encounter states every instance script uses (ScriptDev2 <c>EncounterState</c>: vmangos AI/ScriptedInstance.h:17-24,
/// mangos-classic AI/ScriptDevAI/include/sc_instance.h). EventAI ACTION_T_SET_INST_DATA rows write these values.
/// </summary>
public static class EncounterState
{
    public const uint NotStarted = 0;
    public const uint InProgress = 1;
    public const uint Fail = 2;
    public const uint Done = 3;
    public const uint Special = 4;
}

/// <summary>
/// The script state of one dungeon or raid instance map (vmangos <c>InstanceData</c>, Maps/InstanceData.h:36-88): all-purpose 32- and 64-bit
/// data by type (<see cref="GetData"/> / <see cref="SetData"/>, what EventAI SET_INST_DATA and SET_INST_DATA64 write), the creation and load
/// hooks, the creature and game object creation hooks, and the save string (<see cref="GetSaveData"/>, vmangos <c>Save()</c>) that
/// <see cref="SaveToDB"/> hands to the instance save.
/// <para>
/// Lifecycle (vmangos Map::CreateInstanceData, Maps/Map.cpp:1987-2037): <see cref="InstanceManager"/> creates the data when it creates the
/// instance map of a map that has a script (<see cref="InstanceScriptRegistry"/>), calls <see cref="Initialize"/>, then <see cref="Load"/> with
/// the save string the instance save holds, if any. The data is an <see cref="IMapUpdater"/> of its map, so anything on the map finds it with
/// <c>map.FindUpdater&lt;InstanceData&gt;()</c> (vmangos <c>Map::GetInstanceData</c>).
/// </para>
/// <para>Thread affinity: world thread (the map's).</para>
/// </summary>
public abstract class InstanceData : IMapUpdater
{
    protected InstanceData(Map instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Instance = instance;
    }

    /// <summary>The instance map (vmangos <c>InstanceData::instance</c>).</summary>
    public Map Instance { get; }

    /// <summary>Where <see cref="SaveToDB"/> sends the save string; <see cref="InstanceManager"/> stores it on the instance save.</summary>
    internal Action<InstanceData, string>? Saving { get; set; }

    /// <summary>The live quest journal's completed, unrewarded predicate; false until its world feature supplies it.</summary>
    internal Func<Player, uint, bool> QuestCompleteUnrewarded { get; set; } = static (_, _) => false;

    /// <summary>Diagnostics (the parts of a script that are not ported are logged at debug level).</summary>
    public ILogger Logger { get; internal set; } = NullLogger.Instance;

    /// <summary>vmangos <c>Initialize</c>: a fresh state (called for every creation, before <see cref="Load"/>).</summary>
    public virtual void Initialize()
    {
    }

    /// <summary>vmangos <c>Load(char const* data)</c>: restore the state from a save string <see cref="GetSaveData"/> produced.</summary>
    public virtual void Load(string data)
    {
    }

    /// <summary>vmangos <c>Save()</c>: the string the instance save keeps; null saves nothing.</summary>
    public virtual string? GetSaveData() => null;

    /// <summary>
    /// vmangos <c>InstanceData::SaveToDB</c> (Maps/InstanceData.cpp:24-40): the save string goes to the instance save (vmangos
    /// <c>UPDATE instance SET data</c>). Nothing happens when <see cref="GetSaveData"/> returns null.
    /// </summary>
    public void SaveToDB()
    {
        if (GetSaveData() is { } data)
        {
            Saving?.Invoke(this, data);
        }
    }

    /// <summary>vmangos <c>GetData</c>: all-purpose 32-bit data by type (0 when the script does not keep that type).</summary>
    public virtual uint GetData(uint type) => 0;

    /// <summary>vmangos <c>SetData</c>: all-purpose 32-bit data by type (what EventAI ACTION_T_SET_INST_DATA writes).</summary>
    public virtual void SetData(uint type, uint data)
    {
    }

    /// <summary>vmangos <c>GetData64</c>: all-purpose 64-bit data by type (0 when not kept).</summary>
    public virtual ulong GetData64(uint type) => 0;

    /// <summary>vmangos <c>SetData64</c>: all-purpose 64-bit data by type (what EventAI ACTION_T_SET_INST_DATA64 writes: a unit's GUID).</summary>
    public virtual void SetData64(uint type, ulong data)
    {
    }

    /// <summary>vmangos <c>IsEncounterInProgress</c>: used by the map's entry check (not consulted yet).</summary>
    public virtual bool IsEncounterInProgress => false;

    /// <summary>vmangos <c>OnCreatureCreate</c>: a creature was added to the instance map (grid load, summon).</summary>
    public virtual void OnCreatureCreate(Creature creature)
    {
    }

    /// <summary>ScriptDev2 instance encounter hook when a creature first enters combat.</summary>
    public virtual void OnCreatureEnterCombat(Creature creature)
    {
    }

    /// <summary>ScriptDev2 instance encounter hook when a creature evades.</summary>
    public virtual void OnCreatureEvade(Creature creature)
    {
    }

    /// <summary>ScriptDev2 InstanceData::OnCreatureDeath: a creature of this map died, after its AI death hook.</summary>
    public virtual void OnCreatureDeath(Creature creature)
    {
    }

    /// <summary>ScriptDev2 InstanceData::OnPlayerEnter, after the player joins this map.</summary>
    public virtual void OnPlayerEnter(Player player)
    {
    }

    /// <summary>ScriptDev2 instance hook after a creature respawns in the same map.</summary>
    public virtual void OnCreatureRespawn(Creature creature)
    {
    }

    /// <summary>vmangos <c>OnObjectCreate</c>: a game object was added to the instance map (grid load, summon); it is not visible yet.</summary>
    public virtual void OnObjectCreate(GameObject go)
    {
    }

    /// <summary>ScriptDev2 instance hook when a tracked game object respawns.</summary>
    public virtual void OnObjectSpawn(GameObject go)
    {
    }

    /// <summary>vmangos <c>Update</c>: every map tick.</summary>
    public virtual void Update(uint diffMs)
    {
    }

    void IMapUpdater.Update(Map map, uint diffMs) => Update(diffMs);

    void IMapUpdater.OnPlayerAdding(Map map, Player player) => OnPlayerEnter(player);

    void IMapUpdater.OnPlayerRemoved(Map map, Player player)
    {
    }
}

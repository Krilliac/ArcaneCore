using ArcaneCore.Game.Creatures;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>
/// Engine hooks of the instance scripts beyond vmangos' InstanceData: the creature despawn callback, hard-coded creature groups with their
/// group-despawn callback, the map's variables (with spawns gated on them) and the game-event / holiday queries (mangos-classic
/// InstanceData::OnCreatureDespawn and OnCreatureGroupDespawn, Map::GetVariableManager, IsHolidayActive).
/// </summary>
public abstract partial class InstanceData
{
    private readonly Dictionary<uint, int> _variables = [];
    private readonly Dictionary<uint, (uint VariableId, int Value)> _variableGatedSpawns = [];
    private readonly Dictionary<uint, CreatureGroupState> _groupOfSpawn = [];

    /// <summary>Whether a game event is running (vmangos <c>sGameEventMgr.IsActiveEvent</c>); false until the world wires the event state.</summary>
    internal Func<ushort, bool> GameEventActive { get; set; } = static _ => false;

    /// <summary>Whether an event of this client holiday id is running (<c>IsActiveHoliday</c>); false until the world wires the event state.</summary>
    internal Func<uint, bool> HolidayActive { get; set; } = static _ => false;

    /// <summary>mangos-classic <c>IsHolidayActive(HolidayIds)</c> (GameEventMgr.cpp:1148-1151) for a script: holiday 0 is never active.</summary>
    protected bool IsHolidayActive(uint holidayId) => holidayId != 0 && HolidayActive(holidayId);

    /// <summary>vmangos <c>sGameEventMgr.IsActiveEvent(eventId)</c> for a script.</summary>
    protected bool IsGameEventActive(ushort eventId) => GameEventActive(eventId);

    /// <summary>
    /// mangos-classic <c>InstanceData::OnCreatureDespawn</c> (called from Creature::RemoveCorpse, Entities/Creature.cpp:287-288): the corpse of
    /// a creature of this map is being removed (corpse decay, a forced despawn of a dead or living creature). The creature is dead but still
    /// on the map at its death position, so it can still cast a triggered spell.
    /// </summary>
    public virtual void OnCreatureDespawn(Creature creature)
    {
    }

    /// <summary>
    /// mangos-classic <c>InstanceData::OnCreatureGroupDespawn</c> (CreatureGroup::RemoveObject, Maps/SpawnGroup.cpp:512-523): the last living
    /// member of a group registered with <see cref="RegisterCreatureGroup"/> died or left the map. <paramref name="last"/> is that member.
    /// </summary>
    protected virtual void OnCreatureGroupDespawn(uint groupId, Creature last)
    {
    }

    /// <summary>
    /// A creature group by database spawn guids (the port does not import <c>spawn_group</c>; the scripts that need one name its members as
    /// the source's "Prototype, hardcoded" scripts do). A member counts while it is alive on the map; when the count drops to zero
    /// <see cref="OnCreatureGroupDespawn"/> runs, again after the group has come back and emptied again.
    /// </summary>
    protected void RegisterCreatureGroup(uint groupId, IEnumerable<uint> spawnGuids)
    {
        ArgumentNullException.ThrowIfNull(spawnGuids);
        var state = new CreatureGroupState(groupId);
        foreach (uint guid in spawnGuids)
        {
            _groupOfSpawn[guid] = state;
        }
    }

    /// <summary>mangos-classic <c>Map::GetVariableManager().GetVariable(id)</c>: 0 when never set.</summary>
    public int GetVariable(uint variableId) => _variables.GetValueOrDefault(variableId);

    /// <summary>
    /// mangos-classic <c>Map::GetVariableManager().SetVariable(id, value)</c>: the map's variable changes and every spawn gated on it with
    /// <see cref="GateCreatureSpawnOnVariable"/> is brought in line (spawned when its grid is loaded and the condition now holds, removed
    /// when it no longer does). Instance variables are not saved (the source's Initialize sets them afresh at every creation).
    /// </summary>
    public void SetVariable(uint variableId, int value)
    {
        int old = GetVariable(variableId);
        _variables[variableId] = value;
        if (old == value)
        {
            return;
        }

        uint[] gated = [.. _variableGatedSpawns.Where(p => p.Value.VariableId == variableId).Select(p => p.Key)];
        if (gated.Length > 0)
        {
            Instance.FindUpdater<CreatureMapSystem>()?.RefreshSpawns(gated);
        }
    }

    /// <summary>
    /// A creature spawn that exists only while a map variable has a value: cmangos <c>spawn_group.WorldState</c> with a CONDITION_WORLDSTATE
    /// (42) equal-to condition (classic-db's "Uldaman - Annora (11073)" group, condition 700001). Register it from the constructor or
    /// <see cref="Initialize"/>, before the grids load.
    /// </summary>
    protected void GateCreatureSpawnOnVariable(uint spawnGuid, uint variableId, int value)
        => _variableGatedSpawns[spawnGuid] = (variableId, value);

    /// <summary>Whether the creature spawn with this database guid may exist now (asked at grid load and by RefreshSpawns).</summary>
    public bool AllowsCreatureSpawn(uint spawnGuid)
        => !_variableGatedSpawns.TryGetValue(spawnGuid, out (uint VariableId, int Value) gate) || GetVariable(gate.VariableId) == gate.Value;

    /// <summary>The engine: a creature of this map is alive on it (created alive, respawned).</summary>
    internal void NotifyCreatureAlive(Creature creature)
    {
        if (creature.Spawn is { } spawn && _groupOfSpawn.TryGetValue(spawn.Guid, out CreatureGroupState? group))
        {
            group.Alive.Add(spawn.Guid);
        }
    }

    /// <summary>The engine: a creature of this map died or left it.</summary>
    internal void NotifyCreatureGone(Creature creature)
    {
        if (creature.Spawn is { } spawn && _groupOfSpawn.TryGetValue(spawn.Guid, out CreatureGroupState? group)
            && group.Alive.Remove(spawn.Guid) && group.Alive.Count == 0)
        {
            OnCreatureGroupDespawn(group.Id, creature);
        }
    }

    private sealed class CreatureGroupState(uint id)
    {
        public uint Id { get; } = id;
        public HashSet<uint> Alive { get; } = [];
    }
}

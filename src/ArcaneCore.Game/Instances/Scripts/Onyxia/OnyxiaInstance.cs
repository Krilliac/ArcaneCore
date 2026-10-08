using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Onyxia;

/// <summary>mangos-classic instance_onyxias_lair::{SetData,OnObjectCreate}; vmangos instance_onyxia_lair::OnObjectCreate.
/// The egg trap spell creates GO 176510, whose creation summons a whelp (not every static egg on grid load).</summary>
[InstanceScript(249)]
public sealed class OnyxiaInstance(Map map) : ScriptedInstance(map, 1)
{
    private CreatureMapSystem? _registered;
    private readonly HashSet<GameObject> _pendingSpawners = [];
    private readonly HashSet<Creature> _deadWarders = [];
    public override uint GetData(uint type) => type == 0 ? Encounters[0] : 0;
    public override bool IsEncounterInProgress => Encounters[0] is EncounterState.InProgress or EncounterState.Special;
    public override void SetData(uint type, uint data)
    {
        if (type != 0) return;
        Encounters[0] = data;
        if (data == EncounterState.InProgress)
        {
            foreach (Creature warder in _deadWarders)
                if (!warder.IsAlive) _registered?.ForceRespawn(warder);
        }
        else if (data is EncounterState.Fail or EncounterState.Done)
        {
            foreach (Creature warder in _deadWarders) _registered?.ForcedDespawn(warder, 1);
        }
        SaveIfDone(data);
    }
    protected override uint AfterLoad(int index, uint state) => state == EncounterState.Special ? EncounterState.NotStarted : base.AfterLoad(index, state);
    public override void OnCreatureCreate(Creature creature)
    {
        StoreCreature(creature);
        if (creature.System is { } system) Register(system);
    }
    private void Register(CreatureMapSystem system)
    {
        if (ReferenceEquals(system, _registered)) return;
        _registered = system;
        Instance.Combat.UnitKilled += OnCreatureKilled;
        system.RegisterEntryAi(10184, c => new OnyxiaAI(c, this));
    }
    private void OnCreatureKilled(Unit? killer, Unit victim)
    {
        if (victim is Creature { Entry: 12129 } warder) _deadWarders.Add(warder);
    }
    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry == 176510) _pendingSpawners.Add(go);
    }
    public override void Update(uint diffMs)
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } system) return;
        Register(system);
        Creature? trigger = GetSingleCreatureFromStorage(12758);
        if (trigger is null) return;
        foreach (GameObject spawner in _pendingSpawners.ToArray())
        {
            if (!spawner.IsSpawned) { _pendingSpawners.Remove(spawner); continue; }
            if (system.SummonAt(trigger, 11262, spawner.X, spawner.Y, spawner.Z, 0, null, 60000) is not null)
                _pendingSpawners.Remove(spawner);
        }
    }
}

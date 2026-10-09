using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>
/// mangos-classic .../ruins_of_ahnqiraj/ruins_of_ahnqiraj.cpp SetData, GetData, Load:
/// six encounter slots (ruins_of_ahnqiraj.h), Kurinnaxx state and Ossirian's intro after Kurinnaxx.
/// Andorov's spawn, the Rajaxx army waves and Ossirian's crystals are in RuinsOfAhnQirajEvents.cs.
/// </summary>
[InstanceScript(509)]
public sealed partial class RuinsOfAhnQirajInstance(Map instance) : ScriptedInstance(instance, 6)
{
    private readonly KurinnaxxSandTrapAI _sandTraps = new();

    public override uint GetData(uint type) => type < Encounters.Length ? Encounters[type] : 0;

    public override bool IsEncounterInProgress => Encounters.Contains(EncounterState.InProgress);

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry is 15339 or 15370 or 15341 or 15471 or
            15385 or 15388 or 15386 or 15390 or 15389 or 15392 or 15391 or 15590)
        {
            StoreCreature(creature);
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry == KurinnaxxSandTrapScript.TrapEntry)
        {
            Instance.FindUpdater<GameObjectMapSystem>()?.RegisterAi(go.Entry, _sandTraps);
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type >= (uint)Encounters.Length)
        {
            return;
        }

        if (Encounters[type] == data)
        {
            return;
        }

        Encounters[type] = data;
        if (type == 1 && data == EncounterState.InProgress) BeginRajaxxWaves();
        if (type == 1 && data is EncounterState.Fail or EncounterState.Done) EndRajaxxWaves(data);
        if (type == 0 && data == EncounterState.Done) SpawnAndorovIfReady();
        if (type == 5 && data == EncounterState.Fail) ResetCrystals();
        if (data == EncounterState.Done && GetSingleCreatureFromStorage(15339) is { } ossirian)
        {
            if (type == 0)
                Instance.FindUpdater<CreatureMapSystem>()?.SayText(ossirian, 11720);
        }

        SaveIfDone(data);
    }
}

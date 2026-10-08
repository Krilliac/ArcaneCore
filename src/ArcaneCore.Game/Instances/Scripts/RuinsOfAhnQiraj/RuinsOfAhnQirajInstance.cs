using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>
/// mangos-classic .../ruins_of_ahnqiraj/ruins_of_ahnqiraj.cpp SetData, GetData, Load:
/// six encounter slots (ruins_of_ahnqiraj.h), Kurinnaxx state and Ossirian's death announcement.
/// Andorov/Rajaxx and the remaining boss events are not implemented here.
/// </summary>
[InstanceScript(509)]
public sealed class RuinsOfAhnQirajInstance(Map instance) : ScriptedInstance(instance, 6)
{
    private readonly KurinnaxxSandTrapAI _sandTraps = new();

    public override uint GetData(uint type) => type < Encounters.Length ? Encounters[type] : 0;

    public override bool IsEncounterInProgress => Encounters.Contains(EncounterState.InProgress);

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry == 15339)
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
        if (type != 0)
        {
            NotPorted(type, data, "(an AQ20 encounter other than Kurinnaxx)");
            return;
        }

        if (Encounters[type] == data)
        {
            return;
        }

        Encounters[type] = data;
        if (data == EncounterState.Done && GetSingleCreatureFromStorage(15339) is { } ossirian)
        {
            // vmangos boss_kurinnaxx.cpp JustDied: broadcast_text SAY_BREACHED.
            Instance.FindUpdater<CreatureMapSystem>()?.SayText(ossirian, 11720);
        }

        SaveIfDone(data);
    }
}

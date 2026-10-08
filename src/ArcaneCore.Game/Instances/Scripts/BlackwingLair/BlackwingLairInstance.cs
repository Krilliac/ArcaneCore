using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic AI/ScriptDevAI/scripts/eastern_kingdoms/blackwing_lair/blackwing_lair.cpp:
/// SetData (Lashlayer and drakes), OnObjectCreate (Lashlayer door/suppression devices), Load.
/// The other encounters are intentionally not implemented. The 13-slot save layout matches blackwing_lair.h.
/// </summary>
[InstanceScript(469)]
public sealed class BlackwingLairInstance(Map instance) : ScriptedInstance(instance, 13)
{
    public override uint GetData(uint type) => type < Encounters.Length ? Encounters[type] : 0;

    public override bool IsEncounterInProgress => Encounters.Take(8).Contains(EncounterState.InProgress);

    public override void SetData(uint type, uint data)
    {
        if (type is not (2 or 3 or 5))
        {
            NotPorted(type, data, "(a Blackwing Lair encounter outside Lashlayer, Firemaw and Flamegor)");
            return;
        }

        if (Encounters[type] == data)
        {
            return;
        }

        Encounters[type] = data;
        if (type == 2 && data == EncounterState.Done)
        {
            DoUseDoorOrButton(179365);
        }

        SaveIfDone(data);
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry == 179365)
        {
            StoreGameObject(go);
            OpenIf(go, GetData(2) == EncounterState.Done);
        }
        else if (go.Entry == 179784 && GetData(2) == EncounterState.Done)
        {
            go.LootState = GameObjectLootState.JustDeactivated;
        }
    }
}

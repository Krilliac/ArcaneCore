using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic AI/ScriptDevAI/scripts/eastern_kingdoms/blackwing_lair/blackwing_lair.cpp:
/// SetData (Lashlayer and drakes), OnObjectCreate (Lashlayer door/suppression devices), Load.
/// The other encounters are intentionally not implemented. The 13-slot save layout matches blackwing_lair.h.
/// </summary>
[InstanceScript(469)]
public sealed partial class BlackwingLairInstance(Map instance) : ScriptedInstance(instance, 13)
{
    public override uint GetData(uint type) => type < Encounters.Length ? Encounters[type] : 0;

    public override bool IsEncounterInProgress => Encounters.Take(8).Contains(EncounterState.InProgress);

    public override void SetData(uint type, uint data)
    {
        if (type >= Encounters.Length)
        {
            return;
        }

        if (Encounters[type] == data)
        {
            return;
        }

        uint previous = Encounters[type];
        Encounters[type] = data;
        if (type < 8)
        {
            UpdateMainGate();
        }

        if (type == 0 && data == EncounterState.Fail)
        {
            ResetRazorgore();
        }
        if (type == 7)
        {
            UpdateNefarian(data);
        }

        // MC SetData TYPE_VAELASTRASZ: "prevent the players from running back to the first room" - Razorgore's exit is shut while
        // Vaelastrasz is fought and open again otherwise (MC toggles it on every change but SPECIAL; this sets the state it reaches).
        if (type == 1 && data != EncounterState.Special && GetData(0) == EncounterState.Done
            && GetSingleGameObjectFromStorage(176965) is { } razorgoreExit)
        {
            razorgoreExit.State = data == EncounterState.InProgress ? GameObjectState.Ready : GameObjectState.Active;
        }

        uint door = type switch
        {
            0 => 176965u, 1 => 179364u, 2 => 179365u, 6 => 179117u, _ => 0u,
        };
        if (door != 0 && data == EncounterState.Done && previous != EncounterState.Done)
        {
            DoUseDoorOrButton(door);
            if (type == 6 && GetSingleGameObjectFromStorage(179116) is { } side)
                side.State = GameObjectState.Active;
        }

        if (data == EncounterState.Done || type is >= 9 and <= 12)
        {
            SaveToDB();
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry is 176964 or 176965 or 176966 or 179115 or 179116 or 179117 or 179364 or 179365 or 177808)
        {
            StoreGameObject(go);
            uint gate = go.Entry switch
            {
                176965 => 0, 179364 => 1, 179365 => 2, 179116 or 179117 => 6, _ => uint.MaxValue,
            };
            OpenIf(go, gate != uint.MaxValue && GetData(gate) == EncounterState.Done);
        }
        else if (go.Entry == 179784 && GetData(2) == EncounterState.Done)
        {
            go.LootState = GameObjectLootState.JustDeactivated;
        }
        TrackEncounterObject(go);
        if (go.Entry == 177808 && GetData(0) != EncounterState.Done)
            go.Flags |= GameObjectFlags.NoInteract;
        if (go.Entry == 176964) UpdateMainGate();
        if (go.Entry == 176966) UpdateNefarian(GetData(7));
    }
}

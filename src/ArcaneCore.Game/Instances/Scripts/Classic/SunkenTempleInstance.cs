using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// The Temple of Atal'Hakkar (109), from mangos-classic sunken_temple.cpp and sunken_templeScripts.cpp.
/// The instance's creature, statue, flame, summon and wave hooks are in Scripts/SunkenTemple/SunkenTempleInstance.Events.cs.
/// </summary>
[InstanceScript(MapId)]
public sealed partial class SunkenTempleInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 109;
    public const int MaxEncounter = 5;

    public const uint TypeAvatar = 4;
    public const uint TypeAtalarion = 0;
    public const uint TypeProtectors = 1;
    public const uint TypeJammalan = 2;
    public const uint TypeMalfurion = 3;

    public const uint GoHakkarDoor1 = 149432;
    public const uint GoHakkarDoor2 = 149433;

    public override void OnObjectCreate(GameObject go)
    {
        RecordTempleObject(go);
        if (go.Entry is GoHakkarDoor1 or GoHakkarDoor2)
        {
            StoreGameObject(go);
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type != TypeAvatar)
        {
            SetTempleData(type, data);
            return;
        }

        if (data == EncounterState.Special)
        {
            DouseTempleFlame();
            return;
        }

        // "Prevent double processing"
        if (Encounters[TypeAvatar] == data)
        {
            return;
        }

        if (data is EncounterState.InProgress or EncounterState.Fail)
        {
            if (data == EncounterState.InProgress && !BeginTempleAvatar())
            {
                return;
            }
            if (data == EncounterState.Fail)
            {
                FailTempleAvatar();
            }
        }

        DoUseDoorOrButton(GoHakkarDoor1);
        DoUseDoorOrButton(GoHakkarDoor2);
        Encounters[TypeAvatar] = data;
        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type < MaxEncounter ? Encounters[type] : 0;

    /// <summary>"Here a bit custom, to have proper mechanics for the statue events": anything but DONE starts over.</summary>
    protected override uint AfterLoad(int index, uint state) => state != EncounterState.Done ? EncounterState.NotStarted : state;
}

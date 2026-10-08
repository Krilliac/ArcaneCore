using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// The Temple of Atal'Hakkar (map 109): the Avatar of Hakkar part of ScriptDev2's <c>instance_sunken_temple</c> (mangos-classic
/// AI/ScriptDevAI/scripts/eastern_kingdoms/sunken_temple/sunken_temple.cpp, SetData's TYPE_AVATAR branch, GetData, Load and the Hakkar doors
/// of OnObjectCreate; sunken_temple.h:8-61). classic-db z2815 EventAI sets TYPE_AVATAR (4) to SPECIAL when the Shade of Hakkar is hit by its
/// suppression spell.
/// <para>
/// Ported: the five states and their save string (a loaded state other than DONE becomes NOT_STARTED, the script's own rule for the statue
/// events); TYPE_AVATAR SPECIAL changes no state (it counts a doused flame in the original); any other avatar value equal to the current one is
/// ignored, otherwise both combat doors (149432, 149433) are used and the value is kept. Not ported (logged at debug level): the flames, their
/// yells and the avatar's summon on SPECIAL, the shade's summon and the evil circles on IN_PROGRESS, their despawn on FAIL, and every other
/// type of the instance (Atal'alarion, the protectors, Jammal'an, Malfurion).
/// </para>
/// </summary>
[InstanceScript(MapId)]
public sealed class SunkenTempleInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 109;
    public const int MaxEncounter = 5;

    public const uint TypeAvatar = 4;

    public const uint GoHakkarDoor1 = 149432;
    public const uint GoHakkarDoor2 = 149433;

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry is GoHakkarDoor1 or GoHakkarDoor2)
        {
            StoreGameObject(go);
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type != TypeAvatar)
        {
            NotPorted(type, data, "(a Sunken Temple event other than the Avatar of Hakkar)");
            return;
        }

        if (data == EncounterState.Special)
        {
            NotPorted(type, data, "(the eternal flames and the avatar's summon)");
            return;
        }

        // "Prevent double processing"
        if (Encounters[TypeAvatar] == data)
        {
            return;
        }

        if (data is EncounterState.InProgress or EncounterState.Fail)
        {
            NotPorted(type, data, "(the shade, the evil circles and the flames)");
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

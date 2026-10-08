using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Shadowfang Keep (map 33): the state part of ScriptDev2's <c>instance_shadowfang_keep</c> (mangos-classic
/// AI/ScriptDevAI/scripts/eastern_kingdoms/shadowfang_keep/instance_shadowfang_keep.cpp:130-262, shadowfang_keep.h:8-56). classic-db z2815
/// EventAI sets TYPE_RETHILGORE (2) and TYPE_NANDOS (4) on aggro (1), evade (2) and death (3), and TYPE_FENRUS (3) the same way.
/// <para>
/// Ported: the six encounter states and their save string, the courtyard door when the prisoners are freed (TYPE_FREE_NPC done), Arugal's door
/// when Wolf Master Nandos is done, the sorcerer's door after the fourth voidwalker, and the doors that are created open when their encounter is
/// done. Not ported (logged at debug level): Ada's and Ash's speech when Rethilgore dies, Archmage Arugal's Fenrus dialogue and summon, the
/// Arugal intro dialogue and Vincent's death pose, the Nandos pack event.
/// </para>
/// </summary>
[InstanceScript(MapId)]
public sealed class ShadowfangKeepInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 33;
    public const int MaxEncounter = 6;

    public const uint TypeFreeNpc = 1;
    public const uint TypeRethilgore = 2;
    public const uint TypeFenrus = 3;
    public const uint TypeNandos = 4;
    public const uint TypeIntro = 5;
    public const uint TypeVoidwalker = 6;

    public const uint GoCourtyardDoor = 18895;
    public const uint GoSorcererDoor = 18972;
    public const uint GoArugalDoor = 18971;
    public const uint GoArugalFocus = 18973;

    public override void OnObjectCreate(GameObject go)
    {
        switch (go.Entry)
        {
            case GoCourtyardDoor:
                OpenIf(go, Encounters[0] == EncounterState.Done);
                break;
            case GoSorcererDoor:
                // "we ignore voidwalkers, because if the server restarts they won't be there, but Fenrus is dead so the door can't be opened"
                OpenIf(go, Encounters[2] == EncounterState.Done);
                break;
            case GoArugalDoor:
                OpenIf(go, Encounters[3] == EncounterState.Done);
                break;
            case GoArugalFocus:
                break;
            default:
                return;
        }

        StoreGameObject(go);
    }

    public override void SetData(uint type, uint data)
    {
        switch (type)
        {
            case TypeFreeNpc:
                if (data == EncounterState.Done)
                {
                    DoUseDoorOrButton(GoCourtyardDoor);
                }

                Encounters[0] = data;
                break;
            case TypeRethilgore:
                if (data == EncounterState.Done)
                {
                    NotPorted(type, data, "(Ada's and Ash's speech)");
                }

                Encounters[1] = data;
                break;
            case TypeFenrus:
                if (data == EncounterState.Done)
                {
                    NotPorted(type, data, "(Archmage Arugal's summon and dialogue)");
                }

                Encounters[2] = data;
                break;
            case TypeNandos:
                if (data == EncounterState.Done)
                {
                    DoUseDoorOrButton(GoArugalDoor);
                }

                Encounters[3] = data;
                break;
            case TypeIntro:
                Encounters[4] = data;
                break;
            case TypeVoidwalker:
                if (data == EncounterState.Done)
                {
                    Encounters[5]++;
                    if (Encounters[5] > 3)
                    {
                        DoUseDoorOrButton(GoSorcererDoor);
                    }
                }

                break;
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type switch
    {
        TypeFreeNpc => Encounters[0],
        TypeRethilgore => Encounters[1],
        TypeFenrus => Encounters[2],
        TypeNandos => Encounters[3],
        TypeIntro => Encounters[4],
        _ => 0,
    };
}

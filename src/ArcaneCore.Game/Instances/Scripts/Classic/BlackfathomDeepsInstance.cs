using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Blackfathom Deeps (map 48): the state part of ScriptDev2's <c>instance_blackfathom_deeps</c> (mangos-classic
/// AI/ScriptDevAI/scripts/kalimdor/blackfathom_deeps/instance_blackfathom_deeps.cpp, blackfathom_deeps.h:8-33). classic-db z2815 EventAI sets
/// TYPE_KELRIS (1) to DONE when Twilight Lord Kelris dies ("EventAI must set instance data (1,3) at his death").
/// <para>
/// Ported: the three states and their save string (Kelris only ever becomes DONE, once), the portal door when the shrine event is done and the
/// portal door and shrines that are created open when it is. The shrine event's waves and the Fathom Stone are in
/// BlackfathomDeeps/BlackfathomDeepsInstance.Events.cs.
/// </para>
/// </summary>
[InstanceScript(MapId)]
public sealed partial class BlackfathomDeepsInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 48;
    public const int MaxEncounter = 3;

    public const uint TypeKelris = 1;
    public const uint TypeShrine = 2;
    public const uint TypeAquanis = 3;

    public const uint GoPortalDoor = 21117;
    public const uint GoShrine1 = 21118;
    public const uint GoShrine4 = 21121;

    public override void OnObjectCreate(GameObject go)
    {
        switch (go.Entry)
        {
            case GoPortalDoor:
                OpenIf(go, Encounters[1] == EncounterState.Done);
                StoreGameObject(go);
                break;
            case >= GoShrine1 and <= GoShrine4:
                OpenIf(go, Encounters[1] == EncounterState.Done);
                break;
        }
    }

    public override void SetData(uint type, uint data)
    {
        switch (type)
        {
            case TypeKelris:
                if (Encounters[0] != EncounterState.Done && data == EncounterState.Done)
                {
                    Encounters[0] = data;
                }

                break;
            case TypeShrine:
                Encounters[1] = data;
                if (data == EncounterState.InProgress)
                {
                    QueueNextWave();
                }
                else if (data == EncounterState.Done)
                {
                    DoUseDoorOrButton(GoPortalDoor);
                }

                break;
            case TypeAquanis:
                Encounters[2] = data;
                break;
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type switch
    {
        TypeKelris => Encounters[0],
        TypeShrine => Encounters[1],
        TypeAquanis => Encounters[2],
        _ => 0,
    };
}

using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Zul'Gurub (map 309): the Ohgan part of ScriptDev2's <c>instance_zulgurub</c> (mangos-classic
/// AI/ScriptDevAI/scripts/eastern_kingdoms/zulgurub/zulgurub.cpp, SetData's TYPE_OHGAN branch, GetData and Load; zulgurub.h:8-20). classic-db
/// z2815 EventAI sets TYPE_OHGAN (5) to SPECIAL when a Vilebranch Speaker dies ("SPECIAL instance data is set via ACID").
/// <para>
/// Ported: the eight-slot save string, TYPE_OHGAN, and priest death/power bookkeeping for Hakkar (ZulGurubPriestState.cs).
/// Not ported: Mandokir running downstairs on SPECIAL, priest encounter side effects, Lor'khan and Zath.
/// </para>
/// </summary>
[InstanceScript(MapId)]
public sealed partial class ZulGurubInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 309;
    public const int MaxEncounter = 8;

    public const uint TypeOhgan = 5;

    public override void SetData(uint type, uint data)
    {
        if (SetPriestData(type, data))
        {
            return;
        }

        if (type != TypeOhgan)
        {
            NotPorted(type, data, "(a Zul'Gurub event other than Ohgan's)");
            return;
        }

        if (data == EncounterState.Special)
        {
            NotPorted(type, data, "(Bloodlord Mandokir moving downstairs)");
        }

        Encounters[TypeOhgan] = data;
        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type < MaxEncounter ? Encounters[type] : 0;
}

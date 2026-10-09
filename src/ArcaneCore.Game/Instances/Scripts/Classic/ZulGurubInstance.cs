using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Zul'Gurub (map 309): the Ohgan part of ScriptDev2's <c>instance_zulgurub</c> (mangos-classic
/// AI/ScriptDevAI/scripts/eastern_kingdoms/zulgurub/zulgurub.cpp, SetData's TYPE_OHGAN branch, GetData and Load; zulgurub.h:8-20). classic-db
/// z2815 EventAI sets TYPE_OHGAN (5) to SPECIAL when a Vilebranch Speaker dies ("SPECIAL instance data is set via ACID").
/// <para>
/// Ported: the eight-slot save string, TYPE_OHGAN with Mandokir running downstairs on SPECIAL, TYPE_LORKHAN/TYPE_ZATH, the Arlokk
/// forcefield and gong (ZulGurubObjects.cs), and priest death/power bookkeeping for Hakkar (ZulGurubPriestState.cs).
/// Not ported: Mar'li's egg and Arlokk's gong respawn on FAIL (Mar'li's AI resets her eggs itself).
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

        if (type is 6 or 7)
        {
            Encounters[type] = data;
            SaveIfDone(data);
            return;
        }

        if (type != TypeOhgan)
        {
            NotPorted(type, data, "(a Zul'Gurub event other than Ohgan's)");
            return;
        }

        if (data == EncounterState.Special)
        {
            // mangos-classic zulgurub.cpp SetData(TYPE_OHGAN, SPECIAL).
            GetSingleCreatureFromStorage(11382)?.Motion.MovePoint(1, -12196.30f, -1948.37f, 130.31f, run: true);
        }

        Encounters[TypeOhgan] = data;
        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type < MaxEncounter ? Encounters[type] : 0;
}

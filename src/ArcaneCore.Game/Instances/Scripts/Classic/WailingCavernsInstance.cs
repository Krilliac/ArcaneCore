using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Wailing Caverns (map 43): the state part of ScriptDev2's <c>instance_wailing_caverns</c> (mangos-classic
/// AI/ScriptDevAI/scripts/kalimdor/wailing_caverns/wailing_caverns.cpp:66-145, wailing_caverns.h:8-18). classic-db z2815 EventAI sets the four
/// Fanglords (Anacondra 0, Cobrahn 1, Pythas 2, Serpentis 3) on aggro, evade and death, and Mutanus (5).
/// <para>
/// Ported: the six states and their save string, and the Disciple of Naralex state: once the four Fanglords are done it becomes SPECIAL (when it
/// was NOT_STARTED or FAIL), which is what lets the disciple's escort start. Not ported: the disciple's intro yell, the escort itself, the
/// mysterious chest of "Fortune Awaits".
/// </para>
/// </summary>
[InstanceScript(MapId)]
public sealed class WailingCavernsInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 43;
    public const int MaxEncounter = 6;

    public const uint TypeAnacondra = 0;
    public const uint TypeCobrahn = 1;
    public const uint TypePythas = 2;
    public const uint TypeSerpentis = 3;
    public const uint TypeDisciple = 4;
    public const uint TypeMutanus = 5;

    public override void SetData(uint type, uint data)
    {
        if (type < MaxEncounter)
        {
            Encounters[type] = data;
        }

        // "Set to special in order to start the escort event; only if all four bosses are done"
        if (Encounters[0] == EncounterState.Done && Encounters[1] == EncounterState.Done && Encounters[2] == EncounterState.Done
            && Encounters[3] == EncounterState.Done && Encounters[4] is EncounterState.NotStarted or EncounterState.Fail)
        {
            if (Encounters[4] == EncounterState.NotStarted)
            {
                NotPorted(type, data, "(the Disciple of Naralex's intro yell)");
            }

            Encounters[4] = EncounterState.Special;
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type < MaxEncounter ? Encounters[type] : 0;
}

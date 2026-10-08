using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Blackrock Depths (map 230): the Tomb of the Seven part of ScriptDev2's <c>instance_blackrock_depths</c> (mangos-classic
/// AI/ScriptDevAI/scripts/eastern_kingdoms/blackrock_depths/instance_blackrock_depths.cpp, SetData's TYPE_TOMB_OF_SEVEN branch, GetData and
/// Load; blackrock_depths.h:8-28, 90-98, 218). classic-db z2815 EventAI sets TYPE_TOMB_OF_SEVEN (4) to FAIL when one of the seven dwarves
/// (9034-9040) reaches home after an evade.
/// <para>
/// Ported: the thirteen states and their save string (a state saved IN_PROGRESS comes back NOT_STARTED, except index 6, which the original
/// compares with TYPE_IRON_HALL); the tomb: a value equal to the current one is ignored, every change uses the entrance door (170576), FAIL
/// respawns the dead dwarves, DONE uses the exit door (170577). Not ported (logged at debug level): calling the next dwarf on IN_PROGRESS, the
/// fight rounds, the chest of the Seven on DONE, and every other type of the instance (Ring of Law, vault, Rocknot, Lyceum, Iron Hall, jail
/// break, Flamelash, Hurley, bridge, bar, Plugger, Nagmara).
/// </para>
/// </summary>
[InstanceScript(MapId)]
public sealed class BlackrockDepthsInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 230;
    public const int MaxEncounter = 13;

    public const uint TypeRingOfLaw = 1;
    public const uint TypeTombOfSeven = 4;
    public const uint TypeIronHall = 6;
    public const uint TypeNagmara = 13;

    public const uint GoTombEnter = 170576;
    public const uint GoTombExit = 170577;
    public const uint GoChestSeven = 169243;

    /// <summary>aTombDwarfes: Anger'rel, Seeth'rel, Dope'rel, Gloom'rel, Vile'rel, Hate'rel, Doom'rel.</summary>
    public static readonly uint[] TombDwarves = [9035, 9038, 9040, 9037, 9036, 9034, 9039];

    public override void OnCreatureCreate(Creature creature)
    {
        if (Array.IndexOf(TombDwarves, creature.Template.Entry) >= 0)
        {
            StoreCreature(creature);
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry is GoTombEnter or GoTombExit or GoChestSeven)
        {
            StoreGameObject(go);
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type != TypeTombOfSeven)
        {
            NotPorted(type, data, "(a Blackrock Depths event other than the Tomb of the Seven)");
            return;
        }

        // "Don't set the same data twice"
        if (data != Encounters[3])
        {
            DoUseDoorOrButton(GoTombEnter); // combat door
            if (data == EncounterState.InProgress)
            {
                NotPorted(type, data, "(calling the next dwarf)");
            }

            if (data == EncounterState.Fail)
            {
                foreach (uint entry in TombDwarves)
                {
                    if (GetSingleCreatureFromStorage(entry) is { IsAlive: false } dwarf)
                    {
                        dwarf.System?.ForceRespawn(dwarf);
                    }
                }
            }

            if (data == EncounterState.Done)
            {
                NotPorted(type, data, "(the chest of the Seven)");
                DoUseDoorOrButton(GoTombExit);
            }

            Encounters[3] = data;
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type is >= TypeRingOfLaw and <= TypeNagmara ? Encounters[type - 1] : 0;

    /// <summary>The original's Load skips index 6 (it compares the index with TYPE_IRON_HALL, which is 6).</summary>
    protected override uint AfterLoad(int index, uint state)
        => state == EncounterState.InProgress && index != TypeIronHall ? EncounterState.NotStarted : state;
}

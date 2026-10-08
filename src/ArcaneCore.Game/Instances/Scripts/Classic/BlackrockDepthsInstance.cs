using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Blackrock Depths (230), from mangos-classic instance_blackrock_depths.cpp.
/// The arena, vault, bar, Iron Hall and creature event hooks are in Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs.
/// </summary>
[InstanceScript(MapId)]
public sealed partial class BlackrockDepthsInstance(Map instance) : ScriptedInstance(instance, MaxEncounter)
{
    public const uint MapId = 230;
    public const int MaxEncounter = 13;

    public const uint TypeRingOfLaw = 1;
    public const uint TypeVault = 2;
    public const uint TypeRocknot = 3;
    public const uint TypeTombOfSeven = 4;
    public const uint TypeLyceum = 5;
    public const uint TypeIronHall = 6;
    public const uint TypeQuestJailBreak = 7;
    public const uint TypeFlamelash = 8;
    public const uint TypeHurley = 9;
    public const uint TypeBridge = 10;
    public const uint TypeBar = 11;
    public const uint TypePlugger = 12;
    public const uint TypeNagmara = 13;

    public const uint GoTombEnter = 170576;
    public const uint GoTombExit = 170577;
    public const uint GoChestSeven = 169243;

    /// <summary>aTombDwarfes: Anger'rel, Seeth'rel, Dope'rel, Gloom'rel, Vile'rel, Hate'rel, Doom'rel.</summary>
    public static readonly uint[] TombDwarves = [9035, 9038, 9040, 9037, 9036, 9034, 9039];

    public override void OnCreatureCreate(Creature creature)
    {
        RecordDepthsCreature(creature);
        if (Array.IndexOf(TombDwarves, creature.Template.Entry) >= 0)
        {
            StoreCreature(creature);
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        RecordDepthsObject(go);
        if (go.Entry is GoTombEnter or GoTombExit or GoChestSeven)
        {
            StoreGameObject(go);
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type != TypeTombOfSeven)
        {
            SetDepthsData(type, data);
            return;
        }

        // "Don't set the same data twice"
        if (data != Encounters[3])
        {
            DoUseDoorOrButton(GoTombEnter); // combat door
            if (data == EncounterState.InProgress)
            {
                CallNextDwarf();
            }

            if (data == EncounterState.Fail)
            {
                ResetDwarfRound();
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
                RespawnDepthsObject(GoChestSeven);
                DoUseDoorOrButton(GoTombExit);
            }

            Encounters[3] = data;
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type == TypeRocknot && Encounters[2] == EncounterState.InProgress && BarAleCount == 3
        ? EncounterState.Special
        : type is >= TypeRingOfLaw and <= TypeNagmara ? Encounters[type - 1] : 0;

    /// <summary>The original's Load skips index 6 (it compares the index with TYPE_IRON_HALL, which is 6).</summary>
    protected override uint AfterLoad(int index, uint state)
        => state == EncounterState.InProgress && index != TypeIronHall ? EncounterState.NotStarted : state;
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// mangos-classic .../zulgurub/zulgurub.cpp OnCreatureCreate, SetData and RemoveHakkarPowerStack.
/// Priest deaths feed their DONE states through the map kill hook until their individual AIs are ported.
/// This is state/power bookkeeping, not an implementation of those priest encounters.
/// </summary>
public sealed partial class ZulGurubInstance
{
    private bool _priestDeathsSubscribed;

    public override void OnCreatureCreate(Creature creature)
    {
        if (!_priestDeathsSubscribed)
        {
            Instance.Combat.UnitKilled += OnRaidUnitKilled;
            _priestDeathsSubscribed = true;
        }

        if (creature.Template.Entry == 14834)
        {
            StoreCreature(creature);
        }
    }

    private void OnRaidUnitKilled(Unit? killer, Unit victim)
    {
        if (victim is not Creature creature)
        {
            return;
        }

        uint priest = creature.Template.Entry switch
        {
            14517 => 0, // Jeklik
            14507 => 1, // Venoxis
            14510 => 2, // Marli
            14509 => 3, // Thekal
            14515 => 4, // Arlokk
            _ => uint.MaxValue,
        };
        if (priest < 5)
        {
            SetData(priest, EncounterState.Done);
        }
    }

    private bool SetPriestData(uint type, uint data)
    {
        if (type >= 5)
        {
            return false;
        }

        if (Encounters[type] == data)
        {
            return true;
        }

        Encounters[type] = data;
        if (data == EncounterState.Done && GetSingleCreatureFromStorage(14834) is { IsAlive: true } hakkar)
        {
            Instance.FindUpdater<CreatureMapSystem>()?.CastSpell(hakkar, 24693, hakkar, triggered: true);
        }

        SaveIfDone(data);
        return true;
    }
}

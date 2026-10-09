using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// vmangos scripts/kalimdor/silithus/temple_of_ahnqiraj/temple_of_ahnqiraj.h TYPE_* order and
/// instance_temple_of_ahnqiraj.cpp SetData, OnObjectCreate, Load, CheckConditionCriteriaMeet.
/// Ten save slots include the unused reference slot 9. GPL reference was used for facts only.
/// </summary>
[InstanceScript(531)]
public sealed class TempleOfAhnQirajInstance(Map map) : ScriptedInstance(map, 10), IInstanceConditionFacts
{
    public const uint Skeram = 0, Sartura = 1, Fankriss = 2, Huhuran = 3,
        Twins = 4, CThun = 5, BugTrio = 6, Viscidus = 7, Ouro = 8;
    private readonly HashSet<ObjectGuid> _deadBugs = [];

    public override uint GetData(uint type) => type < Encounters.Length ? Encounters[type] : 0;
    public bool? HasCompletedEncounter(uint dbcEncounterId) => dbcEncounterId switch
    {
        715 => GetData(Twins) == EncounterState.Done,
        716 => GetData(Ouro) == EncounterState.Done,
        _ => null,
    };
    public override bool IsEncounterInProgress => Encounters.Any(state => state is EncounterState.InProgress or EncounterState.Special);
    public override bool CheckConditionCriteriaMeet(Player player, uint conditionId)
        => conditionId < Encounters.Length && Encounters[conditionId] == EncounterState.Done;

    public override void SetData(uint type, uint data)
    {
        if (type > Ouro) return;
        if (type == BugTrio && data == EncounterState.InProgress && Encounters[type] != EncounterState.InProgress)
            _deadBugs.Clear();
        if (Encounters[type] == data) return;
        bool wasTwinsFighting = type == Twins && Encounters[type] == EncounterState.InProgress;
        Encounters[type] = data;
        if (data == EncounterState.Done)
        {
            switch (type)
            {
                case Skeram: DoUseDoorOrButton(180636); break;
                case Huhuran: DoUseDoorOrButton(180634); break;
                case Twins: DoUseDoorOrButton(180635); break;
            }
        }
        // vmangos SetData closes the twins' entrance during combat and reopens it on a wipe or kill.
        if (type == Twins && wasTwinsFighting != (data == EncounterState.InProgress))
            DoUseDoorOrButton(180634);
        SaveIfDone(data);
    }

    /// <summary>vmangos boss_bug_trioAI::JustDied + instance_temple_of_ahnqiraj::SetData(SPECIAL).
    /// Count distinct deaths so duplicate death notifications cannot complete the trio.</summary>
    public void BugDied(Creature bug)
    {
        if (bug.Entry is not (15511 or 15543 or 15544) || Encounters[BugTrio] != EncounterState.InProgress
            || !_deadBugs.Add(bug.Guid)) return;
        if (_deadBugs.Count == 3) SetData(BugTrio, EncounterState.Done);
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry is not (180636 or 180634 or 180635)) return;
        StoreGameObject(go);
        OpenIf(go, go.Entry switch
        {
            180636 => GetData(Skeram) == EncounterState.Done,
            180634 => GetData(Huhuran) == EncounterState.Done && GetData(Twins) != EncounterState.InProgress,
            _ => GetData(Twins) == EncounterState.Done,
        });
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Entry is 15511 or 15543 or 15544
            || creature.Entry == 15263 && creature.System?.SummonerOf(creature) is null)
            StoreCreature(creature);
    }

    internal void RestoreBugTrio(CreatureMapSystem system)
    {
        foreach (uint entry in new uint[] { 15511, 15543, 15544 })
        {
            Creature? bug = system.Creatures.FirstOrDefault(c => c.Entry == entry);
            if (bug is { IsAlive: false }) system.ForceRespawn(bug);
        }
    }
}

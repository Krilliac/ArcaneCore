using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

public sealed partial class NaxxramasInstance
{
    private void SpawnSapphiron()
    {
        // mangos-classic naxxramas.h sapphironPositions and
        // naxxramas.cpp instance_naxxramas::Update (22-second birth delay).
        if (GetSingleCreatureFromStorage(15989) is not null
            || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures
            || creatures.Content.FindTemplate(15989) is not { } template) return;
        creatures.SpawnTemporary(template, 3521.48f, -5234.87f, 137.626f, 4.53329f);
    }

    public void OpenGuardianWindows()
    {
        for (uint entry = 181402; entry <= 181405; entry++) SetDoor(entry, true);
    }

    private void ActivateReward(uint entry)
    {
        if (GetSingleGameObjectFromStorage(entry) is not { } go) return;
        go.Flags &= ~GameObjectFlags.NoInteract;
        Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(go);
    }
    // Only the Military/Construct/Frostwyrm doors are owned here. These IDs and their
    // transitions come from mangos-classic naxxramas.h and instance_naxxramas::SetData.
    private void OnPartTwoStateChanged(uint type, uint state)
    {
        switch (type)
        {
            case Razuvious when state == EncounterState.Done:
                if (Instance.FindUpdater<CreatureMapSystem>() is { } razuviousCreatures)
                    foreach (Creature add in razuviousCreatures.Creatures.Where(c => c.Entry == 16803).ToArray())
                        razuviousCreatures.ForcedDespawn(add, 0);
                break;
            case Gothik:
                if (state is (EncounterState.Fail or EncounterState.Done)
                    && Instance.FindUpdater<CreatureMapSystem>() is { } gothicCreatures)
                {
                    foreach (Creature add in gothicCreatures.Creatures.Where(c => c.Entry is 16124 or 16125 or 16126 or 16127 or 16148 or 16150).ToArray())
                        gothicCreatures.ForcedDespawn(add, 0);
                }
                SetDoor(181124, state is not (EncounterState.InProgress or EncounterState.Special));
                SetDoor(181170, state is EncounterState.Special or EncounterState.Done or EncounterState.Fail);
                if (state == EncounterState.Done)
                {
                    SetDoor(181125, true);
                    SetDoor(181119, true);
                }
                break;
            case Horsemen:
                if (state == EncounterState.Fail)
                {
                    // vmangos instance_naxxramas.cpp SetData(TYPE_FOUR_HORSEMEN, FAIL): the death counter restarts and every dead
                    // horseman respawns. A horseman still fighting goes home with the rest (vmangos HandleEvadeOutOfHome evades all
                    // four together), so a partial wipe leaves four fresh horsemen and four deaths still to earn.
                    _horsemenDead.Clear();
                    RespawnDeadHorsemen();
                    foreach (uint entry in HorsemenEntries)
                        if (GetSingleCreatureFromStorage(entry) is { IsAlive: true, IsEvading: false } horseman && horseman.Combat.IsInCombat)
                            horseman.AI?.EnterEvadeMode();
                }
                if (state is (EncounterState.Fail or EncounterState.Done)
                    && Instance.FindUpdater<CreatureMapSystem>() is { } horsemenCreatures)
                    foreach (Creature spirit in horsemenCreatures.Creatures.Where(c => c.Entry is 16775 or 16776 or 16777 or 16778).ToArray())
                        horsemenCreatures.ForcedDespawn(spirit, 0);
                SetDoor(181119, state is not EncounterState.InProgress);
                if (state == EncounterState.Done)
                {
                    SetDoor(181210, true);
                    SetDoor(181230, true);
                    ActivateReward(181366); // Four Horsemen chest
                    ActivateReward(181578); // Military portal
                }
                break;
            case Patchwerk when state == EncounterState.Done:
                SetDoor(181123, true);
                break;
            case Gluth when state == EncounterState.Done:
                SetDoor(181120, true);
                SetDoor(181121, true);
                break;
            case Thaddius:
                if (state == EncounterState.Fail)
                {
                    _constructAddsDead.Clear();
                    _addReviveMs = _overloadMs = 0;
                    foreach (uint entry in new uint[] { 15929, 15930 })
                        if (GetSingleCreatureFromStorage(entry) is { } add)
                        {
                            if (add.IsAlive && add.AI is ThaddiusAddAI ai) ai.Revive();
                            else Instance.FindUpdater<CreatureMapSystem>()?.ForceRespawn(add);
                        }
                }
                if (state != EncounterState.Special) SetDoor(181121, state is not EncounterState.InProgress);
                if (state == EncounterState.Done)
                {
                    SetDoor(181213, true);
                    SetDoor(181232, true);
                    ActivateReward(181576); // Construct portal
                }
                break;
            case Sapphiron when state == EncounterState.Done:
                SetDoor(181225, true);
                break;
            case KelThuzad:
                SetDoor(181228, state is not EncounterState.InProgress);
                if (state == EncounterState.InProgress && GetSingleCreatureFromStorage(15990)?.AI is KelThuzadAI kelThuzad)
                    kelThuzad.BeginPhaseOne();
                if (state is EncounterState.Fail or EncounterState.Done)
                {
                    _guardianCheckMs = 0;
                    if (Instance.FindUpdater<CreatureMapSystem>() is { } system)
                        foreach (Creature guardian in system.Creatures.Where(c => c.Entry == 16441).ToArray())
                            system.ForcedDespawn(guardian, 0);
                }
                if (state == EncounterState.Fail)
                    for (uint entry = 181402; entry <= 181405; entry++) SetDoor(entry, false);
                break;
        }
    }

    private void RestorePartTwoDoor(GameObject go)
    {
        switch (go.Entry)
        {
            case 181124: OpenIf(go, GetData(Gothik) is not EncounterState.InProgress); break;
            case 181170: OpenIf(go, GetData(Gothik) is EncounterState.Special or EncounterState.Done); break;
            case 181125: OpenIf(go, GetData(Gothik) == EncounterState.Done); break;
            case 181119: OpenIf(go, GetData(Gothik) == EncounterState.Done && GetData(Horsemen) != EncounterState.InProgress); break;
            case 181123: OpenIf(go, GetData(Patchwerk) == EncounterState.Done); break;
            case 181120: OpenIf(go, GetData(Gluth) == EncounterState.Done); break;
            case 181121: OpenIf(go, GetData(Gluth) == EncounterState.Done && GetData(Thaddius) != EncounterState.InProgress); break;
            case 181210 or 181230: OpenIf(go, GetData(Horsemen) == EncounterState.Done); break;
            case 181213 or 181232: OpenIf(go, GetData(Thaddius) == EncounterState.Done); break;
            case 181225: OpenIf(go, GetData(Sapphiron) == EncounterState.Done); break;
            case 181228: OpenIf(go, GetData(KelThuzad) != EncounterState.InProgress); break;
            case 181366 or 181578 when GetData(Horsemen) == EncounterState.Done:
                go.Flags &= ~GameObjectFlags.NoInteract;
                break;
            case 181576 when GetData(Thaddius) == EncounterState.Done:
                go.Flags &= ~GameObjectFlags.NoInteract;
                break;
        }
    }
}

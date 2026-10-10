using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// The Military Quarter, Construct Quarter and Frostwyrm Lair slots (6-14) of Naxxramas' ScriptDev2 save layout (naxxramas.h
/// TYPE_*): their doors, rewards, multi-creature encounter bookkeeping and area triggers. The Arachnid/Plague part of the class
/// (NaxxramasInstance.cs) calls <see cref="OnPartTwoStateChanged"/>, <see cref="OnPartTwoObjectCreate"/>,
/// <see cref="OnPartTwoCreatureDeath"/> and <see cref="UpdatePartTwo"/>; the wing-end portals of slots 8 and 12 and the Frostwyrm
/// Lair gate (<c>WingsCleared</c>, <c>BlocksAreaTriggerTeleport</c>) are handled there.
/// Source: mangos-classic AI/ScriptDevAI/scripts/eastern_kingdoms/naxxramas/naxxramas.cpp
/// instance_naxxramas::{SetData,OnObjectCreate,OnCreatureCreate,DoHandleAreaTrigger}; vmangos instance_naxxramas.cpp for the Four
/// Horsemen wipe. Reimplemented, no GPL source copied.
/// </summary>
public sealed partial class NaxxramasInstance
{
    public const uint Razuvious = 6, Gothik = 7, Horsemen = 8;
    public const uint Patchwerk = 9, Grobbulus = 10, Gluth = 11, Thaddius = 12;
    public const uint Sapphiron = 13, KelThuzad = 14;

    /// <summary>naxxramas.h NPC_BLAUMEUX, NPC_MOGRAINE, NPC_THANE, NPC_ZELIEK.</summary>
    public static readonly uint[] HorsemenEntries = [16065, 16062, 16064, 16063];

    /// <summary>
    /// The game objects this part drives (naxxramas.h GO_MILI_*, GO_CONS_*, GO_KELTHUZAD_*, the Four Horsemen chest). The
    /// wing-end portals and eye ramps (181210/181230/181213/181232/181576/181578) belong to the Portals table of the other part.
    /// </summary>
    private static readonly HashSet<uint> PartTwoObjects =
    [
        181124, 181170, 181125, 181119, 181123, 181120, 181121, 181225, 181228, 181402, 181403, 181404, 181405, 181366,
    ];

    private readonly HashSet<uint> _horsemenDead = [];
    private readonly HashSet<uint> _constructAddsDead = [];
    private uint _addReviveMs;
    private uint _overloadMs;
    private uint _sapphironSpawnMs;
    private uint _guardianCheckMs;
    private bool _respawnHorsemen;

    public override void Initialize()
    {
        base.Initialize();
        _horsemenDead.Clear();
        _constructAddsDead.Clear();
        _addReviveMs = _overloadMs = _sapphironSpawnMs = _guardianCheckMs = 0;
        _respawnHorsemen = false;
    }

    /// <summary>ScriptDev2 instance_naxxramas::SetData(TYPE_FOUR_HORSEMEN): one shared encounter, credited only on all four distinct deaths.</summary>
    public void RecordHorsemanDeath(uint entry)
    {
        if (entry is not (16065 or 16062 or 16064 or 16063) || GetData(Horsemen) == EncounterState.Done) return;
        _horsemenDead.Add(entry);
        if (_horsemenDead.Count == 4) SetData(Horsemen, EncounterState.Done);
    }

    /// <summary>SD2 boss_thaddiusAddsAI: both adds must fall within ten seconds; fourteen seconds later their Teslas overload.</summary>
    public void RecordConstructAddDeath(uint entry)
    {
        if (entry is not (15929 or 15930) || GetData(Thaddius) == EncounterState.Done) return;
        _constructAddsDead.Add(entry);
        if (_constructAddsDead.Count == 2) { _addReviveMs = 0; _overloadMs = 14000; }
        else _addReviveMs = 10000;
    }

    public bool ConstructAddsDefeated => _constructAddsDead.Count == 2;

    public void StartGuardianChecks() => _guardianCheckMs = 2000;

    private void UpdatePartTwo(uint diffMs)
    {
        if (_respawnHorsemen)
        {
            _respawnHorsemen = false;
            RespawnDeadHorsemen();
        }
        if (_guardianCheckMs > 0)
        {
            if (_guardianCheckMs > diffMs) _guardianCheckMs -= diffMs;
            else
            {
                _guardianCheckMs = 2000;
                // mangos-classic naxxramas.cpp Update / EVENT_GUARDIAN_SHACKLE:
                // more than three controlled guardians breaks the shackles.
                if (Instance.FindUpdater<CreatureMapSystem>() is { } system
                    && system.Creatures.Count(c => c.Entry == 16441 && c.IsAlive && (c.UnitFlags & UnitFlags.Stunned) != 0) > 3
                    && GetSingleCreatureFromStorage(15990) is { IsAlive: true } kelthuzad)
                    system.CastSpell(kelthuzad, 29910, kelthuzad, triggered: true);
            }
        }
        if (_sapphironSpawnMs > 0)
        {
            if (_sapphironSpawnMs > diffMs) _sapphironSpawnMs -= diffMs;
            else
            {
                _sapphironSpawnMs = 0;
                SpawnSapphiron();
            }
        }
        if (_addReviveMs > 0)
        {
            if (_addReviveMs > diffMs) _addReviveMs -= diffMs;
            else
            {
                _addReviveMs = 0;
                foreach (uint entry in _constructAddsDead)
                    if (GetSingleCreatureFromStorage(entry) is { } add)
                    {
                        if (add.IsAlive && add.AI is ThaddiusAddAI addAi) addAi.Revive();
                        else Instance.FindUpdater<CreatureMapSystem>()?.ForceRespawn(add);
                    }
                _constructAddsDead.Clear();
            }
        }
        if (_overloadMs > 0)
        {
            if (_overloadMs > diffMs) _overloadMs -= diffMs;
            else
            {
                _overloadMs = 0;
                if (GetSingleCreatureFromStorage(15930) is { IsAlive: true } feugen)
                    feugen.System?.CastSpell(feugen, 28359, feugen, triggered: true);
                SetData(Thaddius, EncounterState.Special);
                if (GetSingleCreatureFromStorage(15928) is { } thaddius)
                    thaddius.UnitFlags &= ~(UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer);
            }
        }
    }

    public override void OnCreatureCreate(Creature creature)
    {
        StoreCreature(creature);
        OnLivingPoisonTriggerCreated(creature);
        OnFaerlinaFollowerCreated(creature);
        // vmangos instance_naxxramas::OnCreatureCreate: a horseman created dead while the encounter is not done respawns, so the
        // four distinct deaths (counted in memory only, like vmangos m_horsemenDeathCounter) stay reachable after a restart or a
        // grid reload. Deferred to the next instance update: the creature is still being added to the map here.
        if (creature.Entry is 16065 or 16062 or 16064 or 16063 && !creature.IsAlive && GetData(Horsemen) != EncounterState.Done)
            _respawnHorsemen = true;
    }

    /// <summary>vmangos instance_naxxramas::SetData(TYPE_FOUR_HORSEMEN, FAIL): every dead horseman comes back.</summary>
    private void RespawnDeadHorsemen()
    {
        if (GetData(Horsemen) == EncounterState.Done || Instance.FindUpdater<CreatureMapSystem>() is not { } system) return;
        foreach (uint entry in HorsemenEntries)
            if (GetSingleCreatureFromStorage(entry) is { IsAlive: false } dead)
                system.ForceRespawn(dead);
    }

    private void OnPartTwoCreatureDeath(Creature creature)
    {
        // mangos-classic boss_gothik.cpp SummonedCreatureJustDied and anchor-spell chain:
        // a live-side trainee/knight/rider reappears as its spectral counterpart.
        if (GetData(Gothik) is not (EncounterState.InProgress or EncounterState.Special)) return;
        uint spectral = creature.Entry switch
        {
            16124 => 16127u, 16125 => 16148u, 16126 => 16150u, _ => 0u,
        };
        if (spectral == 0 || Instance.FindUpdater<CreatureMapSystem>() is not { } system
            || system.Content.FindTemplate(spectral) is not { } template
            || GetSingleGameObjectFromStorage(181170) is not { } gate) return;
        Creature? trigger = system.Creatures.Where(c => c.Entry == 16137 && c.Y > gate.Y)
            .MinBy(c => Math.Abs(c.X - creature.X) + Math.Abs(c.Y - creature.Y));
        if (trigger is null) return; // requires ClassicDB's spectral-side sub-boss trigger spawns
        Creature add = system.SpawnTemporary(template, trigger.X, trigger.Y, trigger.Z, trigger.Orientation, creature);
        if (Instance.Players.FirstOrDefault(p => p.IsAlive && p.Y > gate.Y) is { } player)
            add.AI?.AttackStart(player);
    }

    private void OnPartTwoObjectCreate(GameObject go)
    {
        if (!PartTwoObjects.Contains(go.Entry)) return;
        StoreGameObject(go);
        RestorePartTwoDoor(go);
    }

    public override bool OnGameObjectUse(Player player, GameObject go)
    {
        if (go.Entry != 181356) return false;
        // mangos-classic boss_sapphiron.cpp GOUse_go_sapphiron_birth.
        if (GetData(Sapphiron) == EncounterState.NotStarted && GetSingleCreatureFromStorage(15989) is null)
            SetData(Sapphiron, EncounterState.Special);
        return false; // SD2 lets the object's normal activation/animation proceed.
    }

    public override void OnAreaTrigger(Player player, uint triggerId)
    {
        // mangos-classic naxxramas.cpp AreaTrigger_at_naxxramas: game masters and the dead trigger nothing.
        if (player.IsGameMaster || !player.IsAlive) return;
        if (HandleFaerlinaIntroTrigger(triggerId)) return;
        // mangos-classic naxxramas.cpp instance_naxxramas::DoHandleAreaTrigger: Kel'Thuzad's trigger only sets the encounter
        // in progress; SetData starts the channel (KelThuzadAI.BeginPhaseOne). He enters combat in phase two, not here.
        if (triggerId == 4112 && GetData(KelThuzad) is EncounterState.NotStarted or EncounterState.Fail)
            SetData(KelThuzad, EncounterState.InProgress);
        else if (triggerId == 4113 && GetData(Thaddius) == EncounterState.NotStarted
            && GetSingleCreatureFromStorage(15928) is { } thaddius)
        {
            SetData(Thaddius, EncounterState.Special);
            thaddius.System?.SayText(thaddius, -1533029);
        }
    }

    private void SetDoor(uint entry, bool open)
    {
        if (GetSingleGameObjectFromStorage(entry) is { } door)
            door.State = open ? GameObjectState.Active : GameObjectState.Ready;
    }

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
                    ActivateReward(181366); // Four Horsemen chest; the Military portal and eye ramps are the Portals table's
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
                break;
            case Sapphiron:
                if (state == EncounterState.Special) _sapphironSpawnMs = 22000;
                if (state == EncounterState.Done) SetDoor(181225, true);
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
            case 181225: OpenIf(go, GetData(Sapphiron) == EncounterState.Done); break;
            case 181228: OpenIf(go, GetData(KelThuzad) != EncounterState.InProgress); break;
            case 181366 when GetData(Horsemen) == EncounterState.Done:
                go.Flags &= ~GameObjectFlags.NoInteract;
                break;
        }
    }
}

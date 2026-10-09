using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// Naxxramas' 16-slot ScriptDev2 save layout (naxxramas.h MAX_ENCOUNTER and TYPE_*).
/// Slots 0-5 belong to the Arachnid/Plague lane; this file owns slots 6-14.
/// Source: mangos-classic AI/ScriptDevAI/scripts/eastern_kingdoms/naxxramas/naxxramas.cpp
/// instance_naxxramas::{SetData,OnObjectCreate,OnCreatureCreate,Load}.
/// </summary>
[InstanceScript(533)]
public sealed partial class NaxxramasInstance(Map map) : ScriptedInstance(map, 16)
{
    public const uint Razuvious = 6, Gothik = 7, Horsemen = 8;
    public const uint Patchwerk = 9, Grobbulus = 10, Gluth = 11, Thaddius = 12;
    public const uint Sapphiron = 13, KelThuzad = 14;

    /// <summary>naxxramas.h NPC_BLAUMEUX, NPC_MOGRAINE, NPC_THANE, NPC_ZELIEK.</summary>
    public static readonly uint[] HorsemenEntries = [16065, 16062, 16064, 16063];

    private readonly HashSet<uint> _horsemenDead = [];
    private readonly HashSet<uint> _constructAddsDead = [];
    private uint _addReviveMs;
    private uint _overloadMs;
    private uint _sapphironSpawnMs;
    private uint _guardianCheckMs;

    public override uint GetData(uint type) => type < Encounters.Length ? Encounters[type] : 0;
    public override bool IsEncounterInProgress => Encounters.Contains(EncounterState.InProgress)
        || GetData(Gothik) == EncounterState.Special;

    public override void Initialize()
    {
        base.Initialize();
        _horsemenDead.Clear();
        _constructAddsDead.Clear();
        _addReviveMs = _overloadMs = _sapphironSpawnMs = _guardianCheckMs = 0;
    }

    public override void SetData(uint type, uint data)
    {
        if (type >= Encounters.Length) return;
        if (type < Razuvious)
        {
            // Reserved for the separate Arachnid/Plague implementation.
            Encounters[type] = data;
            SaveIfDone(data);
            return;
        }
        if (Encounters[type] == data) return;
        uint previous = Encounters[type];
        Encounters[type] = data;
        if (type == Sapphiron && data == EncounterState.Special) _sapphironSpawnMs = 22000;
        OnPartTwoStateChanged(type, previous, data);
        SaveIfDone(data);
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

    public override void Update(uint diffMs)
    {
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

    public override void OnCreatureCreate(Creature creature) => StoreCreature(creature);

    public override void OnCreatureDeath(Creature creature)
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

    public override void OnObjectCreate(GameObject go)
    {
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
        // mangos-classic naxxramas.cpp instance_naxxramas::DoHandleAreaTrigger.
        if (triggerId == 4112 && GetData(KelThuzad) is EncounterState.NotStarted or EncounterState.Fail)
        {
            if (GetSingleCreatureFromStorage(15990)?.AI is KelThuzadAI boss)
                boss.AttackStart(player);
        }
        else if (triggerId == 4113 && GetData(Thaddius) == EncounterState.NotStarted
            && GetSingleCreatureFromStorage(15928) is { } thaddius)
        {
            SetData(Thaddius, EncounterState.Special);
            thaddius.System?.SayText(thaddius, -1533029);
        }
    }

    /// <summary>naxxramas.cpp DoHandleAreaTrigger(AREATRIGGER_FROSTWYRM_TELE): four wing end bosses must be defeated.</summary>
    public bool FrostwyrmUnlocked => GetData(2) == EncounterState.Done
        && GetData(5) == EncounterState.Done
        && GetData(Horsemen) == EncounterState.Done
        && GetData(Thaddius) == EncounterState.Done;

    private void SetDoor(uint entry, bool open)
    {
        if (GetSingleGameObjectFromStorage(entry) is { } door)
            door.State = open ? GameObjectState.Active : GameObjectState.Ready;
    }
}

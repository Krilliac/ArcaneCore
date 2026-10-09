using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// Encounter bookkeeping from mangos-classic blackwing_lair.cpp:
/// OnCreatureCreate, OnCreatureEnterCombat, OnCreatureDeath, SetData64,
/// SetData, Update, InitiateBreath and InitiateDrakonid.
/// </summary>
public sealed partial class BlackwingLairInstance
{
    private readonly HashSet<ObjectGuid> _eggs = [];
    private readonly Dictionary<ObjectGuid, (float X, float Y, float Z, float O, bool Temporary)> _eggLocations = [];
    private readonly HashSet<ObjectGuid> _brokenEggs = [];
    private readonly HashSet<ObjectGuid> _defenders = [];
    private readonly HashSet<ObjectGuid> _drakonids = [];
    private readonly HashSet<ObjectGuid> _bones = [];
    private Creature? _victor;
    private uint _defenseMs, _eggResetMs, _nefarianSpawnMs;
    private int _orcCount, _dragonCount, _drakonidDeaths;

    public int EggCount => _eggs.Count;
    public int BrokenEggCount => _brokenEggs.Count;
    public int DrakonidDeaths => _drakonidDeaths;

    /// <summary>SD2 OnCreatureCreate stores only the throne-room Lord Victor Nefarius (not Vaelastrasz's intro summon).</summary>
    public bool IsEncounterVictor(ObjectGuid guid) => _victor is { } victor && victor.Guid == guid;

    public override void Initialize()
    {
        base.Initialize();
        _eggs.Clear(); _eggLocations.Clear(); _brokenEggs.Clear(); _defenders.Clear(); _drakonids.Clear(); _bones.Clear();
        _defenseMs = _eggResetMs = _nefarianSpawnMs = 0;
        _orcCount = _dragonCount = _drakonidDeaths = 0;
        _victor = null;
    }

    private void TrackEncounterObject(GameObject go)
    {
        if (go.Entry == 177807)
        {
            _eggs.Add(go.Guid);
            _eggLocations[go.Guid] = (go.X, go.Y, go.Z, go.Orientation, go.Spawn is null);
        }
        if (go.Entry == 179804) _bones.Add(go.Guid);
    }

    /// <summary>SD2 SetData64(DATA_DRAGON_EGG): each distinct egg can count once; the last enters phase two.</summary>
    public bool DestroyEgg(GameObject egg)
    {
        if (egg.Entry != 177807 || !_eggs.Contains(egg.Guid) || GetData(0) != EncounterState.InProgress
            || !_brokenEggs.Add(egg.Guid)) return false;
        egg.LootState = GameObjectLootState.JustDeactivated;
        if (_brokenEggs.Count == _eggs.Count)
        {
            SetData(0, EncounterState.Special);
            SetOrbLocked(true);
            CleanupDefenders();
            if (GetSingleCreatureFromStorage(12435) is { } razorgore)
            {
                razorgore.System?.RemoveAuras(razorgore, 19832);
                razorgore.AI?.OnReceiveAiEvent(1, razorgore, razorgore, 0);
            }
        }
        return true;
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Entry is 12435 or 13020 or 11583 or 12557) StoreCreature(creature);
        if (creature.Entry == 10162 && creature.Z > 430)
        {
            _victor = creature;
            StoreCreature(creature);
        }
        if (creature.Entry is 12416 or 12420 or 12422)
        {
            _defenders.Add(creature.Guid);
            if (creature.Entry == 12422) _dragonCount++; else _orcCount++;
        }
        if (creature.Entry is >= 14261 and <= 14265 or 14302) _drakonids.Add(creature.Guid);
    }

    public override void OnCreatureEnterCombat(Creature creature)
    {
        if (creature.Entry == 12557 && GetData(0) != EncounterState.Done)
        {
            SetData(0, EncounterState.InProgress);
            _defenseMs = 40000;
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        switch (creature.Entry)
        {
            case 12557: SetOrbLocked(false); break;
            case 12435 when GetData(0) != EncounterState.Special && GetData(0) != EncounterState.Done:
                SetData(0, EncounterState.Fail); break;
            case 12416:
            case 12420: if (_defenders.Remove(creature.Guid)) _orcCount--; break;
            case 12422: if (_defenders.Remove(creature.Guid)) _dragonCount--; break;
            case >= 14261 and <= 14265:
            case 14302:
                if (_drakonids.Remove(creature.Guid) && GetData(7) == EncounterState.InProgress
                    && ++_drakonidDeaths >= 42) SetData(7, EncounterState.Special);
                break;
        }
    }

    public override bool OnGameObjectUse(Player player, GameObject go)
    {
        if (go.Entry != 177808) return false;
        if (GetData(0) != EncounterState.InProgress || (go.Flags & GameObjectFlags.NoInteract) != 0
            || GetSingleCreatureFromStorage(12557) is { IsAlive: true }
            || GetSingleCreatureFromStorage(12435) is not { IsAlive: true } razorgore)
            return true;
        CastPlayerTargetSpell?.Invoke(player, 19832, razorgore.Guid);
        return true;
    }

    public override void OnAreaTrigger(Player player, uint triggerId)
    {
        if (triggerId == 3626 && player.IsAlive && !player.IsGameMaster
            && GetSingleCreatureFromStorage(13020) is { AI: VaelastraszAI vael })
            vael.BeginIntro();
    }

    private void SetOrbLocked(bool locked)
    {
        if (GetSingleGameObjectFromStorage(177808) is { } orb)
            orb.Flags = locked ? orb.Flags | GameObjectFlags.NoInteract : orb.Flags & ~GameObjectFlags.NoInteract;
    }

    private void CleanupDefenders()
    {
        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        foreach (ObjectGuid guid in _defenders)
            if (system?.FindCreature(guid) is { } defender) system.ForcedDespawn(defender, 1);
        _defenders.Clear(); _orcCount = _dragonCount = 0;
        _defenseMs = 0;
    }

    private void ResetRazorgore()
    {
        CleanupDefenders();
        _brokenEggs.Clear();
        _eggResetMs = 30000;
        _defenseMs = 0;
        SetOrbLocked(true);
        if (GetSingleCreatureFromStorage(12435) is { } razorgore)
        {
            razorgore.System?.SayText(razorgore, 9591); // SAY_RAZORGORE_DEATH (-1469025, broadcast_text 9591)
            if (razorgore.IsAlive) razorgore.System?.CastSpell(razorgore, 23024, null, triggered: true);
        }
    }

    private void UpdateMainGate()
    {
        if (GetSingleGameObjectFromStorage(176964) is { } gate)
            gate.State = Encounters.Take(8).Any(s => s is EncounterState.InProgress or EncounterState.Special)
                ? GameObjectState.Ready : GameObjectState.Active;
    }

    private void UpdateNefarian(uint state)
    {
        if (GetSingleGameObjectFromStorage(176966) is { } gate)
            gate.State = state is EncounterState.InProgress or EncounterState.Special
                ? GameObjectState.Ready : GameObjectState.Active;
        if (state == EncounterState.Special)
        {
            _nefarianSpawnMs = 5000;
            CleanupSpawners();
            if (_victor is { } victor)
                victor.System?.ForcedDespawn(victor, 2500);
        }
        else if (state == EncounterState.Fail)
        {
            _nefarianSpawnMs = 0;
            _drakonidDeaths = 0;
            CleanupSpawners();
            CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
            foreach (ObjectGuid guid in _drakonids)
                if (system?.FindCreature(guid) is { } add) system.ForcedDespawn(add, 1);
            _drakonids.Clear();
            if (system?.Creatures.FirstOrDefault(c => c.Entry == 11583) is { } nefarian)
                system.ForcedDespawn(nefarian, 1);
            if (_victor is { } victor)
            {
                if (system?.FindCreature(victor.Guid) is { IsAlive: false } dead)
                    system.ForceRespawn(dead);
                else if (system?.FindCreature(victor.Guid) is null
                    && system?.Content.FindTemplate(10162) is { } template)
                    system.SpawnTemporary(template, victor.X, victor.Y, victor.Z, victor.Orientation);
            }
            foreach (ObjectGuid guid in _bones)
                if (Instance.FindUpdater<GameObjectMapSystem>()?.Find(guid) is { } bone)
                    bone.LootState = GameObjectLootState.JustDeactivated;
        }
    }

    private void CleanupSpawners()
    {
        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        if (system is null) return;
        foreach (Creature creature in system.Creatures.Where(c => c.Entry is >= 14307 and <= 14312).ToArray())
            system.ForcedDespawn(creature, 1);
    }

    /// <summary>SD2 boss_nefarianAI phase three: 23362 raises the drakonid bones left by phase one.</summary>
    public int RaiseBones(Creature nefarian)
    {
        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        GameObjectMapSystem? objects = Instance.FindUpdater<GameObjectMapSystem>();
        if (system?.Content.FindTemplate(14605) is not { } template || objects is null) return 0;
        int count = 0;
        foreach (ObjectGuid guid in _bones)
        {
            if (objects.Find(guid) is not { IsSpawned: true } bone) continue;
            Creature construct = system.SpawnTemporary(template, bone.X, bone.Y, bone.Z, bone.Orientation);
            if (Instance.Players.FirstOrDefault(p => p.IsAlive) is { } player) construct.AI?.AttackStart(player);
            bone.LootState = GameObjectLootState.JustDeactivated;
            count++;
        }
        return count;
    }

    public override void Update(uint diffMs)
    {
        if (_eggResetMs > 0 && (_eggResetMs = _eggResetMs > diffMs ? _eggResetMs - diffMs : 0) == 0)
        {
            if (GetSingleCreatureFromStorage(12435) is { IsAlive: false } razorgore)
                Instance.FindUpdater<CreatureMapSystem>()?.ForceRespawn(razorgore);
            GameObjectMapSystem? objects = Instance.FindUpdater<GameObjectMapSystem>();
            foreach (ObjectGuid guid in _eggs.ToArray())
            {
                if (objects?.Find(guid) is { } egg)
                {
                    if (egg.IsSpawned) egg.LootState = GameObjectLootState.Ready;
                    else objects.ForceRespawn(egg);
                }
                else if (objects is not null && _eggLocations.TryGetValue(guid, out var location) && location.Temporary)
                {
                    _eggs.Remove(guid);
                    _eggLocations.Remove(guid);
                    objects.Summon(177807, location.X, location.Y, location.Z, location.O);
                }
            }
        }
        if (_nefarianSpawnMs > 0 && (_nefarianSpawnMs = _nefarianSpawnMs > diffMs ? _nefarianSpawnMs - diffMs : 0) == 0)
        {
            CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
            if (system?.Content.FindTemplate(11583) is { } template)
                system.SpawnTemporary(template, -7348.849f, -1495.134f, 552.5152f, 0);
        }
        if (GetData(0) != EncounterState.InProgress || _defenseMs == 0) return;
        if (_defenseMs > diffMs) { _defenseMs -= diffMs; return; }
        _defenseMs = 15000;
        SpawnDefenders();
    }

    private void SpawnDefenders()
    {
        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        if (system is null) return;
        // SD2 Update: four generators per turn, at most 12 dragonspawn and 40 orcs.
        Creature[] generators = [.. system.Creatures.Where(c => c.Entry == 12434).Take(4)];
        foreach (Creature generator in generators)
        {
            uint entry = _dragonCount < 12 && system.RandomInt(0, 3) >= 2 ? 12422u
                : system.RandomInt(0, 1) == 0 ? 12416u : 12420u;
            if ((entry == 12422 ? _dragonCount >= 12 : _orcCount >= 40)
                || system.Content.FindTemplate(entry) is not { } template) continue;
            Creature defender = system.SpawnTemporary(template, generator.X, generator.Y, generator.Z, generator.Orientation);
            if (Instance.Players.FirstOrDefault(p => p.IsAlive) is { } player) defender.AI?.AttackStart(player);
        }
    }

    public override bool OnSpellEvent(Unit caster, uint eventId)
    {
        if (eventId is >= 8446 and <= 8455)
        {
            uint[] left = [23187, 23308, 23310, 23313, 23315];
            uint[] right = [23189, 23309, 23312, 23314, 23316];
            uint slot = eventId <= 8450 ? 9u : 10u;
            if (GetData(slot) == 0) SetData(slot, eventId <= 8450 ? left[eventId - 8446] : right[eventId - 8451]);
            return true;
        }
        if (eventId is >= 8520 and <= 8529)
        {
            uint[] spawners = [14307, 14309, 14310, 14311, 14312];
            uint slot = eventId <= 8524 ? 11u : 12u;
            if (GetData(slot) == 0) SetData(slot, spawners[(eventId - 8520) % 5]);
            return true;
        }
        return false;
    }
}
